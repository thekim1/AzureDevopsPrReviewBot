using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PrReviewBot.Config;
using PrReviewBot.Models;

namespace PrReviewBot.Services;

// Reviews PRs through a Bifrost LLM gateway (https://docs.getbifrost.ai) using
// its OpenAI-compatible endpoint (/v1/chat/completions). Any provider
// configured in Bifrost works — models are addressed as "provider/model",
// e.g. "anthropic/claude-sonnet-4-5" or "openai/gpt-4o".
public sealed class BifrostReviewService : IReviewService, IDisposable
{
    private readonly BifrostSettings _settings;
    private readonly HttpClient _httpClient;
    private readonly int _maxOutputTokens;
    private readonly bool _scopeCommentsToBatch;
    private readonly bool _stream;
    private readonly TimeSpan _streamIdleTimeout;

    // How long to wait for the usage frame and [DONE] after the model has
    // finished. Bifrost can keep a finished stream open on heartbeats alone
    // when the upstream omits [DONE] (maximhq/bifrost#7108).
    internal TimeSpan FinishGrace { get; init; } = TimeSpan.FromSeconds(10);

    private static readonly JsonSerializerOptions _responseJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public BifrostReviewService(BifrostSettings settings, ReviewSettings? reviewSettings = null)
        : this(settings, reviewSettings, new HttpClientHandler())
    {
    }

    // Lets tests stand in for the server.
    internal BifrostReviewService(BifrostSettings settings, ReviewSettings? reviewSettings, HttpMessageHandler handler)
    {
        _settings = settings;
        ReviewSettings review = reviewSettings ?? new ReviewSettings();
        _maxOutputTokens = review.MaxOutputTokens;
        _scopeCommentsToBatch = review.ScopeExistingCommentsToBatch;
        _stream = review.ShowThinking;
        _streamIdleTimeout = TimeSpan.FromSeconds(Math.Max(1, review.StreamIdleTimeoutSeconds));

        _httpClient = new HttpClient(handler)
        {
            // Ensure trailing slash so relative paths (e.g. "chat/completions") are
            // appended rather than replacing the last segment (RFC 3986 rules).
            BaseAddress = new Uri(settings.BaseUrl.TrimEnd('/') + '/'),
            Timeout = TimeSpan.FromMinutes(5)
        };

        if (!string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", settings.ApiKey);
        }
    }

