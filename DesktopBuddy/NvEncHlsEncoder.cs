using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Lennox.NvEncSharp;
using static Lennox.NvEncSharp.LibNvEnc;
using ResoniteModLoader;

namespace DesktopBuddy;

/// <summary>
/// GPU-accelerated H.264 encoder using NVENC + FFmpeg for HLS muxing.
/// NVENC encodes D3D11 textures on GPU → tiny H.264 bitstream piped to FFmpeg for HLS segment writing.
/// No raw pixel pipe bottleneck — encoded data is ~50KB/frame vs 29MB raw.
/// </summary>
public sealed class NvEncHlsEncoder : IDisposable
{
    private NvEncoder _encoder;
    private NvEncCreateBitstreamBuffer _bitstreamBuffer;
    private bool _initialized;
    private bool _initFailed;
    private readonly string _hlsDir;
    private readonly int _streamId;

    // FFmpeg for MPEG-TS muxing (not encoding) — stdin gets H.264, stdout produces MPEG-TS
    private Process _ffmpeg;
    private Stream _ffmpegStdin;
    private Thread _drainThread;

    // Ring buffer: FFmpeg stdout is continuously drained here. HTTP clients read from it.
    private byte[] _ringBuffer;
    private long _ringWritePos;
    private readonly object _ringLock = new();

    private uint _width, _height;
    private int _totalFrames;
    private readonly uint _fps = 30;
    private long _lastFrameTicks; // For detecting pauses
    private const long PAUSE_THRESHOLD_TICKS = TimeSpan.TicksPerSecond / 5; // 200ms = pause, force IDR for decoder resync

    private const int RING_SIZE = 4 * 1024 * 1024; // 4MB ring buffer

    public string HlsDir => _hlsDir;
    public bool IsInitialized => _initialized;
    public bool IsRunning => _initialized && _ffmpeg != null && !_ffmpeg.HasExited;

    /// <summary>
    /// Read MPEG-TS data from the ring buffer. Returns bytes read.
    /// readPos tracks where this client left off — pass 0 on first call, then pass back the updated value.
    /// </summary>
    private const byte MPEGTS_SYNC = 0x47;
    private const int MPEGTS_PACKET_SIZE = 188;

    public int ReadStream(byte[] buffer, ref long readPos)
    {
        lock (_ringLock)
        {
            long available = _ringWritePos - readPos;
            if (available <= 0) return 0;

            // If client is too far behind, skip to latest data
            if (available > RING_SIZE)
            {
                readPos = _ringWritePos - RING_SIZE;
                available = RING_SIZE;
            }

            // First read: align to MPEG-TS packet boundary (find 0x47 sync byte)
            if (readPos == 0 || available > RING_SIZE / 2)
            {
                long searchStart = readPos;
                bool found = false;
                for (long s = searchStart; s < _ringWritePos - MPEGTS_PACKET_SIZE; s++)
                {
                    byte b = _ringBuffer[(int)(s % RING_SIZE)];
                    // Check for sync byte and verify next packet also starts with sync
                    if (b == MPEGTS_SYNC)
                    {
                        byte next = _ringBuffer[(int)((s + MPEGTS_PACKET_SIZE) % RING_SIZE)];
                        if (next == MPEGTS_SYNC)
                        {
                            readPos = s;
                            available = _ringWritePos - readPos;
                            found = true;
                            break;
                        }
                    }
                }
                if (!found) return 0; // No valid packet found yet
            }

            int toRead = (int)Math.Min(available, buffer.Length);
            for (int i = 0; i < toRead; i++)
            {
                buffer[i] = _ringBuffer[(int)((readPos + i) % RING_SIZE)];
            }
            readPos += toRead;
            return toRead;
        }
    }

    public NvEncHlsEncoder(int streamId, string hlsDir)
    {
        _streamId = streamId;
        _hlsDir = hlsDir;
        if (Directory.Exists(_hlsDir)) { try { Directory.Delete(_hlsDir, true); } catch { } }
        Directory.CreateDirectory(_hlsDir);
    }

