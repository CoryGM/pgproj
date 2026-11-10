using System.Globalization;

namespace PgProj.Parser.Tests;

/// <summary>G-272: the parser and the semantic passes run on a thread with a large stack. G-343: the threads are used again, so a project of thousands of files does not make thousands of threads.</summary>
[TestClass]
public sealed class LargeStackTests
{
    [TestMethod]
    public void TheWorkRunsOnAnotherThreadAndItsResultIsReturned()
    {
        var caller = Environment.CurrentManagedThreadId;

        var worker = LargeStack.Run(() => Environment.CurrentManagedThreadId);

        Assert.AreNotEqual(caller, worker);
    }

    [TestMethod]
    public void WorkAlreadyOnTheLargeStackRunsWhereItIs()
    {
        var (outer, inner) = LargeStack.Run(() => (Environment.CurrentManagedThreadId, LargeStack.Run(() => Environment.CurrentManagedThreadId)));

        Assert.AreEqual(outer, inner);
    }

    [TestMethod]
    public void AnExceptionReachesTheCallerAsItWasThrown()
    {
        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => LargeStack.Run<int>(() => throw new InvalidOperationException("from the large stack")));

        Assert.AreEqual("from the large stack", exception.Message);
    }

    [TestMethod]
    public void TheCallersCultureIsUsed()
    {
        var previous = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");

            var (culture, uiCulture) = LargeStack.Run(() => (CultureInfo.CurrentCulture.Name, CultureInfo.CurrentUICulture.Name));

            Assert.AreEqual("de-DE", culture);
            Assert.AreEqual("fr-FR", uiCulture);
        }
        finally
        {
            (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = previous;
        }
    }

    [TestMethod]
    public void RecursionTooDeepForADefaultStackSucceeds()
    {
        // About 3 KB a level was measured for the readers; this uses far more than a default thread's 1 MB.
        static int Recurse(int remaining) => remaining == 0 ? 0 : 1 + Recurse(remaining - 1) + (Fill(stackalloc byte[1024]) & 0);
        static int Fill(Span<byte> buffer)
        {
            buffer[0] = 1;
            return buffer[0];
        }

        Assert.AreEqual(20_000, LargeStack.Run(() => Recurse(20_000)));
    }

    [TestMethod]
    public void ManyCallsShareAFewThreads()
    {
        // Each call had a thread of its own before G-343: 3,000 calls made 3,000 threads, and libpg_query took a thread-local key on each.
        var threads = Enumerable.Range(0, 3000).Select(static _ => LargeStack.Run(() => Environment.CurrentManagedThreadId)).Distinct().Count();

        Assert.IsLessThanOrEqualTo(Bound, threads, $"{threads} threads for 3,000 calls one after another.");
    }

    [TestMethod]
    public void CallsAtTheSameTimeEachHaveAThreadAndNoMoreAreMade()
    {
        var threads = new System.Collections.Concurrent.ConcurrentDictionary<int, bool>();

        Parallel.For(0, 3000, new ParallelOptions { MaxDegreeOfParallelism = 8 }, _ => threads[LargeStack.Run(() => Environment.CurrentManagedThreadId)] = true);

        Assert.IsLessThanOrEqualTo(Bound + 8, threads.Count, $"{threads.Count} threads for 3,000 calls, 8 at a time.");
    }

    [TestMethod]
    public void EachCallSeesItsCallersAsyncLocalValuesAndNotThePreviousCallers()
    {
        var value = new AsyncLocal<string?>();

        string? first = null;
        var thread = new Thread(() =>
        {
            value.Value = "first caller";
            first = LargeStack.Run(() => value.Value);
        });
        thread.Start();
        thread.Join();
        var second = LargeStack.Run(() => value.Value);

        Assert.AreEqual("first caller", first);
        Assert.IsNull(second);
    }

    // The other tests of this assembly run beside these and use the same threads; this is far below one thread a call.
    private static int Bound => (2 * Environment.ProcessorCount) + 16;
}