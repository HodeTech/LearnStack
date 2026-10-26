using System.Collections.Immutable;
using System.Text.RegularExpressions;
using FluentAssertions;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Identity.Application.Contracts.Seeding;
using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Permissions;
using Mono.Cecil;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Xunit;

namespace LearnStack.Tests.Architecture;

public sealed class IdentityFoundationConventionTests
{
    private static readonly string[] ContextualSaveOwners =
    ["LearnStack.Modules.Identity.Infrastructure.Persistence.ContextualIdentityStorageControl.SaveAsync"];
    private static readonly ImmutableHashSet<(PermissionScope Scope, string Key)> Expected =
    [
        (PermissionScope.Tenant, "identity.membership.admin"),
        (PermissionScope.Organization, "identity.membership.admin"),
        (PermissionScope.Tenant, "identity.role.admin"),
        (PermissionScope.Tenant, "identity.membership_profile.write"),
        (PermissionScope.Organization, "identity.membership_profile.write"),
    ];

    [Fact]
    public void Permission_Definitions_DeclareScope()
    {
        var manifest = IdentityFoundationManifest.Version1;
        RegistryViolations(manifest.Definitions).Should().BeEmpty();
        manifest.Definitions.Should().NotBeEmpty();
        manifest.Templates.Should().HaveCount(8);
        manifest.Templates.Select(template => template.BuiltInKey).Distinct().Should().HaveCount(7);
        foreach (var template in manifest.Templates)
        {
            template.Scope.Should().NotBe(PermissionScope.Platform);
            template.Grants.All(grant => grant.Scope == template.Scope && Expected.Contains((grant.Scope, grant.Key)))
                .Should().BeTrue("the finite manifest deliberately includes empty templates");
        }
    }

    [Fact]
    public void Registry_controls_detect_missing_duplicate_inactive_and_wrong_scope_definitions()
    {
        var definitions = IdentityFoundationManifest.Version1.Definitions;
        RegistryViolations(definitions.RemoveAt(0)).Should().NotBeEmpty();
        RegistryViolations(definitions.Add(definitions[0])).Should().NotBeEmpty();
        RegistryViolations(definitions.SetItem(0, new(definitions[0].Scope, definitions[0].Key, false)))
            .Should().NotBeEmpty();
        RegistryViolations(definitions.SetItem(0, new(PermissionScope.Platform, definitions[0].Key)))
            .Should().NotBeEmpty();
    }

    [Fact]
    public void Identity_Seed_Uses_Finite_Adopted_Manifests()
    {
        var manifest = IdentityFoundationManifest.Version1;
        manifest.Version.Should().Be(1);
        manifest.Fingerprint.Should().MatchRegex("^[a-f0-9]{64}$");
        manifest.TenantAdminKernel.Select(pair => (pair.Scope, pair.Key)).Should().BeEquivalentTo(
            new[] { (PermissionScope.Tenant, "identity.membership.admin"), (PermissionScope.Tenant, "identity.role.admin") });
        manifest.FindTemplate(BuiltInRoleKey.TenantAdmin, PermissionScope.Tenant)!.Grants.Should().HaveCount(3,
            "the initial template includes profile write beyond the two-pair administrator minimum");

        var actor = UserId.From(Guid.Parse("01930000-0000-7000-8000-00000000a001"));
        IIdentityPermissionRequest[] operations =
        [
            new InitializeSeedRoleCommand(Guid.Empty, BuiltInRoleKey.TenantAdmin, PermissionScope.Tenant, 1, ""),
            new CreateFirstSeedAdminMembershipCommand(Guid.Empty, actor, Guid.Empty, 1, ""),
            new CreateSeedMembershipCommand(Guid.Empty, actor, Guid.Empty, PermissionScope.Tenant, 1, ""),
            new CreateSeedMembershipCommand(Guid.Empty, actor, Guid.Empty, PermissionScope.Organization, 1, ""),
            new SetSeedMembershipProfileCommand(Guid.Empty, PermissionScope.Tenant, null, null, 1, ""),
            new SetSeedMembershipProfileCommand(Guid.Empty, PermissionScope.Organization, null, null, 1, ""),
        ];
        operations.Select(operation => operation.RequiredPermission).Distinct().Should().BeEquivalentTo(manifest.Definitions);
        var sql = new Microsoft.EntityFrameworkCore.Migrations.MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
        LearnStack.Modules.Identity.Infrastructure.Persistence.ContextualIdentityStorageDdl.Install(sql);
        var installed = string.Join('\n', sql.Operations.OfType<Microsoft.EntityFrameworkCore.Migrations.Operations.SqlOperation>()
            .Select(operation => operation.Sql));
        installed.Should().Contain(manifest.RegistrySqlValues).And.Contain(manifest.RolePairsSqlValues)
            .And.Contain(manifest.GrantSqlValues).And.Contain(manifest.KernelSqlValues);
    }

