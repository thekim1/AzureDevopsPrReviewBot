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
    public List<ChangedFile> ChangedFiles { get; set; } = new();
    public string Url { get; set; } = "";
}

public class ChangedFile
{
    public string Path { get; set; } = "";
    public string ChangeType { get; set; } = "";
    public string Diff { get; set; } = "";
    public string FileType => Path.Split('.').LastOrDefault() ?? "";
}