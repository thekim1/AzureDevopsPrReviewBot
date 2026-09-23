using PrReviewBot.Services;

namespace PrReviewBot.Tests;

// Console output is process-wide, so these must not run alongside anything
// else that writes to it.
[CollectionDefinition(nameof(ConsoleCollection), DisableParallelization = true)]
public sealed class ConsoleCollection;

[Collection(nameof(ConsoleCollection))]
public class DeferredConsoleTests
{
    private static string Capture(Action<StringWriter> action)
    {
        TextWriter original = Console.Out;
        StringWriter writer = new();
        Console.SetOut(writer);
        try
        {
            action(writer);
        }
        finally
        {
            Console.SetOut(original);
        }

        return writer.ToString();
    }

    [Fact]
    public void WritesImmediatelyWhenNothingIsHeld()
        => Assert.Equal("hello" + Environment.NewLine, Capture(_ => DeferredConsole.WriteLine("hello")));

    [Fact]
    public void HoldsMessagesUntilTheLastHoldEnds()
    {
        string duringOuter = "", duringInner = "";

        string all = Capture(writer =>
        {
            IDisposable outer = DeferredConsole.Hold();
            IDisposable inner = DeferredConsole.Hold();
            DeferredConsole.WriteLine("one");
            inner.Dispose();
            duringInner = writer.ToString();
            DeferredConsole.WriteLine("two");
            duringOuter = writer.ToString();
            outer.Dispose();
            outer.Dispose(); // disposing twice must not release someone else's hold
        });

        Assert.Equal("", duringInner);
        Assert.Equal("", duringOuter);
        Assert.Equal($"one{Environment.NewLine}two{Environment.NewLine}", all);
    }
}
