using System.Collections.Immutable;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Identity.Application.Contracts.Memberships;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Tenancy.Application.Contracts.Locales;
using LearnStack.Modules.Tenancy.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Results;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

/// <summary>Each proof class owns an isolated migrated PostgreSQL database and real app-role authorization pipeline.</summary>
public sealed class MembershipAdministrationFixture : IAsyncLifetime
{
    public IdentityAuthorizationReaderFixture Authorization { get; } = new();
    public string AppConnectionString => Authorization.AppConnectionString;
    public string OwnerConnectionString => Authorization.Membership.Postgres.MigrationConnectionString;

    public async Task InitializeAsync()
    {
        await Authorization.InitializeAsync();
        foreach (var tenant in new[] { SchemaFixture.TenantA, SchemaFixture.TenantB })
            await MutateAsync(tenant, null, null, true, (app, transaction) => ExecuteAsync(app, transaction, """
                INSERT INTO tenant_locales(tenant_id,locale,is_default,is_enabled,sort)
                VALUES(@tenant,'en',true,true,0),(@tenant,'tr',false,true,1);
                UPDATE tenants SET default_organization_id=@organization
                WHERE id=@tenant;
                """, ("tenant", tenant), ("organization", tenant == SchemaFixture.TenantA ? SchemaFixture.OrgA1 : SchemaFixture.OrgB1)));
    }

    public Task DisposeAsync() => Authorization.DisposeAsync();

    internal Task<RoleGrantPipelineHarness> CreateAsync(string correlation, Guid? actor = null, Guid? organization = null) =>
        RoleGrantPipelineHarness.CreateAsync(AppConnectionString, Authority, TenantId.From(SchemaFixture.TenantA),
            UserId.From(actor ?? AdminA), correlation, organization is { } branch ? OrganizationId.From(branch) : null,
            services =>
            {
                services.AddModuleDbContext<TenancyDbContext>();
                services.AddScoped<ITenantLocaleEligibilityReader, TenantLocaleEligibilityReader>();
            });

    public async Task<Result<T>> SendAsync<T>(IRequest<Result<T>> request, string correlation, Guid? actor = null, Guid? organization = null)
    {
        await using var harness = await CreateAsync(correlation, actor, organization);
        return await harness.Sender.Send(request);
    }

    public async Task<(Guid Actor, Guid Membership)> AddMemberAsync(Guid? organization = null, BuiltInRoleKey role = BuiltInRoleKey.Learner)
    {
        var actor = Guid.CreateVersion7();
        await InsertUserAsync(AppConnectionString, actor);
        return (actor, await AddMembershipAsync(actor, organization, role));
    }

    public async Task<Guid> AddMembershipAsync(Guid actor, Guid? organization, BuiltInRoleKey role = BuiltInRoleKey.Learner)
    {
        var member = Guid.CreateVersion7();
        await MutateAsync(SchemaFixture.TenantA, organization, actor, role == BuiltInRoleKey.TenantAdmin,
            (app, transaction) => InsertMemberAsync(app, transaction, SchemaFixture.TenantA, organization, member, actor,
                RoleId(SchemaFixture.TenantA, role, organization is null ? PermissionScope.Tenant : PermissionScope.Organization)));
        return member;
    }

