using Anthropic;
using Anthropic.Models.Messages;
using PrReviewBot.Config;
using PrReviewBot.Models;

namespace PrReviewBot.Services;

public class ClaudeReviewService : IReviewService
{
    private readonly AnthropicClient _client;
    private readonly ClaudeSettings _settings;

    public ClaudeReviewService(ClaudeSettings settings)
    {
        _settings = settings;
        // Official SDK: ApiKey is a property on the client initializer
        _client = new AnthropicClient { ApiKey = settings.ApiKey };
    }

    public async Task<List<ReviewComment>> ReviewPullRequestAsync(PullRequestInfo pr)
    {
        string prompt = ReviewHelpers.BuildReviewPrompt(pr);

        MessageCreateParams parameters = new()
        {
            Model = _settings.Model,
            MaxTokens = 4096,
            System = ReviewHelpers.SystemPrompt,
            Messages =
            [
                new()
                {
                    Role = Role.User,
                    Content = prompt
                }
            ]
        };

        Message response = await _client.Messages.Create(parameters);

        string content = response.Content
            .Select(b => b.Value)
            .OfType<TextBlock>()
            .FirstOrDefault()?.Text ?? "";

        return ReviewHelpers.ParseReviewResponse(content);
    }
}
