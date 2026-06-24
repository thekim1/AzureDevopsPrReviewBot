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

AzureDevOpsService devOpsService = new(settings.AzureDevOps);
ReviewOutputService outputService = new();

string provider = AnsiConsole.Prompt(
    new SelectionPrompt<string>()
        .Title("Which review provider do you want to use?")
        .AddChoices("Claude", "Ollama"));

IReviewService reviewService = provider switch
{
    "Ollama" => new OllamaReviewService(settings.Ollama),
    _ => new ClaudeReviewService(settings.Claude)
};
AnsiConsole.MarkupLine($"[grey]Using {provider} ({Markup.Escape(provider == "Ollama" ? settings.Ollama.Model : settings.Claude.Model)})[/]");

AnsiConsole.Write(new FigletText("PR Review Bot").Color(Color.Blue));

// Fetch PRs (assigned to you + others) across the project
List<PullRequestInfo> pullRequests = [];
await AnsiConsole.Status()
    .Spinner(Spinner.Known.Dots)
    .StartAsync("Fetching pull requests from Azure DevOps...", async ctx =>
    {
        string[] messages =
        [
            "Still fetching... Azure DevOps is having a think 🤔",
            "Maybe Azure is waiting for someone to approve its own PR?",
            "Our connection runs on hamster power. The hamster is tired. 🐹",
            "Azure DevOps appears to be writing a novel...",
            "Have you tried turning it off and on again?",
            "The server is consulting the oracle. Please hold.",
            "Fetching... this is fine. Everything is fine. 🔥",
            "Almost there (probably). We think. No promises.",
        ];

        using CancellationTokenSource cts = new();
        Task tickerTask = StartFunnyTickerAsync(ctx, messages, cts.Token);

        pullRequests = await devOpsService.GetAllActivePullRequestsAsync();
        await cts.CancelAsync();
        await tickerTask;
        ctx.Status($"Found {pullRequests.Count} active PR(s)");
    });

List<PullRequestInfo> assignedToMe = [.. pullRequests.Where(pr => pr.IsAssignedToMe)];
List<PullRequestInfo> otherPrs = [.. pullRequests.Where(pr => !pr.IsAssignedToMe && pr.HasReviewers)];

if (pullRequests.Count == 0)
{
    AnsiConsole.MarkupLine("[green]No active pull requests found in this project.[/]");
    return;
}

AnsiConsole.MarkupLine($"\n[bold]Found {assignedToMe.Count} PR(s) assigned to you, and {otherPrs.Count} other active PR(s).[/]");

// Let user pick which PR(s) to review — grouped by assignment.
// Use a stable, unique label per PR (id + repo) and map it back to the PR.
Dictionary<string, PullRequestInfo> choiceToPr = new(StringComparer.Ordinal);

MultiSelectionPrompt<string> selectionPrompt = new();
selectionPrompt.Title("\nWhich PRs do you want to review?");
selectionPrompt.InstructionsText("[grey]Press [blue]<space>[/] to toggle a PR, [blue]<space>[/] on a group to toggle all in it, [green]<enter>[/] to accept.[/]");

void AddGroup(string groupTitle, List<PullRequestInfo> prs)
{
    if (prs.Count == 0)
    {
        selectionPrompt.AddChoice($"{groupTitle}: (none)");
        return;
    }

    List<string> labels = [];
    foreach (PullRequestInfo pr in prs)
    {
        string label = $"#{pr.Id} ({Markup.Escape(pr.RepositoryName)}): {Markup.Escape(pr.Title)} — {Markup.Escape(pr.Author)}";
        choiceToPr[label] = pr;
        labels.Add(label);
    }
    selectionPrompt.AddChoiceGroup(groupTitle, labels);
}

AddGroup("Assigned to you", assignedToMe);
AddGroup("Other PRs", otherPrs);

List<string> selected = await AnsiConsole.PromptAsync(selectionPrompt);

List<PullRequestInfo> toReview = [];
foreach (string s in selected)
{
    if (choiceToPr.TryGetValue(s, out PullRequestInfo? pr))
    {
        toReview.Add(pr);
    }
}

if (toReview.Count == 0)
{
    AnsiConsole.MarkupLine("[yellow]No PRs selected. Exiting.[/]");
    return;
}

// Ask up-front whether to post comments back to Azure DevOps.
// Reviews are always saved to disk regardless of this choice.
bool postComments = await AnsiConsole.ConfirmAsync(
    "\nPost review comments back to Azure DevOps after each review? (Reviews are always saved to disk.)",
    defaultValue: false);

// Review each PR
foreach (PullRequestInfo pr in toReview)
{
    AnsiConsole.WriteLine();

    List<PrReviewBot.Models.ReviewComment> comments = [];
    await AnsiConsole.Status()
        .Spinner(Spinner.Known.Dots)
        .StartAsync($"Fetching changes for PR #{pr.Id}...", async ctx =>
        {
            await devOpsService.LoadPrChangesAsync(pr);
        });

    await AnsiConsole.Status()
        .Spinner(Spinner.Known.Dots)
        .StartAsync($"Reviewing PR #{pr.Id} with {provider}...", async ctx =>
        {
            string[] messages =
            [
                "The AI is staring at your diff very intensely 👀",
                "Consulting the silicon oracle...",
                "Generating opinions at scale 🤖",
                "The model is judging your variable names. Quietly.",
                "Running on vibes and matrix multiplications.",
                "Almost done — the AI is just adding dramatic tension.",
                "Cross-referencing your code with every Stack Overflow post ever 📚",
                "The tokens are flowing. Wisdom may follow.",
            ];

            using CancellationTokenSource cts = new();
            Task tickerTask = StartFunnyTickerAsync(ctx, messages, cts.Token);

            comments = await reviewService.ReviewPullRequestAsync(pr);
            await cts.CancelAsync();
            await tickerTask;
        });

    ReviewOutputService.DisplayReview(pr, comments);

    string savedPath = outputService.SaveReviewToFile(pr, comments);
    AnsiConsole.MarkupLine($"[grey]📝 Review saved to: {Markup.Escape(savedPath)}[/]");

    if (postComments && comments.Count != 0)
    {
        await AnsiConsole.Status()
            .StartAsync($"Posting comments for PR #{pr.Id}...", async ctx =>
            {
                foreach (ReviewComment comment in comments.Where(c => !c.IsAdditionalObservation))
                {
                    string formatted = ReviewOutputService.FormatCommentForAzureDevOps(comment);
                    await devOpsService.PostCommentToPrAsync(
                        pr.RepositoryId, pr.Id, comment.FilePath,
                        comment.LineNumber, formatted);
                }
            });
        AnsiConsole.MarkupLine("[green]✓ Comments posted![/]");
    }
}

AnsiConsole.MarkupLine("\n[bold green]Review complete![/]");

// Starts a background ticker that updates ctx.Status with rotating funny
// messages after a 5-second grace period. Stops cleanly when cancellationToken
// is cancelled. Await the returned Task after cancelling to ensure it has exited.
static Task StartFunnyTickerAsync(StatusContext ctx, string[] messages, CancellationToken cancellationToken) =>
    Task.Run(async () =>
    {
        try
        {
            await Task.Delay(5000, cancellationToken);
            int i = 0;
            while (!cancellationToken.IsCancellationRequested)
            {
                ctx.Status(messages[i % messages.Length]);
                i++;
                await Task.Delay(5000, cancellationToken);
            }
        }
        catch (OperationCanceledException) { }
    }, cancellationToken);
