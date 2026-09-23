namespace PrReviewBot.Services;

// Loads items one step ahead of the consumer.
//
// Reviewing a PR takes minutes, fetching the next one's changes takes seconds,
// and neither needs the other. Starting the next load as soon as the current
// item is asked for means that by the time the consumer gets to it, it is
// usually already there.
internal sealed class Prefetcher<TItem>(IReadOnlyList<TItem> items, Func<TItem, Task> load)
{
    private readonly Task?[] _loads = new Task?[items.Count];

    // Completes when item `index` is loaded. Also starts loading the item
    // after it, without waiting for that one.
    //
    // A failed load surfaces when its own item is asked for, not earlier, so a
    // problem with one PR is reported against that PR.
    public Task LoadAsync(int index)
    {
        Task current = Start(index);

        if (index + 1 < items.Count)
        {
            Start(index + 1);
        }

        return current;
    }

    private Task Start(int index) => _loads[index] ??= load(items[index]);
}