    public async Task<List<ReviewComment>> ReviewPullRequestAsync(
        PullRequestInfo pr,
        IReadOnlyList<ChangedFile> files,
        IProgress<ReviewProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        string prompt = ReviewHelpers.BuildReviewPrompt(pr, files, _scopeCommentsToBatch);

        Dictionary<string, object> requestBody = new()
        {
            ["model"] = _settings.Model,
            ["messages"] = new object[]
            {
                new { role = "system", content = ReviewHelpers.SystemPrompt },
                new { role = "user", content = prompt }
            },
            ["max_tokens"] = _maxOutputTokens,
            ["stream"] = _stream
        };

        if (_stream)
        {
            // Without this the gateway has no usage block to record for a
            // streamed call, which would leave a hole in exactly the cost
            // tracking the gateway exists to provide.
            requestBody["stream_options"] = new { include_usage = true };
        }

        // Only sent when configured — see BifrostSettings.Temperature for why
        // it is not on by default.
        if (_settings.Temperature.HasValue)
        {
            requestBody["temperature"] = _settings.Temperature.Value;
        }

        // Constrains the model to JSON, the way the native Ollama path does
        // with `format: "json"`. See BifrostSettings.ResponseFormat.
        if (!string.IsNullOrWhiteSpace(_settings.ResponseFormat))
        {
            requestBody["response_format"] = new { type = _settings.ResponseFormat };
        }

        // Provider-specific switches, merged last so they can override
        // anything above. See BifrostSettings.ExtraParameters.
        foreach (KeyValuePair<string, JsonElement> extra in _settings.ExtraParameters)
        {
            requestBody[extra.Key] = extra.Value;
        }

        StringContent content = new(
            JsonSerializer.Serialize(requestBody),
            Encoding.UTF8,
            "application/json");

        // The parameters actually sent, minus the prompt itself. Saved with a
        // failure dump so it is possible to tell "the setting never reached the
        // gateway" apart from "the gateway ignored it" — without this, tuning
        // ExtraParameters is guesswork.
        string sentParameters = JsonSerializer.Serialize(
            requestBody.Where(kv => kv.Key != "messages").ToDictionary(kv => kv.Key, kv => kv.Value),
            new JsonSerializerOptions { WriteIndented = true });

        if (_stream)
        {
            return await ReviewStreamingAsync(content, sentParameters, progress, cancellationToken);
        }

        HttpResponseMessage response = await _httpClient.PostAsync(
            "chat/completions", content, cancellationToken);

        string responseJson = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Bifrost request failed with status {(int)response.StatusCode} ({response.ReasonPhrase}). " +
                $"Model: {_settings.Model}. Response body: {responseJson}");
        }

        ChatCompletionsResponse? result =
            JsonSerializer.Deserialize<ChatCompletionsResponse>(responseJson, _responseJsonOptions);

        ChatChoice? choice = result?.Choices?.FirstOrDefault();
        string text = choice?.Message?.Content ?? "";

        // An empty message is the common failure with reasoning models behind a
        // gateway: the whole output budget goes on reasoning tokens and nothing
        // is left for the answer. The response still carries the evidence for
        // that, so report it instead of failing with a bare parse error.
        if (string.IsNullOrWhiteSpace(text))
        {
            // A reasoning model may put the whole answer inside its thinking
            // and leave `content` empty. Try to recover it — but only accept
            // something that actually deserializes into review comments.
            //
            // Salvage must never make the diagnosis worse. Reasoning prose is
            // full of brackets, and a loose "first [ to last ]" grab returns
            // C# snippets the model was discussing; the JSON error that follows
            // then buries the real failure, which is that the model never
            // answered. So a failed salvage falls through to that real error.
            if (ReviewHelpers.TryFindCommentArray(
                    choice?.Message?.ReasoningContent, out List<ReviewComment>? salvaged)
                && salvaged is not null)
            {
                DeferredConsole.WriteLine(
                    $"Warning: {_settings.Model} left 'content' empty and answered inside its reasoning; "
                    + $"recovered {salvaged.Count} finding(s) from there. "
                    + "Set Bifrost:ExtraParameters to { \"think\": false } to stop it thinking instead of answering.");
                return salvaged;
            }

            throw new ReviewFailedException(
                Explain(choice, result), Diagnostics(sentParameters, responseJson));
        }

        try
        {
            return ReviewHelpers.ParseReviewResponse(text);
        }
        catch (ReviewFailedException ex)
        {
            // Re-throw carrying the whole exchange rather than just the message
            // text, so the saved dump shows what was sent and what came back.
            throw new ReviewFailedException(ex.Message, Diagnostics(sentParameters, responseJson));
        }
    }

    // Reads the server-sent-event stream, surfacing reasoning and answer text
    // as it arrives and reassembling both into the final response.
    private async Task<List<ReviewComment>> ReviewStreamingAsync(
        StringContent content,
        string sentParameters,
        IProgress<ReviewProgress>? progress,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, "chat/completions") { Content = content };
        using HttpResponseMessage response = await _httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"Bifrost request failed with status {(int)response.StatusCode} ({response.ReasonPhrase}). "
                + $"Model: {_settings.Model}. Response body: {body}");
        }

        StringBuilder answer = new();
        StringBuilder reasoning = new();
        string? finishReason = null;
        string? usageJson = null;

        using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using StreamReader reader = new(stream);

        using StreamWatchdog watchdog = new(_streamIdleTimeout, cancellationToken);

        await foreach (string line in StreamLines.ReadAsync(reader, watchdog, cancellationToken))
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            string payload = line[5..].Trim();
            if (payload.Length == 0)
            {
                continue;
            }

            // The end of the answer. The usage chunk comes before this, so
            // nothing is lost by stopping here — and waiting for the server to
            // close the connection instead is what hung the review after the
            // last batch whenever the gateway kept the connection open.
            if (payload == "[DONE]")
            {
                break;
            }

            StreamChunk? chunk;
            try
            {
                chunk = JsonSerializer.Deserialize<StreamChunk>(payload, _responseJsonOptions);
            }
            catch (JsonException)
            {
                // A malformed keep-alive or partial frame is not worth aborting
                // a review that is otherwise progressing.
                continue;
            }

            // A real frame from the model. Heartbeat comments never get here:
            // they do not start with "data:".
            watchdog.Kick();

            if (chunk?.Usage is not null)
            {
                usageJson = JsonSerializer.Serialize(chunk.Usage);

                // Usage is the last frame before [DONE]; once the answer is
                // finished there is nothing further worth waiting for.
                if (finishReason is not null)
                {
                    break;
                }
            }

            StreamChoice? choice = chunk?.Choices?.FirstOrDefault();
            if (choice is null)
            {
                continue;
            }

            if (finishReason is null && choice.FinishReason is not null)
            {
                finishReason = choice.FinishReason;
                watchdog.Finishing(FinishGrace);
            }

            string? thought = choice.Delta?.ReasoningContent;
            if (!string.IsNullOrEmpty(thought))
            {
                reasoning.Append(thought);
                progress?.Report(new ReviewProgress(
                    0, reasoning.Length, answer.Length, thought, IsAnswer: false));
            }

            string? text = choice.Delta?.Content;
            if (!string.IsNullOrEmpty(text))
            {
                answer.Append(text);
                progress?.Report(new ReviewProgress(
                    0, reasoning.Length, answer.Length, text, IsAnswer: true));
            }
        }

        string diagnostics = Diagnostics(
            sentParameters,
            $"{{\"streamed\": true, \"finish_reason\": {JsonSerializer.Serialize(finishReason)}, "
            + $"\"usage\": {usageJson ?? "null"}, "
            + $"\"content_length\": {answer.Length}, \"reasoning_length\": {reasoning.Length}, "
            + $"\"reasoning_content\": {JsonSerializer.Serialize(reasoning.ToString())}}}");

        if (answer.Length == 0)
        {
            if (ReviewHelpers.TryFindCommentArray(reasoning.ToString(), out List<ReviewComment>? salvaged))
            {
                DeferredConsole.WriteLine(
                    $"Warning: {_settings.Model} left 'content' empty and answered inside its reasoning; "
                    + $"recovered {salvaged.Count} finding(s) from there.");
                return salvaged;
            }

            throw new ReviewFailedException(
                $"{_settings.Model} streamed {reasoning.Length} characters of reasoning but no answer"
                + (finishReason is null ? "" : $" (finish_reason=\"{finishReason}\")")
                + $". Raise Review:MaxOutputTokens (currently {_maxOutputTokens}) — on a reasoning model "
                + "the thinking is billed against the same budget as the answer.",
                diagnostics);
        }

        try
        {
            return ReviewHelpers.ParseReviewResponse(answer.ToString());
        }
        catch (ReviewFailedException ex)
        {
            throw new ReviewFailedException(ex.Message, diagnostics);
        }
    }

    private static string Diagnostics(string sentParameters, string responseJson) =>
        $"=== REQUEST PARAMETERS SENT (prompt omitted) ==={Environment.NewLine}{sentParameters}"
        + $"{Environment.NewLine}{Environment.NewLine}=== RESPONSE ==={Environment.NewLine}{responseJson}";

    private string Explain(ChatChoice? choice, ChatCompletionsResponse? result)
    {
        StringBuilder sb = new();
        sb.Append(CultureInfo.InvariantCulture, $"{_settings.Model} returned an empty message.");

        if (!string.IsNullOrWhiteSpace(choice?.FinishReason))
        {
            sb.Append(CultureInfo.InvariantCulture, $" finish_reason=\"{choice.FinishReason}\".");
        }

        if (result?.Usage is { } usage)
        {
            sb.Append(CultureInfo.InvariantCulture,
                $" Tokens: {usage.PromptTokens} prompt, {usage.CompletionTokens} completion");
            if (usage.CompletionTokensDetails?.ReasoningTokens is > 0 and int reasoning)
            {
                sb.Append(CultureInfo.InvariantCulture, $" ({reasoning} of them reasoning)");
            }

            sb.Append('.');
        }

        // Some reasoning models put their thinking in a separate field and
        // leave "content" empty when they run out of room.
        if (!string.IsNullOrWhiteSpace(choice?.Message?.ReasoningContent))
        {
            sb.Append(CultureInfo.InvariantCulture,
                $" The model did emit {choice.Message.ReasoningContent!.Length} characters of reasoning_content but no answer.");
        }

        sb.Append(CultureInfo.InvariantCulture,
            $" If finish_reason is \"length\", or the completion tokens are close to Review:MaxOutputTokens (currently {_maxOutputTokens}), raise that setting — reasoning models need noticeably more room than the answer alone would suggest.");

        return sb.ToString();
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    private sealed class ChatCompletionsResponse
    {
        [JsonPropertyName("choices")]
        public List<ChatChoice>? Choices { get; set; }

        [JsonPropertyName("usage")]
        public ChatUsage? Usage { get; set; }
    }

    private sealed class ChatChoice
    {
        [JsonPropertyName("message")]
        public ChatMessage? Message { get; set; }

        [JsonPropertyName("finish_reason")]
        public string? FinishReason { get; set; }
    }

    private sealed class ChatMessage
    {
        [JsonPropertyName("content")]
        public string? Content { get; set; }

        // Emitted by reasoning models (GLM, DeepSeek, ...) alongside an empty
        // "content" when they run out of output budget mid-thought.
        [JsonPropertyName("reasoning_content")]
        public string? ReasoningContent { get; set; }
    }

    private sealed class StreamChunk
    {
        [JsonPropertyName("choices")]
        public List<StreamChoice>? Choices { get; set; }

        [JsonPropertyName("usage")]
        public ChatUsage? Usage { get; set; }
    }

    private sealed class StreamChoice
    {
        [JsonPropertyName("delta")]
        public ChatMessage? Delta { get; set; }

        [JsonPropertyName("finish_reason")]
        public string? FinishReason { get; set; }
    }

    private sealed class ChatUsage
    {
        [JsonPropertyName("prompt_tokens")]
        public int PromptTokens { get; set; }

        [JsonPropertyName("completion_tokens")]
        public int CompletionTokens { get; set; }

        [JsonPropertyName("completion_tokens_details")]
        public ChatUsageDetails? CompletionTokensDetails { get; set; }
    }

    private sealed class ChatUsageDetails
    {
        [JsonPropertyName("reasoning_tokens")]
        public int? ReasoningTokens { get; set; }
    }
}
