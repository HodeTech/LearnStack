using System.Text.Json;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using Npgsql;
using Xunit;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed class TenancyAdministrationRaceTests(TenancyAdministrationFixture fixture) : IClassFixture<TenancyAdministrationFixture>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Archive_and_default_competitors_wait_then_use_committed_lifecycle_facts(bool defaultWins)
    {
        var create = await fixture.SendAsync(new CreateManagedOrganizationCommand("race-" + Guid.NewGuid().ToString("N"), "Race branch"), "tenancy-race-create");
        Assert.True(create.IsSuccess);
        var id = create.Value!.OrganizationId;
        var tenantVersion = await fixture.TenantVersionAsync();
        await using var holder = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(holder);
        await using var transaction = await holder.BeginTransactionAsync();
        await ScopeAsync(holder, transaction, SchemaFixture.TenantA);
        await LockAsync(holder, transaction, SchemaFixture.TenantA, null, true);
        await ExecuteAsync(holder, transaction, defaultWins
            ? "UPDATE tenants SET default_organization_id=@id,row_version=row_version+1 WHERE id=@tenant"
            : "UPDATE organizations SET status='Archived',row_version=row_version+1 WHERE id=@id",
            ("id", id.Value), ("tenant", SchemaFixture.TenantA));
        var correlation = "tenancy-race-loser-" + Guid.NewGuid().ToString("N");
        await using var contender = await fixture.CreateAsync(correlation);
        var pending = defaultWins
            ? RejectArchiveAsync()
            : RejectDefaultAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await fixture.Membership.ObserveWaitAsync(contender.ApplicationName, holder.ProcessID, pending, deadline.Token);
        Assert.False(pending.IsCompleted);
        await transaction.CommitAsync();
        Assert.Equal(defaultWins ? "business_rule_violation" : "not_found", await pending);
        var detail = await fixture.SendAsync(new GetOrganizationDetailQuery(id), "tenancy-race-current-detail");
        Assert.True(detail.IsSuccess);
        Assert.Equal(defaultWins, detail.Value!.IsDefault);
        Assert.Equal(defaultWins ? ManagedOrganizationStatus.Active : ManagedOrganizationStatus.Archived, detail.Value.Status);
        var operation = defaultWins ? "tenancy.organization.archive" : "tenancy.organization.select_default";
        foreach (var row in await fixture.AuditAsync(correlation, operation))
        {
            using var audit = JsonDocument.Parse(row);
            Assert.NotEqual("success", audit.RootElement.GetProperty("outcome").GetString());
        }
        if (defaultWins)
            Assert.True((await fixture.SendAsync(new SelectDefaultOrganizationCommand(OrganizationId.From(SchemaFixture.OrgA1),
                await fixture.TenantVersionAsync()), "tenancy-race-restore-default")).IsSuccess);

        async Task<string> RejectArchiveAsync() => (await contender.Sender.Send(new ArchiveOrganizationCommand(id, create.Value.Version))).Error!.Code;
        async Task<string> RejectDefaultAsync() => (await contender.Sender.Send(new SelectDefaultOrganizationCommand(id, tenantVersion))).Error!.Code;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Direct_app_role_requires_exact_exclusive_fence_for_default_change(bool sharedOnly)
    {
        var create = await fixture.SendAsync(new CreateManagedOrganizationCommand("fence-" + Guid.NewGuid().ToString("N"), "Fence branch"), "tenancy-fence-create");
        Assert.True(create.IsSuccess);
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(app);
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await ScopeAsync(app, transaction, SchemaFixture.TenantA);
            if (sharedOnly) await LockAsync(app, transaction, SchemaFixture.TenantA, null, false);
            var refused = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(app, transaction,
                "UPDATE tenants SET default_organization_id=@id WHERE id=@tenant",
                ("id", create.Value!.OrganizationId.Value), ("tenant", SchemaFixture.TenantA)));
            Assert.Equal("P0311", refused.SqlState);
            await transaction.RollbackAsync();
        }
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await ScopeAsync(app, transaction, SchemaFixture.TenantA);
            await LockAsync(app, transaction, SchemaFixture.TenantA, null, true);
            await ExecuteAsync(app, transaction, "UPDATE tenants SET default_organization_id=@id WHERE id=@tenant",
                ("id", create.Value!.OrganizationId.Value), ("tenant", SchemaFixture.TenantA));
            await ExecuteAsync(app, transaction, "SET CONSTRAINTS ALL IMMEDIATE");
            await transaction.RollbackAsync();
        }
        // The trigger computes its own signed bigint key, independently compared with the C# protocol.
        Assert.Equal(IdentityAdvisoryLockKey.ForTenant(TenantId.From(SchemaFixture.TenantA)),
            await ScalarAsync<long>(app, null, """
            SELECT ('x'||substr(encode(sha256(int4send(octet_length(convert_to('learnstack.identity.lock.v1','UTF8')))
                ||convert_to('learnstack.identity.lock.v1','UTF8')||int4send(6)||convert_to('tenant','UTF8')
                ||int4send(16)||uuid_send(@tenant)),'hex'),1,16))::bit(64)::bigint
            """, ("tenant", SchemaFixture.TenantA)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Last_active_organization_guard_serializes_real_competing_commits(bool reverse)
    {
        var tenant = Guid.CreateVersion7();
        var organizations = new[] { Guid.CreateVersion7(), Guid.CreateVersion7() };
        await using var holder = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(holder);
        await using (var setup = await holder.BeginTransactionAsync())
        {
            await ScopeAsync(holder, setup, tenant);
            await ExecuteAsync(holder, setup, """
                INSERT INTO tenants(id,slug,display_name,status,created_at,created_by)
                VALUES(@tenant,@slug,'Incomplete fixture','Trial',clock_timestamp(),@actor)
                """, ("tenant", tenant), ("slug", "last-active-" + tenant.ToString("N")), ("actor", UserId.SystemActor.Value));
            foreach (var organization in organizations)
                await ExecuteAsync(holder, setup, """
                    INSERT INTO organizations(id,tenant_id,slug,display_name,status,created_at,created_by)
                    VALUES(@id,@tenant,@slug,'Last active fixture','Active',clock_timestamp(),@actor)
                    """, ("id", organization), ("tenant", tenant), ("slug", "org-" + organization.ToString("N")), ("actor", UserId.SystemActor.Value));
            await setup.CommitAsync();
        }
        await using var transaction = await holder.BeginTransactionAsync();
        await ScopeAsync(holder, transaction, tenant);
        await LockAsync(holder, transaction, tenant, null, true);
        await ExecuteAsync(holder, transaction, "UPDATE organizations SET status='Archived' WHERE id=@id", ("id", organizations[reverse ? 1 : 0]));
        var application = "tenancy-last-active-" + Guid.NewGuid().ToString("N");
        var pending = CompeteAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await fixture.Membership.ObserveWaitAsync(application, holder.ProcessID, pending, deadline.Token);
        await transaction.CommitAsync();
        Assert.Equal("P0312", await pending);
        await using var read = await holder.BeginTransactionAsync();
        await ScopeAsync(holder, read, tenant);
        Assert.Equal(1L, await ScalarAsync<long>(holder, read, "SELECT count(*) FROM organizations WHERE status='Active'"));
        Assert.Equal(1L, await ScalarAsync<long>(holder, read, "SELECT count(*) FROM organizations WHERE status='Archived'"));

        async Task<string> CompeteAsync()
        {
            var connection = new NpgsqlConnectionStringBuilder(fixture.AppConnectionString) { ApplicationName = application }.ConnectionString;
            await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(connection);
            await SchemaQueries.AssertApplicationRoleAsync(app);
            await using var competing = await app.BeginTransactionAsync();
            await ScopeAsync(app, competing, tenant);
            await LockAsync(app, competing, tenant, null, true);
            var failure = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(app, competing,
                "UPDATE organizations SET status='Archived' WHERE id=@id", ("id", organizations[reverse ? 0 : 1])));
            await competing.RollbackAsync();
            return failure.SqlState;
        }
    }

    [Fact]
    public async Task Actual_MUST_default_audit_failure_rolls_back_reference_and_version()
    {
        var created = await fixture.SendAsync(new CreateManagedOrganizationCommand("audit-" + Guid.NewGuid().ToString("N"), "Audit branch"), "tenancy-audit-create");
        Assert.True(created.IsSuccess);
        var version = await fixture.TenantVersionAsync();
        var before = await fixture.SendAsync(new ListOrganizationsQuery(), "tenancy-audit-before");
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.OwnerConnectionString);
        await ExecuteAsync(owner, null, """
            CREATE FUNCTION tenancy_default_audit_fault() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW.correlation_id='tenancy-default-audit-refusal' AND NEW.operation='tenancy.organization.select_default' THEN
                    RAISE EXCEPTION 'fixed audit refusal' USING ERRCODE='23514';
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER tg_tenancy_default_audit_fault BEFORE INSERT ON audit_log
            FOR EACH ROW EXECUTE FUNCTION tenancy_default_audit_fault();
            """);
        try
        {
            await Assert.ThrowsAsync<AuditWriteFailedException>(() => fixture.SendAsync(new SelectDefaultOrganizationCommand(created.Value!.OrganizationId, version), "tenancy-default-audit-refusal"));
            Assert.Equal(version, await fixture.TenantVersionAsync());
            var after = await fixture.SendAsync(new ListOrganizationsQuery(), "tenancy-audit-after");
            Assert.Equal(before.Value!.DefaultOrganizationId, after.Value!.DefaultOrganizationId);
        }
        finally { await ExecuteAsync(owner, null, "DROP TRIGGER tg_tenancy_default_audit_fault ON audit_log; DROP FUNCTION tenancy_default_audit_fault()"); }
    }

    [Fact]
    public async Task Existing_insert_first_provisioning_completes_temporary_NULL_default_without_new_fence()
    {
        var tenant = Guid.CreateVersion7();
        var organization = Guid.CreateVersion7();
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(app);
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, tenant);
        await ExecuteAsync(app, transaction, """
            INSERT INTO tenants(id,slug,display_name,status,created_at,created_by)
            VALUES(@tenant,@slug,'Provisioning compatibility','Trial',clock_timestamp(),@actor);
            INSERT INTO organizations(id,tenant_id,slug,display_name,status,created_at,created_by)
            VALUES(@organization,@tenant,'main','Main','Active',clock_timestamp(),@actor);
            UPDATE tenants SET default_organization_id=@organization WHERE id=@tenant;
            SET CONSTRAINTS ALL IMMEDIATE;
            """, ("tenant", tenant), ("organization", organization), ("slug", "provisioning-" + tenant.ToString("N")), ("actor", UserId.SystemActor.Value));
        await transaction.CommitAsync();
        await using var read = await app.BeginTransactionAsync();
        await ScopeAsync(app, read, tenant);
        Assert.Equal(organization, await ScalarAsync<Guid>(app, read, "SELECT default_organization_id FROM tenants WHERE id=@tenant", ("tenant", tenant)));
    }
}
