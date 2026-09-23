using PrReviewBot.Services;

namespace PrReviewBot.Tests;

public class ConcurrencyHelpersTests
{
    [Fact]
    public async Task SelectStopsFetchingOnceTheLimitIsReached()
    {
        List<int> fetchedCalls = [];

        LimitedSelection<int, bool> selection = await ConcurrencyHelpers.SelectUpToLimitAsync(
            [1, 2, 3, 4, 5], limit: 3,
            async i => { lock (fetchedCalls) { fetchedCalls.Add(i); } await Task.Yield(); return true; },
            ok => ok);

        Assert.Equal([1, 2, 3], selection.Fetched.Select(f => f.Candidate));
        Assert.Equal([4, 5], selection.NotFetched);
        Assert.Equal([1, 2, 3], fetchedCalls.Order());
    }

    [Fact]
    public async Task SelectReplacesFailuresWithLaterCandidates()
    {
        // Same outcome as walking the list one by one: 2 and 4 fail and do not
        // use up a place.
        LimitedSelection<int, bool> selection = await ConcurrencyHelpers.SelectUpToLimitAsync(
            [1, 2, 3, 4, 5, 6], limit: 3,
            i => Task.FromResult(i is not 2 and not 4),
            ok => ok);

        Assert.Equal([1, 2, 3, 4, 5], selection.Fetched.Select(f => f.Candidate));
        Assert.Equal([1, 3, 5], selection.Fetched.Where(f => f.Result).Select(f => f.Candidate));
        Assert.Equal([6], selection.NotFetched);
    }

    [Fact]
    public async Task SelectKeepsCandidateOrderWhateverOrderFetchesFinish()
    {
        LimitedSelection<int, int> selection = await ConcurrencyHelpers.SelectUpToLimitAsync(
            [1, 2, 3], limit: 3,
            async i => { await Task.Delay((4 - i) * 30); return i * 10; },
            _ => true);

        Assert.Equal([(1, 10), (2, 20), (3, 30)], selection.Fetched);
    }

    [Fact]
    public async Task SelectFetchesARoundConcurrently()
    {
        int inFlight = 0, maxInFlight = 0;

        await ConcurrencyHelpers.SelectUpToLimitAsync(
            [1, 2, 3, 4], limit: 4,
            async _ =>
            {
                int now = Interlocked.Increment(ref inFlight);
                InterlockedMax(ref maxInFlight, now);
                await Task.Delay(50);
                Interlocked.Decrement(ref inFlight);
                return true;
            },
            ok => ok);

        Assert.Equal(4, maxInFlight);
    }

    [Fact]
    public async Task SelectWithFewerCandidatesThanLimitFetchesThemAll()
    {
        LimitedSelection<int, bool> selection = await ConcurrencyHelpers.SelectUpToLimitAsync(
            [1, 2], limit: 20, _ => Task.FromResult(false), ok => ok);

        Assert.Equal(2, selection.Fetched.Count);
        Assert.Empty(selection.NotFetched);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(10)]
    [InlineData(11)]
    public async Task ReadAllPagesReturnsEverything(int total)
    {
        int[] source = [.. Enumerable.Range(0, total)];
        List<(int Skip, int Top)> calls = [];

        List<int> all = await ConcurrencyHelpers.ReadAllPagesAsync(5, (skip, top) =>
        {
            calls.Add((skip, top));
            return Task.FromResult(source.Skip(skip).Take(top).ToList());
        });

        Assert.Equal(source, all);
        Assert.Equal((total / 5) + 1, calls.Count);
        Assert.All(calls, c => Assert.Equal(5, c.Top));
        Assert.Equal(Enumerable.Range(0, calls.Count).Select(i => i * 5), calls.Select(c => c.Skip));
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target))
               && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }
}
