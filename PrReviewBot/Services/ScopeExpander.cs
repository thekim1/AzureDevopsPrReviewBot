namespace PrReviewBot.Services;

// Widens a changed region to the code block it sits in, so the model sees a
// whole method (or if-block, or template element) rather than a fixed window
// that stops wherever the line count runs out.
//
// A fixed window is the main source of "missing X" false positives: the null
// check, the dispose or the await the model says is missing is usually a few
// lines outside the window, in the same method.
//
// Works on indentation rather than parsing, so one implementation covers C#,
// TypeScript, Vue templates, JSON and YAML alike: a block is the run of lines
// indented deeper than its header. It widens one level at a time and stops
// before reaching a type or namespace declaration, or when the block would be
// larger than the budget. Anything it cannot make sense of simply leaves the
// region as it was.
internal static class ScopeExpander
{
    private const int TabWidth = 4;

    // Lines that open a type or namespace. Widening past one of these would
    // pull in the whole class, which is what the budget is there to prevent
    // and never what a reviewer needs to judge one change.
    private static readonly string[] TypeDeclarationWords =
    [
        "class ", "record ", "struct ", "interface ", "enum ", "namespace ", "module ", "impl "
    ];

    // Returns the widened [first, last] range, zero-based and inclusive.
    public static (int First, int Last) Expand(IReadOnlyList<string> lines, int first, int last, int maxSpan)
    {
        if (lines.Count == 0 || first < 0 || last >= lines.Count || first > last)
        {
            return (first, last);
        }

        (int First, int Last) current = (first, last);

        while (true)
        {
            (int First, int Last, int Declaration)? outer = EnclosingBlock(lines, current.First, current.Last);
            if (outer is not { } block)
            {
                return current;
            }

            if (IsTypeDeclaration(lines[block.Declaration]) || block.Last - block.First + 1 > maxSpan)
            {
                return current;
            }

            current = (block.First, block.Last);
        }
    }

    // The block immediately enclosing [first, last]: its header line (plus
    // any attributes or doc comments above it) through its closing line, and
    // the line where the declaration itself starts.
    private static (int First, int Last, int Declaration)? EnclosingBlock(IReadOnlyList<string> lines, int first, int last)
    {
        int level = MinIndent(lines, first, last);
        if (level <= 0)
        {
            return null;
        }

        int header = -1;
        for (int i = first - 1; i >= 0; i--)
        {
            if (!IsBlank(lines[i]) && Indent(lines[i]) < level)
            {
                header = i;
                break;
            }
        }

        if (header < 0)
        {
            return null;
        }

        int closer = -1;
        for (int i = last + 1; i < lines.Count; i++)
        {
            if (!IsBlank(lines[i]) && Indent(lines[i]) < level)
            {
                closer = i;
                break;
            }
        }

        // No closer means the block runs to the end of the file.
        if (closer < 0)
        {
            closer = lines.Count - 1;
        }

        // A closing line that belongs to the next construct rather than to
        // this block (Python, YAML, a following statement) is fine to include:
        // it is one line, and it is at the header's own level.
        int declaration = DeclarationStart(lines, header);
        return (WithLeadingAttributes(lines, declaration), closer, declaration);
    }

    // Walks up from the line that opened the block to where its declaration
    // actually starts. Handles an opening brace on its own line (Allman) and a
    // signature split over several lines.
    private static int DeclarationStart(IReadOnlyList<string> lines, int header)
    {
        int start = header;
        string trimmed = lines[start].Trim();

        // `{` alone, or `): Task<X> {` closing a multi-line signature: the
        // declaration begins further up, at this line's indentation or less.
        if (trimmed.StartsWith('{') || trimmed.StartsWith(')') || trimmed.StartsWith(']'))
        {
            int indent = Indent(lines[start]);
            for (int i = start - 1; i >= 0; i--)
            {
                if (IsBlank(lines[i]))
                {
                    continue;
                }

                if (Indent(lines[i]) <= indent)
                {
                    start = i;
                    break;
                }
            }
        }

        return start;
    }

    // Attributes, decorators and doc comments directly above a declaration
    // belong to it.
    private static int WithLeadingAttributes(IReadOnlyList<string> lines, int declaration)
    {
        int start = declaration;
        int declarationIndent = Indent(lines[start]);
        while (start > 0)
        {
            string above = lines[start - 1];
            string aboveTrimmed = above.TrimStart();
            bool belongs = !IsBlank(above)
                && Indent(above) == declarationIndent
                && (aboveTrimmed.StartsWith('[') || aboveTrimmed.StartsWith('@')
                    || aboveTrimmed.StartsWith("//", StringComparison.Ordinal)
                    || aboveTrimmed.StartsWith("/*", StringComparison.Ordinal)
                    || aboveTrimmed.StartsWith('*'));
            if (!belongs)
            {
                break;
            }

            start--;
        }

        return start;
    }

    private static bool IsTypeDeclaration(string line)
    {
        string trimmed = " " + line.Trim();
        return TypeDeclarationWords.Any(w => trimmed.Contains(" " + w, StringComparison.Ordinal));
    }

    private static int MinIndent(IReadOnlyList<string> lines, int first, int last)
    {
        int min = int.MaxValue;
        for (int i = first; i <= last; i++)
        {
            if (!IsBlank(lines[i]))
            {
                min = Math.Min(min, Indent(lines[i]));
            }
        }

        return min == int.MaxValue ? 0 : min;
    }

    internal static int Indent(string line)
    {
        int width = 0;
        foreach (char c in line)
        {
            if (c == ' ')
            {
                width++;
            }
            else if (c == '\t')
            {
                width += TabWidth;
            }
            else
            {
                break;
            }
        }

        return width;
    }

    private static bool IsBlank(string line) => string.IsNullOrWhiteSpace(line);
}
