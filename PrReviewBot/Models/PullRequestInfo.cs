namespace PrReviewBot.Models;

public class PullRequestInfo
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Author { get; set; } = "";
    public string SourceBranch { get; set; } = "";
    public string TargetBranch { get; set; } = "";
    public string RepositoryName { get; set; } = "";
    public string RepositoryId { get; set; } = "";
    public List<ChangedFile> ChangedFiles { get; set; } = [];
    public string Url { get; set; } = "";
    // True when the current user is a reviewer on the PR.
    public bool IsAssignedToMe { get; set; }
    // True when at least one reviewer is assigned to the PR.
    public bool HasReviewers { get; set; }
    // Existing comments already made on the PR (fetched lazily). Passed to
    // the LLM as context so it can avoid duplicating or contradicting feedback.
    public List<PrComment> ExistingComments { get; set; } = [];
}

public class ChangedFile
{
    public string Path { get; set; } = "";
    public string ChangeType { get; set; } = "";
    public string Diff { get; set; } = "";
    public string FileType => Path.Split('.').LastOrDefault() ?? "";
}

// An existing comment already made on the PR by someone else. Sent to the
// LLM as context so it can avoid repeating feedback or build on prior input.
public class PrComment
{
    public string Author { get; set; } = "";
    public string Content { get; set; } = "";
    public string? FilePath { get; set; }
    public int? LineNumber { get; set; }
}
