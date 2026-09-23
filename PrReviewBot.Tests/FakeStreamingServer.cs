using System.Net;
using System.Text;

namespace PrReviewBot.Tests;

// Stands in for a provider that streams a response. After the scripted body
// the stream either ends or, like a server that keeps the connection open,
// never delivers another byte until the reader gives up.
internal sealed class FakeStreamingHandler(string body, bool keepOpen, TimeSpan? heartbeat = null) : HttpMessageHandler
{
    public int Requests { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests++;
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new ScriptedStream(Encoding.UTF8.GetBytes(body), keepOpen, heartbeat))
        });
    }
}

// With a heartbeat, an open stream sends an SSE comment at that interval
// instead of nothing — the way Bifrost keeps idle connections alive.
internal sealed class ScriptedStream(byte[] data, bool keepOpen, TimeSpan? heartbeat = null) : Stream
{
    private static readonly byte[] HeartbeatBytes = Encoding.UTF8.GetBytes(": keep-alive\n\n");
    private int _position;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _position; set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_position < data.Length)
        {
            int count = Math.Min(buffer.Length, data.Length - _position);
            data.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        if (keepOpen && heartbeat is { } interval)
        {
            await Task.Delay(interval, cancellationToken);
            int count = Math.Min(buffer.Length, HeartbeatBytes.Length);
            HeartbeatBytes.AsMemory(0, count).CopyTo(buffer);
            return count;
        }

        if (keepOpen)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        return 0;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
