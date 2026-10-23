using System.Globalization;

namespace PgProj.Parser.Tests;

/// <summary>G-272: the parser and the semantic passes run on a thread with a large stack.</summary>
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
}
