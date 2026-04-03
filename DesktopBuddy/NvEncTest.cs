using System;
using System.Runtime.InteropServices;
using Lennox.NvEncSharp;
using static Lennox.NvEncSharp.LibNvEnc;
using ResoniteModLoader;

namespace DesktopBuddy;

/// <summary>
/// Test NVENC encoder initialization with the updated NvEncSharp library.
/// Call Run() to verify NVENC works on this system.
/// </summary>
public static class NvEncTest
{
    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(
        IntPtr pAdapter, int DriverType, IntPtr Software, uint Flags,
        IntPtr pFeatureLevels, uint FeatureLevels, uint SDKVersion,
        out IntPtr ppDevice, out int pFeatureLevel, out IntPtr ppImmediateContext);

    private const int D3D_DRIVER_TYPE_HARDWARE = 1;
    private const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;

    public static bool Run()
    {
        try
        {
            ResoniteMod.Msg("[NvEncTest] Creating D3D11 device...");
            int hr = D3D11CreateDevice(IntPtr.Zero, D3D_DRIVER_TYPE_HARDWARE, IntPtr.Zero,
                D3D11_CREATE_DEVICE_BGRA_SUPPORT, IntPtr.Zero, 0, 7,
                out IntPtr device, out _, out IntPtr context);
            if (hr < 0)
            {
                ResoniteMod.Msg($"[NvEncTest] D3D11CreateDevice failed hr=0x{hr:X8}");
                return false;
            }
            ResoniteMod.Msg($"[NvEncTest] D3D11 device: {device}");

            ResoniteMod.Msg("[NvEncTest] Opening NVENC encoder...");
            var encoder = OpenEncoderForDirectX(device);
            ResoniteMod.Msg("[NvEncTest] Encoder opened successfully");

            ResoniteMod.Msg("[NvEncTest] Getting H.264 preset config...");
            var presetConfig = encoder.GetEncodePresetConfig(
                NvEncCodecGuids.H264,
                NvEncPresetGuids.LowLatencyDefault).PresetCfg;
            ResoniteMod.Msg("[NvEncTest] Preset config retrieved");

            presetConfig.RcParams.AverageBitRate = 4000000;
            presetConfig.RcParams.MaxBitRate = 8000000;
            presetConfig.RcParams.RateControlMode = NvEncParamsRcMode.Vbr;

            uint width = 1920;
            uint height = 1080;

            unsafe
            {
                NvEncConfig* configPtr = &presetConfig;
                var initParams = new NvEncInitializeParams
                {
                    Version = NV_ENC_INITIALIZE_PARAMS_VER,
                    EncodeGuid = NvEncCodecGuids.H264,
                    EncodeWidth = width,
                    EncodeHeight = height,
                    MaxEncodeWidth = width,
                    MaxEncodeHeight = height,
                    DarWidth = width,
                    DarHeight = height,
                    FrameRateNum = 30,
                    FrameRateDen = 1,
                    EnablePTD = 1,
                    PresetGuid = NvEncPresetGuids.LowLatencyDefault,
                    EncodeConfig = configPtr,
                };

                ResoniteMod.Msg("[NvEncTest] Initializing encoder 1920x1080 H.264...");
                encoder.InitializeEncoder(ref initParams);
            }

            ResoniteMod.Msg("[NvEncTest] Creating bitstream buffer...");
            var bitstreamBuffer = encoder.CreateBitstreamBuffer();

            ResoniteMod.Msg("[NvEncTest] SUCCESS — NVENC encoder initialized at 1920x1080 H.264");

            // Cleanup
            encoder.DestroyBitstreamBuffer(bitstreamBuffer.BitstreamBuffer);
            encoder.DestroyEncoder();
            Marshal.Release(context);
            Marshal.Release(device);

            ResoniteMod.Msg("[NvEncTest] Cleanup complete, test PASSED");
            return true;
        }
        catch (Exception ex)
        {
            ResoniteMod.Msg($"[NvEncTest] FAILED: {ex}");
            return false;
        }
    }
}
