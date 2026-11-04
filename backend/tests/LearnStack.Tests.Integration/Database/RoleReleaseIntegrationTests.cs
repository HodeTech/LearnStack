using System.Collections.Immutable;
using System.Text.Json;
using LearnStack.Modules.Identity.Application.Contracts.Roles;
using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Results;
using MediatR;
using Npgsql;
using Xunit;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed class RoleReleaseIntegrationTests(IdentityAuthorizationReaderFixture fixture)
    : IClassFixture<IdentityAuthorizationReaderFixture>
{
    [Fact]
    public async Task Actual_first_v2_adoption_is_atomic_preserves_profile_removal_and_cannot_repeat_after_a_new_key_is_removed()
    {
        var tenant = SchemaFixture.TenantA;
        var role = RoleId(tenant, BuiltInRoleKey.TenantAdmin, PermissionScope.Tenant);
        var release = IdentityRoleRelease.Version2;
        var kernel = IdentityFoundationManifest.Version1.TenantAdminKernel;
        Assert.True((await SendAsync(tenant, AdminA, new ChangeRoleGrantsCommand(role, 0, kernel), "release-remove-profile")).IsSuccess);
        Assert.Equal("forbidden", (await SendAsync(tenant, AdminA,
            new ChangeRoleGrantsCommand(role, 1, [.. kernel, .. release.TenantAdminDelta]), "release-no-ordinary-exception")).Error!.Code);
        Assert.Equal("forbidden", (await SendAsync(tenant, AdminA,
            new AdoptRoleGrantManifestCommand(role, 1, 2, IdentityFoundationManifest.Version1.Fingerprint), "release-wrong-fingerprint")).Error!.Code);

        await AssertAuditFailureRollsBackAsync(tenant, role);
        var adoption = await SendAsync(tenant, AdminA, new AdoptRoleGrantManifestCommand(role, 1, release.Version, release.Fingerprint), "release-first-adoption");
        Assert.True(adoption.IsSuccess);
        Assert.Equal(2, adoption.Value);
        var adopted = await ReadAsync(tenant, role);
        Assert.Equal(2, adopted.AdoptedVersion);
        Assert.Equal(release.Fingerprint, adopted.Fingerprint);
        Assert.Equal(Tokens(kernel.Concat(release.TenantAdminDelta)), adopted.Grants);
        Assert.DoesNotContain(adopted.Grants, key => key.Contains(IdentityFoundationManifest.ProfileWrite, StringComparison.Ordinal));
        await using (var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.AppConnectionString))
        {
            await using var transaction = await app.BeginTransactionAsync();
            await ScopeAsync(app, transaction, tenant);
            var changes = await ScalarAsync<string>(app, transaction, """
                SELECT changes::text FROM audit_log WHERE correlation_id='release-first-adoption'
                    AND operation='identity.role.adopt_manifest' AND outcome='success'
                    AND operation_class='Must' AND operation_type='SecurityEvent' AND entity_type='Role' AND entity_id=@role
                """, ("role", role.ToString("D")));
            using var json = JsonDocument.Parse(changes);
            Assert.Contains(json.RootElement.EnumerateArray(), field => field.GetProperty("path").GetString()!.EndsWith("/AdoptedReleaseVersion", StringComparison.Ordinal));
            Assert.All(release.TenantAdminDelta, definition => Assert.Contains(definition.Key, changes, StringComparison.Ordinal));
            var check = await ScalarAsync<string>(app, transaction, """
                SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conname='ck_role_permission_grants_registry'
                    AND conrelid='public.role_permission_grants'::regclass
                """);
            Assert.All(release.Definitions, definition => Assert.Contains(definition.Key, check, StringComparison.Ordinal));
        }

        var removed = release.TenantAdminDelta[0];
        ImmutableArray<PermissionDefinition> remaining = [.. kernel, .. release.TenantAdminDelta.Where(pair => pair != removed)];
        Assert.True((await SendAsync(tenant, AdminA, new ChangeRoleGrantsCommand(role, 2, remaining), "release-remove-adopted-key")).IsSuccess);
        Assert.Equal("forbidden", (await SendAsync(tenant, AdminA,
            new AdoptRoleGrantManifestCommand(role, 3, release.Version, release.Fingerprint), "release-repeat-refused")).Error!.Code);
        Assert.Equal("forbidden", (await SendAsync(tenant, AdminA,
            new ChangeRoleGrantsCommand(role, 3, remaining.Add(removed)), "release-ordinary-restore-refused")).Error!.Code);
        Assert.Equal("forbidden", (await SendAsync(tenant, AdminA,
            new ChangeRoleGrantsCommand(role, 3, remaining.Add(new(PermissionScope.Tenant, IdentityFoundationManifest.ProfileWrite))),
            "release-profile-restore-refused")).Error!.Code);
        var final = await ReadAsync(tenant, role);
        Assert.Equal(3, final.Version);
        Assert.Equal(2, final.AdoptedVersion);
        Assert.Equal(release.Fingerprint, final.Fingerprint);
        Assert.Equal(Tokens(remaining), final.Grants);
        await AssertResetRefusedAsync(tenant, role);
        await AssertUnregisteredPairRefusedAsync(tenant, PermissionScope.Tenant, "tenancy.unregistered.write", BuiltInRoleKey.Instructor);
        await AssertUnregisteredPairRefusedAsync(tenant, PermissionScope.Organization,
            BoundedTenancyAdministrationPermissions.OrganizationAdministration.Key, BuiltInRoleKey.OrgInstructor);
    }

    [Fact]
    public async Task First_adoption_waits_for_the_exclusive_Tenant_fence_then_refuses_current_revocation_and_stale_Role_version()
    {
        var tenant = SchemaFixture.TenantB;
        var role = RoleId(tenant, BuiltInRoleKey.TenantAdmin, PermissionScope.Tenant);
        await AddReplacementAdministratorAsync(tenant);
        await WithTenantWaitAsync(tenant, role, "release-wait-revoke", async (app, transaction) =>
        {
            await ExecuteAsync(app, transaction, """
                UPDATE memberships SET status='Suspended',row_version=row_version+1 WHERE id=@member;
                SET CONSTRAINTS ALL IMMEDIATE;
                """, ("member", AdminMembershipB));
        }, "not_found");
        Assert.Equal(1, (await ReadAsync(tenant, role)).AdoptedVersion);
        await using (var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.AppConnectionString))
        {
            await using var transaction = await app.BeginTransactionAsync();
            await ScopeAsync(app, transaction, tenant);
            await LockAsync(app, transaction, tenant, AdminB, true);
            await ExecuteAsync(app, transaction, "UPDATE memberships SET status='Active',row_version=row_version+1 WHERE id=@member; SET CONSTRAINTS ALL IMMEDIATE",
                ("member", AdminMembershipB));
            await transaction.CommitAsync();
        }
        await WithTenantWaitAsync(tenant, role, "release-wait-stale-version", async (app, transaction) =>
        {
            await ExecuteAsync(app, transaction, """
                DELETE FROM role_permission_grants WHERE role_id=@role AND permission_key=@profile;
                UPDATE roles SET row_version=row_version+1 WHERE id=@role;
                SET CONSTRAINTS ALL IMMEDIATE;
                """, ("role", role), ("profile", IdentityFoundationManifest.ProfileWrite));
        }, "concurrency_conflict");
        var after = await ReadAsync(tenant, role);
        Assert.Equal(1, after.Version);
        Assert.Equal(1, after.AdoptedVersion);
        Assert.Equal(Tokens(IdentityFoundationManifest.Version1.TenantAdminKernel), after.Grants);
    }

    [Fact]
    public async Task Grant_writer_uses_an_actual_conflicting_Role_lock_and_rechecks_version_after_that_wait()
    {
        var tenant = SchemaFixture.TenantB;
        var role = RoleId(tenant, BuiltInRoleKey.Instructor, PermissionScope.Tenant);
        await using var blocker = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(blocker);
        await using var transaction = await blocker.BeginTransactionAsync();
        await ScopeAsync(blocker, transaction, tenant);
        await LockAsync(blocker, transaction, tenant, null, false);
        await ExecuteAsync(blocker, transaction, "SELECT id FROM roles WHERE id=@role FOR NO KEY UPDATE", ("role", role));
        await using var harness = await CreateAsync(tenant, AdminB, "release-role-root-wait");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var running = harness.Sender.Send(new ChangeRoleGrantsCommand(role, 0,
            [new(PermissionScope.Tenant, IdentityFoundationManifest.MembershipAdmin)]), deadline.Token);
        var committed = false;
        try
        {
            await ObserveWaitAsync(harness.ApplicationName, blocker.ProcessID, running, deadline.Token, rootLock: true);
            await ExecuteAsync(blocker, transaction, """
                INSERT INTO role_permission_grants(tenant_id,role_id,permission_scope,permission_key)
                VALUES(@tenant,@role,'Tenant',@key);
                UPDATE roles SET row_version=row_version+1 WHERE id=@role;
                SET CONSTRAINTS ALL IMMEDIATE;
                """, ("tenant", tenant), ("role", role), ("key", IdentityFoundationManifest.ProfileWrite));
            await transaction.CommitAsync();
            committed = true;
            Assert.Equal("concurrency_conflict", (await running).Error!.Code);
            Assert.Equal(["Tenant:" + IdentityFoundationManifest.ProfileWrite], (await ReadAsync(tenant, role)).Grants);
        }
        finally
        {
            if (!committed) await transaction.RollbackAsync();
            if (!running.IsCompleted) { deadline.Cancel(); try { await running; } catch (OperationCanceledException) { } }
        }
    }

    private async Task AssertAuditFailureRollsBackAsync(Guid tenant, Guid role)
    {
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.Membership.Postgres.MigrationConnectionString);
        await ExecuteAsync(owner, null, """
            CREATE FUNCTION fn_role_release_audit_proof() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW.operation='identity.role.adopt_manifest' AND NEW.correlation_id='release-audit-rollback' THEN
                    RAISE EXCEPTION 'fixed release audit proof refusal' USING ERRCODE='23514';
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER tg_role_release_audit_proof BEFORE INSERT ON audit_log
                FOR EACH ROW EXECUTE FUNCTION fn_role_release_audit_proof();
            """);
        try
        {
            var release = IdentityRoleRelease.Version2;
            Result<long>? published = null;
            await Assert.ThrowsAsync<AuditWriteFailedException>(async () => published = await SendAsync(tenant, AdminA,
                new AdoptRoleGrantManifestCommand(role, 1, release.Version, release.Fingerprint), "release-audit-rollback"));
            Assert.Null(published);
            var rolledBack = await ReadAsync(tenant, role);
            Assert.Equal(1, rolledBack.Version);
            Assert.Equal(1, rolledBack.AdoptedVersion);
            Assert.Equal(IdentityFoundationManifest.Version1.Fingerprint, rolledBack.Fingerprint);
            Assert.Equal(Tokens(IdentityFoundationManifest.Version1.TenantAdminKernel), rolledBack.Grants);
        }
        finally { await ExecuteAsync(owner, null, "DROP TRIGGER tg_role_release_audit_proof ON audit_log; DROP FUNCTION fn_role_release_audit_proof()"); }
    }

    private async Task AssertResetRefusedAsync(Guid tenant, Guid role)
    {
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(app);
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, tenant);
        await LockAsync(app, transaction, tenant, null, true);
        var failure = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(app, transaction, """
            UPDATE roles SET adopted_release_version=1,adopted_release_fingerprint=@initial,row_version=row_version+1 WHERE id=@role
            """, ("initial", IdentityFoundationManifest.Version1.Fingerprint), ("role", role)));
        Assert.Equal("P0304", failure.SqlState);
        await transaction.RollbackAsync();
    }

    private async Task AssertUnregisteredPairRefusedAsync(Guid tenant, PermissionScope scope, string key, BuiltInRoleKey template)
    {
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(app);
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, tenant);
        await LockAsync(app, transaction, tenant, null, false);
        var failure = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(app, transaction, """
            INSERT INTO role_permission_grants(tenant_id,role_id,permission_scope,permission_key)
            VALUES(@tenant,@role,@scope,@key)
            """, ("tenant", tenant), ("role", RoleId(tenant, template, scope)), ("scope", scope.ToString()), ("key", key)));
        Assert.Equal("23514", failure.SqlState);
        Assert.Equal("ck_role_permission_grants_registry", failure.ConstraintName);
        await transaction.RollbackAsync();
    }

    private async Task WithTenantWaitAsync(Guid tenant, Guid role, string correlation,
        Func<NpgsqlConnection, NpgsqlTransaction, Task> mutate, string expectedFailure)
    {
        await using var blocker = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(blocker);
        await using var transaction = await blocker.BeginTransactionAsync();
        await ScopeAsync(blocker, transaction, tenant);
        await LockAsync(blocker, transaction, tenant, null, true);
        await using var harness = await CreateAsync(tenant, AdminB, correlation);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var release = IdentityRoleRelease.Version2;
        var running = harness.Sender.Send(new AdoptRoleGrantManifestCommand(role, 0, release.Version, release.Fingerprint), deadline.Token);
        var committed = false;
        try
        {
            await ObserveWaitAsync(harness.ApplicationName, blocker.ProcessID, running, deadline.Token);
            await mutate(blocker, transaction);
            await transaction.CommitAsync();
            committed = true;
            Assert.Equal(expectedFailure, (await running).Error!.Code);
        }
        finally
        {
            if (!committed) await transaction.RollbackAsync();
            if (!running.IsCompleted) { deadline.Cancel(); try { await running; } catch (OperationCanceledException) { } }
        }
    }

    private async Task AddReplacementAdministratorAsync(Guid tenant)
    {
        var actor = Guid.CreateVersion7();
        await InsertUserAsync(fixture.AppConnectionString, actor);
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, tenant);
        await LockAsync(app, transaction, tenant, actor, true);
        await InsertMemberAsync(app, transaction, tenant, null, Guid.CreateVersion7(), actor,
            RoleId(tenant, BuiltInRoleKey.TenantAdmin, PermissionScope.Tenant));
        await ExecuteAsync(app, transaction, "SET CONSTRAINTS ALL IMMEDIATE");
        await transaction.CommitAsync();
    }

    private Task<RoleGrantPipelineHarness> CreateAsync(Guid tenant, Guid actor, string correlation) =>
        RoleGrantPipelineHarness.CreateAsync(fixture.AppConnectionString, Authority, TenantId.From(tenant), UserId.From(actor), correlation);

    private async Task<Result<long>> SendAsync(Guid tenant, Guid actor, IRequest<Result<long>> request, string correlation)
    {
        await using var harness = await CreateAsync(tenant, actor, correlation);
        return await harness.Sender.Send(request);
    }

    private async Task<RoleState> ReadAsync(Guid tenant, Guid role)
    {
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(app);
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, tenant);
        await using var command = new NpgsqlCommand("""
            SELECT row_version,adopted_release_version,adopted_release_fingerprint,
                ARRAY(SELECT permission_scope||':'||permission_key FROM role_permission_grants
                    WHERE role_id=@role ORDER BY 1) FROM roles WHERE id=@role
            """, app, transaction);
        command.Parameters.AddWithValue("role", role);
        await using var rows = await command.ExecuteReaderAsync();
        Assert.True(await rows.ReadAsync());
        return new(rows.GetInt64(0), rows.GetInt32(1), rows.GetString(2), rows.GetFieldValue<string[]>(3));
    }

    private async Task ObserveWaitAsync(string application, int blocker, Task running, CancellationToken cancellationToken, bool rootLock = false)
    {
        await using var observer = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.AppConnectionString, cancellationToken);
        while (true)
        {
            if (await ScalarAsync<bool>(observer, null, """
                SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE application_name=@application
                    AND @blocker=ANY(pg_blocking_pids(pid)) AND wait_event_type='Lock'
                    AND (NOT @root_lock OR query LIKE '%SELECT id FROM public.roles%FOR NO KEY UPDATE%'))
                """, ("application", application), ("blocker", blocker), ("root_lock", rootLock))) return;
            if (running.IsCompleted) { await running; Assert.Fail("The real Role release writer did not reach its observed lock wait."); }
            await Task.Delay(20, cancellationToken);
        }
    }

    private static string[] Tokens(IEnumerable<PermissionDefinition> definitions) =>
        [.. definitions.Select(definition => definition.Scope + ":" + definition.Key).Order(StringComparer.Ordinal)];
    private sealed record RoleState(long Version, int AdoptedVersion, string Fingerprint, string[] Grants);
}