    public async Task<MemberState> ReadAsync(Guid membership, Guid? organization = null, Guid? tenant = null)
    {
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(app);
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, tenant ?? SchemaFixture.TenantA, organization);
        await using var command = new NpgsqlCommand("""
            SELECT m.row_version,m.status,p.display_name,p.preferred_locale,m.xmin::text
            FROM memberships m JOIN membership_profiles p ON p.tenant_id=m.tenant_id AND p.membership_id=m.id
            WHERE m.id=@member
            """, app, transaction);
        command.Parameters.AddWithValue("member", membership);
        long version;
        string status, stamp;
        string? display, locale;
        await using (var rows = await command.ExecuteReaderAsync())
        {
            Assert.True(await rows.ReadAsync());
            version = rows.GetInt64(0); status = rows.GetString(1);
            display = rows.IsDBNull(2) ? null : rows.GetString(2);
            locale = rows.IsDBNull(3) ? null : rows.GetString(3); stamp = rows.GetString(4);
            Assert.False(await rows.ReadAsync());
        }
        var assignments = ImmutableDictionary.CreateBuilder<Guid, string>();
        await using var roles = new NpgsqlCommand("SELECT role_id,xmin::text FROM membership_role_assignments WHERE membership_id=@member", app, transaction);
        roles.Parameters.AddWithValue("member", membership);
        await using var roleRows = await roles.ExecuteReaderAsync();
        while (await roleRows.ReadAsync()) assignments.Add(roleRows.GetGuid(0), roleRows.GetString(1));
        return new(version, status, display, locale, stamp, assignments.ToImmutable());
    }

    public async Task<string[]> AuditAsync(string correlation, string operation, Guid? organization = null)
    {
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, SchemaFixture.TenantA, organization);
        await using var command = new NpgsqlCommand("""
            SELECT jsonb_build_object('outcome',outcome,'class',operation_class,'type',operation_type,
                'entity',entity_type,'id',entity_id,'changes',changes,'metadata',metadata)::text
            FROM audit_log WHERE correlation_id=@correlation AND operation=@operation ORDER BY timestamp
            """, app, transaction);
        command.Parameters.AddWithValue("correlation", correlation);
        command.Parameters.AddWithValue("operation", operation);
        var result = new List<string>();
        await using var rows = await command.ExecuteReaderAsync();
        while (await rows.ReadAsync()) result.Add(rows.GetString(0));
        return result.ToArray();
    }

    public Task SetRoleGrantsAsync(BuiltInRoleKey key, PermissionScope scope, params PermissionDefinition[] grants) =>
        MutateAsync(SchemaFixture.TenantA, null, null, true, async (app, transaction) =>
        {
            var role = RoleId(SchemaFixture.TenantA, key, scope);
            await ExecuteAsync(app, transaction, "DELETE FROM role_permission_grants WHERE role_id=@role", ("role", role));
            foreach (var grant in grants)
                await ExecuteAsync(app, transaction, """
                    INSERT INTO role_permission_grants(tenant_id,role_id,permission_scope,permission_key)
                    VALUES(@tenant,@role,@scope,@key)
                    """, ("tenant", SchemaFixture.TenantA), ("role", role), ("scope", grant.Scope.ToString()), ("key", grant.Key));
            await ExecuteAsync(app, transaction, "UPDATE roles SET row_version=row_version+1 WHERE id=@role", ("role", role));
        });

    public async Task MutateAsync(Guid tenant, Guid? organization, Guid? user, bool exclusive,
        Func<NpgsqlConnection, NpgsqlTransaction, Task> mutate)
    {
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(app);
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, tenant, organization);
        await LockAsync(app, transaction, tenant, user, exclusive);
        await mutate(app, transaction);
        await ExecuteAsync(app, transaction, "SET CONSTRAINTS ALL IMMEDIATE");
        await transaction.CommitAsync();
    }

    public async Task ObserveWaitAsync(string application, int blocker, Task running, CancellationToken cancellationToken)
    {
        await using var observer = (NpgsqlConnection)await PostgresFixture.OpenAsync(AppConnectionString, cancellationToken);
        while (true)
        {
            var blocked = await ScalarAsync<bool>(observer, null, """
                SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE application_name=@application
                    AND @blocker=ANY(pg_blocking_pids(pid)) AND wait_event_type='Lock')
                """, ("application", application), ("blocker", blocker));
            if (blocked) return;
            if (running.IsCompleted) { await running; Assert.Fail("The real Membership act did not reach the required database lock wait."); }
            await Task.Delay(20, cancellationToken);
        }
    }
}

public sealed record MemberState(long Version, string Status, string? DisplayName, string? PreferredLocale,
    string RootStamp, ImmutableDictionary<Guid, string> Assignments);
