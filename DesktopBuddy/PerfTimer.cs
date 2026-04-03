using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using ResoniteModLoader;

namespace DesktopBuddy;

/// <summary>
/// Performance timing system. Tracks every step of the capture pipeline
/// and outputs a summary report every 30 seconds.
/// Thread-safe — capture thread and engine thread can both record timings.
/// </summary>
public sealed class PerfTimer : IDisposable
{
    private readonly Stopwatch _reportSw = Stopwatch.StartNew();
    private const int REPORT_INTERVAL_MS = 30_000;

    // Per-step accumulators (lock-free via Interlocked)
    private readonly Dictionary<string, StepStats> _steps = new();
    private readonly object _lock = new();
    private long _totalFrames;
    private long _droppedFrames;

    private class StepStats
    {
        public long TotalTicks;  // Sum of all durations
        public long MaxTicks;    // Worst case
        public long Count;       // Number of samples
    }

    /// <summary>Record a timed step. Call with a Stopwatch that was started before the step.</summary>
    public void Record(string step, long elapsedTicks)
    {
        lock (_lock)
        {
            if (!_steps.TryGetValue(step, out var stats))
            {
                stats = new StepStats();
                _steps[step] = stats;
            }
            stats.TotalTicks += elapsedTicks;
            if (elapsedTicks > stats.MaxTicks) stats.MaxTicks = elapsedTicks;
            stats.Count++;
        }

        // Check if it's time for a report
        if (_reportSw.ElapsedMilliseconds >= REPORT_INTERVAL_MS)
        {
            PrintReport();
            _reportSw.Restart();
        }
    }

    /// <summary>Record a timed step in milliseconds.</summary>
    public void RecordMs(string step, double ms)
    {
        Record(step, (long)(ms * Stopwatch.Frequency / 1000.0));
    }

    public void IncrementFrames() => Interlocked.Increment(ref _totalFrames);
    public void IncrementDropped() => Interlocked.Increment(ref _droppedFrames);

    /// <summary>Convenience: time a step using a disposable scope.</summary>
    public TimedScope Time(string step) => new TimedScope(this, step);

    public readonly struct TimedScope : IDisposable
    {
        private readonly PerfTimer _timer;
        private readonly string _step;
        private readonly long _startTicks;

        public TimedScope(PerfTimer timer, string step)
        {
            _timer = timer;
            _step = step;
            _startTicks = Stopwatch.GetTimestamp();
        }

        public void Dispose()
        {
            long elapsed = Stopwatch.GetTimestamp() - _startTicks;
            _timer.Record(_step, elapsed);
        }
    }

    private void PrintReport()
    {
        Dictionary<string, StepStats> snapshot;
        long frames, dropped;

        lock (_lock)
        {
            snapshot = new Dictionary<string, StepStats>();
            foreach (var kvp in _steps)
            {
                snapshot[kvp.Key] = new StepStats
                {
                    TotalTicks = kvp.Value.TotalTicks,
                    MaxTicks = kvp.Value.MaxTicks,
                    Count = kvp.Value.Count
                };
                // Reset for next period
                kvp.Value.TotalTicks = 0;
                kvp.Value.MaxTicks = 0;
                kvp.Value.Count = 0;
            }
            frames = Interlocked.Exchange(ref _totalFrames, 0);
            dropped = Interlocked.Exchange(ref _droppedFrames, 0);
        }

        if (snapshot.Count == 0 && frames == 0) return;

        double freqMs = Stopwatch.Frequency / 1000.0;
        var lines = new List<string>();
        lines.Add($"[Perf] === 30s Report: {frames} frames, {dropped} dropped ===");

        foreach (var kvp in snapshot)
        {
            if (kvp.Value.Count == 0) continue;
            double avgMs = (kvp.Value.TotalTicks / (double)kvp.Value.Count) / freqMs;
            double maxMs = kvp.Value.MaxTicks / freqMs;
            double totalMs = kvp.Value.TotalTicks / freqMs;
            lines.Add($"[Perf]   {kvp.Key}: avg={avgMs:F2}ms max={maxMs:F2}ms total={totalMs:F0}ms n={kvp.Value.Count}");
        }

        ResoniteMod.Msg(string.Join("\n", lines));
    }

    public void Dispose() { }
}
