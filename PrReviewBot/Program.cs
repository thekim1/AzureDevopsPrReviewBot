using Microsoft.Extensions.Configuration;
using PrReviewBot.Config;
using PrReviewBot.Models;
using PrReviewBot.Services;
using Spectre.Console;

IConfigurationRoot config = new ConfigurationBuilder()
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json", optional: false)
    .AddUserSecrets<Program>(optional: true)
    .AddEnvironmentVariables()
    .Build();

AppSettings settings = config.Get<AppSettings>() ?? new AppSettings();

AzureDevOpsService devOpsService = new AzureDevOpsService(settings.AzureDevOps);
ClaudeReviewService claudeService = new ClaudeReviewService(settings.Claude);
ReviewOutputService outputService = new ReviewOutputService();

AnsiConsole.Write(new FigletText("PR Review Bot").Color(Color.Blue));

// Fetch PRs
List<PullRequestInfo> pullRequests = new List<PullRequestInfo>();
await AnsiConsole.Status()
    .Spinner(Spinner.Known.Dots)
    .StartAsync("Fetching pull requests from Azure DevOps...", async ctx =>
    {
        pullRequests = await devOpsService.GetAssignedPullRequestsAsync();
        ctx.Status($"Found {pullRequests.Count} PR(s) assigned to you");
    });

if (!pullRequests!.Any())
{
    AnsiConsole.MarkupLine("[green]No pull requests assigned to you right now.[/]");
    return;
}

AnsiConsole.MarkupLine($"\n[bold]Found {pullRequests.Count} PR(s) to review:[/]");
for (int i = 0; i < pullRequests.Count; i++)
    AnsiConsole.MarkupLine($"  {i + 1}. [cyan]{pullRequests[i].Title}[/] by {pullRequests[i].Author}");

// Let user pick which PR(s) to review
List<string> choices = pullRequests.Select(pr => $"#{pr.Id}: {pr.Title}").ToList();
choices.Insert(0, "All PRs");

List<string> selected = AnsiConsole.Prompt(
    new MultiSelectionPrompt<string>()
        .Title("\nWhich PRs do you want to review?")
        .AddChoices(choices));

List<PullRequestInfo> toReview = selected.Contains("All PRs")
    ? pullRequests
    : pullRequests.Where(pr => selected.Any(s => s.StartsWith($"#{pr.Id}:"))).ToList();

// Review each PR
foreach (PullRequestInfo pr in toReview)
{
    AnsiConsole.WriteLine();

    List<PrReviewBot.Models.ReviewComment> comments = new();
    await AnsiConsole.Status()
        .Spinner(Spinner.Known.Dots)
        .StartAsync($"Reviewing PR #{pr.Id} with Claude...", async ctx =>
        {
            comments = await claudeService.ReviewPullRequestAsync(pr);
        });

    outputService.DisplayReview(pr, comments);

    var savedPath = outputService.SaveReviewToFile(pr, comments);
    AnsiConsole.MarkupLine($"[grey]📝 Review saved to: {savedPath}[/]");

    // Ask to post comments
    if (comments.Any())
    {
        var postComments = AnsiConsole.Confirm("\nPost these comments to Azure DevOps?", defaultValue: false);
        if (postComments)
        {
            await AnsiConsole.Status()
                .StartAsync("Posting comments...", async ctx =>
                {
                    foreach (ReviewComment comment in comments)
                    {
                        var formatted = outputService.FormatCommentForAzureDevOps(comment);
                        await devOpsService.PostCommentToPrAsync(
                            pr.RepositoryId, pr.Id, comment.FilePath,
                            comment.LineNumber, formatted);
                    }
                });
            AnsiConsole.MarkupLine("[green]✓ Comments posted![/]");
        }
    }
}

AnsiConsole.MarkupLine("\n[bold green]Review complete![/]");