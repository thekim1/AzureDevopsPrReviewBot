namespace PrReviewBot.Models;

// A live update from a review request in flight.
//
// Reasoning models can spend minutes thinking before emitting a single
// character of answer. Without this the terminal shows a spinner and no way to
// tell work from a hang — and no way to see that the thinking, not the answer,
// is what consumes the output budget.
public sealed record ReviewProgress(
    int BatchIndex,
    int ReasoningChars,
    int AnswerChars,
    string? LatestText,
    bool IsAnswer,
    int PromptChars = 0)
{
    // Rough token estimate for display only; the authoritative count comes from
    // the provider's usage block.
    public int ApproxTokens => (ReasoningChars + AnswerChars) / 4;
}
