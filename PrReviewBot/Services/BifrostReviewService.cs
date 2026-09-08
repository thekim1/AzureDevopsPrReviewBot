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

    private static readonly JsonSerializerOptions _responseJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public BifrostReviewService(BifrostSettings settings)
    {
        _settings = settings;

        HttpClientHandler handler = new();
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

    public async Task<List<ReviewComment>> ReviewPullRequestAsync(PullRequestInfo pr)
    {
        string prompt = ReviewHelpers.BuildReviewPrompt(pr);

        object requestBody = new
        {
            model = _settings.Model,
            messages = new object[]
            {
                new { role = "system", content = ReviewHelpers.SystemPrompt },
                new { role = "user", content = prompt }
            },
            max_tokens = 4096,
            stream = false
        };

        StringContent content = new(
            JsonSerializer.Serialize(requestBody),
            Encoding.UTF8,
            "application/json");

        HttpResponseMessage response = await _httpClient.PostAsync("chat/completions", content);

        string responseJson = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Bifrost request failed with status {(int)response.StatusCode} ({response.ReasonPhrase}). " +
                $"Model: {_settings.Model}. Response body: {responseJson}");
        }

        ChatCompletionsResponse? result =
            JsonSerializer.Deserialize<ChatCompletionsResponse>(responseJson, _responseJsonOptions);

        string text = result?.Choices?.FirstOrDefault()?.Message?.Content ?? "";
        return ReviewHelpers.ParseReviewResponse(text);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    private sealed class ChatCompletionsResponse
    {
        [JsonPropertyName("choices")]
        public List<ChatChoice>? Choices { get; set; }
    }

    private sealed class ChatChoice
    {
        [JsonPropertyName("message")]
        public ChatMessage? Message { get; set; }
    }

    private sealed class ChatMessage
    {
        [JsonPropertyName("content")]
        public string? Content { get; set; }
    }
}
