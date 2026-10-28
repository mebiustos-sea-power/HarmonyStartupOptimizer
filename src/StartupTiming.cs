using System;
using System.Diagnostics;

namespace HarmonyStartupOptimizer;

internal sealed class StartupTiming
{
    private readonly Func<double> elapsed;
    internal double? Seconds { get; private set; }

    internal StartupTiming(Func<double> elapsed) { this.elapsed = elapsed; }

    internal static StartupTiming ForCurrentProcess()
    {
        using var process = Process.GetCurrentProcess();
        DateTime started = process.StartTime.ToUniversalTime();
        double precedingSeconds = (DateTime.UtcNow - started).TotalSeconds;
        var clock = Stopwatch.StartNew();
        return new StartupTiming(() => precedingSeconds + clock.Elapsed.TotalSeconds);
    }

    internal bool Complete()
    {
        if (Seconds.HasValue) return false;
        Seconds = elapsed();
        return true;
    }
}
