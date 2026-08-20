using FluentAssertions;
using Xunit;

namespace LearnStack.Tests.Architecture;

/// <summary>
/// The tenancy-edge rules
/// <see href="../../../docs/decisions/0036-tenant-resolution-trusted-inputs.md">ADR-0036</see>
/// assigns to Packet 4, catalogued in
/// <see href="../../../docs/standards/21-architecture-tests-catalogue.md">Standards 21
/// § Tenant and organization resolution</see>.
/// </summary>
/// <remarks>
/// <para>
/// These are <b>source scans</b>, and that is a deliberate choice rather than a
/// shortcut. Each rule is about a symbol not appearing outside one file — a
/// reflection or NetArchTest form would have to observe a call that has no
/// consumer yet, because the resolver that will read these values does not land
/// until Packet 7. A scan can hold the line from the day the symbol exists,
/// which is the day it can first be used wrongly.
/// </para>
/// <para>
/// Comment lines are skipped. Every one of these files argues in prose about the
/// very literal it is forbidden to use, so scanning them raw would fail on the
/// documentation that explains the rule.
/// </para>
/// </remarks>
public sealed class TenancyConventionTests
{
    [Fact]
    public void Effective_Host_Computed_In_One_Place()
    {
        // EffectiveHostAccessor decides what host a request is for — trusted-hop
        // predicate, header, normalization, all of it. A second reader of
        // Request.Host is a second answer, and the one that skips the accessor
        // is the one that skips the trust check.
        Offenders(
                except: Path.Combine("Tenancy", "EffectiveHostAccessor.cs"),
                banned: ["Request.Host", "GetDisplayUrl", "GetEncodedUrl", "X-Forwarded-Host"])
            .Should().BeEmpty(
                "only EffectiveHostAccessor reads a request host (ADR-0036 § Effective "
                + "host and the trusted hop)");
    }

    [Fact]
    public void Tenant_Headers_Are_Never_A_Resolution_Source()
    {
        // The header is an assertion the API compares against its own answer.
        // The moment a second file reads it, the question "did this select a
        // tenant, or check one?" stops having one answer.
        Offenders(
                except: Path.Combine("Tenancy", "TenantAssertionMiddleware.cs"),
                banned: ["X-Tenant-Id", "X-Organization-Id"])
            .Should().BeEmpty(
                "X-Tenant-Id and X-Organization-Id are compared, never resolved from "
                + "(ADR-0036 § The reconciliation matrix)");
    }

    [Fact]
    public void Assertion_Recorder_Is_The_Only_Mismatch_Writer()
    {
        // A rejected assertion is a security event. One writer means one place
        // to change when Packet 9 swaps the logging recorder for the auditing
        // one — and one place that decides the metric's label cardinality.
        Offenders(
                except: Path.Combine("Tenancy", "LoggingTenantAssertionRecorder.cs"),
                banned: [
                    "learnstack_tenant_assertion_mismatch_total",
                    "learnstack_tenant_assertion_unresolved_total",
                ])
            .Should().BeEmpty(
                "only an ITenantAssertionRecorder writes a tenant-assertion mismatch "
                + "(ADR-0036 § Recording a rejected assertion)");
    }

    [Fact]
    public void Assertion_Budget_Does_Not_Depend_On_ICacheService()
    {
        // A tripwire, like Forwarded_Headers_Are_Not_Wired. ICacheService does
        // not exist yet — Packet 5 ships the port — so this cannot yet be a
        // dependency check. It holds the line from now, because the anonymous
        // burst counter is exactly the thing someone will reach for a cache to
        // share across instances, and a cache outage must not decide whether a
        // MUST-class security event is recorded.
        Offenders(except: null, banned: ["ICacheService"], folder: "Tenancy")
            .Should().BeEmpty(
                "the anonymous-burst counters resolve no ICacheService "
                + "(ADR-0036 § Recording a rejected assertion)");
    }

    /// <summary>
    /// Files under <c>LearnStack.Api</c> that mention a banned literal in code.
    /// </summary>
    /// <remarks>
    /// Whitespace is removed from both the source and the literal before the
    /// search, so a violation cannot hide behind a line break — measured, the
    /// first version of this scan was per-line, and a <c>context.Request</c>
    /// whose <c>.Host.Value</c> sat on the next line passed it clean. Comments
    /// are removed first, because every file here argues in prose about the very
    /// literal it is forbidden to write.
    /// </remarks>
    private static List<string> Offenders(
        string? except, IReadOnlyList<string> banned, string? folder = null)
    {
        var root = Path.Combine(RepositoryPaths.BackendSrc(), "LearnStack.Api");
        if (folder is not null)
        {
            root = Path.Combine(root, folder);
        }

        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file);

            if (relative.Split(Path.DirectorySeparatorChar) is var segments
                && (segments.Contains("obj") || segments.Contains("bin")))
            {
                continue;
            }

            // Compared as a path, not a bare name: two files may share a name in
            // different folders, and excluding both because one is exempt is how
            // a rule quietly stops covering half of what it names.
            if (except is not null
                && relative.Equals(except, StringComparison.Ordinal))
            {
                continue;
            }

            var code = WithoutWhitespace(WithoutComments(File.ReadAllText(file)));

            foreach (var literal in banned)
            {
                if (code.Contains(WithoutWhitespace(literal), StringComparison.Ordinal))
                {
                    offenders.Add($"{relative} contains '{literal}'");
                }
            }
        }

        return offenders;
    }

    /// <summary>Strips line and block comments, leaving literals alone.</summary>
    /// <remarks>
    /// Literal state is tracked, because a <c>//</c> inside a string is not a
    /// comment: <c>"https://…"</c> would otherwise truncate the rest of that
    /// line, and anything after it — including a banned literal — would go
    /// unseen. A false negative in a rule that guards the tenancy edge is worth
    /// the twenty lines.
    /// </remarks>
    private static string WithoutComments(string source)
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
    private static int CopyLiteral(string source, int start, System.Text.StringBuilder kept)
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

    private static string WithoutWhitespace(string value) =>
        string.Concat(value.Where(character => !char.IsWhiteSpace(character)));
}