    /// <summary>
    /// Initialize NVENC encoder + FFmpeg muxer. Call on first frame.
    /// d3dDevice: the WGC capture's D3D11 device pointer.
    /// </summary>
    public bool Initialize(IntPtr d3dDevice, uint width, uint height)
    {
        if (_initFailed) return false;
        try
        {
            // NVENC requires even dimensions — round down to avoid garbage edge pixels
            _width = width & ~1u;
            _height = height & ~1u;

            // Use HEVC for >4096 width (NVENC H.264 limit), H.264 otherwise
            bool useHevc = width > 4096 || height > 4096;
            var codecGuid = useHevc ? NvEncCodecGuids.Hevc : NvEncCodecGuids.H264;
            string codecName = useHevc ? "HEVC" : "H.264";

            ResoniteMod.Msg($"[NvEnc:{_streamId}] Initializing: {width}x{height} {codecName} @ {_fps}fps");

            _encoder = OpenEncoderForDirectX(d3dDevice);
            ResoniteMod.Msg($"[NvEnc:{_streamId}] Encoder opened");

            var presetConfig = _encoder.GetEncodePresetConfigEx(
                codecGuid, NvEncPresetGuids.P6, NvEncTuningInfo.LowLatency).PresetCfg;

            presetConfig.RcParams.AverageBitRate = 0;
            presetConfig.RcParams.MaxBitRate = 10_000_000; // 10 Mbps cap
            presetConfig.RcParams.RateControlMode = NvEncParamsRcMode.Constqp;
            presetConfig.RcParams.ConstQP = new NvEncQp { QpInterP = 20, QpInterB = 20, QpIntra = 18 };
            presetConfig.GopLength = 15; // Keyframe every 0.5s — faster recovery from lost frames

            unsafe
            {
                NvEncConfig* configPtr = &presetConfig;
                var initParams = new NvEncInitializeParams
                {
                    Version = NV_ENC_INITIALIZE_PARAMS_VER,
                    EncodeGuid = codecGuid,
                    EncodeWidth = width,
                    EncodeHeight = height,
                    MaxEncodeWidth = width,
                    MaxEncodeHeight = height,
                    DarWidth = width,
                    DarHeight = height,
                    FrameRateNum = _fps,
                    FrameRateDen = 1,
                    EnablePTD = 1,
                    PresetGuid = NvEncPresetGuids.P6,
                    TuningInfo = NvEncTuningInfo.LowLatency,
                    EncodeConfig = configPtr,
                };

                _encoder.InitializeEncoder(ref initParams);
            }

            _bitstreamBuffer = _encoder.CreateBitstreamBuffer();
            ResoniteMod.Msg($"[NvEnc:{_streamId}] NVENC initialized");

            // Start FFmpeg for HLS muxing only — reads encoded H.264 from stdin
            StartFfmpegMuxer(width, height, useHevc);

            _initialized = true;
            ResoniteMod.Msg($"[NvEnc:{_streamId}] Ready: {width}x{height} {codecName}, 6 Mbps VBR");
            return true;
        }
        catch (Exception ex)
        {
            ResoniteMod.Msg($"[NvEnc:{_streamId}] Initialize FAILED: {ex}");
            _initFailed = true;
            return false;
        }
    }

    private void StartFfmpegMuxer(uint width, uint height, bool hevc)
    {
        var ffmpegPath = MjpegServer.FindFfmpeg();
        if (ffmpegPath == null) throw new Exception("FFmpeg not found");

        string codec = hevc ? "hevc" : "h264";
        string playlistPath = Path.Combine(_hlsDir, "stream.m3u8");
        string segPattern = Path.Combine(_hlsDir, "seg%04d.ts");

        // FFmpeg reads already-encoded H.264/HEVC bitstream, just muxes to HLS
        // MPEG-TS to stdout — wallclock timestamps for proper playback timing
        string args = $"-use_wallclock_as_timestamps 1 -probesize 32 -f {codec} -framerate {_fps} -i pipe:0 " +
                      $"-c:v copy " +
                      $"-f mpegts -an pipe:1";

        ResoniteMod.Msg($"[NvEnc:{_streamId}] FFmpeg muxer: {args}");

        _ffmpeg = Process.Start(new ProcessStartInfo
        {
            FileName = ffmpegPath, Arguments = args,
            UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true,
        })!;

        int ffmpegPid = _ffmpeg.Id;
        _ffmpeg.ErrorDataReceived += (s, e) =>
        {
            if (e.Data != null) ResoniteMod.Msg($"[FFmux:{ffmpegPid}] {e.Data}");
        };
        _ffmpeg.BeginErrorReadLine();
        _ffmpegStdin = _ffmpeg.StandardInput.BaseStream;

        ResoniteMod.Msg($"[NvEnc:{_streamId}] FFmpeg muxer PID={ffmpegPid}, stdin ready={_ffmpegStdin.CanWrite}");

        // Drain FFmpeg stdout into ring buffer continuously — prevents stdout from filling and blocking
        _ringBuffer = new byte[RING_SIZE];
        _ringWritePos = 0;
        _drainThread = new Thread(() =>
        {
            try
            {
                var stdout = _ffmpeg.StandardOutput.BaseStream;
                var buf = new byte[65536];
                while (!_ffmpeg.HasExited)
                {
                    int read = stdout.Read(buf, 0, buf.Length);
                    if (read <= 0) break;
                    lock (_ringLock)
                    {
                        for (int i = 0; i < read; i++)
                        {
                            _ringBuffer[(int)(_ringWritePos % RING_SIZE)] = buf[i];
                            _ringWritePos++;
                        }
                    }
                }
                ResoniteMod.Msg($"[NvEnc:{_streamId}] Drain thread ended, total bytes: {_ringWritePos}");
            }
            catch (Exception ex)
            {
                ResoniteMod.Msg($"[NvEnc:{_streamId}] Drain thread error: {ex.Message}");
            }
        }) { IsBackground = true, Name = $"DesktopBuddy_Drain_{_streamId}" };
        _drainThread.Start();
    }

