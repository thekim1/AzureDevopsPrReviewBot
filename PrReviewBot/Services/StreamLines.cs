using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace PrReviewBot.Services;

// Decides when a streamed response has gone quiet for too long.
//
// Only the provider can tell model output from filler, so it calls Kick() for
// each real chunk. Anything else — SSE heartbeat comments in particular — does
// not count. Bifrost sends ": ..." heartbeats to keep proxies from dropping
// idle connections, and a timeout that reset on every line was held open by
// them indefinitely while the model upstream had stopped producing anything.
internal sealed class StreamWatchdog : IDisposable
{
    private const int RecentLineLimit = 5;
    private const int RecentLineChars = 200;

    private readonly CancellationTokenSource _cts;
    private readonly TimeSpan _idleTimeout;
    private readonly Queue<string> _recentLines = new();

    public StreamWatchdog(TimeSpan idleTimeout, CancellationToken cancellationToken)
    {
        _idleTimeout = idleTimeout;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _cts.CancelAfter(idleTimeout);
    }

    public CancellationToken Token => _cts.Token;

    // True once the model has said it is finished. The stream is then only
    // waiting for trailing frames, and running out of patience is the end of
    // the stream rather than a failure.
    public bool IsFinishing { get; private set; }

    public int LinesSinceOutput { get; private set; }

    public TimeSpan IdleTimeout => _idleTimeout;

    // The model produced something: restart the clock.
    public void Kick()
    {
        LinesSinceOutput = 0;
        if (!IsFinishing)
        {
            _cts.CancelAfter(_idleTimeout);
        }
    }

    // The model has finished its answer. Allow a short grace period for the
    // trailing usage frame and end-of-stream marker, which some upstreams
    // never send — Bifrost then keeps the stream open on heartbeats alone.
    public void Finishing(TimeSpan grace)
    {
        IsFinishing = true;
        _cts.CancelAfter(grace);
    }

    public void Saw(string line)
    {
        LinesSinceOutput++;
        _recentLines.Enqueue(line.Length > RecentLineChars ? line[..RecentLineChars] + "…" : line);
        while (_recentLines.Count > RecentLineLimit)
        {
            _recentLines.Dequeue();
        }
    }

    // What the stream looked like when it was abandoned, for the failure dump.
    public string Describe()
    {
        StringBuilder sb = new();
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"Lines received since the model last produced output: {LinesSinceOutput}");
        sb.AppendLine("Last lines received:");
        foreach (string line in _recentLines)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"  {line}");
        }

        return sb.ToString();
    }

    public void Dispose() => _cts.Dispose();
}

internal static class StreamLines
{
    // Reads a streamed response line by line until it ends, the watchdog runs
    // out, or the caller cancels.
    //
    // HttpClient.Timeout does not cover this. With ResponseHeadersRead it stops
    // at the headers, so a stalled stream used to leave the read waiting
    // forever and the review never finished.
    public static async IAsyncEnumerable<string> ReadAsync(
        TextReader reader,
        StreamWatchdog watchdog,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (true)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(watchdog.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (watchdog.IsFinishing)
                {
                    yield break;
                }

                string seconds = watchdog.IdleTimeout.TotalSeconds.ToString("0", CultureInfo.InvariantCulture);
                string alive = watchdog.LinesSinceOutput == 0
                    ? "Nothing at all arrived in that time."
                    : string.Create(CultureInfo.InvariantCulture,
                        $"The connection was alive ({watchdog.LinesSinceOutput} keep-alive or empty line(s) arrived) but the model produced nothing.");

                throw new ReviewFailedException(
                    $"the model produced no output for {seconds} seconds, so the request was abandoned. {alive} "
                    + "Raise Review:StreamIdleTimeoutSeconds if the model is just slow to start.",
                    watchdog.Describe());
            }

            if (line is null)
            {
                yield break;
            }

            watchdog.Saw(line);
            yield return line;
        }
    }
}
