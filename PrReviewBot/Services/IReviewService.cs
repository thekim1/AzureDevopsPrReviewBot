using PrReviewBot.Models;

namespace PrReviewBot.Services;

public interface IReviewService
{
    // Reviews a subset of the PR's changed files. The caller decides how the
    // files are split — see PullRequestReviewer — so that no single request
    // carries more than a model can answer in one output budget.
    //
    // `progress` receives live updates while the model works, when the provider
    // and settings support streaming. Providers that do not stream simply never
    // report, so callers must treat silence as "no information", not "stalled".
    Task<List<ReviewComment>> ReviewPullRequestAsync(
        PullRequestInfo pr,
        IReadOnlyList<ChangedFile> files,
        IProgress<ReviewProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
