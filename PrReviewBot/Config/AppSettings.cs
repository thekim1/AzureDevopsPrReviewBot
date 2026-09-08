namespace PrReviewBot.Config;

public class AppSettings
{
    public AzureDevOpsSettings AzureDevOps { get; set; } = new();
    public ClaudeSettings Claude { get; set; } = new();
    public OllamaSettings Ollama { get; set; } = new();
    public BifrostSettings Bifrost { get; set; } = new();
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

public class OllamaSettings
{
    public string ApiKey { get; set; } = "";
    public string BaseUrl { get; set; } = "https://ollama.com/api";
    public string Model { get; set; } = "glm-5.2:cloud";
}

// Bifrost LLM gateway (https://github.com/maximhq/bifrost). Uses its
// OpenAI-compatible endpoint, so any provider behind the gateway works.
// Models are addressed as "provider/model", e.g. "openai/gpt-4o" or
// "anthropic/claude-sonnet-4-5".
public class BifrostSettings
{
    public string ApiKey { get; set; } = "";
    public string BaseUrl { get; set; } = "http://localhost:8080/v1";
    public string Model { get; set; } = "anthropic/claude-sonnet-4-5";
}
