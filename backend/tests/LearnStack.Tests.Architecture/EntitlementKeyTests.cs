using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using LearnStack.Modules.Tenancy.Domain;
using LearnStack.SharedKernel.Entitlements;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Time;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace LearnStack.Tests.Architecture;

/// <summary>
/// The entitlement vocabulary: a key exists in its registry before anything names it, and a
/// key a plan sells is never stored as a tenant's own flag. Catalogued in
/// <see href="../../../docs/standards/21-architecture-tests-catalogue.md">Standards 21</see>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The spelling is the one-way door.</b> It lands in the Hub's plan validators, in the
/// <c>features</c> jsonb every projection persists, and in a wire schema both repositories
/// pin — so a key invented at a call site misses on every real projection and falls through
/// to its catalog default, which reads as "not entitled" and reports success
/// (<see href="../../../docs/decisions/0045-entitlement-and-feature-flag-socket.md">ADR-0045
/// Amendment 1 § 1</see>).
/// </para>
/// </remarks>
public sealed partial class EntitlementKeyTests
{
    [Fact]
    public void FeatureKey_AllReferences_AreInRegistry()
    {
        // A key type is a record struct over a string, so `new FeatureKey("whatever")`
        // compiles anywhere. What makes the registry the vocabulary is that nothing else
        // constructs one: every production call site reads a static member of FeatureKeys,
        // LimitKeys or KillswitchKeys, and a name that is not there does not resolve
        // (ADR-0021 Amendment 1; ADR-0045 § 6). The converse is deliberately not asserted —
        // the registries carry the Hub's full vocabulary, and a declared key nothing reads
        // yet is expected.
        //
        // Read from IL rather than from types: construction is an instruction inside a
        // method body, which a type-reference scan cannot see.
        var sites = ProductionAssemblies.All()
            .SelectMany(assembly => KeyConstructionSites(assembly.Location))
            .ToList();

        sites.Select(site => site.DeclaringType).Should().Contain(Registries,
            "the premise: the scan sees the registries construct their own keys, or it sees "
            + "nothing and passes for that reason");

        Unsanctioned(sites).Should().BeEmpty(
            "a key is constructed in its registry and read everywhere else — a key spelled at "
            + "a call site is one the Hub never sends (ADR-0045 Amendment 1 § 1)");
    }

    [Fact]
    public void PlanProjected_Keys_NotInTenantFlags()
    {
        // The two halves answer with different authority: a plan-projected key resolves
        // through IEntitlementProvider, a tenant-flag key through tenant_feature_flags. A
        // plan key served from the tenant's own table is a tenant editing its entitlement,
        // which is the one thing the projection exists to prevent (ADR-0045 § 2).
        var planProjected = FeatureKeys.All.Values
            .Where(descriptor => descriptor.Source == FeatureSource.PlanProjected)
            .Select(descriptor => descriptor.Key)
            .ToList();

        var tenantFlags = FeatureKeys.All.Values
            .Where(descriptor => descriptor.Source == FeatureSource.TenantFlag)
            .Select(descriptor => descriptor.Key)
            .ToList();

        planProjected.Should().NotBeEmpty("the premise: there are plan keys to keep out");
        tenantFlags.Should().NotBeEmpty("the premise: and tenant flags the write path admits");

        // The write path is one method, and it takes the typed key rather than a string —
        // so "which key" is a question the compiler asks and this rule can answer.
        var setter = typeof(Tenant).GetMethod(nameof(Tenant.SetFeatureFlag))!;
        setter.GetParameters()[0].ParameterType.Should().Be<FeatureKey>(
            "a string key cannot be checked against the registry the row has to come from");

        foreach (var key in planProjected)
        {
            var tenant = NewTenant();
            var set = () => tenant.SetFeatureFlag(key, "true", Clock, Actor);

            set.Should().Throw<ArgumentException>($"{key} is answered by the plan");
            tenant.FeatureFlags.Should().BeEmpty($"{key} left no row behind");
        }

        foreach (var key in tenantFlags)
        {
            var tenant = NewTenant();
            tenant.SetFeatureFlag(key, "true", Clock, Actor);

            tenant.FeatureFlags.Should().ContainSingle().Which.Key.Should().Be(key.Value);
        }

        // And nothing else reaches the table: no other production code creates the entity,
        // and no SQL writes its rows.
        EntityCreationSites().Should().Equal(
            [$"{typeof(Tenant).FullName}.{nameof(Tenant.SetFeatureFlag)}"],
            "the aggregate root is the only way a tenant_feature_flags row comes into being");

        Directory.EnumerateFiles(SourceScan.SourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Split(Path.DirectorySeparatorChar).Any(segment => segment is "bin" or "obj"))
            .Where(file => TenantFlagWrite().IsMatch(SourceText.WithoutComments(File.ReadAllText(file))))
            .Select(file => Path.GetRelativePath(SourceScan.SourceRoot, file).Replace('\\', '/'))
            .Should().BeEmpty("tenant_feature_flags is written through the aggregate, never by SQL");
    }

