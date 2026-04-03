using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using FFmpeg.AutoGen;
using ResoniteModLoader;

namespace DesktopBuddy;

/// <summary>
/// GPU-accelerated H.264 encoder using FFmpeg's h264_nvenc + in-process MPEG-TS muxing.
/// No external ffmpeg.exe process. No NvEncSharp. D3D11 textures stay on GPU.
/// MPEG-TS data written directly to ring buffer via custom AVIOContext callback.
/// </summary>
public sealed unsafe class FfmpegEncoder : IDisposable
{
    private readonly int _streamId;
    private bool _initialized;
    private bool _initFailed;

    // FFmpeg contexts
    private AVCodecContext* _codecCtx;
    private AVFormatContext* _fmtCtx;
    private AVIOContext* _ioCtx;
    private AVStream* _stream;
    private AVBufferRef* _hwDeviceCtx;
    private AVBufferRef* _hwFramesCtx;
    private AVFrame* _hwFrame;
    private AVPacket* _pkt;

    // Ring buffer: MPEG-TS data written here by AVIO callback. HTTP clients read from it.
    private byte[] _ringBuffer;
    private long _ringWritePos;
    private readonly object _ringLock = new();
    private readonly SemaphoreSlim _dataAvailable = new(0, 1);

    private uint _width, _height;
    private int _totalFrames;
    private readonly uint _fps = 30;

    private const int RING_SIZE = 4 * 1024 * 1024; // 4MB
    private const int AVIO_BUFFER_SIZE = 65536;
    private const byte MPEGTS_SYNC = 0x47;
    private const int MPEGTS_PACKET_SIZE = 188;

    // Async encode: WGC callback signals new frame, encode thread does the work
    private Thread _encodeThread;
    private volatile bool _disposed;
    private readonly AutoResetEvent _frameSignal = new(false);
    private volatile IntPtr _pendingTexture; // Set by WGC callback, consumed by encode thread
    private volatile uint _pendingWidth, _pendingHeight;
    private IntPtr _deviceContext; // FFmpeg's D3D11 device context for encode thread
    private IntPtr _lastTexture; // Last encoded texture for keepalive re-encode
    private uint _lastWidth, _lastHeight;
    private long _startTicks;

    // Pin this delegate so GC doesn't collect it while AVIO holds a pointer
    private avio_alloc_context_write_packet _writeCallbackDelegate;
    private GCHandle _selfHandle;

    public bool IsInitialized => _initialized;
    public bool IsRunning => _initialized;

    // Static init — set FFmpeg DLL search path once
    private static bool _ffmpegPathSet;
    private static readonly object _ffmpegInitLock = new();

    public static void SetFfmpegPath()
    {
        lock (_ffmpegInitLock)
        {
            if (_ffmpegPathSet) return;

            string dllDir = FindFfmpegDlls();
            if (dllDir == null)
            {
                ResoniteMod.Msg("[FFmpeg] FATAL: Could not find FFmpeg shared libraries (avcodec, avformat, avutil)");
                return;
            }

            ffmpeg.RootPath = dllDir;
            DynamicallyLoadedBindings.Initialize();
            ResoniteMod.Msg($"[FFmpeg] Library path: {dllDir}");

            // Log version
            uint ver = ffmpeg.avcodec_version();
            ResoniteMod.Msg($"[FFmpeg] avcodec version: {ver >> 16}.{(ver >> 8) & 0xFF}.{ver & 0xFF}");

            _ffmpegPathSet = true;
        }
    }

    public static string FindFfmpegDlls()
    {
        // FFmpeg DLLs live in {Resonite}/ffmpeg/ — deployed by the build
        var modDir = Path.GetDirectoryName(typeof(FfmpegEncoder).Assembly.Location) ?? "";
        var ffmpegDir = Path.GetFullPath(Path.Combine(modDir, "..", "ffmpeg"));
        if (File.Exists(Path.Combine(ffmpegDir, "avcodec-61.dll")))
            return ffmpegDir;
        return null;
    }

    /// <summary>Wait for new data (up to timeoutMs).</summary>
    public void WaitForData(int timeoutMs)
    {
        _dataAvailable.Wait(timeoutMs);
    }

