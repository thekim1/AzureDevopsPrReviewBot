using Microsoft.Extensions.Configuration;
using PrReviewBot.Config;
using PrReviewBot.Models;
using PrReviewBot.Services;
using Spectre.Console;
using Spectre.Console.Rendering;

// Without this a Windows console keeps its legacy code page, and every glyph
// outside it — ✓, ●, →, emoji — comes out as "V" or "?". Must run before
// Spectre first inspects the console.
Console.OutputEncoding = System.Text.Encoding.UTF8;

// The released zip ships appsettings.json next to the executable, so that copy
// is the one the user edits and it has to be found however the tool is
// launched. A second, optional file in the working directory stays supported so
// one install can be pointed at different organisations from different folders.
IConfigurationRoot config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile(
        Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json"), optional: true)
    .AddUserSecrets<Program>(optional: true)
    .AddEnvironmentVariables()
    .Build();

AppSettings settings = config.Get<AppSettings>() ?? new AppSettings();

AzureDevOpsService devOpsService = new(settings.AzureDevOps, settings.Review);
ReviewOutputService outputService = new();
ReviewValidator validator = new(settings.Review);

string provider = AnsiConsole.Prompt(
    new SelectionPrompt<string>()
        .Title("Which review provider do you want to use?")
        .AddChoices("Claude", "Ollama", "Bifrost"));

string providerModel = provider switch
{
    "Ollama" => settings.Ollama.Model,
    "Bifrost" => settings.Bifrost.Model,
    _ => settings.Claude.Model
};

IReviewService reviewService = provider switch
{
    "Ollama" => new OllamaReviewService(settings.Ollama, settings.Review),
    "Bifrost" => new BifrostReviewService(settings.Bifrost, settings.Review),
    _ => new ClaudeReviewService(settings.Claude, settings.Review)
};
PullRequestReviewer reviewer = new(reviewService, settings.Review);
AnsiConsole.MarkupLine($"[grey]Using {provider} ({Markup.Escape(providerModel)})[/]");

AnsiConsole.Write(new FigletText("PR Review Bot").Color(Color.Blue));

// Fetch PRs (assigned to you + others) across the project
List<PullRequestInfo> pullRequests = [];
await ConsoleStatus.RunAsync("Fetching pull requests from Azure DevOps...", async ctx =>
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

// Review each PR. The next PR's changes are fetched while the current one is
// being reviewed, so after the first PR there is usually nothing to wait for.
Prefetcher<PullRequestInfo> prefetcher = new(toReview, devOpsService.LoadPrChangesAsync);

for (int prIndex = 0; prIndex < toReview.Count; prIndex++)
{
    PullRequestInfo pr = toReview[prIndex];
    AnsiConsole.WriteLine();

    List<PrReviewBot.Models.ReviewComment> comments = [];
    PullRequestReviewResult? reviewResult = null;
    // Usually already loaded in the background while the previous PR was
    // reviewed; a spinner is only shown when there is actually a wait.
    Task loading = prefetcher.LoadAsync(prIndex);
    if (!loading.IsCompleted)
    {
        using (DeferredConsole.Hold())
        {
            await ConsoleStatus.RunAsync($"Fetching changes for PR #{pr.Id}...", _ => loading);
        }
    }

    await loading;

    if (pr.ChangedFiles.Count == 0)
    {
        AnsiConsole.MarkupLine(
            $"[yellow]PR #{pr.Id} has no reviewable source changes — skipping (no API call made).[/]");
        foreach (SkippedFile skipped in pr.SkippedFiles)
        {
            AnsiConsole.MarkupLine($"[grey]  {Markup.Escape(skipped.Path)} — {Markup.Escape(skipped.Reason)}[/]");
        }

        continue;
    }

    // Warnings raised while the display is up — by the provider, or by the
    // next PR loading in the background — are printed once it is gone rather
    // than drawn into the middle of it.
    using (DeferredConsole.Hold())
    {
        if (settings.Review.ShowThinking)
        {
            // Watch the model work. The reasoning counter climbing while the
            // answer stays empty is what an exhausted output budget looks like
            // before it fails, so this is diagnostic as well as reassuring.
            LiveReviewDisplay display = new(settings.Review);
            AnsiConsole.MarkupLine($"[grey]Reviewing PR #{pr.Id} with {provider} — live:[/]");

            await AnsiConsole.Live(new Table().AddColumn(" "))
                .AutoClear(false)
                // The display already sizes itself to the window; this is the
                // backstop for a window resized smaller mid-review.
                .Overflow(VerticalOverflow.Ellipsis)
                .Cropping(VerticalOverflowCropping.Bottom)
                .StartAsync(async liveCtx =>
                {
                    using CancellationTokenSource cts = new();
                    Task painter = RepaintAsync(liveCtx, display, cts.Token);

                    try
                    {
                        reviewResult = await reviewer.ReviewAsync(
                            pr,
                            progress: null,
                            liveProgress: new SynchronousProgress(display.Report),
                            onBatchesPlanned: display.Plan,
                            onBatchCompleted: display.Complete);
                        comments = reviewResult.Comments;
                    }
                    finally
                    {
                        await cts.CancelAsync();
                        await painter;

                        // One final paint so the finished state is what remains
                        // on screen.
                        display.TryRender(out IRenderable final);
                        liveCtx.UpdateTarget(final);
                        liveCtx.Refresh();
                    }
                });
        }
        else
        {
            await ConsoleStatus.RunAsync($"Reviewing PR #{pr.Id} with {provider}...", async ctx =>
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

                    try
                    {
                        // The PR is reviewed in batches; a batch that fails costs
                        // its own files, not the whole review.
                        reviewResult = await reviewer.ReviewAsync(pr, status => ctx.Status(status));
                        comments = reviewResult.Comments;
                    }
                    finally
                    {
                        await cts.CancelAsync();
                        await tickerTask;
                    }
                });
        }
    }

    if (reviewResult is not null && reviewResult.Failures.Count != 0)
    {
        foreach (BatchFailure batchFailure in reviewResult.Failures)
        {
            AnsiConsole.MarkupLine(
                $"\n[red]✗ Part of PR #{pr.Id} could not be reviewed:[/] {Markup.Escape(batchFailure.Exception.Message)}");

            foreach (string path in batchFailure.FilePaths)
            {
                AnsiConsole.MarkupLine($"[grey]  not reviewed: {Markup.Escape(path)}[/]");
            }

            if (!string.IsNullOrWhiteSpace(batchFailure.Exception.RawResponse))
            {
                string dumpPath = outputService.SaveRawResponse(pr, batchFailure.Exception.RawResponse);
                AnsiConsole.MarkupLine($"[grey]  raw provider response: {Markup.Escape(dumpPath)}[/]");
            }
        }

        if (!reviewResult.AnySucceeded)
        {
            AnsiConsole.MarkupLine(
                "[yellow]This PR was NOT reviewed — do not read the absence of comments as approval.[/]");
            continue;
        }

        AnsiConsole.MarkupLine(
            $"[yellow]Partial review: {reviewResult.UnreviewedFiles.Count} of {pr.ChangedFiles.Count} file(s) were not reviewed.[/]");
    }

    // Discard findings the diff cannot support and re-anchor the rest, before
    // anything is shown, saved, or posted.
    ReviewValidationResult validation = validator.Validate(pr, comments);
    comments = validation.Kept;
    ReviewOutputService.DisplayValidationSummary(validation);

    List<string> unreviewedFiles = reviewResult?.UnreviewedFiles ?? [];
    ReviewOutputService.DisplayReview(pr, comments, unreviewedFiles);

    string savedPath = outputService.SaveReviewToFile(pr, comments, unreviewedFiles);
    AnsiConsole.MarkupLine($"[grey]📝 Review saved to: {Markup.Escape(savedPath)}[/]");

    // Only confident findings on PR-changed lines are worth a reviewer's
    // attention in Azure DevOps. Everything else stays in the saved report.
    List<ReviewComment> postable =
    [
        .. comments.Where(c => !c.IsAdditionalObservation
                               && c.Confidence >= settings.Review.MinConfidenceToPost)
    ];

    if (postComments && comments.Count != 0 && postable.Count != comments.Count)
    {
        AnsiConsole.MarkupLine(
            $"[grey]{comments.Count - postable.Count} finding(s) kept in the report only "
            + $"(low confidence or outside the PR's changes).[/]");
    }

    if (postComments && postable.Count != 0)
    {
        await ConsoleStatus.RunAsync($"Posting comments for PR #{pr.Id}...", async ctx =>
            {
                foreach (ReviewComment comment in postable)
                {
                    string formatted = ReviewOutputService.FormatCommentForAzureDevOps(comment);

                    // Anchor against the same iteration and file-change the
                    // diff came from, so the line number is not re-mapped.
                    ChangedFile? file = pr.ChangedFiles.Find(f => f.Path == comment.FilePath);

                    await devOpsService.PostCommentToPrAsync(
                        pr.RepositoryId, pr.Id, comment.FilePath,
                        comment.LineNumber, formatted,
                        pr.LatestIterationId, file?.ChangeTrackingId ?? 0);
                }
            });
        AnsiConsole.MarkupLine("[green]✓ Comments posted![/]");
    }
}

AnsiConsole.MarkupLine("\n[bold green]Review complete![/]");

// Repaints the live table on a timer rather than on every token. Streaming
// delivers thousands of tiny deltas; redrawing on each one would spend more
// time in the console than in the review.
static Task RepaintAsync(LiveDisplayContext liveCtx, LiveReviewDisplay display, CancellationToken cancellationToken) =>
    Task.Run(async () =>
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (display.TryRender(out IRenderable table))
                {
                    liveCtx.UpdateTarget(table);
                }

                await Task.Delay(120, cancellationToken);
            }
        }
        catch (OperationCanceledException) { }
    }, cancellationToken);

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


// Reports synchronously on the calling thread. Progress<T> would marshal each
// update through the synchronization context, reordering a stream that is only
// meaningful in order.
internal sealed class SynchronousProgress(Action<ReviewProgress> handler) : IProgress<ReviewProgress>
{
    public void Report(ReviewProgress value) => handler(value);
}