    [Fact]
    public void The_Entitlement_Key_Scans_Can_Actually_Fail()
    {
        // Nothing in production constructs a key outside its registry or writes the table by
        // SQL, so both rules above pass whether their scans work or not.
        var planted = KeyConstructionSites(typeof(EntitlementKeyTests).Assembly.Location).ToList();

        Unsanctioned(planted).Should().BeEquivalentTo(
            [
                $"{typeof(Probes.KeyInventorProbe).FullName}.{nameof(Probes.KeyInventorProbe.Invent)}",
                $"{typeof(Probes.KeyInventorProbe).FullName}.{nameof(Probes.KeyInventorProbe.Rename)}",
            ],
            "the scan reads a constructor call and a `with` expression — the two ways a name "
            + "that is not a registry member gets spelled");

        TenantFlagWrite().IsMatch("INSERT INTO tenant_feature_flags (tenant_id, key)").Should().BeTrue();
        TenantFlagWrite().IsMatch("update \"public\".\"tenant_feature_flags\" set value = @v").Should().BeTrue();
        TenantFlagWrite().IsMatch("COPY tenant_feature_flags FROM STDIN").Should().BeTrue();
        TenantFlagWrite().IsMatch("GRANT SELECT, INSERT, UPDATE, DELETE ON tenant_feature_flags TO learnstack_app")
            .Should().BeFalse("a grant names the table and writes no row");
        TenantFlagWrite().IsMatch("CREATE POLICY p ON tenant_feature_flags FOR UPDATE USING (true)")
            .Should().BeFalse("a policy names the table and writes no row");
    }

    /// <summary>The three registries, the only types that may construct a key.</summary>
    private static readonly string[] Registries =
        [typeof(FeatureKeys).FullName!, typeof(LimitKeys).FullName!, typeof(KillswitchKeys).FullName!];

    /// <summary>The key types themselves; their own generated members construct them.</summary>
    private static readonly HashSet<string> KeyTypes = new(StringComparer.Ordinal)
    {
        typeof(FeatureKey).FullName!, typeof(LimitKey).FullName!, typeof(KillswitchKey).FullName!,
    };

    /// <summary>The construction sites that are neither a registry nor the key type itself.</summary>
    private static List<string> Unsanctioned(IEnumerable<(string DeclaringType, string Method)> sites) =>
        [.. sites
            .Where(site => !Registries.Contains(site.DeclaringType, StringComparer.Ordinal)
                && !KeyTypes.Contains(site.DeclaringType))
            .Select(site => $"{site.DeclaringType}.{site.Method}")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];

    /// <summary>
    /// Every method in an assembly that <b>spells</b> an entitlement key — a constructor call,
    /// or the <c>init</c> setter a <c>with</c> expression runs.
    /// </summary>
    /// <remarks>
    /// Attributed to the outermost declaring type, so a lambda's closure or an iterator's
    /// state machine is reported as the type that wrote it.
    /// </remarks>
    private static List<(string DeclaringType, string Method)> KeyConstructionSites(string assemblyPath)
    {
        using var module = ModuleDefinition.ReadModule(assemblyPath);

        return [.. module.GetTypes()
            .SelectMany(type => type.Methods.Where(method => method.HasBody)
                .Where(method => method.Body.Instructions.Any(Constructs))
                .Select(method => (DeclaringType: Outermost(type).FullName.Replace('/', '+'), method.Name)))];
    }

    /// <remarks>
    /// <c>default(FeatureKey)</c> is not a spelling and is not scanned: it carries no name to
    /// be wrong about, every registry lookup misses it, and the compiler emits one into every
    /// async state machine that takes a key — clearing its fields on completion.
    /// </remarks>
    private static bool Constructs(Instruction instruction) =>
        instruction.Operand is MethodReference method
        && KeyTypes.Contains(method.DeclaringType.FullName)
        && method.Name is ".ctor" or "set_Value";

    private static TypeDefinition Outermost(TypeDefinition type)
    {
        var outermost = type;
        while (outermost.DeclaringType is { } declaring)
        {
            outermost = declaring;
        }

        return outermost;
    }

    /// <summary>Every method that creates a <see cref="TenantFeatureFlag"/>.</summary>
    private static List<string> EntityCreationSites() =>
        [.. ProductionAssemblies.All()
            .SelectMany(assembly => CreationSites(assembly.Location, typeof(TenantFeatureFlag).FullName!))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];

    private static List<string> CreationSites(string assemblyPath, string entity)
    {
        using var module = ModuleDefinition.ReadModule(assemblyPath);

        return [.. module.GetTypes()
            .Where(type => type.FullName != entity)
            .SelectMany(type => type.Methods.Where(method => method.HasBody)
                .Where(method => method.Body.Instructions.Any(instruction =>
                    instruction.Operand is MethodReference reference
                    && reference.DeclaringType.FullName == entity
                    && reference.Name is ".ctor" or "Create"))
                .Select(method => $"{Outermost(type).FullName.Replace('/', '+')}.{method.Name}"))];
    }

    private static Tenant NewTenant() => Tenant.Create(
        TenantId.From(Guid.Parse("11111111-1111-7111-8111-111111111111")),
        "acme",
        "Acme",
        Clock,
        Actor);

    private static readonly IClock Clock =
        new FixedClock(new DateTimeOffset(2026, 9, 12, 9, 0, 0, TimeSpan.Zero));

    private static readonly UserId Actor =
        UserId.From(Guid.Parse("00000000-0000-7000-8000-000000000001"));

    /// <summary>A statement that writes rows of <c>tenant_feature_flags</c>.</summary>
    [GeneratedRegex(
        @"\b(?:INSERT\s+INTO|MERGE\s+INTO|COPY|UPDATE|DELETE\s+FROM)\s+(?:ONLY\s+)?(?:""?[A-Za-z_][A-Za-z0-9_]*""?\s*\.\s*)?""?tenant_feature_flags""?(?![A-Za-z0-9_])",
        RegexOptions.IgnoreCase)]
    private static partial Regex TenantFlagWrite();
}
