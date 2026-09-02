using System.Reflection;
using FluentAssertions;
using LearnStack.SharedKernel.Tenancy;
using MediatR;
using Xunit;

namespace LearnStack.Tests.Architecture;

/// <summary>
/// What the step-4 authority ceiling admits: which request types may run without a
/// tenant, and which may be reached by a caller LearnStack has not authenticated.
/// </summary>
/// <remarks>
/// <para>
/// Both markers are deliberate holes in a control, and the value of each rule is that
/// it counts its hole. A hole nobody counts becomes a hole everybody uses.
/// </para>
/// <para>
/// <b>They are vacuous today, and the vacuity is real rather than a formality.</b>
/// There is not one production request type in the solution —
/// <c>ProvisionTenantCommand</c> arrives in Packet 7 step 9 and the first
/// <c>[PublicSurface]</c> types in Phase 02d — so the marked sets are empty and the
/// set-membership legs pass over nothing. What is <b>not</b> vacuous is the reverse
/// direction: the enumerated table in Standards 04 must not name a type that carries
/// no marker, and both attributes must keep the shape the pipeline reads them with.
/// Each leg below says which of the two it is.
/// </para>
/// </remarks>
public sealed class RequestSurfaceTests
{
    [Fact]
    public void AllowsUnresolvedTenantContext_Only_On_Provisioning_Commands()
    {
        // Leg 1 — the set, vacuous today. Named provisioning and platform-admin
        // commands only. The allow-list is a literal rather than a pattern on purpose:
        // "any command whose name ends in ProvisionCommand" is a rule an author
        // satisfies by naming, which is not a decision anybody reviewed.
        var marked = RequestTypes()
            .Where(type => type.IsDefined(typeof(AllowsUnresolvedTenantContextAttribute), inherit: false))
            .Select(type => type.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        marked.Should().BeEquivalentTo(
            PermittedUnresolved,
            "the marker exempts a request from the tenant-context assertion at pipeline "
            + "step 4, and the whole value of the set is that adding to it is an edit "
            + "someone reviews");

        // Leg 2 — live now. The behavior reads the attribute with inherit: false, so a
        // change to Inherited = true would silently stop the reader following it: not
        // an error, not a widening, just a marker the pipeline no longer sees.
        AttributeShape(typeof(AllowsUnresolvedTenantContextAttribute))
            .Should().Be((AttributeTargets.Class, false, false));
    }

    [Fact]
    public void PublicSurface_Marker_Set_Is_Enumerated()
    {
        // Leg 1 — vacuous today: every marked type appears in the Standards 04 table.
        var marked = RequestTypes()
            .Where(type => type.IsDefined(typeof(PublicSurfaceAttribute), inherit: false))
            .Select(type => type.Name)
            .ToList();

        var enumerated = EnumeratedPublicSurface();

        marked.Should().BeSubsetOf(enumerated,
            "a type reachable anonymously that the table does not name is a public "
            + "endpoint nobody reviewed");

        // Leg 2 — LIVE, and the half that is not vacuous. The table may not name a
        // type that carries no marker: an entry there reads as a reviewed decision,
        // and one with no attribute behind it is a decision the pipeline never
        // enforces. It ships empty, so this asserts emptiness — and stops being an
        // assertion about nothing the moment Phase 02d writes the first row.
        enumerated.Should().BeSubsetOf(marked,
            "the table is the enumeration of what carries the marker, not a wish list");

        // Leg 3 — live. Same shape guard as its sibling.
        AttributeShape(typeof(PublicSurfaceAttribute))
            .Should().Be((AttributeTargets.Class, false, false));
    }

    [Fact]
    public void PublicSurface_Requests_Are_Never_ReadSensitive()
    {
        // Vacuous on BOTH sides today, and that is stated in the catalogue rather than
        // left for a reader to infer from a green run: the marked set is empty, and
        // there is no audit catalogue in code to classify anything against — IAuditStore
        // and the operation catalogue arrive in Packet 9. What this can assert now is
        // the emptiness that makes the claim trivially true, so that the day a marked
        // type appears without the cross-check existing, this rule is the thing that
        // has to be revisited rather than the thing that quietly passed.
        var marked = RequestTypes()
            .Where(type => type.IsDefined(typeof(PublicSurfaceAttribute), inherit: false))
            .ToList();

        marked.Should().BeEmpty(
            "no [PublicSurface] type may be MUST-class read-sensitive — an anonymous "
            + "GET would become a durable standalone audit write — and until Packet 9 "
            + "ships the audit catalogue there is nothing to check that against, so a "
            + "marked type arriving before it is a decision this rule must be told about");
    }

    /// <summary>
    /// The literal set of request types permitted to run before a tenant is resolved.
    /// </summary>
    /// <remarks>
    /// Empty, because no production request type exists yet. <c>ProvisionTenantCommand</c>
    /// is the first, in Packet 7 step 9, per
    /// <see href="../../../docs/decisions/0042-tenant-provisioning-cross-aggregate-transaction.md">ADR-0042</see>.
    /// </remarks>
    private static readonly string[] PermittedUnresolved = [];

    /// <summary>
    /// The request types named in
    /// <see href="../../../docs/standards/04-api-design.md">Standards 04 § Public surface</see>.
    /// </summary>
    /// <remarks>
    /// Reads the table's data rows rather than parsing Markdown generally: the section
    /// holds one table under a fixed heading, and a general parser for a set the corpus
    /// says ships empty would be more machinery than the rule it serves.
    /// </remarks>
    private static List<string> EnumeratedPublicSurface()
    {
        var path = Path.Combine(RepositoryPaths.RepoRoot(), "docs", "standards", "04-api-design.md");
        var lines = File.ReadAllLines(path);

        var start = Array.FindIndex(lines, line =>
            line.StartsWith("### Public surface", StringComparison.Ordinal));
        start.Should().BeGreaterThan(-1, "the section this rule reads must exist");

        var header = Array.FindIndex(lines, start, line =>
            line.StartsWith("| Request type", StringComparison.Ordinal));
        header.Should().BeGreaterThan(-1, "the enumeration is a table with a named first column");

        var rows = new List<string>();

        // Skip the header and its separator; stop at the first line that is not a row.
        for (var at = header + 2; at < lines.Length && lines[at].StartsWith('|'); at++)
        {
            var cell = lines[at].Split('|', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault()?.Trim().Trim('`');

            if (!string.IsNullOrWhiteSpace(cell))
            {
                rows.Add(cell);
            }
        }

        return rows;
    }

    /// <summary>
    /// Every concrete MediatR request type in the production assemblies.
    /// </summary>
    /// <remarks>
    /// <b>Fails loudly on an assembly it cannot load</b>, rather than dropping it. The
    /// shipped precedent filters unloadable assemblies away, which turns "I could not
    /// read this code" into "this code is clean" — the one failure mode a rule counting
    /// a security hole cannot afford.
    /// </remarks>
    private static List<Type> RequestTypes()
    {
        var types = new List<Type>();

        foreach (var name in ProductionAssemblies)
        {
            var assembly = Assembly.Load(name);

            types.AddRange(assembly.GetTypes()
                .Where(type => type is { IsAbstract: false, IsInterface: false })
                .Where(type => type.GetInterfaces().Any(contract =>
                    contract == typeof(IBaseRequest)
                    || (contract.IsGenericType
                        && contract.GetGenericTypeDefinition() == typeof(IRequest<>)))));
        }

        return types;
    }

    private static (AttributeTargets Targets, bool Inherited, bool AllowMultiple) AttributeShape(
        Type attribute)
    {
        var usage = attribute.GetCustomAttribute<AttributeUsageAttribute>();
        usage.Should().NotBeNull($"{attribute.Name} must declare its usage explicitly");

        return (usage!.ValidOn, usage.Inherited, usage.AllowMultiple);
    }

    private static readonly string[] ProductionAssemblies =
    [
        "LearnStack.Application",
        "LearnStack.SharedKernel",
        "LearnStack.Modules.Tenancy.Application",
        "LearnStack.Modules.Identity.Application",
        "LearnStack.Modules.Customization.Application",
        "LearnStack.Modules.Audit.Application",
        "LearnStack.Modules.Content.Application",
        "LearnStack.Modules.Media.Application",
        "LearnStack.Modules.Education.Application",
    ];
}
