using PrReviewBot.Services;

namespace PrReviewBot.Tests;

public class PrefetcherTests
{
    [Fact]
    public async Task StartsLoadingTheNextItemWhileTheCurrentOneIsInUse()
    {
        List<string> started = [];
        Prefetcher<string> prefetcher = new(["a", "b", "c"], item =>
        {
            lock (started) { started.Add(item); }
            return Task.CompletedTask;
        });

        await prefetcher.LoadAsync(0);

        Assert.Equal(["a", "b"], started);
    }

    [Fact]
    public async Task LoadsEachItemExactlyOnce()
    {
        Dictionary<string, int> loads = [];
        Prefetcher<string> prefetcher = new(["a", "b", "c"], item =>
        {
            lock (loads) { loads[item] = loads.GetValueOrDefault(item) + 1; }
            return Task.CompletedTask;
        });

        for (int i = 0; i < 3; i++)
        {
            await prefetcher.LoadAsync(i);
        }

        Assert.Equal(new Dictionary<string, int> { ["a"] = 1, ["b"] = 1, ["c"] = 1 }, loads);
    }

    [Fact]
    public async Task WaitsForTheRequestedItemToFinish()
    {
        TaskCompletionSource gate = new();
        Prefetcher<string> prefetcher = new(["a"], _ => gate.Task);

        Task load = prefetcher.LoadAsync(0);
        Assert.False(load.IsCompleted);

        gate.SetResult();
        await load;
    }

    [Fact]
    public async Task FailureSurfacesOnItsOwnItemOnly()
    {
        Prefetcher<string> prefetcher = new(["ok", "bad"], item => item == "bad"
            ? Task.FromException(new InvalidOperationException("bad item"))
            : Task.CompletedTask);

        await prefetcher.LoadAsync(0);

        await Assert.ThrowsAsync<InvalidOperationException>(() => prefetcher.LoadAsync(1));
    }
}
