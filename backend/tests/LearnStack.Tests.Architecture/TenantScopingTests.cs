using System.Linq.Expressions;
using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using LearnStack.Modules.Customization.Domain;
using LearnStack.Modules.Customization.Infrastructure.Persistence;
using LearnStack.Modules.Tenancy.Domain;
using LearnStack.Modules.Tenancy.Infrastructure.Persistence;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace LearnStack.Tests.Architecture;

/// <summary>
/// The two defense-in-depth rules
/// <see href="../../../docs/decisions/0003-tenant-isolation-defense-in-depth.md">ADR-0003
/// Amendment 3</see> and
/// <see href="../../../docs/decisions/0017-tenant-organization-hierarchy.md">ADR-0017</see>
/// place on a scoped entity, catalogued in
/// <see href="../../../docs/standards/21-architecture-tests-catalogue.md">Standards 21
/// § Tenancy and isolation</see>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read § What a structural test proves before relying on these.</b> They check
/// that each layer is <i>present</i>: a marker, a tenant key, an EF query filter,
/// and the migration's <c>ENABLE</c> + <c>FORCE</c> + one permissive policy with
/// both clauses. None of that is evidence that isolation <i>holds</i> — the
/// superseded policy template satisfied every structural assertion of this kind
/// while leaking every tenant-wide row across tenants. The binding proof is the
/// integration suite running as <c>learnstack_app</c>.
/// </para>
/// <para>
/// <b>Scope is by table class, not by the presence of a <c>TenantId</c>
/// property.</b> Two entities are deliberately outside: <c>Tenant</c> is
/// tenant-owned <b>self-keyed</b> — it carries the marker and no
/// <c>ITenantOwned</c> implementation, because its <c>Id</c> is the tenant key —
/// and <c>PlatformHostMapping</c> is <b>platform-scoped</b> and carries no marker
/// at all despite having a <c>TenantId</c> property, because it is read in order
/// to determine the tenant. Both exclusions are asserted, not assumed: a marker
/// added to the host map would make host resolution return zero rows forever, and
/// nothing else in the build would notice.
/// </para>
/// </remarks>
public sealed class TenantScopingTests
{
    [Fact]
    public void Every_TenantOwned_Entity_HasFilterAndRlsPolicy()
    {
        var migrations = MigrationSql();

        foreach (var (domain, buildContext) in Modules.Scoped)
        {
            using var context = buildContext();
            var model = context.Model;

            // Per module, not once for the suite: a suite-wide counter is satisfied
            // by Tenancy alone however many other modules contribute nothing, which
            // is the one failure an enumerated list actually produces.
            ScopedEntities(domain).Should().NotBeEmpty(
                $"{domain.GetName().Name} is listed in Modules.Scoped, so it must have "
                + "tenant-owned entities to sweep — a module that has gone quiet is "
                + "either mis-listed or has lost its markers");

            foreach (var entity in ScopedEntities(domain))
            {
                var attribute = entity.GetCustomAttribute<TenantOwnedAttribute>()!;

                // A tenant key: the TenantId property, or Id on the self-keyed class.
                if (attribute.SelfKeyed)
                {
                    typeof(ITenantOwned).IsAssignableFrom(entity).Should().BeFalse(
                        $"{entity.Name} is self-keyed, so it has no TenantId column to filter on");
                }
                else
                {
                    typeof(ITenantOwned).IsAssignableFrom(entity).Should().BeTrue(
                        $"{entity.Name} carries [TenantOwned] and must expose the key the filter reads");
                }

                var entityType = model.FindEntityType(entity);
                entityType.Should().NotBeNull(
                    $"{entity.Name} carries [TenantOwned] and must be mapped by its module's context");
                var mapped = entityType!;

                var table = mapped.GetTableName()!;

                // The EF filter, on everything but the self-keyed class. That one is
                // carried by its policy alone — `tenants` keys on `id`, and a filter
                // comparing Id to the current tenant would be correct but redundant
                // with the policy and wrong the moment a platform-admin path reads it.
                var key = attribute.SelfKeyed ? "Id" : nameof(ITenantOwned.TenantId);

                RowMembersRead(mapped).Should().Contain(
                    key,
                    $"{table}'s filter must compare the row's own {key} — a filter that reads "
                    + "only context members narrows nothing and would return every row");

                AssertRowSecurity(migrations, table, attribute.SelfKeyed ? "id" : "tenant_id");
            }
        }
    }

