using System.Text.Json;
using PrReviewBot.Config;
using PrReviewBot.Services;
using static PrReviewBot.Tests.TestData;

namespace PrReviewBot.Tests;

public class ThinkingLevelTests
{
    private static JsonElement OllamaBody(string think)
        => JsonSerializer.SerializeToElement(OllamaReviewService.BuildRequestBody(
            new OllamaSettings { Model = "glm-5.3-flash:cloud", Think = think }, "prompt", stream: true, maxOutputTokens: 100));

    [Fact]
    public void OllamaSendsALevelNameAsAString()
        => Assert.Equal("low", OllamaBody("low").GetProperty("think").GetString());

    [Theory]
    [InlineData("false", false)]
    [InlineData("True", true)]
    public void OllamaSendsTrueAndFalseAsBooleans(string setting, bool expected)
        => Assert.Equal(expected, OllamaBody(setting).GetProperty("think").GetBoolean());

    [Fact]
    public void OllamaLeavesThinkOutWhenUnset()
        => Assert.False(OllamaBody("  ").TryGetProperty("think", out _));

    [Fact]
    public async Task BifrostSendsReasoningEffortWhenSet()
    {
        FakeStreamingHandler handler = new("data: [DONE]\n\n", keepOpen: false);
        using BifrostReviewService service = new(
            new BifrostSettings { BaseUrl = "http://bifrost.test/v1", ReasoningEffort = "low" },
            new ReviewSettings { ShowThinking = true }, handler);
        PrReviewBot.Models.PullRequestInfo pr = Pr(File("/a.cs"));

        await Assert.ThrowsAsync<ReviewFailedException>(
            () => service.ReviewPullRequestAsync(pr, pr.ChangedFiles, cancellationToken: TestContext.Current.CancellationToken));

        using JsonDocument sent = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal("low", sent.RootElement.GetProperty("reasoning_effort").GetString());
    }

    [Fact]
    public async Task BifrostLeavesReasoningEffortOutWhenUnset()
    {
        FakeStreamingHandler handler = new("data: [DONE]\n\n", keepOpen: false);
        using BifrostReviewService service = new(
            new BifrostSettings { BaseUrl = "http://bifrost.test/v1" }, new ReviewSettings { ShowThinking = true }, handler);
        PrReviewBot.Models.PullRequestInfo pr = Pr(File("/a.cs"));

        await Assert.ThrowsAsync<ReviewFailedException>(
            () => service.ReviewPullRequestAsync(pr, pr.ChangedFiles, cancellationToken: TestContext.Current.CancellationToken));

        using JsonDocument sent = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.False(sent.RootElement.TryGetProperty("reasoning_effort", out _));
    }
}
