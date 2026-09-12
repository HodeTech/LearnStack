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

            // `\/` is an escaped slash, and two of them in a row — `/https:\/\//` — used to
            // read as a comment and swallow the rest of the line. The frontend scans walk
            // TypeScript, where that pattern is ordinary, and a swallowed line hides whatever
            // followed it, including an export a rule is looking for.
            if (c == '/' && i > 0 && source[i - 1] == '\\')
            {
                kept.Append(c);
                i++;
                continue;
            }

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
            if (c is '"' or '\'' or '`')
            {
                i = CopyLiteral(source, i, kept);
                continue;
            }

            kept.Append(c);
            i++;
        }

        return kept.ToString();
    }

    /// <summary>Strips comments <b>and</b> the contents of every literal.</summary>
    /// <remarks>
    /// The scans that look for code — an attribute argument, a declaration — want the text a
    /// compiler sees, not the text a reader does. A rule that searches raw source finds its own
    /// test fixtures: <c>No_Architecture_Test_Is_Skippable</c> reported the very
    /// <c>[Fact(Skip = …)]</c> shapes its companion feeds it, which is why that rule used to
    /// exempt its own file — an exemption that also let the corpus guards be switched off.
    /// Dropping literal contents removes the need for the exemption and the hole with it.
    /// </remarks>
    public static string WithoutCommentsOrLiterals(string source)
    {
        var text = WithoutComments(source);
        var kept = new System.Text.StringBuilder(text.Length);
        var i = 0;

        while (i < text.Length)
        {
            if (text[i] is '"' or '\'' or '`')
            {
                var literal = new System.Text.StringBuilder();
                i = CopyLiteral(text, i, literal);

                // A placeholder rather than nothing, so `x = "a" + "b"` does not become `x = +`
                // and a scan for an empty argument list cannot be fooled by a deleted string.
                kept.Append("\"\"");
                continue;
            }

            kept.Append(text[i]);
            i++;
        }

        return kept.ToString();
    }

    /// <summary>Copies one string or character literal and returns the index after it.</summary>
    /// <remarks>
    /// <para>
    /// Three shapes, because C# has three and they terminate differently: a
    /// normal literal ends at an unescaped quote, a verbatim one (<c>@"…"</c>)
    /// escapes a quote by doubling it, and a raw one opens with a <b>run</b> of
    /// three or more quotes and closes only on a run of the same length. Reading
    /// a raw literal's first quote as its terminator puts the scanner back
    /// inside code while it is still inside a string — which is how a <c>//</c>
    /// there would swallow the rest of the line again.
    /// </para>
    /// <para>
    /// A backtick opens a fourth: TypeScript's template literal, which spans lines and carries
    /// <c>//</c> and <c>/*</c> as text — a URL in one truncated the rest of the file when this
    /// helper was pointed at the frontend.
    /// </para>
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

        // `@"…"`, `$@"…"` and `@$"…"` are all verbatim, and the prefix can be two characters:
        // reading only the character before the quote made `@$"a ""b"" c"` end at the doubled
        // quote, which puts the scanner back into code while it is still inside a string.
        var verbatim = quote == '`';

        for (var prefix = start - 1; prefix >= 0 && source[prefix] is '@' or '$'; prefix--)
        {
            verbatim |= source[prefix] == '@';
        }

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

            if (quote == '`' && c == '\\' && i + 1 < source.Length)
            {
                // A template literal escapes with a backslash, unlike a verbatim C# string.
                kept.Append(c).Append(source[i + 1]);
                i += 2;
                continue;
            }

            if (c == quote)
            {
                if (verbatim && quote != '`' && i + 1 < source.Length && source[i + 1] == quote)
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
