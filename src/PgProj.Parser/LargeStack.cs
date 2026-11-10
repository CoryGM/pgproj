using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.ExceptionServices;

namespace PgProj.Parser;

/// <summary>
/// Runs work that walks parse trees recursively on a thread with a large stack (G-272). A tree libpg_query accepts can be thousands of
/// levels deep, and on a default stack (1 MB for the main thread on Windows) the readers overflowed at a few hundred, which ends the
/// process. The stack is reserved address space, committed only as it is used.
/// </summary>
/// <remarks>
/// The threads are kept and used again (G-343): libpg_query takes a thread-local key from the operating system on the first parse on each
/// thread and never gives it back, so a new thread for every file used up the process's keys (1,024 on Linux) after about a thousand files,
/// and OpenSSL, which needs one, crashed the process at its first connection. A call takes an idle thread, or makes one when none is idle, so
/// there are only as many threads as calls ever ran at once, and a call never waits for another to finish. Each call runs with the caller's
/// culture and execution context (its <see cref="AsyncLocal{T}"/> values), as a new thread did.
/// </remarks>
public static class LargeStack
{
    /// <summary>About 3 KB of stack per level of a tree was measured, so this holds about 80,000 levels, well past the deepest tree
    /// libpg_query returns and <see cref="PgQueryJson.MaxStatementDepth"/>.</summary>
    public const int StackSize = 256 * 1024 * 1024;

    [ThreadStatic]
    private static bool t_onLargeStack;

    private static readonly ConcurrentBag<Worker> s_idle = [];

    public static T Run<T>(Func<T> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (t_onLargeStack)
        {
            return work();
        }

        var culture = CultureInfo.CurrentCulture;
        var uiCulture = CultureInfo.CurrentUICulture;
        var context = ExecutionContext.Capture();
        T result = default!;
        ExceptionDispatchInfo? failure = null;
        using var done = new ManualResetEventSlim();
        if (!s_idle.TryTake(out var worker))
        {
            worker = Worker.Start();
        }

        worker.Post(() =>
        {
            void Call()
            {
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
            }

            if (context is null)
            {
                Call();
            }
            else
            {
                ExecutionContext.Run(context, static state => ((Action)state!)(), (Action)Call);
            }
        }, done);
        done.Wait();
        failure?.Throw();
        return result;
    }

    /// <summary>One large-stack thread, which runs the calls posted to it one at a time and is idle between them.</summary>
    private sealed class Worker
    {
        private readonly AutoResetEvent _posted = new(false);
        private Action? _call;
        private ManualResetEventSlim? _done;

        public static Worker Start()
        {
            var worker = new Worker();
            var thread = new Thread(worker.Loop, StackSize)
            {
                Name = "pgproj parse tree",
                IsBackground = true,
            };

            // The thread is kept for later calls, so it does not take the first caller's execution context: each call brings its own.
            thread.UnsafeStart();
            return worker;
        }

        /// <summary>Runs <paramref name="call"/> on this thread, then sets <paramref name="done"/>, the caller's own.</summary>
        public void Post(Action call, ManualResetEventSlim done)
        {
            _call = call;
            _done = done;
            _posted.Set();
        }

        private void Loop()
        {
            t_onLargeStack = true;
            while (true)
            {
                _posted.WaitOne();
                var call = _call!;
                var done = _done!;
                _call = null;
                _done = null;
                call();

                // Idle again before the caller is told, so a caller's next call finds this thread rather than making another.
                s_idle.Add(this);
                done.Set();
            }
        }
    }
}