    /// <summary>Async wait for new data.</summary>
    public System.Threading.Tasks.Task WaitForDataAsync(int timeoutMs)
    {
        return _dataAvailable.WaitAsync(timeoutMs);
    }

    /// <summary>
    /// Read MPEG-TS data from the ring buffer. Returns bytes read.
    /// </summary>
    public int ReadStream(byte[] buffer, ref long readPos, ref bool aligned)
    {
        lock (_ringLock)
        {
            long available = _ringWritePos - readPos;
            if (available <= 0) return 0;

            if (available > RING_SIZE)
            {
                readPos = _ringWritePos - RING_SIZE;
                available = RING_SIZE;
                aligned = false;
            }

            if (!aligned)
            {
                for (long s = readPos; s < _ringWritePos - MPEGTS_PACKET_SIZE; s++)
                {
                    byte b = _ringBuffer[(int)(s % RING_SIZE)];
                    if (b == MPEGTS_SYNC)
                    {
                        byte next = _ringBuffer[(int)((s + MPEGTS_PACKET_SIZE) % RING_SIZE)];
                        if (next == MPEGTS_SYNC)
                        {
                            readPos = s;
                            available = _ringWritePos - readPos;
                            aligned = true;
                            break;
                        }
                    }
                }
                if (!aligned) return 0;
            }

            int toRead = (int)Math.Min(available, buffer.Length);
            int ringPos = (int)(readPos % RING_SIZE);
            int firstChunk = Math.Min(toRead, RING_SIZE - ringPos);
            Buffer.BlockCopy(_ringBuffer, ringPos, buffer, 0, firstChunk);
            if (firstChunk < toRead)
                Buffer.BlockCopy(_ringBuffer, 0, buffer, firstChunk, toRead - firstChunk);
            readPos += toRead;
            return toRead;
        }
    }

    /// <summary>
    /// Initialize encoder. Call on first frame with the D3D11 device pointer.
    /// </summary>
    private readonly object _initLock = new();

