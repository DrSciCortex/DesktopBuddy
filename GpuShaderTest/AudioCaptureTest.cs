using System;
using System.Runtime.InteropServices;
using System.Threading;
using DesktopBuddy;

/// <summary>
/// Standalone test for WASAPI process loopback audio capture.
/// Run: dotnet run --project GpuShaderTest -- audio
/// Plays system audio for 5 seconds and reports captured sample stats.
/// </summary>
public static class AudioCaptureTest
{
    public static void Run()
    {
        Console.WriteLine("=== Audio Capture Test ===");
        Console.WriteLine("This test captures all system audio except this process for 5 seconds.");
        Console.WriteLine("Play some audio on your system to see it captured.\n");

        var capture = new AudioCapture();

        // Capture all audio except this process (simulates desktop mode excluding Resonite)
        bool started = capture.Start(IntPtr.Zero, AudioCaptureMode.ExcludeProcess);
        if (!started)
        {
            Console.WriteLine("FAILED to start audio capture.");
            Console.WriteLine("This requires Windows 10 Build 20348+ (May 2021 update) or Windows 11.");
            return;
        }

        Console.WriteLine($"Capture started: {capture.SampleRate}Hz, {capture.Channels}ch");
        Console.WriteLine("Capturing for 5 seconds...\n");

        var buffer = new float[48000 * 2]; // 1 second buffer
        long readPos = 0;
        int totalSamples = 0;
        float peakLevel = 0;

        for (int i = 0; i < 50; i++) // 50 * 100ms = 5 seconds
        {
            Thread.Sleep(100);
            int read = capture.ReadSamples(buffer, buffer.Length, ref readPos);
            totalSamples += read;

            // Find peak level
            for (int j = 0; j < read; j++)
            {
                float abs = Math.Abs(buffer[j]);
                if (abs > peakLevel) peakLevel = abs;
            }

            if (i % 10 == 9)
                Console.WriteLine($"  {(i + 1) / 10}s: {totalSamples} samples captured, peak={peakLevel:F4}");
        }

        capture.Dispose();

        Console.WriteLine($"\nTotal: {totalSamples} samples ({totalSamples / capture.Channels / (float)capture.SampleRate:F2}s of audio)");
        Console.WriteLine($"Peak level: {peakLevel:F4} ({(peakLevel > 0.001f ? "audio detected" : "silence/no audio")})");

        if (totalSamples > 0 && peakLevel > 0.001f)
            Console.WriteLine("\n*** PASS — Audio capture working ***");
        else if (totalSamples > 0)
            Console.WriteLine("\n*** PARTIAL — Capture works but no audio detected (was something playing?) ***");
        else
            Console.WriteLine("\n*** FAIL — No samples captured ***");
    }
}
