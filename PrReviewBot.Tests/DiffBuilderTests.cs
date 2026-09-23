using PrReviewBot.Config;
using PrReviewBot.Services;

namespace PrReviewBot.Tests;

public class DiffBuilderTests
{
    // Plain hunks only, so the tests below pin down the hunk format itself.
    private static readonly ReviewSettings Settings = new()
    {
        ContextLines = 2,
        HunkMergeDistance = 1,
        WholeFileMaxLines = 0,
        ScopeMaxLines = 0,
        FileHeaderLines = 0
    };

    private static string Lines(int count, Func<int, string>? line = null)
        => string.Join("\n", Enumerable.Range(1, count).Select(i => line?.Invoke(i) ?? $"line {i}"));

    [Fact]
    public void NumbersAddedLinesInTheNewFileAndRemovedLinesInTheOld()
    {
        string oldText = Lines(10);
        string newText = Lines(10, i => i == 5 ? "changed" : $"line {i}");

        DiffBuilder.DiffResult diff = DiffBuilder.Build(oldText, newText, Settings);

        Assert.True(diff.Success);
        Assert.Contains("-    5 | line 5", diff.Text);
        Assert.Contains("+    5 | changed", diff.Text);
        Assert.Contains("     3 | line 3", diff.Text);
        Assert.DoesNotContain("line 1\n", diff.Text);
        Assert.Contains("@@ ... 2 unchanged line(s) not shown ... @@", diff.Text);
        Assert.Equal(10, diff.NewFileLineCount);
    }

    [Fact]
    public void NewFileIsAllAdditions()
    {
        DiffBuilder.DiffResult diff = DiffBuilder.Build("", "a\nb", Settings);

        Assert.Contains("+    1 | a", diff.Text);
        Assert.Contains("+    2 | b", diff.Text);
    }

    [Fact]
    public void IdenticalContentReportsNoChanges()
        => Assert.Equal("(no textual changes in this file)", DiffBuilder.Build("a\nb", "a\r\nb", Settings).Text);

    [Fact]
    public void RefusesFilesTooLargeToDiff()
    {
        DiffBuilder.DiffResult diff = DiffBuilder.Build(Lines(100), Lines(100), new ReviewSettings { MaxDiffCells = 50 });

        Assert.False(diff.Success);
        Assert.StartsWith("too large to diff", diff.FailureReason);
    }

    [Fact]
    public void MarksTruncationExplicitly()
    {
        DiffBuilder.DiffResult diff = DiffBuilder.Build("", Lines(50), new ReviewSettings { MaxDiffLinesPerFile = 10 });

        Assert.True(diff.IsTruncated);
        Assert.Contains(DiffBuilder.TruncationMarker, diff.Text);
    }

    [Fact]
    public void DetectsBinaryContent()
    {
        Assert.True(DiffBuilder.LooksBinary("PNG\0\0data"));
        Assert.False(DiffBuilder.LooksBinary("plain text"));
    }

    [Fact]
    public void SmallFileIsSentWhole()
    {
        string oldText = Lines(40);
        string newText = Lines(40, i => i == 20 ? "changed" : $"line {i}");

        DiffBuilder.DiffResult diff = DiffBuilder.Build(oldText, newText, new ReviewSettings { WholeFileMaxLines = 300 });

        Assert.True(diff.IsWholeFile);
        Assert.Contains("     1 | line 1", diff.Text);
        Assert.Contains("    40 | line 40", diff.Text);
        Assert.DoesNotContain("not shown", diff.Text);
    }

    [Fact]
    public void LargeFileIsNotSentWhole()
    {
        DiffBuilder.DiffResult diff = DiffBuilder.Build(
            Lines(400), Lines(400, i => i == 200 ? "changed" : $"line {i}"),
            new ReviewSettings { WholeFileMaxLines = 300, ScopeMaxLines = 0, FileHeaderLines = 0, ContextLines = 2 });

        Assert.False(diff.IsWholeFile);
        Assert.Contains("not shown", diff.Text);
    }

    // 200 lines: usings, a class, and a long method whose middle changes.
    private static string CSharpFile(Func<int, string?>? replace = null)
    {
        List<string> lines =
        [
            "using System;",
            "using System.IO;",
            "",
            "namespace Demo;",
            "",
            "public class Worker",
            "{",
            "    private readonly Stream _stream;",
            "",
        ];

        for (int m = 0; m < 6; m++)
        {
            lines.Add($"    public void Method{m}()");
            lines.Add("    {");
            for (int b = 0; b < 25; b++)
            {
                lines.Add($"        Step(\"m{m} b{b}\");");
            }

            lines.Add("    }");
            lines.Add("");
        }

        lines.Add("}");

        return string.Join("\n", lines.Select((l, i) => replace?.Invoke(i + 1) ?? l));
    }

