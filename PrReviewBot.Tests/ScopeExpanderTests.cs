using PrReviewBot.Services;

namespace PrReviewBot.Tests;

public class ScopeExpanderTests
{
    // Zero-based index of the first line equal to `text` once trimmed.
    private static int Find(string[] lines, string text) => Array.FindIndex(lines, l => l.Trim() == text);

    private static string[] Expanded(string[] lines, string changedLine, int maxSpan = 200)
    {
        int i = Find(lines, changedLine);
        Assert.True(i >= 0, $"test setup: '{changedLine}' not found");
        (int first, int last) = ScopeExpander.Expand(lines, i, i, maxSpan);
        return lines[first..(last + 1)];
    }

    [Fact]
    public void AllmanMethodIsWidenedToSignatureAndClosingBrace()
    {
        string[] lines =
        [
            "public class A",
            "{",
            "    [HttpGet]",
            "    public void M()",
            "    {",
            "        var x = 1;",
            "        Use(x);",
            "    }",
            "",
            "    public void Other() { }",
            "}"
        ];

        string[] shown = Expanded(lines, "Use(x);");

        Assert.Equal("    [HttpGet]", shown[0]);
        Assert.Equal("    }", shown[^1]);
        Assert.DoesNotContain(shown, l => l.Contains("Other", StringComparison.Ordinal));
        Assert.DoesNotContain(shown, l => l.Contains("class A", StringComparison.Ordinal));
    }

    [Fact]
    public void NestedBlockWidensOutwardUntilTheMethod()
    {
        string[] lines =
        [
            "class A",
            "{",
            "    void M()",
            "    {",
            "        if (ok)",
            "        {",
            "            Do();",
            "        }",
            "        After();",
            "    }",
            "}"
        ];

        string[] shown = Expanded(lines, "Do();");

        Assert.Equal("    void M()", shown[0]);
        Assert.Equal("    }", shown[^1]);
    }

    [Fact]
    public void MultiLineSignatureIsIncludedFromItsFirstLine()
    {
        string[] lines =
        [
            "class A",
            "{",
            "    public Task<int> M(",
            "        int a,",
            "        int b)",
            "    {",
            "        return Sum(a, b);",
            "    }",
            "}"
        ];

        Assert.Equal("    public Task<int> M(", Expanded(lines, "return Sum(a, b);")[0]);
    }

    [Fact]
    public void KAndRTypeScriptWithWrappedParameters()
    {
        string[] lines =
        [
            "export async function load(",
            "  id: string,",
            "  token: string",
            "): Promise<Blob> {",
            "  const response = await fetch(url);",
            "  return response.blob();",
            "}",
            "",
            "export const other = 1;"
        ];

        string[] shown = Expanded(lines, "const response = await fetch(url);");

        Assert.Equal("export async function load(", shown[0]);
        Assert.Equal("}", shown[^1]);
        Assert.DoesNotContain(shown, l => l.Contains("other", StringComparison.Ordinal));
    }

    [Fact]
    public void VueTemplateWidensToTheEnclosingElement()
    {
        string[] lines =
        [
            "<template>",
            "  <div class=\"modal\">",
            "    <iframe",
            "      v-if=\"isPdf\"",
            "      :src=\"url\"",
            "    />",
            "  </div>",
            "</template>"
        ];

        string[] shown = Expanded(lines, ":src=\"url\"", maxSpan: 6);

        Assert.Equal("  <div class=\"modal\">", shown[0]);
        Assert.Equal("  </div>", shown[^1]);
    }

    [Fact]
    public void StopsWhenTheBlockWouldExceedTheBudget()
    {
        string[] lines = ["void M()", "{", .. Enumerable.Range(0, 50).Select(i => $"    Step{i}();"), "}"];

        (int first, int last) = ScopeExpander.Expand(lines, 20, 20, maxSpan: 10);

        Assert.Equal((20, 20), (first, last));
    }

    [Fact]
    public void NeverWidensIntoATypeEvenWithAttributesAboveIt()
    {
        string[] lines =
        [
            "[ApiController]",
            "public class Controller",
            "{",
            "    private int _count;",
            "}"
        ];

        (int first, int last) = ScopeExpander.Expand(lines, 3, 3, maxSpan: 100);

        Assert.Equal((3, 3), (first, last));
    }

    [Fact]
    public void TopLevelCodeIsLeftAlone()
    {
        string[] lines = ["import x from 'y';", "const a = 1;", "const b = 2;"];

        Assert.Equal((1, 1), ScopeExpander.Expand(lines, 1, 1, 100));
    }

    [Fact]
    public void TabsCountAsIndentation()
    {
        string[] lines = ["func()", "{", "\tif (x)", "\t{", "\t\tgo();", "\t}", "}"];

        string[] shown = Expanded(lines, "go();");

        Assert.Equal("func()", shown[0]);
        Assert.Equal("}", shown[^1]);
    }
}
