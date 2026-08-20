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
                except: "EffectiveHostAccessor.cs",
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
                except: "TenantAssertionMiddleware.cs",
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
                except: "LoggingTenantAssertionRecorder.cs",
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
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal)
                || (except is not null && Path.GetFileName(file) == except))
            {
                continue;
            }

            var lineNumber = 0;
            foreach (var line in File.ReadLines(file))
            {
                lineNumber++;
                var trimmed = line.TrimStart();

                // Prose, not code. These files explain the rule they obey, and
                // the explanation names the literal.
                if (trimmed.StartsWith("//", StringComparison.Ordinal)
                    || trimmed.StartsWith('*'))
                {
                    continue;
                }

                foreach (var literal in banned)
                {
                    if (line.Contains(literal, StringComparison.Ordinal))
                    {
                        offenders.Add(
                            $"{Path.GetFileName(file)}:{lineNumber} contains '{literal}'");
                    }
                }
            }
        }

        return offenders;
    }
}
