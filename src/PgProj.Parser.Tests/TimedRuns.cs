using System.Diagnostics;

namespace PgProj.Parser.Tests;

/// <summary>
/// Timing for the timed tests <c>copilot-tests.md</c> §3 allows (G-321): a directional history of performance, and a comparison before and after a change.
/// A test compares measurements taken in the same run, with a generous margin, never an absolute time, so a slow or busy machine does not fail it.
/// </summary>
internal static class TimedRuns
{
    /// <summary>
    /// The fastest of <paramref name="runs"/> runs of <paramref name="action"/>, after one run to warm up; at least a millisecond, so a ratio is not of noise.
    /// A busy machine only ever adds time to a run, so the fastest is the one nearest the work itself (G-327: a median failed on a small CI runner).
    /// </summary>
    public static TimeSpan Fastest(Action action, int runs = 5)
    {
        action();
        var fastest = long.MaxValue;
        for (var run = 0; run < runs; run++)
        {
            var stopwatch = Stopwatch.StartNew();
            action();
            fastest = Math.Min(fastest, stopwatch.ElapsedTicks);
        }

        return TimeSpan.FromTicks(Math.Max(fastest * TimeSpan.TicksPerSecond / Stopwatch.Frequency, TimeSpan.TicksPerMillisecond));
    }

    /// <summary>How many times longer <paramref name="large"/> takes than <paramref name="small"/>, each the fastest of its runs.</summary>
    public static double Ratio(Action small, Action large, int runs = 5)
        => Fastest(large, runs) / Fastest(small, runs);
}
