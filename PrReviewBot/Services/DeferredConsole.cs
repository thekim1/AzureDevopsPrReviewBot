namespace PrReviewBot.Services;

// Plain console output that waits while a live display is on screen.
//
// Spectre's live table and status spinner redraw a fixed block of lines. A
// Console.WriteLine from a background task in the middle of that — a provider
// warning, or the next PR loading in the background — lands inside the block
// and the display is redrawn on top of it, scrambling both. Messages written
// while a hold is active are printed, in order, when the last hold ends.
internal static class DeferredConsole
{
    private static readonly Lock _lock = new();
    private static readonly List<string> _pending = [];
    private static int _holds;

    public static void WriteLine(string message)
    {
        lock (_lock)
        {
            if (_holds > 0)
            {
                _pending.Add(message);
                return;
            }
        }

        Console.WriteLine(message);
    }

    public static IDisposable Hold()
    {
        lock (_lock)
        {
            _holds++;
        }

        return new Releaser();
    }

    private static void Release()
    {
        string[] flush;
        lock (_lock)
        {
            _holds--;
            if (_holds > 0)
            {
                return;
            }

            flush = [.. _pending];
            _pending.Clear();
        }

        foreach (string message in flush)
        {
            Console.WriteLine(message);
        }
    }

    private sealed class Releaser : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                Release();
            }
        }
    }
}
