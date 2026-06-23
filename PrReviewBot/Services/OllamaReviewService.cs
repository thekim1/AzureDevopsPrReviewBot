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

    private static readonly JsonSerializerOptions _responseJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public OllamaReviewService(OllamaSettings settings)
    {
        _settings = settings;

        HttpClientHandler handler = new();
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

    public async Task<List<ReviewComment>> ReviewPullRequestAsync(PullRequestInfo pr)
    {
        string prompt = ReviewHelpers.BuildReviewPrompt(pr);

        object requestBody = new
        {
            model = _settings.Model,
            system = ReviewHelpers.SystemPrompt,
            prompt,
            stream = false,
            format = "json"
        };

        StringContent content = new(
            JsonSerializer.Serialize(requestBody),
            Encoding.UTF8,
            "application/json");

        HttpResponseMessage response = await _httpClient.PostAsync("generate", content);
        response.EnsureSuccessStatusCode();

        string responseJson = await response.Content.ReadAsStringAsync();

        OllamaGenerateResponse? result =
            JsonSerializer.Deserialize<OllamaGenerateResponse>(responseJson, _responseJsonOptions);

        string text = result?.Response ?? "";
        return ReviewHelpers.ParseReviewResponse(text);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    private sealed class OllamaGenerateResponse
    {
        [JsonPropertyName("response")]
        public string? Response { get; set; }
    }
}