    [Fact]
    public void Every_OrgScoped_Entity_HasOrgIdAndFilter()
    {
        // Enumerated by its own marker, not by filtering the tenant-owned set: an
        // entity carrying only [OrganizationScoped] would otherwise be invisible to
        // both rules, which is the one arrangement neither could report.
        var marked = Modules.Scoped
            .SelectMany(module => module.Domain.GetTypes())
            .Where(type => type.GetCustomAttribute<OrganizationScopedAttribute>() is not null)
            .OrderBy(type => type.Name, StringComparer.Ordinal)
            .ToList();

        marked.Should().NotBeEmpty(
            "tenant_settings is organization-scoped; a rule finding nothing checks nothing");

        var migrations = MigrationSql();

        foreach (var entity in marked)
        {
            using var context = ContextFor(entity);
            typeof(IOrganizationScoped).IsAssignableFrom(entity).Should().BeTrue(
                $"{entity.Name} carries [OrganizationScoped] and must expose OrganizationId");

            // An organization exists only inside a tenant, so the two markers travel
            // together. Asserted rather than assumed, because the tenant-owned rule
            // enumerates its own marker and would not see this entity at all.
            entity.GetCustomAttribute<TenantOwnedAttribute>().Should().NotBeNull(
                $"{entity.Name} is organization-scoped, so it is tenant-owned by construction");

            var entityType = context.Model.FindEntityType(entity)!;
            var table = entityType.GetTableName()!;

            // The mapped column, not the CLR property. `IOrganizationScoped`
            // already declares `OrganizationId?`, so a reflection check on the
            // property type cannot fail — it asserts the compiler. What can go
            // wrong is a configuration marking the column required, which would
            // make a tenant-wide row unrepresentable: null is a scope here, not an
            // absence (ADR-0017).
            entityType.FindProperty(nameof(IOrganizationScoped.OrganizationId))!.IsNullable
                .Should().BeTrue(
                    $"{table}.organization_id must be nullable — null means tenant-wide");

            var members = RowMembersRead(entityType);
            members.Should().Contain(nameof(ITenantOwned.TenantId),
                $"{table}'s filter must still carry the tenant term");
            members.Should().Contain(
                nameof(IOrganizationScoped.OrganizationId),
                $"{table}'s filter must carry the organization term too");

            // The organization term is AND-ed into the same single policy, never a
            // second permissive one.
            var policy = PermissivePolicyFor(migrations, table);
            policy.Should().Contain("organization_id",
                $"{table}'s policy must AND the organization term into the tenant term");

            // And the two AS RESTRICTIVE write guards. Not decoration: measured,
            // with the tenant-scope read hatch set and the delete guard dropped, a
            // DELETE removed another organization's row. USING is also what selects
            // the rows an UPDATE may target, and PostgreSQL has no WITH CHECK for
            // DELETE, so these are the only things closing those two paths.
            RestrictiveGuard(migrations, table, "UPDATE").Should().NotBeNull(
                $"{table} needs an AS RESTRICTIVE FOR UPDATE guard");
            RestrictiveGuard(migrations, table, "DELETE").Should().NotBeNull(
                $"{table} needs an AS RESTRICTIVE FOR DELETE guard");
        }
    }

