namespace PrReviewBot.Models;

public class ReviewComment
{
    public string FilePath { get; set; } = "";
    public int? LineNumber { get; set; }
    public CommentSeverity Severity { get; set; }
    public string Issue { get; set; } = "";
    public string Suggestion { get; set; } = "";
    public string? CodeExample { get; set; }
    public bool IsAdditionalObservation { get; set; }
    public string? Evidence { get; set; }
    public int Confidence { get; set; } = 3;
}

public enum CommentSeverity
{
    Info,
    Warning,
    Critical
}
