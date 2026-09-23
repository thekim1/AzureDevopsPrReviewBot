using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PrReviewBot.Config;
using PrReviewBot.Models;

namespace PrReviewBot.Services;

// Reviews PRs using the Ollama HTTP API (https://docs.ollama.com/api).
// Works against a local Ollama instance (http://localhost:11434/api) or the
// ollama.com cloud (https://ollama.com/api) by changing BaseUrl. When an
// ApiKey is configured it is sent as a Bearer token for cloud authentication.
public sealed class OllamaReviewService : IReviewService, IDisposable
{
    private readonly OllamaSettings _settings;
    private readonly HttpClient _httpClient;
    private readonly int _maxOutputTokens;
    private readonly bool _scopeCommentsToBatch;
    private readonly bool _stream;
    private readonly TimeSpan _streamIdleTimeout;

    private static readonly JsonSerializerOptions _responseJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public OllamaReviewService(OllamaSettings settings, ReviewSettings? reviewSettings = null)
        : this(settings, reviewSettings, new HttpClientHandler())
    {
    }

    // Lets tests stand in for the server.
    internal OllamaReviewService(OllamaSettings settings, ReviewSettings? reviewSettings, HttpMessageHandler handler)
    {
        _settings = settings;
        ReviewSettings review = reviewSettings ?? new ReviewSettings();
        _maxOutputTokens = review.MaxOutputTokens;
        _scopeCommentsToBatch = review.ScopeExistingCommentsToBatch;
        _stream = review.ShowThinking;
        _streamIdleTimeout = TimeSpan.FromSeconds(Math.Max(1, review.StreamIdleTimeoutSeconds));

        _httpClient = new HttpClient(handler)
        {
            // Ensure trailing slash so relative paths (e.g. "generate") are appended
            // rather than replacing the last segment (RFC 3986 resolution rules).
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

        object requestBody = new
        {
            model = _settings.Model,
            system = ReviewHelpers.SystemPrompt,
            prompt,
            stream = _stream,
            // Constrains generation to valid JSON, which also stops a hybrid
            // reasoning model emitting a thinking block instead of an answer.
            format = "json",
            // Analytical task — randomness here produces invented findings.
            options = new { temperature = 0, num_predict = _maxOutputTokens }
        };

        StringContent content = new(
            JsonSerializer.Serialize(requestBody),
            Encoding.UTF8,
            "application/json");

        if (_stream)
        {
            return await ReviewStreamingAsync(content, progress, cancellationToken);
        }

        HttpResponseMessage response = await _httpClient.PostAsync("generate", content, cancellationToken);
        response.EnsureSuccessStatusCode();

        string responseJson = await response.Content.ReadAsStringAsync(cancellationToken);

        OllamaGenerateResponse? result =
            JsonSerializer.Deserialize<OllamaGenerateResponse>(responseJson, _responseJsonOptions);

        string text = result?.Response ?? "";

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ReviewFailedException(
                $"{_settings.Model} returned an empty response"
                + (string.IsNullOrWhiteSpace(result?.DoneReason) ? "" : $" (done_reason=\"{result.DoneReason}\")")
                + $". If it was cut off, raise Review:MaxOutputTokens (currently {_maxOutputTokens}).",
                responseJson);
        }

        return ReviewHelpers.ParseReviewResponse(text);
    }

    // Ollama streams newline-delimited JSON rather than server-sent events:
    // one object per line, each carrying the next fragment in `response` (and
    // `thinking` for a reasoning model), ending with `"done": true`.
    private async Task<List<ReviewComment>> ReviewStreamingAsync(
        StringContent content,
        IProgress<ReviewProgress>? progress,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, "generate") { Content = content };
        using HttpResponseMessage response = await _httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        StringBuilder answer = new();
        StringBuilder reasoning = new();
        string? doneReason = null;

        using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using StreamReader reader = new(stream);

        using StreamWatchdog watchdog = new(_streamIdleTimeout, cancellationToken);

        await foreach (string line in StreamLines.ReadAsync(reader, watchdog, cancellationToken))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            OllamaGenerateResponse? chunk;
            try
            {
                chunk = JsonSerializer.Deserialize<OllamaGenerateResponse>(line, _responseJsonOptions);
            }
            catch (JsonException)
            {
                // A partial frame is not worth abandoning a review over.
                continue;
            }

            if (chunk is null)
            {
                continue;
            }

            watchdog.Kick();

            doneReason ??= chunk.DoneReason;

            if (!string.IsNullOrEmpty(chunk.Thinking))
            {
                reasoning.Append(chunk.Thinking);
                progress?.Report(new ReviewProgress(
                    0, reasoning.Length, answer.Length, chunk.Thinking, IsAnswer: false));
            }

            if (!string.IsNullOrEmpty(chunk.Response))
            {
                answer.Append(chunk.Response);
                progress?.Report(new ReviewProgress(
                    0, reasoning.Length, answer.Length, chunk.Response, IsAnswer: true));
            }

            // The final object. Stop here rather than waiting for the server
            // to close the connection, which it is not obliged to do promptly.
            if (chunk.Done)
            {
                break;
            }
        }

        if (answer.Length == 0)
        {
            if (ReviewHelpers.TryFindCommentArray(reasoning.ToString(), out List<ReviewComment>? salvaged))
            {
                DeferredConsole.WriteLine(
                    $"Warning: {_settings.Model} produced no answer but one was recoverable from its "
                    + $"thinking ({salvaged.Count} finding(s)).");
                return salvaged;
            }

            throw new ReviewFailedException(
                $"{_settings.Model} streamed {reasoning.Length} characters of thinking but no answer"
                + (string.IsNullOrWhiteSpace(doneReason) ? "" : $" (done_reason=\"{doneReason}\")")
                + $". Raise Review:MaxOutputTokens (currently {_maxOutputTokens}).");
        }

        return ReviewHelpers.ParseReviewResponse(answer.ToString());
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    private sealed class OllamaGenerateResponse
    {
        [JsonPropertyName("response")]
        public string? Response { get; set; }

        [JsonPropertyName("done")]
        public bool Done { get; set; }

        [JsonPropertyName("done_reason")]
        public string? DoneReason { get; set; }

        // Reasoning models expose their thinking separately from the answer.
        [JsonPropertyName("thinking")]
        public string? Thinking { get; set; }
    }
}