    [Fact]
    public void Every_Scoping_Interface_Carries_Its_Marker()
    {
        // The reverse direction. The two rules above enumerate the markers and
        // check the interfaces; nothing checked that an entity implementing the
        // interfaces also carries the marker. An entity in that state is filtered
        // at runtime — the sweep gates on the interface — while both rules skip
        // it entirely, so its RLS policy, its tenant key and its migration go
        // unchecked. The pair has to travel together in both directions.
        //
        // Over every module, not one: left reading a single assembly this rule
        // reported nothing when a second module's entity dropped its marker, and
        // the marker is what the two rules above enumerate — so the entity fell
        // out of all three at once and the suite stayed green. Measured.
        var implementors = Modules.Scoped
            .SelectMany(module => module.Domain.GetTypes())
            .Where(typeof(ITenantOwned).IsAssignableFrom)
            .Where(type => type is { IsInterface: false, IsAbstract: false })
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ToList();

        implementors.Should().NotBeEmpty(
            "a rule that finds nothing to check passes for the wrong reason");

        foreach (var entity in implementors)
        {
            entity.GetCustomAttribute<TenantOwnedAttribute>().Should().NotBeNull(
                $"{entity.Name} implements ITenantOwned, so it must carry [TenantOwned] "
                + "— without it both scoping rules skip it while the filter still applies");

            if (typeof(IOrganizationScoped).IsAssignableFrom(entity))
            {
                entity.GetCustomAttribute<OrganizationScopedAttribute>().Should().NotBeNull(
                    $"{entity.Name} implements IOrganizationScoped and must say so");
            }
        }
    }

    [Fact]
    public void The_Host_Map_Carries_No_Tenant_Marker()
    {
        // The negative a marker-gated rule cannot state about itself. The host map
        // has a TenantId property, so any rule keyed on "has a TenantId" would
        // capture it — and a tenant-keyed filter on the one table read *in order
        // to* determine the tenant makes host resolution return zero rows forever,
        // on the anonymous page-load path, with no error anywhere.
        typeof(PlatformHostMapping).GetCustomAttribute<TenantOwnedAttribute>()
            .Should().BeNull("platform_host_to_tenant is platform-scoped");
        typeof(ITenantOwned).IsAssignableFrom(typeof(PlatformHostMapping))
            .Should().BeFalse();

        using var context = Modules.ModelOnly<TenancyDbContext>();
        RowMembersRead(context.Model.FindEntityType(typeof(PlatformHostMapping))!)
            .Should().BeEmpty("a tenant filter here would make host resolution impossible");
    }

    /// <summary>
    /// The names of the members each declared query filter reads <b>off the
    /// entity</b> — the row side of every comparison, and nothing else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The tree, not its text.</b> The first version of these rules asserted
    /// <c>filter.ToString().Contains("TenantId")</c>, which is satisfied by the
    /// context-side member name <c>CurrentTenantId</c> all on its own — so the row
    /// side of the comparison was never checked, and a builder that compared two
    /// context members to each other passed. Measured: that mutation survived the
    /// entire suite. Anchoring on the lambda's own parameter is what makes the
    /// assertion about the thing it names.
    /// </para>
    /// <para>
    /// <c>GetDeclaredQueryFilters()</c> rather than the obsolete
    /// <c>GetQueryFilter()</c>: EF 10 allows several named filters per entity and
    /// the singular accessor throws once more than one exists. All of them are
    /// swept, so a later soft-delete filter adds to this set rather than hiding
    /// the tenant one.
    /// </para>
    /// </remarks>
    private static HashSet<string> RowMembersRead(IReadOnlyEntityType entityType)
    {
        var members = new HashSet<string>(StringComparer.Ordinal);

        foreach (var filter in entityType.GetDeclaredQueryFilters())
        {
            if (filter.Expression is not { } lambda || lambda.Parameters.Count == 0)
            {
                continue;
            }

            new RowMemberVisitor(lambda.Parameters[0], members).Visit(lambda.Body);
        }

        return members;
    }

    /// <summary>Collects member reads whose target is the lambda's own parameter.</summary>
    private sealed class RowMemberVisitor(ParameterExpression row, HashSet<string> members)
        : ExpressionVisitor
    {
        protected override Expression VisitMember(MemberExpression node)
        {
            if (node.Expression == row)
            {
                members.Add(node.Member.Name);
            }

            return base.VisitMember(node);
        }
    }

