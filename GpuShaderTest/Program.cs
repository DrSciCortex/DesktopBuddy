using System;
using System.IO;
using System.Runtime.InteropServices;

unsafe class Program
{
    [DllImport("d3d11.dll")]
    static extern int D3D11CreateDevice(
        IntPtr pAdapter, int DriverType, IntPtr Software, uint Flags,
        IntPtr pFeatureLevels, uint FeatureLevels, uint SDKVersion,
        out IntPtr ppDevice, out int pFeatureLevel, out IntPtr ppImmediateContext);

    const int D3D_DRIVER_TYPE_HARDWARE = 1;
    const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;
    const uint D3D11_CREATE_DEVICE_DEBUG = 0x2;

    // DXGI formats
    const int DXGI_FORMAT_B8G8R8A8_UNORM = 87;
    const int DXGI_FORMAT_R32_UINT = 42;

    // Usage/bind/access
    const int D3D11_USAGE_DEFAULT = 0;
    const int D3D11_USAGE_STAGING = 3;
    const int D3D11_USAGE_DYNAMIC = 2;
    const uint D3D11_CPU_ACCESS_READ = 0x20000;
    const uint D3D11_CPU_ACCESS_WRITE = 0x10000;
    const uint D3D11_BIND_SHADER_RESOURCE = 0x8;
    const uint D3D11_BIND_UNORDERED_ACCESS = 0x80;
    const uint D3D11_BIND_CONSTANT_BUFFER = 0x4;

    // Vtable indices (verified against d3d11.h)
    const int Dev_CreateBuffer = 3;
    const int Dev_CreateTexture2D = 5;
    const int Dev_CreateSRV = 7;
    const int Dev_CreateUAV = 8;
    const int Dev_CreateComputeShader = 18;
    const int Dev_GetDeviceRemovedReason = 38;

    const int Ctx_Map = 14;
    const int Ctx_Unmap = 15;
    const int Ctx_Dispatch = 41;
    const int Ctx_CopyResource = 47;
    const int Ctx_CSSetShaderResources = 67;
    const int Ctx_CSSetUnorderedAccessViews = 68;
    const int Ctx_CSSetShader = 69;
    const int Ctx_CSSetConstantBuffers = 71;

    [StructLayout(LayoutKind.Sequential)]
    struct TEX_DESC { public uint W, H, Mip, Arr; public int Fmt; public uint SC, SQ; public int Usage; public uint Bind, CPU, Misc; }

    [StructLayout(LayoutKind.Sequential)]
    struct MAPPED { public IntPtr pData; public uint RowPitch, DepthPitch; }

    [StructLayout(LayoutKind.Sequential)]
    struct SRV_DESC { public int Fmt, Dim; public uint Mip, MipLevels, P0, P1; }

    [StructLayout(LayoutKind.Sequential)]
    struct UAV_DESC { public int Fmt, Dim; public uint MipSlice, P0, P1; }

    [StructLayout(LayoutKind.Sequential)]
    struct BUF_DESC { public uint Size; public int Usage; public uint Bind, CPU, Misc, Stride; }

    [StructLayout(LayoutKind.Sequential)]
    struct SUBDATA { public IntPtr pSysMem; public uint Pitch, SlicePitch; }

    [StructLayout(LayoutKind.Sequential)]
    struct Constants { public uint Width, Height, Pad0, Pad1; }

    static IntPtr device, context;

