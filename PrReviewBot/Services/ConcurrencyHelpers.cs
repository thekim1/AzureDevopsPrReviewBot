namespace PrReviewBot.Services;

internal static class ConcurrencyHelpers
{
    // Fetches candidates concurrently until `limit` of them have succeeded,
    // without fetching more than it has to.
    //
    // Behaves exactly like walking the candidates one at a time and stopping at
    // the limit — the same candidates are accepted, in the same order — but
    // each round fetches all the still-open slots at once. A candidate that
    // fails does not use a slot, so the next round takes its place from further
    // down the list.
    public static async Task<LimitedSelection<TCandidate, TResult>> SelectUpToLimitAsync<TCandidate, TResult>(
        IReadOnlyList<TCandidate> candidates,
        int limit,
        Func<TCandidate, Task<TResult>> fetch,
        Func<TResult, bool> isSuccess)
    {
        List<(TCandidate Candidate, TResult Result)> fetched = [];
        int succeeded = 0;
        int next = 0;

        while (next < candidates.Count && succeeded < limit)
        {
            int take = Math.Min(limit - succeeded, candidates.Count - next);
            TCandidate[] round = [.. candidates.Skip(next).Take(take)];
            next += take;

            TResult[] results = await Task.WhenAll(round.Select(fetch));

            for (int i = 0; i < round.Length; i++)
            {
                fetched.Add((round[i], results[i]));
                if (isSuccess(results[i]))
                {
                    succeeded++;
                }
            }
        }

        return new LimitedSelection<TCandidate, TResult>(fetched, [.. candidates.Skip(next)]);
    }

    // Reads a skip/top paged API to the end. A page shorter than requested is
    // the last one.
    public static async Task<List<T>> ReadAllPagesAsync<T>(
        int pageSize, Func<int, int, Task<List<T>>> fetchPage)
    {
        List<T> all = [];

        while (true)
        {
            List<T> page = await fetchPage(all.Count, pageSize);
            all.AddRange(page);

            if (page.Count < pageSize)
            {
                return all;
            }
        }
    }
}

// Fetched: every candidate that was fetched, in candidate order, with its
// result. NotFetched: the candidates left over once the limit was reached.
internal sealed record LimitedSelection<TCandidate, TResult>(
    List<(TCandidate Candidate, TResult Result)> Fetched,
    List<TCandidate> NotFetched);