    /// <summary>
    /// The context that maps <paramref name="entity"/>, by the module it lives in.
    /// </summary>
    private static DbContext ContextFor(Type entity)
    {
        var module = Modules.Scoped.SingleOrDefault(candidate => candidate.Domain == entity.Assembly);

        module.Context.Should().NotBeNull(
            $"{entity.Name} lives in {entity.Assembly.GetName().Name}, which is not in Modules.Scoped");

        return module.Context();
    }

    private static IEnumerable<Type> ScopedEntities(Assembly domain) =>
        domain.GetTypes()
            .Where(type => type.GetCustomAttribute<TenantOwnedAttribute>() is not null)
            .OrderBy(type => type.Name, StringComparer.Ordinal);

    [Fact]
    public void Every_Module_With_A_Schema_Is_Swept()
    {
        // The rule above reads an enumerated list, which is what stops it passing
        // vacuously for a module nobody referenced. The cost of enumerating is that
        // the list goes stale — it did once already, when Customization shipped
        // entities the sweep could not see. This is the guard on that: a module
        // Domain assembly carrying a [TenantOwned] entity must be in the list.
        var swept = Modules.Scoped.Select(module => module.Domain.GetName().Name).ToHashSet(StringComparer.Ordinal);

        var withEntities = Modules.Names
            .Select(module => Assembly.Load($"LearnStack.Modules.{module}.Domain"))
            .Where(assembly => ScopedEntities(assembly).Any())
            .Select(assembly => assembly.GetName().Name!)
            .ToList();

        withEntities.Should().NotBeEmpty("a rule that finds nothing to check passes for the wrong reason");
        withEntities.Should().OnlyContain(
            name => swept.Contains(name),
            "a module that ships a tenant-owned entity is swept by Every_TenantOwned_Entity_HasFilterAndRlsPolicy; "
            + "add it to Modules.Scoped with the context that maps it");
    }

    /// <summary>
    /// Every migration source in the repository, concatenated.
    /// </summary>
    /// <remarks>
    /// Both chains, and both spellings. EF writes the tenancy chain's tables
    /// through <c>migrationBuilder.CreateTable(...)</c> and the platform chain
    /// writes <c>outbox_messages</c> and <c>idempotency_keys</c> through
    /// <c>migrationBuilder.Sql("CREATE TABLE …")</c> because neither is an EF
    /// entity. A scan that classified on one token would silently cover one chain.
    /// </remarks>
    private static string MigrationSql()
    {
        var files = Directory
            .EnumerateDirectories(RepositoryPaths.BackendSrc(), "Migrations", SearchOption.AllDirectories)
            .SelectMany(directory => Directory.EnumerateFiles(directory, "*.cs"))
            .Where(file => !file.EndsWith(".Designer.cs", StringComparison.Ordinal))
            .ToList();

        files.Should().NotBeEmpty("the migration scan has nothing to read");

        return string.Join("\n", files.Select(File.ReadAllText));
    }

    private static void AssertRowSecurity(string migrations, string table, string keyColumn)
    {
        migrations.Should().MatchRegex($@"ALTER TABLE\s+{Regex.Escape(table)}\s+ENABLE ROW LEVEL SECURITY",
            $"{table} must ENABLE row level security");
        migrations.Should().MatchRegex($@"ALTER TABLE\s+{Regex.Escape(table)}\s+FORCE\s+ROW LEVEL SECURITY",
            $"{table} must FORCE row level security — without it the owner bypasses its own policies");

        var policy = PermissivePolicyFor(migrations, table);
        policy.Should().Contain("USING", $"{table}'s policy needs a USING clause");
        policy.Should().Contain("WITH CHECK",
            $"{table}'s policy needs an explicit WITH CHECK — USING alone leaves writes unconstrained");

        PolicyReadsTheTenant(policy, keyColumn).Should().BeTrue(
            $"{table}'s policy compares {keyColumn} to app.tenant_id in both clauses — a USING "
            + "that reads the tenant and a WITH CHECK that does not admits a write into any "
            + "tenant, and a policy that reads neither isolates nothing");
    }

