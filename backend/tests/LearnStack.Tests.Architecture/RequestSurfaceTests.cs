using System.Reflection;
using FluentAssertions;
using LearnStack.SharedKernel.Persistence;
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

    [Fact]
    public void Requests_Are_Never_Streamed()
    {
        // MediatR dispatches a stream request through IStreamPipelineBehavior<,>, and
        // this solution registers none — CanonicalBehaviorOrder registers only
        // IPipelineBehavior<,>. So a stream request reaches its handler with no
        // authority ceiling, no validation, no audit classification and no
        // TransactionBehavior, which means no SET LOCAL app.tenant_id. Row Level
        // Security keeps EF reads fail-closed, so the exposure is every effect that is
        // not an EF read: the outbox, the cache, provider adapters — each under a
        // context nothing checked.
        //
        // Banned rather than supported because nothing needs it and the alternative is
        // a second parallel pipeline. Zero stream usage exists today, which is exactly
        // what makes the ban cheap now and expensive later.
        var streamed = RequestTypes()
            .Where(type => type.GetInterfaces().Any(contract =>
                contract.IsGenericType
                && contract.GetGenericTypeDefinition() == typeof(IStreamRequest<>)))
            .Select(type => type.FullName!)
            .ToList();

        streamed.Should().BeEmpty(
            "a stream request runs with no pipeline behaviors at all — declare it "
            + "IRequest<Result<T>> and page with the cursor grammar instead");
    }

    [Fact]
    public void The_Request_Filter_Sees_Every_Shape_MediatR_Dispatches()
    {
        // The rules above are only as wide as this predicate, and today every set it
        // produces is empty — so deleting an arm changes nothing any of them assert.
        // Driven directly against local types for that reason: a detector with no
        // positive case is a detector nobody has run.
        //
        // The stream arm is the one that matters. Measured against MediatR 12.4.1,
        // typeof(IStreamRequest<string>).GetInterfaces() is empty and
        // IBaseRequest.IsAssignableFrom(IStreamRequest<string>) is false, so a stream
        // request is invisible to the ordinary IRequest<> test — which is exactly how
        // it came to be invisible to all four rules.
        IsRequest(typeof(ProbeQuery)).Should().BeTrue();
        IsRequest(typeof(ProbeStreamed)).Should().BeTrue(
            "a stream request satisfies neither IBaseRequest nor IRequest<>");
        IsRequest(typeof(ProbeNotARequest)).Should().BeFalse();

        typeof(IBaseRequest).IsAssignableFrom(typeof(IStreamRequest<string>))
            .Should().BeFalse("the measurement the stream arm exists for");
    }

    private sealed record ProbeQuery : IRequest<string>;

    private sealed record ProbeStreamed : IStreamRequest<string>;

    private sealed record ProbeNotARequest;

    [Fact]
    public void Cross_Aggregate_Writes_Are_Confined_To_Tenant_Provisioning()
    {
        // ADR-0042 sanctions ONE operation to write two aggregate roots in one
        // transaction, by enumeration rather than by principle — a tenant whose default
        // organization failed to commit is a tenant no request can serve, and a second
        // transaction is a window in which exactly that state exists.
        //
        // Counted by PORT TYPE, not by name. The catalogue registered this as a scan for
        // DbSet use in handlers, and under the shipped dependency rules that scan can
        // never fire: Application → Infrastructure is forbidden, so no handler can name a
        // DbSet at all. A rule at Implemented status that cannot fire is worse than one
        // at Registered, because the catalogue then claims coverage it does not have.
        // IAggregateWriteStore is a type, so renaming a port does not escape this.
        var offenders = ProductionAssemblies()
            .Select(Assembly.Load)
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type is { IsAbstract: false, IsInterface: false })
            .Where(type => type.GetInterfaces().Any(contract =>
                contract.IsGenericType
                && contract.GetGenericTypeDefinition() == typeof(IRequestHandler<,>)))
            .Where(type => type.GetConstructors()
                .Any(constructor => constructor.GetParameters()
                    .Count(parameter => WritesAnAggregate(parameter.ParameterType)) > 1))
            .Select(type => type.Name)
            .Distinct()
            .ToList();

        offenders.Should().BeEquivalentTo(
            ["ProvisionTenantCommandHandler"],
            "a handler taking two aggregate write ports writes across an aggregate "
            + "boundary in one transaction, which ADR-0042 sanctions for exactly one "
            + "operation — a second name here is a decision that needs its own record");
    }

    /// <summary>Whether a constructor parameter is a write port for some aggregate.</summary>
    /// <remarks>
    /// Walks the interface's own hierarchy rather than matching a name: the ports modules
    /// declare — <c>ITenantWriteStore</c>, <c>IOrganizationWriteStore</c> — derive from
    /// the generic, and it is the derivation the rule counts.
    /// </remarks>
    private static bool WritesAnAggregate(Type parameterType) =>
        IsAggregateWriteStore(parameterType)
        || parameterType.GetInterfaces().Any(IsAggregateWriteStore);

    private static bool IsAggregateWriteStore(Type type) =>
        type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IAggregateWriteStore<,>);

    [Fact]
    public void The_Sweep_Covers_Every_Production_Assembly()
    {
        // The rules above are only as wide as this. Asserted separately because a
        // narrowed sweep does not fail — it passes, over less code, which is the one
        // outcome a rule that counts a security hole cannot afford. Loading each is
        // part of the assertion: a project this test cannot load is one the test csproj
        // has not referenced, and the fix is the reference, not a smaller scan.
        var names = ProductionAssemblies().ToList();

        names.Should().HaveCountGreaterThan(30,
            "backend/src holds every module in four project shapes plus the core "
            + "assemblies; a count this far below that means the enumeration broke");

        names.Should().Contain("LearnStack.Modules.Tenancy.Application.Contracts",
            "add-mediatr-handler puts command records here, so this is where the first "
            + "marker carrier lands — and where the first version of this file did not look");

        var unloadable = names
            .Where(name =>
            {
                try
                {
                    Assembly.Load(name);
                    return false;
                }
                catch (FileNotFoundException)
                {
                    return true;
                }
            })
            .ToList();

        unloadable.Should().BeEmpty(
            "add a ProjectReference for each of these to the architecture test project");
    }

    /// <summary>
    /// The literal set of request types permitted to run before a tenant is resolved.
    /// </summary>
    /// <remarks>
    /// One entry. <c>ProvisionTenantCommand</c> creates the tenant it names, so it
    /// legitimately runs before any tenant is resolved — there is none until it succeeds.
    /// It is emphatically not anonymous: what gates it is Phase 03's permission check,
    /// and until then its only callers are the seeder and the tests. Adding a second
    /// entry is an edit somebody reviews, which is the whole value of the list; the rule
    /// went red the moment this command landed, which is the list working. See
    /// <see href="../../../docs/decisions/0042-tenant-provisioning-cross-aggregate-transaction.md">ADR-0042</see>.
    /// </remarks>
    private static readonly string[] PermittedUnresolved = ["ProvisionTenantCommand"];

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
    /// Every concrete MediatR request type in every production assembly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Derived from the tree, never a literal list.</b> The first version named nine
    /// assemblies. Measured: an identically marked request type failed all three rules
    /// from <c>LearnStack.Modules.Tenancy.Application</c> and passed all three from
    /// <c>LearnStack.Modules.Tenancy.Application.Contracts</c> — which is where
    /// <c>add-mediatr-handler</c> tells an author to put a command record, and therefore
    /// where <c>ProvisionTenantCommand</c>, the first type to carry either marker, is
    /// scheduled to land. <c>TenantContextBehavior</c> reads both markers off
    /// <c>typeof(TRequest)</c> and knows nothing about assembly lists, so the pipeline
    /// would have granted the widest surface it can grant while the rule whose whole job
    /// is to count that grant reported clean. This is the same failure the catalogue
    /// already names for a sibling rule: a rule whose job is the negative cannot be
    /// scoped to where the positives happen to live.
    /// </para>
    /// <para>
    /// <b>Fails loudly on an assembly it cannot load</b>, rather than dropping it — and
    /// not via <c>GetReferencedAssemblies</c>, which lists only assemblies whose types
    /// the compiler actually emitted a reference to and would therefore <i>shrink</i>
    /// the sweep. A project this test cannot load is a project the test csproj has not
    /// referenced, and the right outcome is a red build naming it, not a smaller scan.
    /// </para>
    /// </remarks>
    private static List<Type> RequestTypes()
    {
        var types = new List<Type>();

        foreach (var name in ProductionAssemblies())
        {
            var assembly = Assembly.Load(name);

            types.AddRange(assembly.GetTypes()
                .Where(type => type is { IsAbstract: false, IsInterface: false })
                .Where(IsRequest));
        }

        return types;
    }

    /// <summary>
    /// Whether <paramref name="type"/> is something MediatR will dispatch.
    /// </summary>
    /// <remarks>
    /// <c>IStreamRequest&lt;&gt;</c> is checked explicitly and is not redundant: measured
    /// against MediatR 12.4.1, <c>typeof(IStreamRequest&lt;string&gt;).GetInterfaces()</c>
    /// is empty and <c>IBaseRequest.IsAssignableFrom</c> is <c>false</c>, so a stream
    /// request is invisible to the ordinary test. It is worth catching here even though
    /// <c>Requests_Are_Never_Streamed</c> bans the shape outright — this rule counts a
    /// security hole, and counting it correctly must not depend on a second rule staying
    /// green.
    /// </remarks>
    private static bool IsRequest(Type type) =>
        type.GetInterfaces().Any(contract =>
            contract == typeof(IBaseRequest)
            || (contract.IsGenericType
                && (contract.GetGenericTypeDefinition() == typeof(IRequest<>)
                    || contract.GetGenericTypeDefinition() == typeof(IStreamRequest<>))));

    private static (AttributeTargets Targets, bool Inherited, bool AllowMultiple) AttributeShape(
        Type attribute)
    {
        var usage = attribute.GetCustomAttribute<AttributeUsageAttribute>();
        usage.Should().NotBeNull($"{attribute.Name} must declare its usage explicitly");

        return (usage!.ValidOn, usage.Inherited, usage.AllowMultiple);
    }

    /// <summary>Every <c>LearnStack.*</c> project under <c>backend/src</c>.</summary>
    private static IEnumerable<string> ProductionAssemblies() =>
        Directory.EnumerateFiles(
                RepositoryPaths.BackendSrc(), "LearnStack.*.csproj", SearchOption.AllDirectories)
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)
            .OrderBy(name => name, StringComparer.Ordinal);
}
