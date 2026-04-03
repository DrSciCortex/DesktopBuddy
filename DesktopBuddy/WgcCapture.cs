using System;
using System.Runtime.InteropServices;
using System.Threading;
using WinRT;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace DesktopBuddy;

/// <summary>
/// Windows.Graphics.Capture based screen/window capture.
/// GPU-accelerated, per-window, no GDI overhead.
/// </summary>
public sealed class WgcCapture : IDisposable
{
    // COM interop for creating capture items from HWND without picker
    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow([In] IntPtr window, [In] ref Guid iid);
        IntPtr CreateForMonitor([In] IntPtr monitor, [In] ref Guid iid);
    }

    // Access underlying DXGI interface from WinRT surface
    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface([In] ref Guid iid);
    }

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    // D3D11 native interop
    [DllImport("d3d11.dll", EntryPoint = "D3D11CreateDevice")]
    private static extern int D3D11CreateDevice(
        IntPtr pAdapter, int DriverType, IntPtr Software, uint Flags,
        IntPtr pFeatureLevels, uint FeatureLevels, uint SDKVersion,
        out IntPtr ppDevice, out int pFeatureLevel, out IntPtr ppImmediateContext);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibraryW(string lpFileName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

    private static unsafe int CallCreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice)
    {
        var lib = LoadLibraryW("d3d11.dll");
        var proc = GetProcAddress(lib, "CreateDirect3D11DeviceFromDXGIDevice");
        if (proc == IntPtr.Zero) { graphicsDevice = IntPtr.Zero; return -1; }

        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)proc;
        IntPtr result;
        int hr = fn(dxgiDevice, &result);
        graphicsDevice = result;
        return hr;
    }

    private const int D3D_DRIVER_TYPE_HARDWARE = 1;
    private const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;

    // ID3D11Device vtable indices
    private const int ID3D11Device_CreateTexture2D = 5;
    // ID3D11DeviceContext vtable indices
    private const int ID3D11DeviceContext_Map = 14;
    private const int ID3D11DeviceContext_Unmap = 15;
    private const int ID3D11DeviceContext_CopyResource = 47;

    // D3D11 structures
    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11_TEXTURE2D_DESC
    {
        public uint Width, Height, MipLevels, ArraySize;
        public int Format; // DXGI_FORMAT
        public uint SampleCount, SampleQuality;
        public int Usage; // D3D11_USAGE
        public uint BindFlags, CPUAccessFlags, MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11_MAPPED_SUBRESOURCE
    {
        public IntPtr pData;
        public uint RowPitch;
        public uint DepthPitch;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11_SUBRESOURCE_DATA
    {
        public IntPtr pSysMem;
        public uint SysMemPitch;
        public uint SysMemSlicePitch;
    }

    private const int DXGI_FORMAT_B8G8R8A8_UNORM = 87;
    private const int D3D11_USAGE_STAGING = 3;
    private const uint D3D11_CPU_ACCESS_READ = 0x20000;

    /// <summary>
    /// Called with (d3dDevice, srcTexture, width, height) for each captured frame.
    /// Runs on WGC background thread. Texture only valid during callback.
    /// </summary>
    public Action<IntPtr, IntPtr, int, int> OnGpuFrame;

    private IntPtr _hwnd;
    private bool _isDesktop;
    private IDirect3DDevice _winrtDevice;
    private IntPtr _d3dDevice;
    private IntPtr _d3dContext;
    private GraphicsCaptureItem _item;
    private Direct3D11CaptureFramePool _framePool;
    private GraphicsCaptureSession _session;

    private IntPtr _stagingTexture;
    private IntPtr _encodeTexture; // Persistent GPU texture for NVENC encoding + keepalive
    private int _encodeTexW, _encodeTexH;
    private byte[] _buffer;
    private GCHandle _pinnedBuffer;
    private readonly object _frameLock = new();
    private volatile bool _frameReady;
    private Thread _keepaliveThread;
    private volatile bool _closed;
    private int _lastWidth, _lastHeight;
    private int _framesCaptured;
    private bool _disposed;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public int FramesCaptured => _framesCaptured;
    public bool IsValid => !_disposed && !_closed && _item != null && (_isDesktop || (IsWindow(_hwnd) && !IsIconic(_hwnd)));

    /// <summary>
    /// Initialize WGC capture for a window (hwnd) or entire desktop (hwnd=IntPtr.Zero uses primary monitor).
    /// </summary>
    public bool Init(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _isDesktop = hwnd == IntPtr.Zero;
        try
        {
            ResoniteModLoader.ResoniteMod.Msg("[WgcCapture] Step 1: D3D11CreateDevice");
            int hr = D3D11CreateDevice(IntPtr.Zero, D3D_DRIVER_TYPE_HARDWARE, IntPtr.Zero,
                D3D11_CREATE_DEVICE_BGRA_SUPPORT, IntPtr.Zero, 0, 7,
                out _d3dDevice, out _, out _d3dContext);
            if (hr < 0) { ResoniteModLoader.ResoniteMod.Msg($"[WgcCapture] D3D11CreateDevice failed hr=0x{hr:X8}"); return false; }

            ResoniteModLoader.ResoniteMod.Msg("[WgcCapture] Step 2: QueryInterface IDXGIDevice");
            var dxgiGuid = new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
            Marshal.QueryInterface(_d3dDevice, ref dxgiGuid, out IntPtr dxgiDevice);

            ResoniteModLoader.ResoniteMod.Msg("[WgcCapture] Step 3: CreateDirect3D11DeviceFromDXGIDevice");
            hr = CallCreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out IntPtr inspectable);
            Marshal.Release(dxgiDevice);
            if (hr < 0 || inspectable == IntPtr.Zero)
            {
                ResoniteModLoader.ResoniteMod.Msg($"[WgcCapture] CreateDirect3D11DeviceFromDXGIDevice failed hr=0x{hr:X8}");
                return false;
            }

            ResoniteModLoader.ResoniteMod.Msg("[WgcCapture] Step 4: GetObjectForIUnknown → IDirect3DDevice");
            _winrtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
            Marshal.Release(inspectable);

            ResoniteModLoader.ResoniteMod.Msg("[WgcCapture] Step 5: CreateCaptureItem");
            if (hwnd == IntPtr.Zero)
            {
                var monitorHandle = MonitorFromPoint(0, 0, 1);
                ResoniteModLoader.ResoniteMod.Msg($"[WgcCapture] Monitor handle: {monitorHandle}");
                _item = CreateItemForMonitor(monitorHandle);
            }
            else
            {
                _item = CreateItemForWindow(hwnd);
            }

            if (_item == null) { ResoniteModLoader.ResoniteMod.Msg("[WgcCapture] CaptureItem is null"); return false; }

            _item.Closed += (sender, args) =>
            {
                ResoniteModLoader.ResoniteMod.Msg("[WgcCapture] Closed event fired!");
                _closed = true;
            };
            ResoniteModLoader.ResoniteMod.Msg("[WgcCapture] Subscribed to Closed event");

            Width = _item.Size.Width;
            Height = _item.Size.Height;
            ResoniteModLoader.ResoniteMod.Msg($"[WgcCapture] Step 6: Item created {Width}x{Height}");

            ResoniteModLoader.ResoniteMod.Msg("[WgcCapture] Step 7: CreateFreeThreaded frame pool");
            _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                _winrtDevice,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                2,
                _item.Size);

            ResoniteModLoader.ResoniteMod.Msg("[WgcCapture] Step 8: FrameArrived += OnFrameArrived");
            _framePool.FrameArrived += OnFrameArrived;

            ResoniteModLoader.ResoniteMod.Msg("[WgcCapture] Step 9: CreateCaptureSession");
            _session = _framePool.CreateCaptureSession(_item);
            _session.IsBorderRequired = false;
            _session.IsCursorCaptureEnabled = true;

            ResoniteModLoader.ResoniteMod.Msg("[WgcCapture] Step 10: StartCapture");
            _session.StartCapture();

            ResoniteModLoader.ResoniteMod.Msg("[WgcCapture] Init complete, capture running");
            return true;
        }
        catch (Exception ex)
        {
            ResoniteModLoader.ResoniteMod.Msg($"[WgcCapture] Init failed: {ex.Message}");
            return false;
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(int x, int y, uint dwFlags);

    [DllImport("combase.dll")]
    private static extern int WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string sourceString, int length, out IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

    private static IntPtr GetActivationFactory(string className, Guid iid)
    {
        WindowsCreateString(className, className.Length, out IntPtr hstring);
        RoGetActivationFactory(hstring, ref iid, out IntPtr factory);
        WindowsDeleteString(hstring);
        return factory;
    }

    private static GraphicsCaptureItem CreateItemForWindow(IntPtr hwnd)
    {
        var interopGuid = new Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
        var factoryPtr = GetActivationFactory("Windows.Graphics.Capture.GraphicsCaptureItem", interopGuid);
        var interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factoryPtr);
        Marshal.Release(factoryPtr);

        var itemGuid = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
        var ptr = interop.CreateForWindow(hwnd, ref itemGuid);
        var item = MarshalInterface<GraphicsCaptureItem>.FromAbi(ptr);
        Marshal.Release(ptr);
        return item;
    }

    private static GraphicsCaptureItem CreateItemForMonitor(IntPtr hmon)
    {
        var interopGuid = new Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
        var factoryPtr = GetActivationFactory("Windows.Graphics.Capture.GraphicsCaptureItem", interopGuid);
        var interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factoryPtr);
        Marshal.Release(factoryPtr);

        var itemGuid = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
        var ptr = interop.CreateForMonitor(hmon, ref itemGuid);
        var item = MarshalInterface<GraphicsCaptureItem>.FromAbi(ptr);
        Marshal.Release(ptr);
        return item;
    }

    private int _frameLog;

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        try
        {
        using var frame = sender.TryGetNextFrame();
        if (frame == null) { if (_frameLog < 3) ResoniteModLoader.ResoniteMod.Msg("[WgcCapture] OnFrameArrived: frame is null"); return; }

        var size = frame.ContentSize;
        int w = size.Width;
        int h = size.Height;
        _frameLog++;
        if (_frameLog <= 5) ResoniteModLoader.ResoniteMod.Msg($"[WgcCapture] OnFrameArrived #{_frameLog}: {w}x{h}");
        if (w <= 0 || h <= 0) { if (_frameLog <= 5) ResoniteModLoader.ResoniteMod.Msg("[WgcCapture] bad size, skip"); return; }

        if (w != Width || h != Height)
        {
            ResoniteModLoader.ResoniteMod.Msg($"[WgcCapture] Resize {Width}x{Height} -> {w}x{h}");
            Width = w; Height = h;
            _framePool.Recreate(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2,
                new SizeInt32 { Width = w, Height = h });
            return; // Skip this frame
        }

        if (_frameLog <= 5) ResoniteModLoader.ResoniteMod.Msg("[WgcCapture] Getting surface texture");
        // Get the raw IUnknown* for the surface via WinRT marshaling
        IntPtr surfaceAbi = MarshalInterface<IDirect3DSurface>.FromManaged(frame.Surface);
        if (surfaceAbi == IntPtr.Zero) { if (_frameLog <= 5) ResoniteModLoader.ResoniteMod.Msg("[WgcCapture] surfaceAbi is null"); return; }

        // QI for IDirect3DDxgiInterfaceAccess
        var dxgiAccessGuid = new Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
        int qiHr = Marshal.QueryInterface(surfaceAbi, ref dxgiAccessGuid, out IntPtr dxgiAccessPtr);
        if (qiHr < 0 || dxgiAccessPtr == IntPtr.Zero) { if (_frameLog <= 5) ResoniteModLoader.ResoniteMod.Msg($"[WgcCapture] QI for DxgiAccess failed hr=0x{qiHr:X8}"); return; }

        // Call GetInterface to get ID3D11Texture2D
        var texGuid = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
        IntPtr srcTexture;
        unsafe
        {
            var vtable = *(IntPtr**)dxgiAccessPtr;
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)vtable[3];
            Guid localTexGuid = texGuid;
            IntPtr tex;
            int getHr = fn(dxgiAccessPtr, &localTexGuid, &tex);
            srcTexture = tex;
            if (getHr < 0) { Marshal.Release(dxgiAccessPtr); if (_frameLog <= 5) ResoniteModLoader.ResoniteMod.Msg($"[WgcCapture] GetInterface failed hr=0x{getHr:X8}"); return; }
        }
        Marshal.Release(dxgiAccessPtr);
        if (_frameLog <= 5) ResoniteModLoader.ResoniteMod.Msg($"[WgcCapture] srcTexture={srcTexture}");

        try
        {
            // Copy to persistent encode texture (for NVENC + keepalive re-encoding)
            Interlocked.Exchange(ref _lastFrameTicks, DateTime.UtcNow.Ticks);
            EnsureEncodeTexture(w, h);
            ContextCopyResource(_d3dContext, _encodeTexture, srcTexture);

            // GPU encode callback — NVENC encodes from the persistent copy
            try { OnGpuFrame?.Invoke(_d3dDevice, _encodeTexture, w, h); }
            catch (Exception gpuEx) { ResoniteModLoader.ResoniteMod.Msg($"[WgcCapture] OnGpuFrame error: {gpuEx}"); }

            // Copy to staging for CPU readback (local preview)
            EnsureStagingTexture(w, h);
            if (_frameLog <= 5) ResoniteModLoader.ResoniteMod.Msg($"[WgcCapture] CopyResource staging={_stagingTexture}");
            ContextCopyResource(_d3dContext, _stagingTexture, srcTexture);

            var mapped = new D3D11_MAPPED_SUBRESOURCE();
            int hr = ContextMap(_d3dContext, _stagingTexture, 0, 1, 0, ref mapped);
            if (_frameLog <= 5) ResoniteModLoader.ResoniteMod.Msg($"[WgcCapture] Map hr=0x{hr:X8} pData={mapped.pData} pitch={mapped.RowPitch}");
            if (hr < 0) return;

            try
            {
                int bufSize = w * h * 4;
                EnsureBuffer(bufSize);
                int srcPitch = (int)mapped.RowPitch;
                int dstStride = w * 4;
                unsafe
                {
                    byte* src = (byte*)mapped.pData;
                    fixed (byte* dst = _buffer)
                    {
                        // Y-flip + BGRA→RGBA in one pass
                        for (int y = 0; y < h; y++)
                        {
                            uint* srcRow = (uint*)(src + y * srcPitch);
                            uint* dstRow = (uint*)(dst + (h - 1 - y) * dstStride);
                            for (int x = 0; x < w; x++)
                            {
                                uint px = srcRow[x];
                                uint b = px & 0xFFu;
                                uint g = (px >> 8) & 0xFFu;
                                uint r = (px >> 16) & 0xFFu;
                                dstRow[x] = r | (g << 8) | (b << 16) | 0xFF000000u;
                            }
                        }
                    }
                }
                lock (_frameLock)
                {
                    _lastWidth = w; _lastHeight = h;
                    _frameReady = true; _framesCaptured++;
                }
                if (_frameLog <= 5) ResoniteModLoader.ResoniteMod.Msg($"[WgcCapture] Frame #{_framesCaptured} ready, {w}x{h}, bufSize={bufSize}");
            }
            finally { ContextUnmap(_d3dContext, _stagingTexture, 0); }
        }
        catch (Exception ex)
        {
            if (_frameLog <= 5) ResoniteModLoader.ResoniteMod.Msg($"[WgcCapture] OnFrameArrived exception: {ex.Message}");
        }
        finally { Marshal.Release(srcTexture); }
        }
        catch (Exception ex)
        {
            if (_frameLog <= 10) ResoniteModLoader.ResoniteMod.Msg($"[WgcCapture] OnFrameArrived OUTER exception: {ex.Message}\n{ex.StackTrace}");
        }
    }

    /// <summary>
    /// Take the latest captured frame. Returns BGRA pixel data or null if no new frame.
    /// Buffer is valid until next call.
    /// </summary>
    public byte[] TakeFrame(out int width, out int height)
    {
        if (!_frameReady)
        {
            width = 0;
            height = 0;
            return null;
        }

        lock (_frameLock)
        {
            _frameReady = false;
            width = _lastWidth;
            height = _lastHeight;
            return _buffer;
        }
    }

    private void EnsureEncodeTexture(int w, int h)
    {
        if (_encodeTexture != IntPtr.Zero && w == _encodeTexW && h == _encodeTexH) return;
        if (_encodeTexture != IntPtr.Zero) { Marshal.Release(_encodeTexture); _encodeTexture = IntPtr.Zero; }

        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = (uint)w, Height = (uint)h,
            MipLevels = 1, ArraySize = 1,
            Format = DXGI_FORMAT_B8G8R8A8_UNORM,
            SampleCount = 1, SampleQuality = 0,
            Usage = 0, // D3D11_USAGE_DEFAULT — GPU read/write
            BindFlags = 0,
            CPUAccessFlags = 0,
            MiscFlags = 0
        };
        DeviceCreateTexture2D(_d3dDevice, ref desc, IntPtr.Zero, out _encodeTexture);
        _encodeTexW = w; _encodeTexH = h;

        // Start keepalive thread if not already running
        if (_keepaliveThread == null)
        {
            _keepaliveThread = new Thread(() =>
            {
                while (!_disposed)
                {
                    Thread.Sleep(100); // 10fps keepalive
                    if (_disposed || OnGpuFrame == null || _encodeTexture == IntPtr.Zero) continue;
                    // Only send keepalive if WGC hasn't fired recently
                    long msSinceFrame = (DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastFrameTicks)) / TimeSpan.TicksPerMillisecond;
                    if (msSinceFrame > 150) // 150ms idle = send keepalive
                    {
                        try { OnGpuFrame.Invoke(_d3dDevice, _encodeTexture, _encodeTexW, _encodeTexH); }
                        catch (Exception kaEx) { ResoniteModLoader.ResoniteMod.Msg($"[WgcCapture] Keepalive error: {kaEx.Message}"); }
                    }
                }
            }) { IsBackground = true, Name = "WGC_Keepalive" };
            _keepaliveThread.Start();
        }
    }

    private long _lastFrameTicks;

    private void EnsureStagingTexture(int w, int h)
    {
        if (_stagingTexture != IntPtr.Zero)
        {
            // Check if size matches - if not, release and recreate
            // For simplicity, track size
            if (w == _lastWidth && h == _lastHeight) return;
            Marshal.Release(_stagingTexture);
            _stagingTexture = IntPtr.Zero;
        }

        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = (uint)w,
            Height = (uint)h,
            MipLevels = 1,
            ArraySize = 1,
            Format = DXGI_FORMAT_B8G8R8A8_UNORM,
            SampleCount = 1,
            SampleQuality = 0,
            Usage = D3D11_USAGE_STAGING,
            BindFlags = 0,
            CPUAccessFlags = D3D11_CPU_ACCESS_READ,
            MiscFlags = 0
        };

        DeviceCreateTexture2D(_d3dDevice, ref desc, IntPtr.Zero, out _stagingTexture);
    }

    private void EnsureBuffer(int size)
    {
        if (_buffer != null && _buffer.Length == size) return;
        if (_pinnedBuffer.IsAllocated) _pinnedBuffer.Free();
        _buffer = new byte[size];
        _pinnedBuffer = GCHandle.Alloc(_buffer, GCHandleType.Pinned);
    }

    // D3D11 vtable calls via raw COM
    private static unsafe void ContextCopyResource(IntPtr context, IntPtr dst, IntPtr src)
    {
        var vtable = *(IntPtr**)context;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, void>)vtable[ID3D11DeviceContext_CopyResource];
        fn(context, dst, src);
    }

    private static unsafe int ContextMap(IntPtr context, IntPtr resource, uint subresource, int mapType, uint mapFlags, ref D3D11_MAPPED_SUBRESOURCE mapped)
    {
        var vtable = *(IntPtr**)context;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int, uint, ref D3D11_MAPPED_SUBRESOURCE, int>)vtable[ID3D11DeviceContext_Map];
        return fn(context, resource, subresource, mapType, mapFlags, ref mapped);
    }

    private static unsafe void ContextUnmap(IntPtr context, IntPtr resource, uint subresource)
    {
        var vtable = *(IntPtr**)context;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)vtable[ID3D11DeviceContext_Unmap];
        fn(context, resource, subresource);
    }

    private static unsafe void DeviceCreateTexture2D(IntPtr device, ref D3D11_TEXTURE2D_DESC desc, IntPtr initialData, out IntPtr texture)
    {
        var vtable = *(IntPtr**)device;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, ref D3D11_TEXTURE2D_DESC, IntPtr, out IntPtr, int>)vtable[ID3D11Device_CreateTexture2D];
        int hr = fn(device, ref desc, initialData, out texture);
        if (hr < 0) throw new COMException("CreateTexture2D failed", hr);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _session?.Dispose(); } catch { }
        try { _framePool?.Dispose(); } catch { }
        _item = null;

        if (_encodeTexture != IntPtr.Zero) { Marshal.Release(_encodeTexture); _encodeTexture = IntPtr.Zero; }
        if (_stagingTexture != IntPtr.Zero) { Marshal.Release(_stagingTexture); _stagingTexture = IntPtr.Zero; }
        if (_d3dContext != IntPtr.Zero) { Marshal.Release(_d3dContext); _d3dContext = IntPtr.Zero; }
        if (_d3dDevice != IntPtr.Zero) { Marshal.Release(_d3dDevice); _d3dDevice = IntPtr.Zero; }
        try { _winrtDevice?.Dispose(); } catch { }

        if (_pinnedBuffer.IsAllocated) _pinnedBuffer.Free();
        _buffer = null;
    }
}