    /// <summary>
    /// Encode a D3D11 texture and write encoded data to FFmpeg muxer.
    /// Call from WGC OnFrameArrived callback with the source texture.
    /// </summary>
    public void EncodeFrame(IntPtr srcTexture, uint width, uint height)
    {
        if (_initFailed) return;
        if (!_initialized) return;
        // Allow original odd dimensions (NVENC rounds internally)
        if ((width & ~1u) != _width || (height & ~1u) != _height)
        {
            if (_totalFrames == 0)
                ResoniteMod.Msg($"[NvEnc:{_streamId}] Skipping frame: size mismatch init={_width}x{_height} frame={width}x{height}");
            return;
        }
        if (_ffmpeg == null || _ffmpeg.HasExited)
        {
            ResoniteMod.Msg($"[NvEnc:{_streamId}] FFmpeg not running (HasExited={_ffmpeg?.HasExited}), cannot encode");
            return;
        }

        try
        {
            var reg = new NvEncRegisterResource
            {
                Version = NV_ENC_REGISTER_RESOURCE_VER,
                BufferFormat = NvEncBufferFormat.Argb,
                BufferUsage = NvEncBufferUsage.NvEncInputImage,
                ResourceToRegister = srcTexture,
                Width = width,
                Height = height,
                Pitch = 0
            };

            using var registration = _encoder.RegisterResource(ref reg);

            // Detect pause: if >200ms since last frame, force IDR + SPS/PPS so decoder can resync
            long now = DateTime.UtcNow.Ticks;
            bool forceKeyframe = _totalFrames == 0 || (now - _lastFrameTicks) > PAUSE_THRESHOLD_TICKS;
            _lastFrameTicks = now;

            uint picFlags = 0;
            if (forceKeyframe)
                picFlags = (uint)(NvEncPicFlags.FlagForceidr | NvEncPicFlags.FlagOutputSpspps);

            var pic = new NvEncPicParams
            {
                Version = NV_ENC_PIC_PARAMS_VER,
                PictureStruct = NvEncPicStruct.Frame,
                InputBuffer = reg.AsInputPointer(),
                BufferFmt = NvEncBufferFormat.Argb,
                InputWidth = width,
                InputHeight = height,
                EncodePicFlags = picFlags,
                OutputBitstream = _bitstreamBuffer.BitstreamBuffer,
                InputTimeStamp = (ulong)_totalFrames,
                InputDuration = 1000 / _fps
            };

            _encoder.EncodePicture(ref pic);

            // Get encoded bitstream and pipe to FFmpeg muxer
            using (var bitstreamData = _encoder.LockBitstreamAndCreateStream(ref _bitstreamBuffer))
            {
                long len = bitstreamData.Length;
                try
                {
                    bitstreamData.CopyTo(_ffmpegStdin);
                    _ffmpegStdin.Flush();
                }
                catch (IOException)
                {
                    ResoniteMod.Msg($"[NvEnc:{_streamId}] Pipe write failed (FFmpeg may have exited)");
                    return;
                }
                _totalFrames++;
                if (_totalFrames <= 5 || _totalFrames % 300 == 0 || forceKeyframe)
                    ResoniteMod.Msg($"[NvEnc:{_streamId}] Frame #{_totalFrames}: {len} bytes{(forceKeyframe ? " [IDR]" : "")} ({width}x{height})");
            }
        }
        catch (Exception ex)
        {
            ResoniteMod.Msg($"[NvEnc:{_streamId}] EncodeFrame error (frame {_totalFrames}): {ex}");
        }
    }

    public void Dispose()
    {
        _initialized = false;
        try { _ffmpegStdin?.Close(); } catch { }
        try { if (_ffmpeg != null && !_ffmpeg.HasExited) _ffmpeg.Kill(); } catch { }
        try { _encoder.DestroyBitstreamBuffer(_bitstreamBuffer.BitstreamBuffer); } catch { }
        try { _encoder.DestroyEncoder(); } catch { }
        try { Directory.Delete(_hlsDir, true); } catch { }
        ResoniteMod.Msg($"[NvEnc:{_streamId}] Disposed, {_totalFrames} total frames");
    }
}
