using System.Diagnostics;

namespace PgProj.Semantics.Tests;

/// <summary>
/// Timing for the timed tests <c>copilot-tests.md</c> §3 allows (G-321): a directional history of performance, and a comparison before and after a change.
/// A test compares measurements taken in the same run, with a generous margin, never an absolute time, so a slow or busy machine does not fail it.
/// </summary>
internal static class TimedRuns
{
    /// <summary>The median time of <paramref name="runs"/> runs of <paramref name="action"/>, after one run to warm up; at least a millisecond, so a ratio is not of noise.</summary>
    public static TimeSpan Median(Action action, int runs = 5)
    {
        action();
        var times = new long[runs];
        for (var run = 0; run < runs; run++)
        {
            var stopwatch = Stopwatch.StartNew();
            action();
            times[run] = stopwatch.ElapsedTicks;
        }

        Array.Sort(times);
        return TimeSpan.FromTicks(Math.Max(times[runs / 2] * TimeSpan.TicksPerSecond / Stopwatch.Frequency, TimeSpan.TicksPerMillisecond));
    }

    /// <summary>How many times longer <paramref name="large"/> takes than <paramref name="small"/>, each the median of its runs.</summary>
    public static double Ratio(Action small, Action large, int runs = 5)
        => Median(large, runs) / Median(small, runs);
}
