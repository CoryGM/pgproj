using System.Globalization;
using System.Runtime.ExceptionServices;

namespace PgProj.Parser;

/// <summary>
/// Runs work that walks parse trees recursively on a thread with a large stack (G-272). A tree libpg_query accepts can be thousands of
/// levels deep, and on a default stack (1 MB for the main thread on Windows) the readers overflowed at a few hundred, which ends the
/// process. The stack is reserved address space, committed only as it is used.
/// </summary>
public static class LargeStack
{
    /// <summary>About 3 KB of stack per level of a tree was measured, so this holds about 80,000 levels, well past the deepest tree
    /// libpg_query returns and <see cref="PgQueryJson.MaxStatementDepth"/>.</summary>
    public const int StackSize = 256 * 1024 * 1024;

    [ThreadStatic]
    private static bool t_onLargeStack;

    public static T Run<T>(Func<T> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (t_onLargeStack)
        {
            return work();
        }

        var culture = CultureInfo.CurrentCulture;
        var uiCulture = CultureInfo.CurrentUICulture;
        T result = default!;
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(
            () =>
            {
                t_onLargeStack = true;
                CultureInfo.CurrentCulture = culture;
                CultureInfo.CurrentUICulture = uiCulture;
                try
                {
                    result = work();
                }
                catch (Exception exception)
                {
                    failure = ExceptionDispatchInfo.Capture(exception);
                }
            },
            StackSize)
        {
            Name = "pgproj parse tree",
            IsBackground = true,
        };
        thread.Start();
        thread.Join();
        failure?.Throw();
        return result;
    }
}
