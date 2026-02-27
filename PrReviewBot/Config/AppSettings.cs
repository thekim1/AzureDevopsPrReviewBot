namespace PrReviewBot.Config;

public class AppSettings
{
    public AzureDevOpsSettings AzureDevOps { get; set; } = new();
    public ClaudeSettings Claude { get; set; } = new();
}

public class AzureDevOpsSettings
{
    public string OrganizationUrl { get; set; } = "";
    public string Project { get; set; } = "";
    public string PersonalAccessToken { get; set; } = "";
    public string ReviewerEmail { get; set; } = "";
}

public class ClaudeSettings
{
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "claude-sonnet-4-6";
}
