using Spectre.Console;

namespace PrReviewBot.Services;

// Every status spinner in the tool goes through here.
//
// Spectre.Console (0.57.2, and still on main) has a race in the thread that
// animates a spinner: ProgressRefreshThread.Dispose only stops the thread if
// the thread has already started running. A spinner that finishes before its
// thread gets going leaves that thread behind, refreshing forever.
//
// On its own that is harmless. Next to a live display it is a deadlock: the
// orphan takes the console's write lock and then the live display's lock,
// while the live display's UpdateTarget takes them in the opposite order. It
// started happening when PRs began loading in the background — the "fetching
// changes" spinner for an already-loaded PR finished in microseconds — and it
// froze the whole review, timeouts included, since those were blocked too.
//
// Holding every spinner open for a moment guarantees its thread has started
// before it is stopped, so it is always stopped.
internal static class ConsoleStatus
{
    internal static readonly TimeSpan MinimumDuration = TimeSpan.FromMilliseconds(250);

    public static Task RunAsync(string status, Func<StatusContext, Task> work)
        => RunAsync(AnsiConsole.Console, status, work);

    internal static Task RunAsync(IAnsiConsole console, string status, Func<StatusContext, Task> work)
        => console.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync(status, async ctx =>
            {
                Task minimum = Task.Delay(MinimumDuration);
                try
                {
                    await work(ctx);
                }
                finally
                {
                    await minimum;
                }
            });
}
