using System.Text.RegularExpressions;
using FluentAssertions;
using Mono.Cecil;
using LearnStack.Modules.Tenancy.Domain;
using LearnStack.Modules.Tenancy.Infrastructure.Persistence;
using LearnStack.SharedKernel.Entitlements;
using Microsoft.EntityFrameworkCore;
using NetArchTest.Rules;
using Xunit;

namespace LearnStack.Tests.Architecture;

/// <summary>
/// The entitlement socket's rules
/// (<see href="../../../docs/decisions/0045-entitlement-and-feature-flag-socket.md">ADR-0045</see>),
/// catalogued in
/// <see href="../../../docs/standards/21-architecture-tests-catalogue.md">Standards 21</see>.
/// </summary>
public sealed partial class EntitlementConventionTests
{
    [Fact]
    public void Modules_Do_Not_Read_Entitlement_Cache_Directly()
    {
        // An IEntitlementProvider implementation is the only reader and the only writer of
        // platform_entitlement_cache; IFeatureFlags composes over the port, which is what
        // makes swapping the registered provider change the answer. A plan-projected key that
        // resolved by SELECT would bypass the provider — Tenancy included, because the
        // table's mapping lives there and the next read would too.
        //
        // The entity. The schema's four sites name it by construction — the entity itself,
        // its EF configuration, and TenancyDbContext's DbSet, which the query filter rides on
        // (the migrations name it by string) — and so may the provider. Everything else that
        // names PlatformEntitlement is reaching for its rows.
        var naming = ProductionAssemblies.All()
            .SelectMany(assembly => Types.InAssembly(assembly)
                .That().HaveDependencyOn(typeof(PlatformEntitlement).FullName!)
                .GetTypes())
            .ToList();

        naming.Should().Contain(typeof(TenancyDbContext),
            "the premise: the scan sees the DbSet that maps the table, or it sees nothing");

        naming.Where(type => !MayNameTheEntitlementRow(type))
            .Select(type => type.FullName)
            .Should().BeEmpty(
                "only the schema's own sites and an IEntitlementProvider implementation name "
                + "the row — module code reads entitlement through IFeatureFlags (ADR-0045 § 2)");

        // The context's exemption is for the MAPPING, not for the type. A query helper declared
        // on TenancyDbContext would launder the read: the caller names only the context, which
        // every module may, and the entity leg above would see nothing.
        ContextMembersNaming(typeof(TenancyDbContext), typeof(PlatformEntitlement))
            .Should().Equal(
                [$"get_{nameof(TenancyDbContext.PlatformEntitlements)}"],
                "the context maps the table and reads nothing from it");

        // SQL. No statement reads or writes the table outside the provider. The premise is the
        // scan's own enumeration — the same files the rule reads, asserted to contain the ones
        // that name the table at all, so a scan that walked an empty tree fails here instead of
        // reporting clean.
        var scanned = Directory.EnumerateFiles(SourceScan.SourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Split(Path.DirectorySeparatorChar).Any(segment => segment is "bin" or "obj"))
            .ToList();

        scanned.Where(file => SourceText.WithoutComments(File.ReadAllText(file))
                .Contains("platform_entitlement_cache", StringComparison.Ordinal))
            .Should().NotBeEmpty("the premise: the enumeration this leg walks reaches the files that name the table");

        scanned
            .Where(file => EntitlementCacheStatement().IsMatch(SourceText.WithoutComments(File.ReadAllText(file))))
            .Select(file => Path.GetRelativePath(SourceScan.SourceRoot, file).Replace('\\', '/'))
            .Where(file => !ProviderSqlSites.Contains(file))
            .Should().BeEmpty(
                "no SQL reads or writes platform_entitlement_cache but an IEntitlementProvider "
                + "implementation's");
    }

