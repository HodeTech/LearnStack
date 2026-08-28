namespace LearnStack.Tests.Architecture;

/// <summary>
/// Comment stripping for the source scans, shared because both scanning rules
/// need the same answer and a second implementation is a second answer.
/// </summary>
internal static class SourceText
{
    /// <summary>Strips line and block comments, leaving literals alone.</summary>
    /// <remarks>
    /// Literal state is tracked, because a <c>//</c> inside a string is not a
    /// comment: <c>"https://…"</c> would otherwise truncate the rest of that
    /// line, and anything after it — including a banned literal — would go
    /// unseen. A false negative in a rule that guards the tenancy edge is worth
    /// the twenty lines.
    /// </remarks>
    public static string WithoutComments(string source)
    {
        var kept = new System.Text.StringBuilder(source.Length);
        var i = 0;

        while (i < source.Length)
        {
            var c = source[i];

            if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                while (i < source.Length && source[i] != '\n')
                {
                    i++;
                }

                continue;
            }

            if (c == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                var close = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = close < 0 ? source.Length : close + 2;
                continue;
            }

            // A literal is copied through verbatim, so nothing inside it is read
            // as a comment marker — and nothing inside it is lost, so a banned
            // literal written as a string is still found.
            if (c is '"' or '\'')
            {
                i = CopyLiteral(source, i, kept);
                continue;
            }

            kept.Append(c);
            i++;
        }

        return kept.ToString();
    }

    /// <summary>Copies one string or character literal and returns the index after it.</summary>
    /// <remarks>
    /// Three shapes, because C# has three and they terminate differently: a
    /// normal literal ends at an unescaped quote, a verbatim one (<c>@"…"</c>)
    /// escapes a quote by doubling it, and a raw one opens with a <b>run</b> of
    /// three or more quotes and closes only on a run of the same length. Reading
    /// a raw literal's first quote as its terminator puts the scanner back
    /// inside code while it is still inside a string — which is how a <c>//</c>
    /// there would swallow the rest of the line again.
    /// </remarks>
    public static int CopyLiteral(string source, int start, System.Text.StringBuilder kept)
    {
        var quote = source[start];

        if (quote == '"')
        {
            var opening = 0;
            while (start + opening < source.Length && source[start + opening] == '"')
            {
                opening++;
            }

            if (opening >= 3)
            {
                return CopyRawLiteral(source, start, opening, kept);
            }
        }

        var verbatim = start > 0 && source[start - 1] == '@';
        var i = start;

        kept.Append(source[i]);
        i++;

        while (i < source.Length)
        {
            var c = source[i];

            if (!verbatim && c == '\\' && i + 1 < source.Length)
            {
                kept.Append(c).Append(source[i + 1]);
                i += 2;
                continue;
            }

            if (c == quote)
            {
                if (verbatim && i + 1 < source.Length && source[i + 1] == quote)
                {
                    kept.Append(c).Append(source[i + 1]);
                    i += 2;
                    continue;
                }

                kept.Append(c);
                return i + 1;
            }

            // An unterminated non-verbatim literal cannot span a line; bailing
            // keeps a malformed file from swallowing the rest of the scan.
            if (!verbatim && c == '\n')
            {
                return i;
            }

            kept.Append(c);
            i++;
        }

        return i;
    }

    /// <summary>Copies a raw string literal, closing only on a run of the opening length.</summary>
    private static int CopyRawLiteral(
        string source, int start, int opening, System.Text.StringBuilder kept)
    {
        var i = start;
        kept.Append(source, i, opening);
        i += opening;

        while (i < source.Length)
        {
            if (source[i] != '"')
            {
                kept.Append(source[i]);
                i++;
                continue;
            }

            var run = 0;
            while (i + run < source.Length && source[i + run] == '"')
            {
                run++;
            }

            kept.Append(source, i, run);
            i += run;

            if (run >= opening)
            {
                return i;
            }
        }

        return i;
    }

    /// <summary>
    /// The source with every whitespace character removed, so a banned literal
    /// cannot hide behind a line break.
    /// </summary>
    public static string WithoutWhitespace(string value) =>
        string.Concat(value.Where(character => !char.IsWhiteSpace(character)));
}