    [Fact]
    public void The_Tenant_Term_Check_Can_Actually_Fail()
    {
        // Every real policy carries the term in both clauses, so the check above passes
        // whether it reads the clauses or not. Each probe breaks one of them.
        const string Term = "tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid";

        PolicyReadsTheTenant($"CREATE POLICY p ON t USING ({Term}) WITH CHECK ({Term});", "tenant_id")
            .Should().BeTrue();
        PolicyReadsTheTenant($"CREATE POLICY p ON t USING ({Term}) WITH CHECK (true);", "tenant_id")
            .Should().BeFalse("a WITH CHECK that ignores the tenant admits a write into any tenant");
        PolicyReadsTheTenant($"CREATE POLICY p ON t USING (organization_id IS NULL) WITH CHECK ({Term});", "tenant_id")
            .Should().BeFalse("a USING that ignores the tenant reads every tenant's rows");
        PolicyReadsTheTenant($"CREATE POLICY p ON t USING ({Term}) WITH CHECK ({Term});", "id")
            .Should().BeFalse("the self-keyed table compares its own id, not a tenant_id it lacks");
    }

    /// <summary>
    /// Whether both clauses of a policy compare the key column to the announced tenant.
    /// </summary>
    private static bool PolicyReadsTheTenant(string policy, string keyColumn)
    {
        var usingAt = policy.IndexOf("USING", StringComparison.Ordinal);
        var checkAt = policy.IndexOf("WITH CHECK", StringComparison.Ordinal);

        if (usingAt < 0 || checkAt < usingAt)
        {
            return false;
        }

        var term = new Regex(
            $@"(?<![A-Za-z0-9_]){Regex.Escape(keyColumn)}\s*=\s*NULLIF\(\s*current_setting\(\s*'app\.tenant_id'\s*,\s*true\s*\)\s*,\s*''\s*\)::uuid");

        return term.IsMatch(policy[usingAt..checkAt]) && term.IsMatch(policy[checkAt..]);
    }

    /// <summary>
    /// The one permissive policy on <paramref name="table"/>.
    /// </summary>
    /// <remarks>
    /// Exactly one, asserted here rather than assumed. Two permissive policies are
    /// OR-ed by PostgreSQL, which is the defect ADR-0003 Amendment 3 corrects and
    /// the reason it is worth counting: the superseded template shipped two and
    /// every tenant-wide row was visible across tenants. <c>AS RESTRICTIVE</c>
    /// policies are excluded from the count — they narrow rather than widen, and
    /// the organization-scoped class is required to carry two of them.
    /// </remarks>
    private static string PermissivePolicyFor(string migrations, string table)
    {
        // Case-insensitively, because SQL keywords are: a second policy written
        // `create policy … as restrictive` would be neither counted nor excluded, and the
        // count is the whole of what this rule proves.
        var statements = Regex
            .Matches(
                migrations,
                $@"CREATE POLICY\s+\w+\s+ON\s+{Regex.Escape(table)}\b(?<body>.*?);",
                RegexOptions.Singleline | RegexOptions.IgnoreCase)
            .Select(match => match.Value)
            .Where(statement => !statement.Contains("AS RESTRICTIVE", StringComparison.OrdinalIgnoreCase))
            .ToList();

        statements.Should().ContainSingle(
            $"{table} must carry exactly one permissive policy — PostgreSQL OR-s two together, "
            + "which is how the superseded template leaked every tenant-wide row");

        return statements[0];
    }

    private static string? RestrictiveGuard(string migrations, string table, string command)
    {
        var match = Regex.Match(
            migrations,
            $@"CREATE POLICY\s+\w+\s+ON\s+{Regex.Escape(table)}\s+AS RESTRICTIVE FOR {command}\b.*?;",
            RegexOptions.Singleline);

        return match.Success ? match.Value : null;
    }
}