    static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "ffmpeg")
        {
            FfmpegEncoderTest.Run();
            return;
        }
        if (args.Length > 0 && args[0] == "audio")
        {
            AudioCaptureTest.Run();
            return;
        }

        Console.WriteLine("=== GPU Compute Shader Test ===");

        // Create device with debug layer
        uint flags = D3D11_CREATE_DEVICE_BGRA_SUPPORT | D3D11_CREATE_DEVICE_DEBUG;
        int hr = D3D11CreateDevice(IntPtr.Zero, D3D_DRIVER_TYPE_HARDWARE, IntPtr.Zero,
            flags, IntPtr.Zero, 0, 7, out device, out _, out context);
        if (hr < 0)
        {
            Console.WriteLine($"Debug device failed (0x{hr:X8}), trying without debug");
            flags = D3D11_CREATE_DEVICE_BGRA_SUPPORT;
            hr = D3D11CreateDevice(IntPtr.Zero, D3D_DRIVER_TYPE_HARDWARE, IntPtr.Zero,
                flags, IntPtr.Zero, 0, 7, out device, out _, out context);
        }
        if (hr < 0) { Console.WriteLine($"FATAL: D3D11CreateDevice failed 0x{hr:X8}"); return; }
        Console.WriteLine($"Device created (debug={(flags & D3D11_CREATE_DEVICE_DEBUG) != 0})");

        int testW = 64, testH = 64;

        try
        {
            // 1. Load compiled shader
            string shaderPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "DesktopBuddy", "obj", "Debug", "net10.0-windows10.0.22621.0", "BgraToRgba.cso");
            if (!File.Exists(shaderPath))
            {
                // Try alternate path
                shaderPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "DesktopBuddy", "obj", "BgraToRgba.cso");
            }
            if (!File.Exists(shaderPath)) { Console.WriteLine($"Shader not found at: {shaderPath}"); return; }
            var bytecode = File.ReadAllBytes(shaderPath);
            Console.WriteLine($"Shader loaded: {bytecode.Length} bytes from {shaderPath}");

            var computeShader = CreateComputeShader(bytecode);
            Console.WriteLine($"CreateComputeShader OK: 0x{computeShader:X}");
            CheckDeviceRemoved("CreateComputeShader");

            // 2. Create source BGRA texture with test data
            var srcDesc = new TEX_DESC { W = (uint)testW, H = (uint)testH, Mip = 1, Arr = 1, Fmt = DXGI_FORMAT_B8G8R8A8_UNORM, SC = 1, SQ = 0, Usage = D3D11_USAGE_DEFAULT, Bind = 0, CPU = 0, Misc = 0 };
            var srcTex = CreateTexture2D(ref srcDesc, IntPtr.Zero);
            Console.WriteLine($"Source BGRA texture: 0x{srcTex:X}");

            // Fill with known data via staging
            var stagingSrcDesc = new TEX_DESC { W = (uint)testW, H = (uint)testH, Mip = 1, Arr = 1, Fmt = DXGI_FORMAT_B8G8R8A8_UNORM, SC = 1, SQ = 0, Usage = D3D11_USAGE_STAGING, Bind = 0, CPU = D3D11_CPU_ACCESS_WRITE | D3D11_CPU_ACCESS_READ, Misc = 0 };
            var stagingSrc = CreateTexture2D(ref stagingSrcDesc, IntPtr.Zero);
            var mapped = new MAPPED();
            hr = Map(stagingSrc, 0, 4, 0, ref mapped); // WRITE_DISCARD=4... wait, staging uses WRITE=2 not WRITE_DISCARD
            if (hr < 0)
            {
                // Try MAP_WRITE (2) for staging
                hr = Map(stagingSrc, 0, 2, 0, ref mapped);
            }
            if (hr < 0) { Console.WriteLine($"Map staging src failed 0x{hr:X8}"); return; }

            // Fill: BGRA pixel = B=0xAA, G=0xBB, R=0xCC, A=0xDD
            for (int y = 0; y < testH; y++)
            {
                uint* row = (uint*)((byte*)mapped.pData + y * mapped.RowPitch);
                for (int x = 0; x < testW; x++)
                    row[x] = 0xDDCCBBAA; // BGRA: B=AA, G=BB, R=CC, A=DD
            }
            Unmap(stagingSrc, 0);
            CopyResource(srcTex, stagingSrc);
            Console.WriteLine("Source filled with BGRA test pattern (B=AA G=BB R=CC A=DD)");

            // 3. Create SRV-bindable copy texture — same BGRA format as source so CopyResource works
            var srvTexDesc = new TEX_DESC { W = (uint)testW, H = (uint)testH, Mip = 1, Arr = 1, Fmt = DXGI_FORMAT_B8G8R8A8_UNORM, SC = 1, SQ = 0, Usage = D3D11_USAGE_DEFAULT, Bind = D3D11_BIND_SHADER_RESOURCE, CPU = 0, Misc = 0 };
            var srvTex = CreateTexture2D(ref srvTexDesc, IntPtr.Zero);
            Console.WriteLine($"SRV texture (BGRA): 0x{srvTex:X}");

            var srvDesc = new SRV_DESC { Fmt = DXGI_FORMAT_B8G8R8A8_UNORM, Dim = 4, Mip = 0, MipLevels = 1 };
            var srv = CreateSRV(srvTex, ref srvDesc);
            Console.WriteLine($"SRV: 0x{srv:X}");

            // 4. Create UAV output texture (R32_UINT)
            var uavTexDesc = new TEX_DESC { W = (uint)testW, H = (uint)testH, Mip = 1, Arr = 1, Fmt = DXGI_FORMAT_R32_UINT, SC = 1, SQ = 0, Usage = D3D11_USAGE_DEFAULT, Bind = D3D11_BIND_UNORDERED_ACCESS, CPU = 0, Misc = 0 };
            var uavTex = CreateTexture2D(ref uavTexDesc, IntPtr.Zero);
            Console.WriteLine($"UAV texture: 0x{uavTex:X}");

            var uavDesc = new UAV_DESC { Fmt = DXGI_FORMAT_R32_UINT, Dim = 4, MipSlice = 0 }; // D3D11_UAV_DIMENSION_TEXTURE2D = 4
            var uav = CreateUAV(uavTex, ref uavDesc);
            Console.WriteLine($"UAV: 0x{uav:X}");

            // 5. Create constant buffer
            var cbDesc = new BUF_DESC { Size = 16, Usage = D3D11_USAGE_DYNAMIC, Bind = D3D11_BIND_CONSTANT_BUFFER, CPU = D3D11_CPU_ACCESS_WRITE };
            var cb = CreateBuffer(ref cbDesc);
            Console.WriteLine($"Constant buffer: 0x{cb:X}");

            CheckDeviceRemoved("After resource creation");

            // 6. Copy source BGRA to SRV texture (reinterpret as R32_UINT)
            CopyResource(srvTex, srcTex);
            Console.WriteLine("CopyResource src->srvTex OK");

            // 7. Update constants
            var cbMapped = new MAPPED();
            hr = Map(cb, 0, 4, 0, ref cbMapped); // D3D11_MAP_WRITE_DISCARD = 4
            if (hr < 0) { Console.WriteLine($"Map CB failed 0x{hr:X8}"); return; }
            var constants = (Constants*)cbMapped.pData;
            constants->Width = (uint)testW;
            constants->Height = (uint)testH;
            Unmap(cb, 0);
            Console.WriteLine("Constants updated");

            // 8. Dispatch!
            var vtable = *(IntPtr**)context;

            Console.Write("CSSetShader... ");
            var csSetShader = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, uint, void>)vtable[Ctx_CSSetShader];
            csSetShader(context, computeShader, IntPtr.Zero, 0);
            Console.WriteLine("OK");
            CheckDeviceRemoved("CSSetShader");

            Console.Write("CSSetShaderResources... ");
            IntPtr srvPtr = srv;
            var csSetSRV = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, void>)vtable[Ctx_CSSetShaderResources];
            csSetSRV(context, 0, 1, &srvPtr);
            Console.WriteLine("OK");

            Console.Write("CSSetUnorderedAccessViews... ");
            IntPtr uavPtr = uav;
            var csSetUAV = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, IntPtr, void>)vtable[Ctx_CSSetUnorderedAccessViews];
            csSetUAV(context, 0, 1, &uavPtr, IntPtr.Zero);
            Console.WriteLine("OK");

            Console.Write("CSSetConstantBuffers... ");
            IntPtr cbPtr = cb;
            var csSetCB = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, void>)vtable[Ctx_CSSetConstantBuffers];
            csSetCB(context, 0, 1, &cbPtr);
            Console.WriteLine("OK");

            uint gx = ((uint)testW + 15) / 16, gy = ((uint)testH + 15) / 16;
            Console.Write($"Dispatch({gx}, {gy}, 1)... ");
            var dispatch = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint, void>)vtable[Ctx_Dispatch];
            dispatch(context, gx, gy, 1);
            Console.WriteLine("OK");
            CheckDeviceRemoved("Dispatch");

            // Unbind
            IntPtr nullPtr = IntPtr.Zero;
            csSetSRV(context, 0, 1, &nullPtr);
            csSetUAV(context, 0, 1, &nullPtr, IntPtr.Zero);

            // 9. Read back result
            var stagingOutDesc = new TEX_DESC { W = (uint)testW, H = (uint)testH, Mip = 1, Arr = 1, Fmt = DXGI_FORMAT_R32_UINT, SC = 1, SQ = 0, Usage = D3D11_USAGE_STAGING, Bind = 0, CPU = D3D11_CPU_ACCESS_READ, Misc = 0 };
            var stagingOut = CreateTexture2D(ref stagingOutDesc, IntPtr.Zero);
            CopyResource(stagingOut, uavTex);

            var outMapped = new MAPPED();
            hr = Map(stagingOut, 0, 1, 0, ref outMapped); // D3D11_MAP_READ = 1
            if (hr < 0) { Console.WriteLine($"Map output failed 0x{hr:X8}"); return; }

            // Check pixel at (0, height-1) which should be the Y-flipped version of (0, 0)
            // Input BGRA: 0xDDCCBBAA → B=AA, G=BB, R=CC, A=DD
            // Expected RGBA output: R=CC, G=BB, B=AA, A=FF → as uint LE: 0xFF_AA_BB_CC
            uint* outRow0 = (uint*)((byte*)outMapped.pData + (testH - 1) * outMapped.RowPitch); // Y-flipped: row 0 input → row H-1 output
            uint pixel = outRow0[0];
            Unmap(stagingOut, 0);

            uint expected = 0xFFAABBCC; // RGBA: R=CC, G=BB, B=AA, A=FF
            Console.WriteLine($"\nResult pixel[0,H-1]: 0x{pixel:X8}");
            Console.WriteLine($"Expected:            0x{expected:X8}");

            if (pixel == expected)
                Console.WriteLine("\n*** PASS — BGRA→RGBA + Y-flip working correctly! ***");
            else
                Console.WriteLine($"\n*** FAIL — pixel mismatch! Got 0x{pixel:X8}, expected 0x{expected:X8} ***");

            // Cleanup
            Marshal.Release(stagingOut);
            Marshal.Release(stagingSrc);
            Marshal.Release(srcTex);
            Marshal.Release(srvTex);
            Marshal.Release(srv);
            Marshal.Release(uavTex);
            Marshal.Release(uav);
            Marshal.Release(cb);
            Marshal.Release(computeShader);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\nEXCEPTION: {ex}");
            CheckDeviceRemoved("after exception");
        }

        Marshal.Release(context);
        Marshal.Release(device);
        Console.WriteLine("\nDone.");
    }

    static void CheckDeviceRemoved(string ctx)
    {
        var vtable = *(IntPtr**)device;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, int>)vtable[Dev_GetDeviceRemovedReason];
        int hr = fn(device);
        if (hr < 0) Console.WriteLine($"!!! DEVICE REMOVED after {ctx}: 0x{hr:X8}");
    }

    static IntPtr CreateTexture2D(ref TEX_DESC desc, IntPtr init)
    {
        var vtable = *(IntPtr**)device;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, ref TEX_DESC, IntPtr, out IntPtr, int>)vtable[Dev_CreateTexture2D];
        int hr = fn(device, ref desc, init, out IntPtr tex);
        if (hr < 0) throw new COMException($"CreateTexture2D failed", hr);
        return tex;
    }

    static IntPtr CreateComputeShader(byte[] bytecode)
    {
        var vtable = *(IntPtr**)device;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, nuint, IntPtr, out IntPtr, int>)vtable[Dev_CreateComputeShader];
        fixed (byte* p = bytecode)
        {
            int hr = fn(device, (IntPtr)p, (nuint)bytecode.Length, IntPtr.Zero, out IntPtr shader);
            if (hr < 0) throw new COMException($"CreateComputeShader failed 0x{hr:X8}", hr);
            return shader;
        }
    }

    static IntPtr CreateSRV(IntPtr resource, ref SRV_DESC desc)
    {
        var vtable = *(IntPtr**)device;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, ref SRV_DESC, out IntPtr, int>)vtable[Dev_CreateSRV];
        int hr = fn(device, resource, ref desc, out IntPtr srv);
        if (hr < 0) throw new COMException($"CreateSRV failed 0x{hr:X8}", hr);
        return srv;
    }

    static IntPtr CreateUAV(IntPtr resource, ref UAV_DESC desc)
    {
        var vtable = *(IntPtr**)device;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, ref UAV_DESC, out IntPtr, int>)vtable[Dev_CreateUAV];
        int hr = fn(device, resource, ref desc, out IntPtr uav);
        if (hr < 0) throw new COMException($"CreateUAV failed 0x{hr:X8}", hr);
        return uav;
    }

    static IntPtr CreateBuffer(ref BUF_DESC desc)
    {
        var vtable = *(IntPtr**)device;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, ref BUF_DESC, IntPtr, out IntPtr, int>)vtable[Dev_CreateBuffer];
        int hr = fn(device, ref desc, IntPtr.Zero, out IntPtr buf);
        if (hr < 0) throw new COMException($"CreateBuffer failed 0x{hr:X8}", hr);
        return buf;
    }

    static int Map(IntPtr resource, uint sub, int mapType, uint flags, ref MAPPED mapped)
    {
        var vtable = *(IntPtr**)context;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int, uint, ref MAPPED, int>)vtable[Ctx_Map];
        return fn(context, resource, sub, mapType, flags, ref mapped);
    }

    static void Unmap(IntPtr resource, uint sub)
    {
        var vtable = *(IntPtr**)context;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)vtable[Ctx_Unmap];
        fn(context, resource, sub);
    }

    static void CopyResource(IntPtr dst, IntPtr src)
    {
        var vtable = *(IntPtr**)context;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, void>)vtable[Ctx_CopyResource];
        fn(context, dst, src);
    }
}