    public bool Initialize(IntPtr d3dDevice, uint width, uint height)
    {
        lock (_initLock)
        {
        if (_initialized) return true;
        if (_initFailed) return false;

        try
        {
            SetFfmpegPath();
            if (!_ffmpegPathSet) { _initFailed = true; return false; }

            _width = width & ~1u;
            _height = height & ~1u;

            bool useHevc = width > 4096 || height > 4096;
            string codecName = useHevc ? "hevc_nvenc" : "h264_nvenc";

            ResoniteMod.Msg($"[FfmpegEnc:{_streamId}] Initializing: {width}x{height} {codecName} @ {_fps}fps");

            // Find NVENC encoder
            var codec = ffmpeg.avcodec_find_encoder_by_name(codecName);
            if (codec == null)
            {
                ResoniteMod.Msg($"[FfmpegEnc:{_streamId}] {codecName} not found, trying software h264");
                codec = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_H264);
            }
            if (codec == null) { ResoniteMod.Msg($"[FfmpegEnc:{_streamId}] No H.264 encoder found"); _initFailed = true; return false; }

            _codecCtx = ffmpeg.avcodec_alloc_context3(codec);
            if (_codecCtx == null) { _initFailed = true; return false; }

            // Configure encoder — optimized for low-latency desktop streaming
            _codecCtx->width = (int)_width;
            _codecCtx->height = (int)_height;
            _codecCtx->time_base = new AVRational { num = 1, den = (int)_fps };
            _codecCtx->framerate = new AVRational { num = (int)_fps, den = 1 };
            _codecCtx->gop_size = (int)_fps; // Keyframe every 1s
            _codecCtx->max_b_frames = 0; // No B-frames — critical for low latency
            _codecCtx->pix_fmt = AVPixelFormat.AV_PIX_FMT_D3D11;
            _codecCtx->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY;
            _codecCtx->bit_rate = 8_000_000; // 8 Mbps target — good for 1080p motion
            _codecCtx->rc_max_rate = 12_000_000; // 12 Mbps ceiling for burst scenes
            _codecCtx->rc_buffer_size = 8_000_000; // 1s buffer at target rate

            // Set up D3D11 hardware device context using our existing device
            SetupHardwareContext(d3dDevice);

            // NVENC options — p1 is fastest encode, ull = ultra low latency
            AVDictionary* opts = null;
            ffmpeg.av_dict_set(&opts, "preset", "p1", 0); // Fastest encode
            ffmpeg.av_dict_set(&opts, "tune", "ull", 0); // Ultra low latency
            ffmpeg.av_dict_set(&opts, "rc", "vbr", 0); // Variable bitrate — auto adapts to motion
            ffmpeg.av_dict_set(&opts, "zerolatency", "1", 0);
            ffmpeg.av_dict_set(&opts, "delay", "0", 0); // No encode delay
            ffmpeg.av_dict_set(&opts, "rc-lookahead", "0", 0); // No lookahead — encode immediately

            int ret = ffmpeg.avcodec_open2(_codecCtx, codec, &opts);
            ffmpeg.av_dict_free(&opts);
            if (ret < 0) { ResoniteMod.Msg($"[FfmpegEnc:{_streamId}] avcodec_open2 failed: {FfmpegError(ret)}"); _initFailed = true; return false; }

            ResoniteMod.Msg($"[FfmpegEnc:{_streamId}] Codec opened: {codecName}");

            // Allocate frame and packet
            _hwFrame = ffmpeg.av_frame_alloc();
            _pkt = ffmpeg.av_packet_alloc();

            // Set up MPEG-TS muxer with custom I/O to ring buffer
            SetupMuxer();

            // Cache device context for encode thread
            var hwDevCtxData = (AVHWDeviceContext*)_hwDeviceCtx->data;
            var d3d11DevCtxData = (AVD3D11VADeviceContext*)hwDevCtxData->hwctx;
            _deviceContext = (IntPtr)d3d11DevCtxData->device_context;

            _ringBuffer = new byte[RING_SIZE];
            _ringWritePos = 0;
            _initialized = true;

            _startTicks = System.Diagnostics.Stopwatch.GetTimestamp();

            // Start background encode thread
            _encodeThread = new Thread(EncodeThreadLoop) { IsBackground = true, Name = $"FfmpegEnc_{_streamId}" };
            _encodeThread.Start();

            ResoniteMod.Msg($"[FfmpegEnc:{_streamId}] Ready: {_width}x{_height} {codecName}");
            return true;
        }
        catch (Exception ex)
        {
            ResoniteMod.Msg($"[FfmpegEnc:{_streamId}] Initialize FAILED: {ex}");
            _initFailed = true;
            return false;
        }
        } // lock
    }

    private void SetupHardwareContext(IntPtr d3dDevice)
    {
        // Create hw device context and inject our D3D11 device
        _hwDeviceCtx = ffmpeg.av_hwdevice_ctx_alloc(AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA);
        if (_hwDeviceCtx == null) throw new Exception("av_hwdevice_ctx_alloc failed");

        var hwDevCtx = (AVHWDeviceContext*)_hwDeviceCtx->data;
        var d3d11DevCtx = (AVD3D11VADeviceContext*)hwDevCtx->hwctx;

        // Set our existing D3D11 device — FFmpeg will use it for NVENC
        d3d11DevCtx->device = (ID3D11Device*)d3dDevice;
        // Note: device_context is optional — FFmpeg will create one if null

        int ret = ffmpeg.av_hwdevice_ctx_init(_hwDeviceCtx);
        if (ret < 0) throw new Exception($"av_hwdevice_ctx_init failed: {FfmpegError(ret)}");

        ResoniteMod.Msg($"[FfmpegEnc:{_streamId}] D3D11VA hardware context initialized with device 0x{d3dDevice:X}");

        // Create hardware frames context
        _hwFramesCtx = ffmpeg.av_hwframe_ctx_alloc(_hwDeviceCtx);
        if (_hwFramesCtx == null) throw new Exception("av_hwframe_ctx_alloc failed");

        var framesCtx = (AVHWFramesContext*)_hwFramesCtx->data;
        framesCtx->format = AVPixelFormat.AV_PIX_FMT_D3D11;
        framesCtx->sw_format = AVPixelFormat.AV_PIX_FMT_BGRA;
        framesCtx->width = (int)_width;
        framesCtx->height = (int)_height;
        framesCtx->initial_pool_size = 0; // Let FFmpeg manage pool — fixed value causes texture creation failure

        ret = ffmpeg.av_hwframe_ctx_init(_hwFramesCtx);
        if (ret < 0) throw new Exception($"av_hwframe_ctx_init failed: {FfmpegError(ret)}");

        _codecCtx->hw_frames_ctx = ffmpeg.av_buffer_ref(_hwFramesCtx);
        ResoniteMod.Msg($"[FfmpegEnc:{_streamId}] Hardware frames context initialized: {_width}x{_height} D3D11/BGRA");
    }

    private void SetupMuxer()
    {
        // Pin ourselves so the write callback can reference us
        _selfHandle = GCHandle.Alloc(this);

        // Allocate MPEG-TS format context
        AVFormatContext* fmtCtx = null;
        int ret = ffmpeg.avformat_alloc_output_context2(&fmtCtx, null, "mpegts", null);
        if (ret < 0 || fmtCtx == null) throw new Exception($"avformat_alloc_output_context2 failed: {FfmpegError(ret)}");
        _fmtCtx = fmtCtx;

        // Create custom AVIO context — write callback feeds ring buffer
        byte* ioBuffer = (byte*)ffmpeg.av_malloc(AVIO_BUFFER_SIZE);
        _writeCallbackDelegate = WriteCallback;
        _ioCtx = ffmpeg.avio_alloc_context(
            ioBuffer, AVIO_BUFFER_SIZE,
            1, // write_flag
            (void*)GCHandle.ToIntPtr(_selfHandle),
            null, // read
            _writeCallbackDelegate,
            null  // seek
        );
        if (_ioCtx == null) throw new Exception("avio_alloc_context failed");

        _fmtCtx->pb = _ioCtx;
        _fmtCtx->flags |= ffmpeg.AVFMT_FLAG_CUSTOM_IO;

        // Add video stream
        _stream = ffmpeg.avformat_new_stream(_fmtCtx, null);
        if (_stream == null) throw new Exception("avformat_new_stream failed");

        ffmpeg.avcodec_parameters_from_context(_stream->codecpar, _codecCtx);
        _stream->time_base = _codecCtx->time_base;

        // Write MPEG-TS header
        ret = ffmpeg.avformat_write_header(_fmtCtx, null);
        if (ret < 0) throw new Exception($"avformat_write_header failed: {FfmpegError(ret)}");

        ResoniteMod.Msg($"[FfmpegEnc:{_streamId}] MPEG-TS muxer ready (in-process, no external ffmpeg)");
    }

    private static int WriteCallback(void* opaque, byte* buf, int buf_size)
    {
        var handle = GCHandle.FromIntPtr((IntPtr)opaque);
        var encoder = (FfmpegEncoder)handle.Target;
        return encoder.OnMpegTsData(buf, buf_size);
    }

    private int OnMpegTsData(byte* buf, int buf_size)
    {
        if (buf_size <= 0) return 0;

        // Copy MPEG-TS data to ring buffer
        lock (_ringLock)
        {
            // Copy from unmanaged buffer to managed ring buffer
            int ringPos = (int)(_ringWritePos % RING_SIZE);
            int firstChunk = Math.Min(buf_size, RING_SIZE - ringPos);

            Marshal.Copy((IntPtr)buf, _ringBuffer, ringPos, firstChunk);
            if (firstChunk < buf_size)
                Marshal.Copy((IntPtr)(buf + firstChunk), _ringBuffer, 0, buf_size - firstChunk);

            _ringWritePos += buf_size;
        }

        if (_dataAvailable.CurrentCount == 0)
            try { _dataAvailable.Release(); } catch { }

        return buf_size;
    }

    /// <summary>
    /// Queue a D3D11 texture for encoding. Returns immediately — encoding happens on background thread.
    /// The texture must be persistent (not released after this call).
    /// </summary>
    public void EncodeFrame(IntPtr srcTexture, uint width, uint height)
    {
        if (_initFailed || !_initialized) return;
        if ((width & ~1u) != _width || (height & ~1u) != _height)
        {
            if (_totalFrames == 0)
                ResoniteMod.Msg($"[FfmpegEnc:{_streamId}] Skipping frame: size mismatch init={_width}x{_height} frame={width}x{height}");
            return;
        }

        // Queue frame for encode thread — overwrites previous if it hasn't been consumed yet (drop old frame)
        _pendingTexture = srcTexture;
        _pendingWidth = width;
        _pendingHeight = height;
        _frameSignal.Set();
    }

    private void EncodeThreadLoop()
    {
        ResoniteMod.Msg($"[FfmpegEnc:{_streamId}] Encode thread started");
        long frameInterval = System.Diagnostics.Stopwatch.Frequency / _fps; // ~33ms at 30fps
        long lastEncodeTicks = 0;

        while (!_disposed)
        {
            // Wait for new frame signal — short timeout to check keepalive
            _frameSignal.WaitOne(5);
            if (_disposed) break;

            // Throttle to 30fps max
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if ((now - lastEncodeTicks) < frameInterval)
                continue;

            IntPtr texture = _pendingTexture;
            if (texture != IntPtr.Zero)
            {
                // New frame from WGC
                _pendingTexture = IntPtr.Zero;
                _lastTexture = texture;
                _lastWidth = _pendingWidth;
                _lastHeight = _pendingHeight;
            }
            else if (_lastTexture != IntPtr.Zero)
            {
                // Keepalive: re-encode last texture
                texture = _lastTexture;
            }
            else
            {
                continue;
            }
            lastEncodeTicks = now;

            try
            {
                EncodeFrameInternal(texture, _lastWidth, _lastHeight);
            }
            catch (Exception ex)
            {
                ResoniteMod.Msg($"[FfmpegEnc:{_streamId}] Encode thread error (frame {_totalFrames}): {ex}");
            }
        }
        ResoniteMod.Msg($"[FfmpegEnc:{_streamId}] Encode thread ended");
    }

    private void EncodeFrameInternal(IntPtr srcTexture, uint width, uint height)
    {
        int ret;

        using (DesktopBuddyMod.Perf.Time("ffmpeg_get_buffer"))
        {
            ret = ffmpeg.av_hwframe_get_buffer(_hwFramesCtx, _hwFrame, 0);
            if (ret < 0) { ResoniteMod.Msg($"[FfmpegEnc:{_streamId}] av_hwframe_get_buffer failed: {FfmpegError(ret)}"); return; }
        }

        // Copy our D3D11 texture to the frame's D3D11 texture
        using (DesktopBuddyMod.Perf.Time("ffmpeg_tex_copy"))
        {
            IntPtr dstTexture = (IntPtr)_hwFrame->data[0];
            int dstIndex = (int)_hwFrame->data[1];
            CopyTextureToFrame(_deviceContext, dstTexture, dstIndex, srcTexture, (int)_width, (int)_height);
        }

        _hwFrame->pts = _totalFrames;
        _hwFrame->width = (int)_width;
        _hwFrame->height = (int)_height;

        // Encode (GPU — NVENC handles keyframes via gop_size=15)
        using (DesktopBuddyMod.Perf.Time("ffmpeg_encode"))
        {
            ret = ffmpeg.avcodec_send_frame(_codecCtx, _hwFrame);
            ffmpeg.av_frame_unref(_hwFrame);
            if (ret < 0) { ResoniteMod.Msg($"[FfmpegEnc:{_streamId}] avcodec_send_frame failed: {FfmpegError(ret)}"); return; }
        }

        // Receive encoded packets and mux to MPEG-TS → ring buffer
        using (DesktopBuddyMod.Perf.Time("ffmpeg_mux"))
        {
            while (true)
            {
                ret = ffmpeg.avcodec_receive_packet(_codecCtx, _pkt);
                if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF) break;
                if (ret < 0) { ResoniteMod.Msg($"[FfmpegEnc:{_streamId}] avcodec_receive_packet failed: {FfmpegError(ret)}"); break; }

                _pkt->stream_index = _stream->index;
                ffmpeg.av_packet_rescale_ts(_pkt, _codecCtx->time_base, _stream->time_base);

                ret = ffmpeg.av_interleaved_write_frame(_fmtCtx, _pkt);
                if (ret < 0) ResoniteMod.Msg($"[FfmpegEnc:{_streamId}] av_interleaved_write_frame failed: {FfmpegError(ret)}");

                ffmpeg.av_packet_unref(_pkt);
            }
        }

        _totalFrames++;
        if (_totalFrames <= 5 || _totalFrames % 300 == 0)
            ResoniteMod.Msg($"[FfmpegEnc:{_streamId}] Frame #{_totalFrames} ({width}x{height}), ringPos={_ringWritePos}");
    }

    /// <summary>
    /// Copy source D3D11 texture to the frame's texture array via CopySubresourceRegion.
    /// The source is a single texture, the dest is a texture array (frame->data[0] is texture, data[1] is index).
    /// </summary>
    private static void CopyTextureToFrame(IntPtr deviceContext, IntPtr dstTexture, int dstArrayIndex, IntPtr srcTexture, int width, int height)
    {
        // ID3D11DeviceContext::CopySubresourceRegion vtable index = 46
        const int Ctx_CopySubresourceRegion = 46;

        // D3D11_BOX struct
        var box = stackalloc uint[6]; // left, top, front, right, bottom, back
        box[0] = 0; box[1] = 0; box[2] = 0;
        box[3] = (uint)width; box[4] = (uint)height; box[5] = 1;

        var vtable = *(IntPtr**)deviceContext;
        // CopySubresourceRegion(context, dst, dstSubresource, dstX, dstY, dstZ, src, srcSubresource, pSrcBox)
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, uint, IntPtr, uint, void*, void>)vtable[Ctx_CopySubresourceRegion];
        fn(deviceContext, dstTexture, (uint)dstArrayIndex, 0, 0, 0, srcTexture, 0, box);
    }

    private static string FfmpegError(int error)
    {
        var buf = stackalloc byte[256];
        ffmpeg.av_strerror(error, buf, 256);
        return Marshal.PtrToStringAnsi((IntPtr)buf) ?? $"error {error}";
    }

    public FfmpegEncoder(int streamId)
    {
        _streamId = streamId;
    }

    public void Dispose()
    {
        _initialized = false;
        _disposed = true;
        _frameSignal.Set(); // Wake encode thread so it exits
        _encodeThread?.Join(2000);

        if (_fmtCtx != null)
        {
            try { ffmpeg.av_write_trailer(_fmtCtx); } catch { }
        }

        if (_pkt != null) { var p = _pkt; ffmpeg.av_packet_free(&p); _pkt = null; }
        if (_hwFrame != null) { var f = _hwFrame; ffmpeg.av_frame_free(&f); _hwFrame = null; }
        if (_codecCtx != null) { var c = _codecCtx; ffmpeg.avcodec_free_context(&c); _codecCtx = null; }
        if (_hwFramesCtx != null) { var h = _hwFramesCtx; ffmpeg.av_buffer_unref(&h); _hwFramesCtx = null; }
        // Don't unref _hwDeviceCtx device pointer — we don't own it (WgcCapture does)
        if (_hwDeviceCtx != null) { var h = _hwDeviceCtx; ffmpeg.av_buffer_unref(&h); _hwDeviceCtx = null; }

        if (_fmtCtx != null)
        {
            if (_fmtCtx->pb != null)
            {
                var pb = _fmtCtx->pb;
                ffmpeg.avio_context_free(&pb);
                _fmtCtx->pb = null;
            }
            ffmpeg.avformat_free_context(_fmtCtx);
            _fmtCtx = null;
        }

        if (_selfHandle.IsAllocated) _selfHandle.Free();

        try { if (_dataAvailable.CurrentCount == 0) _dataAvailable.Release(); } catch { }
        try { _dataAvailable.Dispose(); } catch { }

        ResoniteMod.Msg($"[FfmpegEnc:{_streamId}] Disposed, {_totalFrames} total frames");
    }
}
