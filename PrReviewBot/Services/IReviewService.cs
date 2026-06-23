using PrReviewBot.Models;

namespace PrReviewBot.Services;

public interface IReviewService
{
    Task<List<ReviewComment>> ReviewPullRequestAsync(PullRequestInfo pr);
}
