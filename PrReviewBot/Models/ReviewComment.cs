namespace PrReviewBot.Models;

public class ReviewComment
{
    public string FilePath { get; set; } = "";
    public int? LineNumber { get; set; }
    public CommentSeverity Severity { get; set; }
    public string Issue { get; set; } = "";
    public string Suggestion { get; set; } = "";
    public string? CodeExample { get; set; }
}

public enum CommentSeverity
{
    Info,
    Warning,
    Critical
}
