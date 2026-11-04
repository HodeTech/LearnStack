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
public sealed partial class RoleGrantCommandIntegrationTests(IdentityAuthorizationReaderFixture fixture)
    : IClassFixture<IdentityAuthorizationReaderFixture>
{
    [Fact]
    public async Task Actual_orgless_admin_authors_Organization_template_under_Tenant_ceiling_with_one_MUST_Role_diff()
    {
        var role = RoleId(SchemaFixture.TenantA, BuiltInRoleKey.OrgInstructor, PermissionScope.Organization);
        const string Correlation = "grant-org-authoring";
        try
        {
            await using var harness = await CreateAsync(Correlation);
            var result = await harness.Sender.Send(new ChangeRoleGrantsCommand(role, 0,
                [new(PermissionScope.Organization, IdentityFoundationManifest.MembershipAdmin)]));
            Assert.True(result.IsSuccess);
            Assert.Equal(1, result.Value);
            var graph = await ReadRoleAsync(role);
            Assert.Equal(1, graph.Version);
            Assert.Equal(["Organization:identity.membership.admin"], graph.Grants);
            var audit = await AuditAsync(Correlation, "identity.role.change_grants");
            Assert.Equal("success", audit.Outcome);
            Assert.Equal("Must:SecurityEvent:Role:" + role.ToString("D"), audit.Classification);
            using var changes = JsonDocument.Parse(audit.Changes);
            var grant = Assert.Single(changes.RootElement.EnumerateArray(), field => field.GetProperty("path").GetString()
                == "/Role/" + role.ToString("D") + "/Grants/Organization~1identity.membership.admin/PermissionKey");
            Assert.Equal(JsonValueKind.Null, grant.GetProperty("before").ValueKind);
            Assert.Equal(IdentityFoundationManifest.MembershipAdmin, grant.GetProperty("after").GetString());
            using var metadata = JsonDocument.Parse(audit.Metadata);
            var authority = metadata.RootElement.GetProperty("authorization");
            Assert.Equal("identity.role.change_grants", authority.GetProperty("policy").GetString());
            Assert.Equal(AdminMembershipA.ToString("D"), Assert.Single(authority.GetProperty("memberships").EnumerateArray()).GetString());
            Assert.Equal(RoleId(SchemaFixture.TenantA, BuiltInRoleKey.TenantAdmin, PermissionScope.Tenant).ToString("D"),
                Assert.Single(authority.GetProperty("roles").EnumerateArray()).GetString());
            Assert.DoesNotContain("provider_subject", audit.Metadata, StringComparison.Ordinal);
        }
        finally { await RestoreAsync(BuiltInRoleKey.OrgInstructor, PermissionScope.Organization); }
    }

    [Fact]
    public async Task Organization_actor_and_orgless_reissue_never_promote_Organization_grants_to_Tenant_role_administration()
    {
        var member = await fixture.AddMemberAsync(SchemaFixture.OrgA1, BuiltInRoleKey.OrgAdmin);
        var role = RoleId(SchemaFixture.TenantA, BuiltInRoleKey.Instructor, PermissionScope.Tenant);
        await using var branch = await CreateAsync("grant-org-admin-branch", member.Actor, SchemaFixture.OrgA1);
        var request = new ChangeRoleGrantsCommand(role, 0, []);
        Assert.Equal("forbidden", (await branch.Sender.Send(request)).Error!.Code);
        Assert.False(branch.Unit.HasActiveTransaction);
        await using var orgless = await CreateAsync("grant-org-admin-orgless", member.Actor);
        Assert.Equal("not_found", (await orgless.Sender.Send(request)).Error!.Code);
        Assert.False(orgless.Unit.HasActiveTransaction);
        Assert.Equal(0, (await ReadRoleAsync(role)).Version);
    }

    [Fact]
    public async Task Foreign_target_and_wrong_expected_version_are_refused_without_changes()
    {
        var foreign = RoleId(SchemaFixture.TenantB, BuiltInRoleKey.Instructor, PermissionScope.Tenant);
        Assert.Equal("not_found", (await SendAsync(new ChangeRoleGrantsCommand(foreign, 0, []), "grant-foreign-target")).Error!.Code);
        var local = RoleId(SchemaFixture.TenantA, BuiltInRoleKey.Instructor, PermissionScope.Tenant);
        Assert.Equal("concurrency_conflict", (await SendAsync(new ChangeRoleGrantsCommand(local, 99, []), "grant-target-version")).Error!.Code);
        Assert.Equal(0, (await ReadRoleAsync(local)).Version);
    }

    [Fact]
    public async Task Removed_profile_authority_cannot_self_restore_through_change_or_v1_adoption_and_kernel_cannot_be_removed()
    {
        var role = RoleId(SchemaFixture.TenantA, BuiltInRoleKey.TenantAdmin, PermissionScope.Tenant);
        var secondAdministrator = await AddSecondAdministratorAsync();
        try
        {
            var kernel = IdentityFoundationManifest.Version1.TenantAdminKernel;
            Assert.True((await SendAsync(new ChangeRoleGrantsCommand(role, 0, kernel), "grant-profile-removal")).IsSuccess);
            var full = IdentityFoundationManifest.Version1.FindTemplate(BuiltInRoleKey.TenantAdmin, PermissionScope.Tenant)!.Grants;
            Assert.Equal("forbidden", (await SendAsync(new ChangeRoleGrantsCommand(role, 1, full), "grant-self-restore")).Error!.Code);
            Assert.Equal("forbidden", (await SendAsync(new AdoptRoleGrantManifestCommand(role, 1, 1,
                IdentityFoundationManifest.Version1.Fingerprint), "grant-self-adopt")).Error!.Code);
            Assert.Equal("forbidden", (await SendAsync(new ChangeRoleGrantsCommand(role, 1, []), "grant-kernel-removal")).Error!.Code);
            Assert.Equal(1, (await ReadRoleAsync(role)).Version);
            Assert.Equal(kernel.Select(definition => definition.Scope + ":" + definition.Key).Order(StringComparer.Ordinal),
                (await ReadRoleAsync(role)).Grants);
        }
        finally
        {
            await RestoreAsync(BuiltInRoleKey.TenantAdmin, PermissionScope.Tenant);
            await ChangeAdministratorAsync(secondAdministrator, "Revoked");
        }
    }

    [Fact]
    public async Task Historical_and_unknown_adoption_cannot_restore_retained_Organization_grants()
    {
        var role = RoleId(SchemaFixture.TenantA, BuiltInRoleKey.Learner, PermissionScope.Organization);
        try
        {
            Assert.True((await SendAsync(new ChangeRoleGrantsCommand(role, 0, []), "grant-before-adoption")).IsSuccess);
            var before = await ReadRoleAsync(role);
            Assert.Equal(1, before.Version);
            Assert.Empty(before.Grants);
            Assert.Equal(1, before.AdoptedReleaseVersion);
            Assert.Equal(IdentityFoundationManifest.Version1.Fingerprint, before.AdoptedReleaseFingerprint);
            foreach (var version in new[] { 1, 2 })
            {
                var correlation = $"grant-historical-adoption-{version}";
                var refused = await SendAsync(new AdoptRoleGrantManifestCommand(role, before.Version, version,
                    IdentityFoundationManifest.Version1.Fingerprint), correlation);
                Assert.Equal("forbidden", refused.Error!.Code);
                var after = await ReadRoleAsync(role);
                Assert.Equal(before.Version, after.Version);
                Assert.Equal(before.Grants, after.Grants);
                Assert.Equal(before.AdoptedReleaseVersion, after.AdoptedReleaseVersion);
                Assert.Equal(before.AdoptedReleaseFingerprint, after.AdoptedReleaseFingerprint);
                // No outcome filter: the helper verifies exactly one resource event.
                // Refusal precedes target designation, so no Role ID is fabricated.
                var row = await AuditAsync(correlation, "identity.role.adopt_manifest");
                Assert.Equal("denied", row.Outcome);
                Assert.Equal("Must:SecurityEvent:Role:", row.Classification);
                Assert.Empty(row.Changes);
                var denial = await AuditAsync(correlation, "identity.authorization.deny");
                Assert.Equal("denied", denial.Outcome);
                Assert.Equal("Must:SecurityEvent::", denial.Classification);
                using var metadata = JsonDocument.Parse(denial.Metadata);
                Assert.Equal("capability", metadata.RootElement.GetProperty("denial_reason").GetString());
            }
        }
        finally { await RestoreAsync(BuiltInRoleKey.Learner, PermissionScope.Organization); }
    }

    [Fact]
    public async Task Actual_committed_admin_suspension_during_the_exclusive_lock_wait_refuses_the_postwait_act()
    {
        var replacement = await AddSecondAdministratorAsync();
        var role = RoleId(SchemaFixture.TenantA, BuiltInRoleKey.TenantAdmin, PermissionScope.Tenant);
        try
        {
            await using var blocker = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.AppConnectionString);
            await using var transaction = await blocker.BeginTransactionAsync();
            await ScopeAsync(blocker, transaction, SchemaFixture.TenantA);
            await LockAsync(blocker, transaction, SchemaFixture.TenantA, null, true);
            await using var harness = await CreateAsync("grant-postwait-revoked");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var running = harness.Sender.Send(new ChangeRoleGrantsCommand(role, 0, IdentityFoundationManifest.Version1.TenantAdminKernel), deadline.Token);
            var committed = false;
            try
            {
                await ObserveTenantWaitAsync(harness.ApplicationName, blocker.ProcessID, running, deadline.Token);
                await ExecuteAsync(blocker, transaction, """
                    UPDATE memberships SET status='Suspended',row_version=row_version+1
                    WHERE id=@member;
                    SET CONSTRAINTS ALL IMMEDIATE;
                    """, ("member", AdminMembershipA));
                await transaction.CommitAsync();
                committed = true;
                var result = await running;
                Assert.Equal("not_found", result.Error!.Code);
                Assert.Equal(0, (await ReadRoleAsync(role)).Version);
                var denial = await AuditAsync("grant-postwait-revoked", "identity.authorization.deny");
                using var metadata = JsonDocument.Parse(denial.Metadata);
                Assert.Empty(metadata.RootElement.GetProperty("authorization").GetProperty("memberships").EnumerateArray());
                Assert.Empty(metadata.RootElement.GetProperty("authorization").GetProperty("roles").EnumerateArray());
            }
            finally
            {
                if (!committed) await transaction.RollbackAsync();
                if (!running.IsCompleted) { deadline.Cancel(); try { await running; } catch (OperationCanceledException) { } }
            }
        }
        finally
        {
            await ChangeAdministratorAsync(AdminMembershipA, "Active", restoreVersion: true);
            await ChangeAdministratorAsync(replacement, "Revoked");
            await RestoreAsync(BuiltInRoleKey.TenantAdmin, PermissionScope.Tenant);
        }
    }

    [Fact]
    public async Task Actual_MUST_failure_rolls_back_the_Role_version_and_contained_grant_without_a_result()
    {
        const string Correlation = "grant-audit-refused";
        var role = RoleId(SchemaFixture.TenantA, BuiltInRoleKey.Instructor, PermissionScope.Tenant);
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.Membership.Postgres.MigrationConnectionString);
        await ExecuteAsync(owner, null, """
            CREATE FUNCTION fn_role_grant_audit_proof() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW.operation='identity.role.change_grants' AND NEW.correlation_id='grant-audit-refused' THEN
                    RAISE EXCEPTION 'fixed grant audit proof refusal' USING ERRCODE='23514';
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER tg_role_grant_audit_proof BEFORE INSERT ON audit_log
            FOR EACH ROW EXECUTE FUNCTION fn_role_grant_audit_proof();
            """);
        try
        {
            await using var harness = await CreateAsync(Correlation);
            Result<long>? published = null;
            var failure = await Assert.ThrowsAsync<AuditWriteFailedException>(async () => published = await harness.Sender.Send(
                new ChangeRoleGrantsCommand(role, 0, [new(PermissionScope.Tenant, IdentityFoundationManifest.ProfileWrite)])));
            Assert.Equal("audit_unavailable", failure.Error.Code);
            Assert.Null(published);
            var graph = await ReadRoleAsync(role);
            Assert.Equal(0, graph.Version);
            Assert.Empty(graph.Grants);
        }
        finally
        {
            await ExecuteAsync(owner, null, "DROP TRIGGER tg_role_grant_audit_proof ON audit_log; DROP FUNCTION fn_role_grant_audit_proof()");
            await RestoreAsync(BuiltInRoleKey.Instructor, PermissionScope.Tenant);
        }
    }

    private Task<RoleGrantPipelineHarness> CreateAsync(string correlation, Guid? actor = null, Guid? organization = null) =>
        RoleGrantPipelineHarness.CreateAsync(fixture.AppConnectionString, Authority, TenantId.From(SchemaFixture.TenantA),
            UserId.From(actor ?? AdminA), correlation, organization is { } branch ? OrganizationId.From(branch) : null);

    private async Task<Result<long>> SendAsync(IRequest<Result<long>> request, string correlation)
    {
        await using var harness = await CreateAsync(correlation);
        return await harness.Sender.Send(request);
    }

    private async Task<(long Version, string[] Grants, int AdoptedReleaseVersion, string AdoptedReleaseFingerprint)> ReadRoleAsync(Guid id)
    {
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(app);
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, SchemaFixture.TenantA);
        var version = await ScalarAsync<long>(app, transaction, "SELECT row_version FROM roles WHERE id=@id", ("id", id));
        var release = await ScalarAsync<int>(app, transaction, "SELECT adopted_release_version FROM roles WHERE id=@id", ("id", id));
        var fingerprint = await ScalarAsync<string>(app, transaction, "SELECT adopted_release_fingerprint FROM roles WHERE id=@id", ("id", id));
        await using var command = new NpgsqlCommand("SELECT permission_scope||':'||permission_key FROM role_permission_grants WHERE role_id=@id ORDER BY 1", app, transaction);
        command.Parameters.AddWithValue("id", id);
        await using var rows = await command.ExecuteReaderAsync();
        var grants = new List<string>();
        while (await rows.ReadAsync()) grants.Add(rows.GetString(0));
        return (version, grants.ToArray(), release, fingerprint);
    }

    private async Task RestoreAsync(BuiltInRoleKey key, PermissionScope scope)
    {
        var template = IdentityFoundationManifest.Version1.FindTemplate(key, scope)!;
        var role = RoleId(SchemaFixture.TenantA, key, scope);
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, SchemaFixture.TenantA);
        await LockAsync(app, transaction, SchemaFixture.TenantA, null, true);
        await ExecuteAsync(app, transaction, "DELETE FROM role_permission_grants WHERE role_id=@role AND NOT(permission_key=ANY(@keys))",
            ("role", role), ("keys", template.Grants.Select(definition => definition.Key).ToArray()));
        foreach (var definition in template.Grants)
            await ExecuteAsync(app, transaction, """
                INSERT INTO role_permission_grants(tenant_id,role_id,permission_scope,permission_key)
                VALUES(@tenant,@role,@scope,@key) ON CONFLICT DO NOTHING
                """, ("tenant", SchemaFixture.TenantA), ("role", role), ("scope", scope.ToString()), ("key", definition.Key));
        await ExecuteAsync(app, transaction, "UPDATE roles SET row_version=0,updated_at=NULL,updated_by=NULL WHERE id=@role; SET CONSTRAINTS ALL IMMEDIATE", ("role", role));
        await transaction.CommitAsync();
    }

    private async Task<(string Outcome, string Classification, string Changes, string Metadata)> AuditAsync(
        string correlation, string operation, string? outcome = null)
    {
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, SchemaFixture.TenantA);
        await using var command = new NpgsqlCommand("""
            SELECT outcome,operation_class||':'||operation_type||':'||coalesce(entity_type,'')||':'||coalesce(entity_id,''),
                coalesce(changes::text,''),metadata::text FROM audit_log
            WHERE correlation_id=@correlation AND operation=@operation AND (@outcome IS NULL OR outcome=@outcome)
            """, app, transaction);
        command.Parameters.AddWithValue("correlation", correlation);
        command.Parameters.AddWithValue("operation", operation);
        command.Parameters.Add(new NpgsqlParameter("outcome", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)outcome ?? DBNull.Value });
        await using var rows = await command.ExecuteReaderAsync();
        Assert.True(await rows.ReadAsync());
        var result = (rows.GetString(0), rows.GetString(1), rows.GetString(2), rows.GetString(3));
        Assert.False(await rows.ReadAsync());
        return result;
    }

    private async Task<Guid> AddSecondAdministratorAsync()
    {
        var actor = Guid.CreateVersion7();
        var member = Guid.CreateVersion7();
        await InsertUserAsync(fixture.AppConnectionString, actor);
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, SchemaFixture.TenantA);
        await LockAsync(app, transaction, SchemaFixture.TenantA, actor, true);
        await InsertMemberAsync(app, transaction, SchemaFixture.TenantA, null, member, actor,
            RoleId(SchemaFixture.TenantA, BuiltInRoleKey.TenantAdmin, PermissionScope.Tenant));
        await transaction.CommitAsync();
        return member;
    }

    private async Task ChangeAdministratorAsync(Guid member, string status, bool restoreVersion = false)
    {
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, SchemaFixture.TenantA);
        Guid? user = status == "Active" ? await ScalarAsync<Guid>(app, transaction,
            "SELECT user_id FROM memberships WHERE id=@member", ("member", member)) : null;
        await LockAsync(app, transaction, SchemaFixture.TenantA, user, true);
        await ExecuteAsync(app, transaction, """
            UPDATE memberships SET status=@status,row_version=CASE WHEN @restore THEN 0 ELSE row_version+1 END,
                deleted_at=CASE WHEN @status='Revoked' THEN clock_timestamp() ELSE NULL END,
                deleted_by=CASE WHEN @status='Revoked' THEN @actor ELSE NULL END
            WHERE id=@member AND (status<>@status OR @restore);
            SET CONSTRAINTS ALL IMMEDIATE;
            """, ("member", member), ("status", status), ("actor", UserId.SystemActor.Value), ("restore", restoreVersion));
        await transaction.CommitAsync();
    }

    private async Task ObserveTenantWaitAsync(string application, int blocker, Task running, CancellationToken cancellationToken)
    {
        await using var observer = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.AppConnectionString, cancellationToken);
        while (true)
        {
            await using var command = new NpgsqlCommand("""
                SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE application_name=@application
                    AND @blocker=ANY(pg_blocking_pids(pid)) AND wait_event_type='Lock')
                """, observer);
            command.Parameters.AddWithValue("application", application);
            command.Parameters.AddWithValue("blocker", blocker);
            if ((bool)(await command.ExecuteScalarAsync(cancellationToken))!) return;
            if (running.IsCompleted) { await running; Assert.Fail("The real Role mutation did not reach the observed Tenant lock wait."); }
            await Task.Delay(20, cancellationToken);
        }
    }
}