    [Fact]
    public void The_Entitlement_Cache_Scan_Can_Actually_Fail()
    {
        // Nothing reads the table today — NullEntitlementProvider answers from constants — so
        // the rule above passes whether its checks work or not.
        EntitlementCacheStatement().IsMatch("SELECT plan_code FROM platform_entitlement_cache WHERE tenant_id = @t").Should().BeTrue();
        EntitlementCacheStatement().IsMatch("insert into \"public\".\"platform_entitlement_cache\" (tenant_id)").Should().BeTrue();
        EntitlementCacheStatement().IsMatch("UPDATE platform_entitlement_cache SET generation = @g").Should().BeTrue();
        EntitlementCacheStatement().IsMatch("DELETE FROM ONLY platform_entitlement_cache").Should().BeTrue();
        EntitlementCacheStatement().IsMatch("JOIN platform_entitlement_cache e ON e.tenant_id = t.id").Should().BeTrue();
        EntitlementCacheStatement().IsMatch("SELECT e.plan_code FROM tenants t, platform_entitlement_cache e").Should().BeTrue(
            "an implicit join puts the table after a comma");
        EntitlementCacheStatement().IsMatch("DELETE FROM tenants USING platform_entitlement_cache e").Should().BeTrue(
            "and USING is the other way a statement names a second table");

        // The schema's own statements are not row access.
        EntitlementCacheStatement().IsMatch("ALTER TABLE platform_entitlement_cache FORCE ROW LEVEL SECURITY").Should().BeFalse();
        EntitlementCacheStatement().IsMatch("GRANT SELECT, INSERT, UPDATE ON platform_entitlement_cache TO learnstack_app").Should().BeFalse();
        EntitlementCacheStatement().IsMatch("CREATE POLICY p ON platform_entitlement_cache USING (true)").Should().BeFalse();
        EntitlementCacheStatement().IsMatch("RAISE EXCEPTION 'platform_entitlement_cache_valid_until_nullable'").Should().BeFalse();

        Types.InAssembly(typeof(EntitlementConventionTests).Assembly)
            .That().HaveName(nameof(EntitlementReaderProbe))
            .And().HaveDependencyOn(typeof(PlatformEntitlement).FullName!)
            .GetTypes()
            .Should().ContainSingle()
            .Which.Should().Match<Type>(type => !MayNameTheEntitlementRow(type));
    }

    /// <summary>
    /// The files whose SQL may act on <c>platform_entitlement_cache</c>, by exact path under
    /// <c>backend/src</c>.
    /// </summary>
    /// <remarks>
    /// Empty: the implementation that reads and writes the table is the Hub-backed provider,
    /// in Phase 02c, which adds its own path here.
    /// </remarks>
    private static readonly HashSet<string> ProviderSqlSites = new(StringComparer.Ordinal);

    /// <summary>The types allowed to name <see cref="PlatformEntitlement"/>.</summary>
    /// <remarks>
    /// A compiler-generated nested type is judged by the type that declares it.
    /// </remarks>
    private static bool MayNameTheEntitlementRow(Type type)
    {
        var declaring = type;
        while (declaring.DeclaringType is { } outer)
        {
            declaring = outer;
        }

        return declaring == typeof(PlatformEntitlement)
            || declaring == typeof(TenancyDbContext)
            || declaring.FullName == "LearnStack.Modules.Tenancy.Infrastructure.Persistence.PlatformEntitlementConfiguration"
            || typeof(IEntitlementProvider).IsAssignableFrom(declaring);
    }

    /// <summary>
    /// A statement that reads or writes the table's rows — not one that names it in DDL, a
    /// grant or a policy.
    /// </summary>
    [GeneratedRegex(
        @"(?:\b(?:FROM|JOIN|INTO|UPDATE|COPY|USING|TRUNCATE(?:\s+TABLE)?)\s+|,\s*)(?:ONLY\s+)?(?:""?[A-Za-z_][A-Za-z0-9_]*""?\s*\.\s*)?""?platform_entitlement_cache""?(?![A-Za-z0-9_])",
        RegexOptions.IgnoreCase)]
    private static partial Regex EntitlementCacheStatement();

    /// <summary>
    /// The methods of a <c>DbContext</c> that name a guarded entity.
    /// </summary>
    /// <remarks>
    /// Shared with <c>Modules_Do_Not_Write_AuditLog_Directly</c>, which has the same shape: a
    /// context is exempt because it maps the row, and mapping it is a property getter.
    /// </remarks>
    internal static List<string> ContextMembersNaming(Type context, Type entity)
    {
        using var module = ModuleDefinition.ReadModule(context.Assembly.Location);

        var definition = module.GetType(context.FullName)
            ?? throw new InvalidOperationException($"{context.FullName} is the context this rule reads.");

        return Il.MethodsNaming(definition, entity.FullName!);
    }

    /// <summary>
    /// Reads the table through the DbSet, for <c>The_Entitlement_Cache_Scan_Can_Actually_Fail</c>.
    /// Never constructed.
    /// </summary>
    private sealed class EntitlementReaderProbe(TenancyDbContext context)
    {
        public Task<PlatformEntitlement?> ReadAsync() => context.PlatformEntitlements.FirstOrDefaultAsync();
    }
}
