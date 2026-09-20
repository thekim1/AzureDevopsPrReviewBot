using System.Text;
﻿using Anthropic;
using Anthropic.Models.Messages;
using PrReviewBot.Config;
using PrReviewBot.Models;

namespace PrReviewBot.Services;

public class ClaudeReviewService : IReviewService
{
    private readonly AnthropicClient _client;
    private readonly ClaudeSettings _settings;
    private readonly int _maxOutputTokens;
    private readonly bool _scopeCommentsToBatch;
    private readonly bool _stream;

    public ClaudeReviewService(ClaudeSettings settings, ReviewSettings? reviewSettings = null)
    {
        _settings = settings;
        ReviewSettings review = reviewSettings ?? new ReviewSettings();
        _maxOutputTokens = review.MaxOutputTokens;
        _scopeCommentsToBatch = review.ScopeExistingCommentsToBatch;
        _stream = review.ShowThinking;
        // Official SDK: ApiKey is a property on the client initializer
        _client = new AnthropicClient { ApiKey = settings.ApiKey };
    }

    public async Task<List<ReviewComment>> ReviewPullRequestAsync(
        PullRequestInfo pr,
        IReadOnlyList<ChangedFile> files,
        IProgress<ReviewProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        string prompt = ReviewHelpers.BuildReviewPrompt(pr, files, _scopeCommentsToBatch);

        MessageCreateParams parameters = new()
        {
            Model = _settings.Model,
            MaxTokens = _maxOutputTokens,
            // Temperature is deliberately not set. It is deprecated on the
            // Anthropic API — models released after Claude Opus 4.6 reject any
            // value other than 1.0 with a 400 — and these models are already
            // stable enough on analytical work without it.
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

        if (_stream)
        {
            return await ReviewStreamingAsync(parameters, progress, cancellationToken);
        }

        Message response = await _client.Messages.Create(parameters, cancellationToken: cancellationToken);

        string content = response.Content
            .Select(b => b.Value)
            .OfType<TextBlock>()
            .FirstOrDefault()?.Text ?? "";

        if (string.IsNullOrWhiteSpace(content))
        {
            throw new ReviewFailedException(
                $"{_settings.Model} returned no text (stop_reason=\"{response.StopReason}\"). "
                + $"If that is \"max_tokens\", raise Review:MaxOutputTokens (currently {_maxOutputTokens}).");
        }

        return ReviewHelpers.ParseReviewResponse(content);
    }

    // Streams the response so the terminal can show work in progress.
    private async Task<List<ReviewComment>> ReviewStreamingAsync(
        MessageCreateParams parameters,
        IProgress<ReviewProgress>? progress,
        CancellationToken cancellationToken)
    {
        StringBuilder answer = new();
        StringBuilder reasoning = new();

        await foreach (RawMessageStreamEvent evt in
            _client.Messages.CreateStreaming(parameters, cancellationToken: cancellationToken))
        {
            if (!evt.TryPickContentBlockDelta(out RawContentBlockDeltaEvent? blockDelta))
            {
                continue;
            }

            // Union order: text, input JSON, citations, thinking, signature.
            blockDelta.Delta.Switch(
                text =>
                {
                    answer.Append(text.Text);
                    progress?.Report(new ReviewProgress(
                        0, reasoning.Length, answer.Length, text.Text, IsAnswer: true));
                },
                _ => { },
                _ => { },
                thinking =>
                {
                    reasoning.Append(thinking.Thinking);
                    progress?.Report(new ReviewProgress(
                        0, reasoning.Length, answer.Length, thinking.Thinking, IsAnswer: false));
                },
                _ => { });
        }

        if (answer.Length == 0)
        {
            throw new ReviewFailedException(
                $"{_settings.Model} streamed no answer"
                + (reasoning.Length == 0 ? "" : $" (but {reasoning.Length} characters of thinking)")
                + $". If it was cut off, raise Review:MaxOutputTokens (currently {_maxOutputTokens}).");
        }

        return ReviewHelpers.ParseReviewResponse(answer.ToString());
    }
}
