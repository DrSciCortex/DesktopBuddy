using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using ResoniteModLoader;

namespace DesktopBuddy;

/// <summary>
/// HTTP server serving MPEG-TS streams from NvEncHlsEncoder (NVENC GPU + FFmpeg muxing).
/// NVENC encodes on GPU → tiny H.264 piped to FFmpeg → MPEG-TS stdout → HTTP to clients.
/// Fully async — no thread-per-client, scales to many concurrent viewers.
///
/// GET /stream/{streamId} → continuous MPEG-TS stream
/// </summary>
public sealed class MjpegServer : IDisposable
{
    private HttpListener _listener;
    private volatile bool _running;
    private readonly int _port;

    private readonly ConcurrentDictionary<int, FfmpegEncoder> _encoders = new();

    public int Port => _port;

    public MjpegServer(int port = 48080)
    {
        _port = port;
        _listener = new HttpListener();
        _running = true;
    }

    public void Start()
    {
        try
        {
            _listener.Prefixes.Add($"http://+:{_port}/");
            _listener.Start();
            ResoniteMod.Msg($"[MjpegServer] Listening on http://+:{_port}/");
        }
        catch (Exception ex)
        {
            ResoniteMod.Msg($"[MjpegServer] http://+ failed ({ex.Message}), adding urlacl...");
            // Try to add urlacl so http://+ works (needed for cloudflare tunnel Host header)
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "netsh",
                    Arguments = $"http add urlacl url=http://+:{_port}/ user=Everyone",
                    UseShellExecute = true,
                    Verb = "runas",
                    CreateNoWindow = true,
                    WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
                };
                var proc = System.Diagnostics.Process.Start(psi);
                proc?.WaitForExit(5000);
                ResoniteMod.Msg($"[MjpegServer] urlacl result: {proc?.ExitCode}");
            }
            catch (Exception urlEx)
            {
                ResoniteMod.Msg($"[MjpegServer] urlacl failed: {urlEx.Message}");
            }

            // Retry http://+
            _listener.Close();
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://+:{_port}/");
            _listener.Start();
            ResoniteMod.Msg($"[MjpegServer] Listening on http://+:{_port}/ (after urlacl)");
        }
        // Fire-and-forget async listen loop (runs on thread pool)
        _ = ListenLoopAsync();
    }

    public FfmpegEncoder CreateEncoder(int streamId)
    {
        var enc = new FfmpegEncoder(streamId);
        _encoders[streamId] = enc;
        ResoniteMod.Msg($"[MjpegServer] Created encoder for stream {streamId}");
        return enc;
    }

    public void StopEncoder(int streamId)
    {
        if (_encoders.TryRemove(streamId, out var enc))
            enc.Dispose();
    }

    private async Task ListenLoopAsync()
    {
        ResoniteMod.Msg("[MjpegServer] Async listen loop started");
        while (_running)
        {
            try
            {
                var ctx = await _listener.GetContextAsync().ConfigureAwait(false);
                _ = HandleRequestAsync(ctx); // Fire-and-forget per request
            }
            catch (HttpListenerException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                ResoniteMod.Msg($"[MjpegServer] Listen error: {ex.Message}");
            }
        }
        ResoniteMod.Msg("[MjpegServer] Async listen loop ended");
    }

    private async Task HandleRequestAsync(HttpListenerContext ctx)
    {
        try
        {
            var path = ctx.Request.Url?.AbsolutePath ?? "/";
            if (path.StartsWith("/stream/"))
                await ServeStreamAsync(ctx, path).ConfigureAwait(false);
            else
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
            }
        }
        catch { try { ctx.Response.Close(); } catch { } }
    }

    private async Task ServeStreamAsync(HttpListenerContext ctx, string urlPath)
    {
        var parts = urlPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) { ctx.Response.StatusCode = 404; ctx.Response.Close(); return; }

        if (!int.TryParse(parts[1], out int streamId) || !_encoders.TryGetValue(streamId, out var encoder))
        {
            ResoniteMod.Msg($"[MjpegServer] Stream {streamId} not found");
            ctx.Response.StatusCode = 404;
            ctx.Response.Close();
            return;
        }

        // Wait for encoder to be ready (async, no thread blocked)
        int waitCount = 0;
        while (!encoder.IsRunning && waitCount < 50)
        {
            await Task.Delay(100).ConfigureAwait(false);
            waitCount++;
        }
        if (!encoder.IsRunning)
        {
            ResoniteMod.Msg($"[MjpegServer] Stream {streamId} encoder not ready after {waitCount * 100}ms");
            ctx.Response.StatusCode = 503;
            ctx.Response.Close();
            return;
        }

        ResoniteMod.Msg($"[MjpegServer] Serving stream {streamId} to {ctx.Request.RemoteEndPoint}");
        ctx.Response.ContentType = "video/mp2t";
        ctx.Response.SendChunked = true;
        ctx.Response.StatusCode = 200;

        long totalBytes = 0;
        long readPos = 0; // Start from latest data
        bool aligned = false; // MPEG-TS sync state — only scan once per client
        try
        {
            var buffer = new byte[65536];
            while (_running && encoder.IsRunning)
            {
                int read = encoder.ReadStream(buffer, ref readPos, ref aligned);
                if (read > 0)
                {
                    await ctx.Response.OutputStream.WriteAsync(buffer, 0, read).ConfigureAwait(false);
                    totalBytes += read;
                }
                else
                {
                    await encoder.WaitForDataAsync(50).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            ResoniteMod.Msg($"[MjpegServer] Stream {streamId} error: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try { ctx.Response.Close(); } catch { }
            ResoniteMod.Msg($"[MjpegServer] Stream {streamId} ended, sent {totalBytes} bytes");
        }
    }

    public void Dispose()
    {
        _running = false;
        foreach (var kvp in _encoders) kvp.Value.Dispose();
        _encoders.Clear();
        try { _listener.Stop(); } catch { }
        try { _listener.Close(); } catch { }
    }

}
