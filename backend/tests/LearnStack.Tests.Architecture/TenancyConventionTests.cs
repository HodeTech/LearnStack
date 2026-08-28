using System.Reflection;
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
/// Most of these are <b>source scans</b>, and that is a deliberate choice rather
/// than a shortcut. Each rule is about a symbol not appearing outside one file —
/// a reflection or NetArchTest form would have to observe a call that has no
/// consumer yet, because the resolver that will read these values does not land
/// until Packet 7. A scan can hold the line from the day the symbol exists,
/// which is the day it can first be used wrongly. Where the type a rule names
/// now exists, the rule adds a reflection check alongside the scan rather than
/// replacing it: the two catch different mistakes.
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
        // The anonymous burst counter is exactly the thing someone reaches for a
        // cache to share across instances, and a cache outage must not decide
        // whether a MUST-class security event is recorded.
        //
        // This began as a tripwire because ICacheService did not exist. Packet 5
        // ships it, so the rule is now what the catalogue promised: a real
        // dependency check as well as a text scan. Both are kept — reflection
        // catches an injected dependency, the scan catches a service-locator
        // resolve, and neither sees the other's case.
        Injectors().Should().BeEmpty(
            "no type under Tenancy takes an ICacheService "
            + "(ADR-0036 § Recording a rejected assertion)");

        Offenders(except: null, banned: ["ICacheService"], folder: "Tenancy")
            .Should().BeEmpty(
                "and none resolves one by name either "
                + "(ADR-0036 § Recording a rejected assertion)");
    }

    [Theory]
    // `required: true` for the type that exists — a rule accepting zero
    // declarations of `Organization` would stay green if the aggregate were
    // deleted, which is the vacuity this catalogue calls out generally.
    // `OrganizationBranding` is genuinely zero-or-one: it ships with the token
    // merge in Phase 06, and stating the rule now is what stops the first one
    // landing in the wrong module.
    [InlineData("Organization", true)]
    [InlineData("OrganizationBranding", false)]
    public void Organization_Aggregate_Declared_In_Tenancy_Domain(string typeName, bool required)
    {
        // ADR-0017's original sample put Organization in Identity; Amendment 2
        // moved it to Tenancy, and Identity now holds OrganizationId by value and
        // reads organization data through an application contract. A second
        // declaration is how the two drift back apart.
        //
        // The assembly set is ENUMERATED rather than discovered. A rule that
        // scanned loaded assemblies would silently skip the module nobody
        // referenced, and pass vacuously — the failure
        // Meta_NetArchTest_DetectsAPlantedViolation guards against generally.
        //
        // OrganizationBranding does not exist yet (Phase 06 ships it with the
        // token merge). The rule still runs: "exactly one, in Tenancy" is
        // satisfied by none as well as by one, and stating it now is what stops
        // the first one landing in the wrong module.
        var declarations = ModuleDomainAssemblies()
            .SelectMany(assembly => assembly.GetTypes()
                .Where(type => type.Name == typeName)
                .Select(type => $"{type.FullName} in {assembly.GetName().Name}"))
            .ToList();

        if (required)
        {
            declarations.Should().ContainSingle(
                $"{typeName} is declared exactly once across every module Domain "
                + "assembly (ADR-0017 Amendment 2)");
        }
        else
        {
            declarations.Should().HaveCountLessThanOrEqualTo(1,
                $"{typeName} does not exist yet; when it does it is declared once, "
                + "in Tenancy (ADR-0017 Amendment 2)");
        }

        declarations
            .Where(d => !d.EndsWith("LearnStack.Modules.Tenancy.Domain", StringComparison.Ordinal))
            .Should().BeEmpty($"and Tenancy is where {typeName} is declared");
    }

    /// <summary>
    /// The <c>Domain</c> assembly of every module, by name.
    /// </summary>
    private static IEnumerable<Assembly> ModuleDomainAssemblies() =>
        ModuleNames.Select(module => Assembly.Load($"LearnStack.Modules.{module}.Domain"));

    private static readonly string[] ModuleNames =
    [
        "Tenancy", "Identity", "Customization", "Audit", "Content", "Media", "Education",
    ];

    /// <summary>
    /// Types in the <c>LearnStack.Api.Tenancy</c> namespace that take an
    /// <see cref="LearnStack.SharedKernel.Caching.ICacheService"/> as a
    /// constructor parameter or hold one in a field.
    /// </summary>
    private static List<string> Injectors()
    {
        var cache = typeof(LearnStack.SharedKernel.Caching.ICacheService);

        return typeof(LearnStack.Api.Versioning.ApiVersioningExtensions).Assembly
            .GetTypes()
            .Where(type => type.Namespace?.StartsWith(
                "LearnStack.Api.Tenancy", StringComparison.Ordinal) == true)
            .Where(type =>
                type.GetConstructors().Any(constructor =>
                    constructor.GetParameters().Any(p => cache.IsAssignableFrom(p.ParameterType)))
                || type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic
                        | BindingFlags.Public)
                    .Any(field => cache.IsAssignableFrom(field.FieldType)))
            .Select(type => type.FullName!)
            .ToList();
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

            var code = SourceText.WithoutWhitespace(
                SourceText.WithoutComments(File.ReadAllText(file)));

            foreach (var literal in banned)
            {
                if (code.Contains(SourceText.WithoutWhitespace(literal), StringComparison.Ordinal))
                {
                    offenders.Add($"{relative} contains '{literal}'");
                }
            }
        }

        return offenders;
    }
}
