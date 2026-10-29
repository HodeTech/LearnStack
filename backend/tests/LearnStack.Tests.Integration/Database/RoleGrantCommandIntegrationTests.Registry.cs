using System.Collections.Immutable;
using System.Text.Json;
using LearnStack.Modules.Identity.Application.Contracts.Roles;
using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Results;
using MediatR;
using Npgsql;
using Xunit;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class RoleGrantCommandIntegrationTests
{
    [Theory]
    [InlineData(PermissionScope.Tenant, "identity.membership.admin")]
    [InlineData(PermissionScope.Organization, "identity.membership.admin")]
    [InlineData(PermissionScope.Tenant, "identity.role.admin")]
    [InlineData(PermissionScope.Tenant, "identity.membership_profile.write")]
    [InlineData(PermissionScope.Organization, "identity.membership_profile.write")]
    [InlineData(PermissionScope.Tenant, "tenancy.organization.admin")]
    [InlineData(PermissionScope.Tenant, "tenancy.locale.write")]
    [InlineData(PermissionScope.Tenant, "tenancy.setting.write")]
    public async Task Every_assignable_pair_requires_a_legal_delegation_target_with_committed_denial_and_no_refused_mutation(
        PermissionScope permissionScope, string permissionKey)
    {
        var pair = new PermissionDefinition(permissionScope, permissionKey);
        if (IdentityRoleRelease.Version2.TenantAdminDelta.Contains(pair))
        {
            await AssertReleasedAssignablePairAsync(pair);
            return;
        }
        var roleAdministration = permissionKey == "identity.role.admin";
        var matchingKind = roleAdministration ? BuiltInRoleKey.TenantAdmin
            : permissionScope == PermissionScope.Tenant ? BuiltInRoleKey.Instructor : BuiltInRoleKey.OrgInstructor;
        var oppositeScope = roleAdministration ? PermissionScope.Tenant
            : permissionScope == PermissionScope.Tenant ? PermissionScope.Organization : PermissionScope.Tenant;
        var oppositeKind = oppositeScope == PermissionScope.Tenant ? BuiltInRoleKey.Instructor : BuiltInRoleKey.OrgInstructor;
        var matchingRole = RoleId(SchemaFixture.TenantA, matchingKind, permissionScope);
        var oppositeRole = RoleId(SchemaFixture.TenantA, oppositeKind, oppositeScope);
        // Role administration is confined to TenantAdmin and its inseparable kernel.
        // Its positive mutation removes the optional profile grant while retaining that kernel.
        ImmutableArray<PermissionDefinition> desired = roleAdministration ? IdentityFoundationManifest.Version1.TenantAdminKernel : [pair];
        var correlation = "registry-delegation-" + Guid.NewGuid().ToString("N");
        try
        {
            var matchingBefore = await ReadRoleAsync(matchingRole);
            var positive = await SendAsync(new ChangeRoleGrantsCommand(matchingRole, matchingBefore.Version, desired), correlation + "-positive");
            Assert.True(positive.IsSuccess);
            var matchingAfter = await ReadRoleAsync(matchingRole);
            Assert.Equal(matchingBefore.Version + 1, matchingAfter.Version);
            Assert.Equal(desired.Select(definition => definition.Scope + ":" + definition.Key).Order(StringComparer.Ordinal), matchingAfter.Grants);
            Assert.Equal("success", (await AuditAsync(correlation + "-positive", "identity.role.change_grants")).Outcome);

            // This checks actual target-pair delegation, including the RoleAdmin template restriction.
            // It does not invent an underprivileged administrator or a future profile editing route.
            var before = await ReadRoleAsync(oppositeRole);
            var refused = await SendAsync(new ChangeRoleGrantsCommand(oppositeRole, before.Version, [pair]), correlation);
            Assert.Equal("forbidden", refused.Error!.Code);
            var after = await ReadRoleAsync(oppositeRole);
            Assert.Equal(before.Version, after.Version);
            Assert.Equal(before.Grants, after.Grants);
            var denial = await AuditAsync(correlation, "identity.authorization.deny");
            Assert.Equal("denied", denial.Outcome);
            using var metadata = System.Text.Json.JsonDocument.Parse(denial.Metadata);
            Assert.Equal("delegation", metadata.RootElement.GetProperty("denial_reason").GetString());
        }
        finally
        {
            await RestoreAsync(matchingKind, permissionScope);
            await RestoreAsync(oppositeKind, oppositeScope);
        }
    }

    private async Task AssertReleasedAssignablePairAsync(PermissionDefinition pair)
    {
        // Adoption is monotonic. Each release case owns its database instead of
        // permanently upgrading the v1 graph used by the five original cases.
        await using var database = await DisposableSchemaDatabase.CreateAsync(fixture.Membership.Postgres);
        await SeedSchemaAsync(database.MigrationConnectionString, database.AppConnectionString);
        await InstallReleasedPairLifecycleAsync(database.AppConnectionString);
        var release = IdentityRoleRelease.Version2;
        var administrator = RoleId(SchemaFixture.TenantA, BuiltInRoleKey.TenantAdmin, PermissionScope.Tenant);
        var correlation = "registry-release-delegation-" + Guid.NewGuid().ToString("N");
        var initial = await ReadReleasedRoleAsync(database.AppConnectionString, administrator);
        Assert.Equal(1, initial.AdoptedVersion);
        Assert.Equal(IdentityFoundationManifest.Version1.Fingerprint, initial.Fingerprint);
        Assert.DoesNotContain(pair.Scope + ":" + pair.Key, initial.Grants);
        var adoption = await SendReleasedAsync(database.AppConnectionString,
            new AdoptRoleGrantManifestCommand(administrator, initial.Version, release.Version, release.Fingerprint), correlation + "-adopt");
        Assert.True(adoption.IsSuccess);
        var adopted = await ReadReleasedRoleAsync(database.AppConnectionString, administrator);
        Assert.Equal(initial.Version + 1, adopted.Version);
        Assert.Equal(release.Version, adopted.AdoptedVersion);
        Assert.Equal(release.Fingerprint, adopted.Fingerprint);
        Assert.Equal(initial.Grants.Concat(release.TenantAdminDelta.Select(definition => definition.Scope + ":" + definition.Key))
            .Order(StringComparer.Ordinal), adopted.Grants);
        var adoptionAudit = await ReleasedPairAuditAsync(database.AppConnectionString, correlation + "-adopt", "identity.role.adopt_manifest");
        Assert.Equal("success", adoptionAudit.Outcome);
        Assert.Equal("Must:SecurityEvent:Role:" + administrator.ToString("D"), adoptionAudit.Classification);

        var matchingRole = RoleId(SchemaFixture.TenantA, BuiltInRoleKey.Instructor, PermissionScope.Tenant);
        var matchingBefore = await ReadReleasedRoleAsync(database.AppConnectionString, matchingRole);
        var positive = await SendReleasedAsync(database.AppConnectionString,
            new ChangeRoleGrantsCommand(matchingRole, matchingBefore.Version, [pair]), correlation + "-positive");
        Assert.True(positive.IsSuccess);
        var matchingAfter = await ReadReleasedRoleAsync(database.AppConnectionString, matchingRole);
        Assert.Equal(matchingBefore.Version + 1, matchingAfter.Version);
        Assert.Equal([pair.Scope + ":" + pair.Key], matchingAfter.Grants);
        var success = await ReleasedPairAuditAsync(database.AppConnectionString, correlation + "-positive", "identity.role.change_grants");
        Assert.Equal("success", success.Outcome);
        Assert.Equal("Must:SecurityEvent:Role:" + matchingRole.ToString("D"), success.Classification);

        // The real Role authoring consumer refuses a Tenant pair on an Organization
        // template. These cases do not claim a human Tenancy administration route.
        var oppositeRole = RoleId(SchemaFixture.TenantA, BuiltInRoleKey.OrgInstructor, PermissionScope.Organization);
        var before = await ReadReleasedRoleAsync(database.AppConnectionString, oppositeRole);
        var refused = await SendReleasedAsync(database.AppConnectionString,
            new ChangeRoleGrantsCommand(oppositeRole, before.Version, [pair]), correlation);
        Assert.Equal("forbidden", refused.Error!.Code);
        var after = await ReadReleasedRoleAsync(database.AppConnectionString, oppositeRole);
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(before.AdoptedVersion, after.AdoptedVersion);
        Assert.Equal(before.Fingerprint, after.Fingerprint);
        Assert.Equal(before.Grants, after.Grants);
        var denial = await ReleasedPairAuditAsync(database.AppConnectionString, correlation, "identity.authorization.deny");
        Assert.Equal("denied", denial.Outcome);
        Assert.Equal("Must:SecurityEvent::", denial.Classification);
        using var metadata = JsonDocument.Parse(denial.Metadata);
        Assert.Equal("delegation", metadata.RootElement.GetProperty("denial_reason").GetString());
        var authority = metadata.RootElement.GetProperty("authorization");
        Assert.Equal(AdminMembershipA.ToString("D"), Assert.Single(authority.GetProperty("memberships").EnumerateArray()).GetString());
        Assert.Equal(administrator.ToString("D"), Assert.Single(authority.GetProperty("roles").EnumerateArray()).GetString());
    }

    private static async Task InstallReleasedPairLifecycleAsync(string connectionString)
    {
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(connectionString);
        await SchemaQueries.AssertApplicationRoleAsync(app);
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, SchemaFixture.TenantA);
        await ExecuteAsync(app, transaction, """
            INSERT INTO public.tenants(id,slug,display_name,status,created_at,created_by,row_version)
            VALUES(@tenant,'registry-release-proof','Registry release proof','Trial',clock_timestamp(),@actor,0)
            """, ("tenant", SchemaFixture.TenantA), ("actor", UserId.SystemActor.Value));
        foreach (var organization in new[] { SchemaFixture.OrgA1, SchemaFixture.OrgA2 })
            await ExecuteAsync(app, transaction, """
                INSERT INTO public.organizations(id,tenant_id,slug,display_name,status,created_at,created_by,row_version)
                VALUES(@organization,@tenant,@slug,'Registry release branch','Active',clock_timestamp(),@actor,0)
                """, ("organization", organization), ("tenant", SchemaFixture.TenantA),
                ("slug", "branch-" + organization.ToString("N")), ("actor", UserId.SystemActor.Value));
        await transaction.CommitAsync();
    }

    private static async Task<Result<long>> SendReleasedAsync(string connectionString, IRequest<Result<long>> request, string correlation)
    {
        await using var harness = await RoleGrantPipelineHarness.CreateAsync(connectionString, Authority,
            TenantId.From(SchemaFixture.TenantA), UserId.From(AdminA), correlation);
        return await harness.Sender.Send(request);
    }

    private static async Task<ReleasedPairRoleState> ReadReleasedRoleAsync(string connectionString, Guid role)
    {
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(connectionString);
        await SchemaQueries.AssertApplicationRoleAsync(app);
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, SchemaFixture.TenantA);
        await using var command = new NpgsqlCommand("""
            SELECT row_version,adopted_release_version,adopted_release_fingerprint,
                ARRAY(SELECT permission_scope||':'||permission_key FROM role_permission_grants
                    WHERE role_id=@role ORDER BY 1) FROM roles WHERE id=@role
            """, app, transaction);
        command.Parameters.AddWithValue("role", role);
        await using var rows = await command.ExecuteReaderAsync();
        Assert.True(await rows.ReadAsync());
        var result = new ReleasedPairRoleState(rows.GetInt64(0), rows.GetInt32(1), rows.GetString(2), rows.GetFieldValue<string[]>(3));
        Assert.False(await rows.ReadAsync());
        return result;
    }

    private static async Task<(string Outcome, string Classification, string Metadata)> ReleasedPairAuditAsync(
        string connectionString, string correlation, string operation)
    {
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(connectionString);
        await SchemaQueries.AssertApplicationRoleAsync(app);
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, SchemaFixture.TenantA);
        await using var command = new NpgsqlCommand("""
            SELECT outcome,operation_class||':'||operation_type||':'||coalesce(entity_type,'')||':'||coalesce(entity_id,''),metadata::text
            FROM audit_log WHERE correlation_id=@correlation AND operation=@operation
            """, app, transaction);
        command.Parameters.AddWithValue("correlation", correlation);
        command.Parameters.AddWithValue("operation", operation);
        await using var rows = await command.ExecuteReaderAsync();
        Assert.True(await rows.ReadAsync());
        var result = (rows.GetString(0), rows.GetString(1), rows.GetString(2));
        Assert.False(await rows.ReadAsync());
        return result;
    }

    private sealed record ReleasedPairRoleState(long Version, int AdoptedVersion, string Fingerprint, string[] Grants);
}
