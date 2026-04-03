using System;
using System.IO;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

/// <summary>
/// Standalone test for FFmpeg.AutoGen h264_nvenc encoding + MPEG-TS muxing.
/// Run: dotnet run --project GpuShaderTest -- ffmpeg
/// </summary>
public static unsafe class FfmpegEncoderTest
{
    public static void Run()
    {
        Console.WriteLine("=== FFmpeg Native Encoder Test ===");

        // Set FFmpeg DLL path
        string ffmpegDir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "ffmpeg"));
        if (!Directory.Exists(ffmpegDir))
        {
            Console.WriteLine($"FFmpeg DLLs not found at: {ffmpegDir}");
            return;
        }
        ffmpeg.RootPath = ffmpegDir;
        Console.WriteLine($"FFmpeg path: {ffmpegDir}");
        Console.Write("Initializing bindings... ");
        try
        {
            DynamicallyLoadedBindings.Initialize();
            Console.WriteLine("OK");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Init failed: {ex.Message}");
            // Try anyway — some functions may still work
        }

        uint ver = ffmpeg.avcodec_version();
        Console.WriteLine($"avcodec version: {ver >> 16}.{(ver >> 8) & 0xFF}.{ver & 0xFF}");

        // Create D3D11 device
        int hr = D3D11CreateDevice(IntPtr.Zero, 1, IntPtr.Zero, 0x20, IntPtr.Zero, 0, 7,
            out IntPtr device, out _, out IntPtr context);
        if (hr < 0) { Console.WriteLine($"D3D11CreateDevice failed: 0x{hr:X8}"); return; }
        Console.WriteLine($"D3D11 device: 0x{device:X}");

        int width = 1920, height = 1080;

        try
        {
            // 1. Find h264_nvenc
            var codec = ffmpeg.avcodec_find_encoder_by_name("h264_nvenc");
            if (codec == null) { Console.WriteLine("h264_nvenc not found"); return; }
            Console.WriteLine($"Codec found: {Marshal.PtrToStringAnsi((IntPtr)codec->name)}");

            // 2. Allocate codec context
            var codecCtx = ffmpeg.avcodec_alloc_context3(codec);
            codecCtx->width = width;
            codecCtx->height = height;
            codecCtx->time_base = new AVRational { num = 1, den = 30 };
            codecCtx->framerate = new AVRational { num = 30, den = 1 };
            codecCtx->gop_size = 15;
            codecCtx->max_b_frames = 0;
            codecCtx->pix_fmt = AVPixelFormat.AV_PIX_FMT_D3D11;

            // 3. Inject our D3D11 device into FFmpeg's hw device context (same device = fast CopyResource)
            Console.Write("Setting up D3D11VA hw device context with our device... ");
            var hwDeviceCtx = ffmpeg.av_hwdevice_ctx_alloc(AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA);
            if (hwDeviceCtx == null) { Console.WriteLine("FAILED to alloc"); return; }

            var hwDevData = (AVHWDeviceContext*)hwDeviceCtx->data;
            var d3d11DevCtx = (AVD3D11VADeviceContext*)hwDevData->hwctx;
            d3d11DevCtx->device = (ID3D11Device*)device;

            int ret = ffmpeg.av_hwdevice_ctx_init(hwDeviceCtx);
            if (ret < 0) { Console.WriteLine($"FAILED: {FfmpegError(ret)}"); return; }
            Console.WriteLine("OK");

            // 4. Set up hardware frames context
            Console.Write("Setting up hw frames context... ");
            var hwFramesCtx = ffmpeg.av_hwframe_ctx_alloc(hwDeviceCtx);
            var framesData = (AVHWFramesContext*)hwFramesCtx->data;
            framesData->format = AVPixelFormat.AV_PIX_FMT_D3D11;
            framesData->sw_format = AVPixelFormat.AV_PIX_FMT_BGRA;
            framesData->width = width;
            framesData->height = height;
            framesData->initial_pool_size = 0; // Let FFmpeg manage pool size

            ret = ffmpeg.av_hwframe_ctx_init(hwFramesCtx);
            if (ret < 0) { Console.WriteLine($"FAILED: {FfmpegError(ret)}"); return; }
            Console.WriteLine("OK");

            codecCtx->hw_frames_ctx = ffmpeg.av_buffer_ref(hwFramesCtx);

            // 5. Open codec
            Console.Write("Opening codec... ");
            AVDictionary* opts = null;
            ffmpeg.av_dict_set(&opts, "preset", "p6", 0);
            ffmpeg.av_dict_set(&opts, "tune", "ll", 0);
            ffmpeg.av_dict_set(&opts, "rc", "constqp", 0);
            ffmpeg.av_dict_set(&opts, "qp", "20", 0);

            ret = ffmpeg.avcodec_open2(codecCtx, codec, &opts);
            ffmpeg.av_dict_free(&opts);
            if (ret < 0) { Console.WriteLine($"FAILED: {FfmpegError(ret)}"); return; }
            Console.WriteLine("OK");

            // 6. Set up MPEG-TS muxer with memory output
            Console.Write("Setting up MPEG-TS muxer... ");
            AVFormatContext* fmtCtx = null;
            ret = ffmpeg.avformat_alloc_output_context2(&fmtCtx, null, "mpegts", null);
            if (ret < 0) { Console.WriteLine($"FAILED: {FfmpegError(ret)}"); return; }

            long totalMuxedBytes = 0;
            // Custom AVIO write callback
            avio_alloc_context_write_packet writeCallback = (void* opaque, byte* buf, int buf_size) =>
            {
                totalMuxedBytes += buf_size;
                return buf_size;
            };

            byte* ioBuffer = (byte*)ffmpeg.av_malloc(65536);
            var ioCtx = ffmpeg.avio_alloc_context(ioBuffer, 65536, 1, null, null, writeCallback, null);
            fmtCtx->pb = ioCtx;
            fmtCtx->flags |= ffmpeg.AVFMT_FLAG_CUSTOM_IO;

            var stream = ffmpeg.avformat_new_stream(fmtCtx, null);
            ffmpeg.avcodec_parameters_from_context(stream->codecpar, codecCtx);
            stream->time_base = codecCtx->time_base;

            ret = ffmpeg.avformat_write_header(fmtCtx, null);
            if (ret < 0) { Console.WriteLine($"FAILED: {FfmpegError(ret)}"); return; }
            Console.WriteLine("OK");

            // 7. Encode some frames
            var frame = ffmpeg.av_frame_alloc();
            var pkt = ffmpeg.av_packet_alloc();

            Console.WriteLine($"\nEncoding {10} test frames at {width}x{height}...");
            for (int i = 0; i < 10; i++)
            {
                ret = ffmpeg.av_hwframe_get_buffer(hwFramesCtx, frame, 0);
                if (ret < 0) { Console.WriteLine($"  av_hwframe_get_buffer failed: {FfmpegError(ret)}"); break; }

                frame->pts = i;
                if (i == 0) frame->pict_type = AVPictureType.AV_PICTURE_TYPE_I;

                ret = ffmpeg.avcodec_send_frame(codecCtx, frame);
                ffmpeg.av_frame_unref(frame);
                if (ret < 0) { Console.WriteLine($"  send_frame failed: {FfmpegError(ret)}"); break; }

                while (true)
                {
                    ret = ffmpeg.avcodec_receive_packet(codecCtx, pkt);
                    if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF) break;
                    if (ret < 0) { Console.WriteLine($"  receive_packet failed: {FfmpegError(ret)}"); break; }

                    pkt->stream_index = stream->index;
                    ffmpeg.av_packet_rescale_ts(pkt, codecCtx->time_base, stream->time_base);
                    ffmpeg.av_interleaved_write_frame(fmtCtx, pkt);
                    ffmpeg.av_packet_unref(pkt);
                }
                Console.WriteLine($"  Frame {i}: encoded, total muxed={totalMuxedBytes} bytes");
            }

            ffmpeg.av_write_trailer(fmtCtx);
            Console.WriteLine($"\n*** PASS — Encoded 10 frames, {totalMuxedBytes} bytes MPEG-TS output ***");

            // Cleanup
            ffmpeg.av_packet_free(&pkt);
            ffmpeg.av_frame_free(&frame);
            ffmpeg.avcodec_free_context(&codecCtx);
            ffmpeg.av_buffer_unref(&hwFramesCtx);
            ffmpeg.av_buffer_unref(&hwDeviceCtx);
            ffmpeg.avio_context_free(&ioCtx);
            ffmpeg.avformat_free_context(fmtCtx);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\nEXCEPTION: {ex}");
        }

        // Don't release device/context — FFmpeg took ownership when we injected it
        Console.WriteLine("Done.");
    }

    static string FfmpegError(int error)
    {
        var buf = stackalloc byte[256];
        ffmpeg.av_strerror(error, buf, 256);
        return Marshal.PtrToStringAnsi((IntPtr)buf) ?? $"error {error}";
    }

    [DllImport("d3d11.dll")]
    static extern int D3D11CreateDevice(
        IntPtr pAdapter, int DriverType, IntPtr Software, uint Flags,
        IntPtr pFeatureLevels, uint FeatureLevels, uint SDKVersion,
        out IntPtr ppDevice, out int pFeatureLevel, out IntPtr ppImmediateContext);
}
