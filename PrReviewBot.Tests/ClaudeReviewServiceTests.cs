using Anthropic.Models.Messages;
using PrReviewBot.Services;

namespace PrReviewBot.Tests;

public class ClaudeReviewServiceTests
{
    [Fact]
    public void SystemPromptCarriesACacheBreakpoint()
    {
        MessageCreateParams p = ClaudeReviewService.BuildParameters("claude-sonnet-4-6", 1000, new ReviewPrompt("shared", "batch"));

        Assert.True(p.System!.TryPickTextBlockParams(out IReadOnlyList<TextBlockParam>? system));
        TextBlockParam block = Assert.Single(system);
        Assert.Equal(ReviewHelpers.SystemPrompt, block.Text);
        Assert.NotNull(block.CacheControl);
    }

    [Fact]
    public void CacheBreakpointSitsBetweenSharedPrefixAndBatch()
    {
        MessageCreateParams p = ClaudeReviewService.BuildParameters("claude-sonnet-4-6", 1000, new ReviewPrompt("shared", "batch"));

        List<TextBlockParam> blocks = UserTextBlocks(p);

        Assert.Equal(["shared", "batch"], blocks.Select(b => b.Text));
        Assert.NotNull(blocks[0].CacheControl);
        Assert.Null(blocks[1].CacheControl);
    }

    [Fact]
    public void EmptyBatchPartIsNotSentAsAnEmptyBlock()
    {
        MessageCreateParams p = ClaudeReviewService.BuildParameters("claude-sonnet-4-6", 1000, new ReviewPrompt("shared", "  "));

        Assert.Equal(["shared"], UserTextBlocks(p).Select(b => b.Text));
    }

    [Fact]
    public void PassesModelAndOutputBudgetThrough()
    {
        MessageCreateParams p = ClaudeReviewService.BuildParameters("claude-sonnet-4-6", 1234, new ReviewPrompt("s", "b"));

        Assert.Equal(1234, p.MaxTokens);
        Assert.Contains("claude-sonnet-4-6", p.Model.ToString());
    }

    private static List<TextBlockParam> UserTextBlocks(MessageCreateParams p)
    {
        MessageParam message = Assert.Single(p.Messages);
        Assert.True(message.Content.TryPickContentBlockParams(out IReadOnlyList<ContentBlockParam>? content));

        List<TextBlockParam> blocks = [];
        foreach (ContentBlockParam block in content)
        {
            Assert.True(block.TryPickText(out TextBlockParam? text));
            blocks.Add(text);
        }

        return blocks;
    }
}