    private static readonly ReviewSettings Widening = new()
    {
        WholeFileMaxLines = 50,
        ContextLines = 2,
        HunkMergeDistance = 1,
        ScopeMaxLines = 120,
        FileHeaderLines = 0
    };

    [Fact]
    public void ChangeIsWidenedToItsWholeMethod()
    {
        string oldText = CSharpFile();
        string newText = CSharpFile(n => oldText.Split('\n')[n - 1] == "        Step(\"m2 b12\");" ? "        Step(\"changed\");" : null);

        DiffBuilder.DiffResult diff = DiffBuilder.Build(oldText, newText, Widening);

        Assert.Contains("public void Method2()", diff.Text);
        Assert.Contains("Step(\"m2 b0\");", diff.Text);
        Assert.Contains("Step(\"m2 b24\");", diff.Text);
        Assert.DoesNotContain("Method1()", diff.Text);
        Assert.DoesNotContain("Method3()", diff.Text);
        Assert.DoesNotContain("public class Worker", diff.Text);
    }

    [Fact]
    public void FileHeaderIsIncluded()
    {
        string oldText = CSharpFile();
        string newText = CSharpFile(n => oldText.Split('\n')[n - 1] == "        Step(\"m5 b12\");" ? "        Step(\"changed\");" : null);

        DiffBuilder.DiffResult diff = DiffBuilder.Build(oldText, newText, new ReviewSettings
        {
            WholeFileMaxLines = 50, ContextLines = 2, HunkMergeDistance = 1, ScopeMaxLines = 0, FileHeaderLines = 9
        });

        Assert.Contains("     1 | using System;", diff.Text);
        Assert.Contains("private readonly Stream _stream;", diff.Text);
        Assert.DoesNotContain("Method0()", diff.Text);
    }

    [Fact]
    public void VueHeaderStartsAtTheScriptBlock()
    {
        List<string> lines = ["<template>"];
        lines.AddRange(Enumerable.Range(0, 60).Select(i => $"  <div>{i}</div>"));
        lines.Add("</template>");
        lines.Add("<script setup lang=\"ts\">");
        lines.Add("import { ref } from 'vue';");
        lines.AddRange(Enumerable.Range(0, 60).Select(i => $"const v{i} = ref({i});"));
        lines.Add("</script>");
        string oldText = string.Join("\n", lines);
        string newText = oldText.Replace("const v50 = ref(50);", "const v50 = ref(5000);", StringComparison.Ordinal);

        DiffBuilder.DiffResult diff = DiffBuilder.Build(oldText, newText, new ReviewSettings
        {
            WholeFileMaxLines = 50, ContextLines = 1, HunkMergeDistance = 0, ScopeMaxLines = 0, FileHeaderLines = 3
        }, "/src/Thing.vue");

        Assert.Contains("import { ref } from 'vue';", diff.Text);
        Assert.DoesNotContain("<div>0</div>", diff.Text);
    }

    [Fact]
    public void WideningThatWouldOverflowFallsBackToPlainHunks()
    {
        string oldText = CSharpFile();
        string[] oldLines = oldText.Split('\n');
        // A change in every method: widened, that is ~170 lines.
        string newText = CSharpFile(n => oldLines[n - 1].Contains("b12", StringComparison.Ordinal) ? "        Step(\"changed\");" : null);

        DiffBuilder.DiffResult diff = DiffBuilder.Build(oldText, newText, Widening);
        DiffBuilder.DiffResult capped = DiffBuilder.Build(oldText, newText, new ReviewSettings
        {
            WholeFileMaxLines = 50, ContextLines = 2, HunkMergeDistance = 1, ScopeMaxLines = 120,
            FileHeaderLines = 0, MaxDiffLinesPerFile = 60
        });

        Assert.Contains("Step(\"m0 b0\");", diff.Text);
        Assert.False(capped.IsTruncated);
        Assert.DoesNotContain("Step(\"m0 b0\");", capped.Text);
        Assert.Equal(6, capped.Text.Split('\n').Count(l => l.Contains("changed", StringComparison.Ordinal) && l.StartsWith('+')));
    }
}