    [Fact]
    public void Identity_Membership_Invariants_Use_Fresh_Ordered_Locks()
    {
        string[] admitted =
        [
            "LearnStack.Modules.Identity.Application.Seeding.InitializeSeedRoleCommandHandler.Handle",
            "LearnStack.Modules.Identity.Application.Seeding.SeedMembershipCreation.CreateAsync",
            "LearnStack.Modules.Identity.Application.Seeding.SetSeedMembershipProfileCommandHandler.Handle",
        ];
        Callers(typeof(IIdentityMutationLocks).FullName!, nameof(IIdentityMutationLocks.AcquireAsync))
            .Should().BeEquivalentTo(admitted);
        Callers(typeof(IIdentityMutationLocks).FullName!, nameof(IIdentityMutationLocks.RequireCurrent))
            .Should().BeEquivalentTo(admitted);
        Callers(typeof(DatabaseFacade).FullName!, "set_AutoSavepointsEnabled").Should().Equal(ContextualSaveOwners,
            "only the closed whole-graph adapter may suspend automatic savepoints, with finally restoration and full rollback on failure");
        var root = SourceScan.SourceRoot;
        var files = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Split(Path.DirectorySeparatorChar).Intersect(["obj", "bin"]).Any())
            .Select(File.ReadAllText);
        files.SelectMany(SessionCalls).Should().BeEmpty("production uses transaction-level advisory calls only");
        var helper = File.ReadAllText(Path.Combine(root, "LearnStack.Infrastructure", "Persistence", "IdentityMutationLocks.cs"));
        helper.Should().Contain("GetTenantContextAnnouncement").And.Contain("Generation")
            .And.Contain("ReferenceEquals(_transaction, unit.Transaction)")
            .And.Contain("ReferenceEquals(_connection, unit.Connection)");
        var code = SourceText.WithoutComments(helper);
        code.IndexOf("foreach (var user in plan.UserIds)", StringComparison.Ordinal)
            .Should().BeLessThan(code.IndexOf("IdentityAdvisoryLockKey.ForTenant", StringComparison.Ordinal));
        var save = File.ReadAllText(Path.Combine(root, "Modules", "Identity", "LearnStack.Modules.Identity.Infrastructure",
            "Persistence", "ContextualIdentityStores.cs"));
        SourceText.WithoutWhitespace(save).Should().Contain("finally{db.Database.AutoSavepointsEnabled=savepoints;}")
            .And.Contain("unit.MarkRollbackOnly()");
    }

    [Theory]
    [InlineData("SELECT pg_advisory_lock(1)")]
    [InlineData("SELECT pg_try_advisory_lock_shared(1)")]
    [InlineData("SELECT pg_advisory_unlock_all()")]
    public void Session_api_control_detects_a_planted_borrowed_lifetime(string sql)
    {
        SessionCalls(sql).Should().ContainSingle();
        SessionCalls("SELECT pg_advisory_xact_lock(1), pg_advisory_xact_lock_shared(2)").Should().BeEmpty();
        SessionCalls("// pg_advisory_lock(1)\nSELECT 1").Should().BeEmpty();
    }

    private static IEnumerable<string> RegistryViolations(IEnumerable<PermissionDefinition> definitions)
    {
        var actual = definitions.ToArray();
        if (actual.Length != Expected.Count || actual.Select(definition => (definition.Scope, definition.Key)).Distinct().Count() != actual.Length)
            yield return "missing-or-duplicate";
        if (actual.Any(definition => !definition.IsActive || !Expected.Contains((definition.Scope, definition.Key))))
            yield return "inactive-or-scope";
    }

    private static IEnumerable<string> SessionCalls(string source) => Regex.Matches(SourceText.WithoutComments(source),
        @"\bpg_(?:try_)?advisory_(?:lock|unlock)(?:_shared|_all)?\s*\(", RegexOptions.IgnoreCase)
        .Select(match => match.Value);

    private static string[] Callers(string typeName, string methodName) => ProductionAssemblies.All().SelectMany(assembly =>
    {
        using var module = ModuleDefinition.ReadModule(assembly.Location);
        return module.Types.Where(type => type.Name != "<Module>")
            .SelectMany(type => Il.Methods(type).Where(method => method.Definition.HasBody
                && method.Definition.Body.Instructions.Any(instruction => instruction.Operand is MethodReference call
                    && call.DeclaringType.FullName == typeName && call.Name == methodName))
                .Select(method => type.FullName + "." + method.DeclaredAs.TrimStart('.'))).ToArray();
    }).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
}
