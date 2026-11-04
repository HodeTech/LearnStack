using System.Data.Common;
using System.Text.Json;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Tenancy.Application.Abstractions;
using LearnStack.Modules.Tenancy.Application.Contracts.Locales;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.Modules.Tenancy.Domain;
using LearnStack.Modules.Tenancy.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed class TenancyAdministrationAdditionalProofTests(TenancyAdministrationFixture fixture) : IClassFixture<TenancyAdministrationFixture>
{
    [Fact]
    public async Task Page_and_detail_each_use_one_actual_SQL_snapshot_across_a_competing_commit()
    {
        var created = await fixture.SendAsync(new CreateManagedOrganizationCommand("snapshot-" + Guid.NewGuid().ToString("N"), "Before snapshot"), "tenancy-snapshot-create");
        Assert.True(created.IsSuccess);
        var id = created.Value!.OrganizationId;
        var before = await fixture.SendAsync(new ListOrganizationsQuery(null, 100), "tenancy-snapshot-before");
        Assert.True(before.IsSuccess);
        await using var harness = await fixture.CreateAsync("tenancy-snapshot-owner");
        await using var frame = await harness.Unit.BeginTransactionAsync(TransactionMode.ReadOnly);
        var context = harness.Services.GetRequiredService<ITenantContext>();
        await harness.Unit.SetTenantContextAsync(context);
        var witness = new AfterQueryCommit(async () => await fixture.Membership.MutateAsync(SchemaFixture.TenantA, null, null, true,
            async (app, transaction) =>
            {
                await ExecuteAsync(app, transaction, "UPDATE tenants SET default_organization_id=@id,row_version=row_version+1 WHERE id=@tenant",
                    ("id", id.Value), ("tenant", SchemaFixture.TenantA));
                await ExecuteAsync(app, transaction, "UPDATE organizations SET display_name='After snapshot',row_version=row_version+1 WHERE id=@id", ("id", id.Value));
            }));
        await using var db = new TenancyDbContext(new DbContextOptionsBuilder<TenancyDbContext>().UseNpgsql(harness.Unit.Connection)
            .AddInterceptors(witness).Options, new StaticTenantContextAccessor(context));
        await db.Database.UseTransactionAsync(harness.Unit.Transaction);
        var reader = new TenancyAdministrationReader(db, harness.Unit, context);
        var page = await reader.ListOrganizationsAsync(null, 100);
        Assert.NotNull(page);
        Assert.Equal(1, witness.Queries);
        Assert.Equal(before.Value!.DefaultOrganizationId, page.DefaultOrganizationId);
        Assert.Equal(before.Value.TenantVersion, page.TenantVersion);
        var oldRow = Assert.Single(page.Items, row => row.OrganizationId == id);
        Assert.Equal("Before snapshot", oldRow.DisplayName);
        Assert.False(oldRow.IsDefault);
        Assert.Equal(created.Value.Version, oldRow.Version);
        var fresh = await reader.ReadOrganizationDetailAsync(id);
        Assert.NotNull(fresh);
        Assert.True(fresh.IsDefault);
        Assert.Equal("After snapshot", fresh.Organization.DisplayName);
        Assert.Equal(created.Value.Version + 1, fresh.Organization.Version);
        Assert.Equal(2, witness.Queries);
        witness.Rearm(async () => await fixture.Membership.MutateAsync(SchemaFixture.TenantA, null, null, true,
            async (app, transaction) =>
            {
                await ExecuteAsync(app, transaction, "UPDATE tenants SET default_organization_id=@id,row_version=row_version+1 WHERE id=@tenant",
                    ("id", before.Value.DefaultOrganizationId!.Value.Value), ("tenant", SchemaFixture.TenantA));
                await ExecuteAsync(app, transaction, "UPDATE organizations SET display_name='After detail snapshot',row_version=row_version+1 WHERE id=@id", ("id", id.Value));
            }));
        var detail = await reader.ReadOrganizationDetailAsync(id);
        Assert.NotNull(detail);
        Assert.Equal(3, witness.Queries);
        Assert.True(detail.IsDefault);
        Assert.Equal("After snapshot", detail.Organization.DisplayName);
        var current = await reader.ReadOrganizationDetailAsync(id);
        Assert.NotNull(current);
        Assert.False(current.IsDefault);
        Assert.Equal("After detail snapshot", current.Organization.DisplayName);
        Assert.Empty(db.ChangeTracker.Entries());
        await frame.FailAsync();
    }

    [Fact]
    public async Task Postwait_current_permission_revocation_refuses_rename_and_preserves_target()
    {
        var created = await fixture.SendAsync(new CreateManagedOrganizationCommand("permission-race-" + Guid.NewGuid().ToString("N"), "Unchanged after revoke"), "tenancy-revoke-race-create");
        Assert.True(created.IsSuccess);
        var role = RoleId(SchemaFixture.TenantA, BuiltInRoleKey.TenantAdmin, PermissionScope.Tenant);
        var permission = BoundedTenancyAdministrationPermissions.OrganizationAdministration;
        await using var holder = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(holder);
        await using var transaction = await holder.BeginTransactionAsync();
        await ScopeAsync(holder, transaction, SchemaFixture.TenantA);
        await LockAsync(holder, transaction, SchemaFixture.TenantA, null, true);
        await ExecuteAsync(holder, transaction, "SELECT id FROM roles WHERE id=@role FOR NO KEY UPDATE", ("role", role));
        await ExecuteAsync(holder, transaction, "DELETE FROM role_permission_grants WHERE role_id=@role AND permission_key=@key",
            ("role", role), ("key", permission.Key));
        await ExecuteAsync(holder, transaction, "UPDATE roles SET row_version=row_version+1 WHERE id=@role", ("role", role));
        await ExecuteAsync(holder, transaction, "SET CONSTRAINTS ALL IMMEDIATE");
        var correlation = "tenancy-revoke-race-loser";
        await using var contender = await fixture.CreateAsync(correlation);
        var pending = contender.Sender.Send(new RenameOrganizationCommand(created.Value!.OrganizationId, created.Value.Version, "Must not commit"));
        var committed = false;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await fixture.Membership.ObserveWaitAsync(contender.ApplicationName, holder.ProcessID, pending, deadline.Token);
            Assert.False(pending.IsCompleted);
            await transaction.CommitAsync();
            committed = true;
            Assert.Equal("forbidden", (await pending).Error!.Code);
            await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.AppConnectionString);
            await using var read = await app.BeginTransactionAsync();
            await ScopeAsync(app, read, SchemaFixture.TenantA);
            Assert.Equal("Unchanged after revoke", await ScalarAsync<string>(app, read, "SELECT display_name FROM organizations WHERE id=@id", ("id", created.Value.OrganizationId.Value)));
            Assert.Equal(created.Value.Version, await ScalarAsync<long>(app, read, "SELECT row_version FROM organizations WHERE id=@id", ("id", created.Value.OrganizationId.Value)));
            foreach (var row in await fixture.AuditAsync(correlation, "tenancy.organization.rename"))
            {
                using var audit = JsonDocument.Parse(row);
                Assert.NotEqual("success", audit.RootElement.GetProperty("outcome").GetString());
            }
        }
        finally
        {
            if (!committed)
                await transaction.RollbackAsync();
            await fixture.Membership.MutateAsync(SchemaFixture.TenantA, null, null, true, async (app, restore) =>
            {
                await ExecuteAsync(app, restore, "SELECT id FROM roles WHERE id=@role FOR NO KEY UPDATE", ("role", role));
                await ExecuteAsync(app, restore, """
                    INSERT INTO role_permission_grants(tenant_id,role_id,permission_scope,permission_key)
                    VALUES(@tenant,@role,'Tenant',@key) ON CONFLICT DO NOTHING;
                    UPDATE roles SET row_version=row_version+1 WHERE id=@role;
                    """, ("tenant", SchemaFixture.TenantA), ("role", role), ("key", permission.Key));
            });
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Second_locale_save_failure_poisoning_survives_an_outer_caller_absorbing_it(bool cancellation)
    {
        var before = await fixture.TenantVersionAsync();
        Exception injected = cancellation ? new OperationCanceledException("fixed second save cancellation")
            : new InvalidOperationException("fixed second save programming failure");
        var failure = new FailSecondSave(injected);
        await using (var harness = await fixture.CreateAsync("tenancy-locale-save-poison", configure: services =>
            services.AddSingleton<ISaveChangesInterceptor>(failure)))
        {
            await using var outer = await harness.Unit.BeginTransactionAsync();
            await harness.Unit.SetTenantContextAsync(harness.Services.GetRequiredService<ITenantContext>());
            var actual = await Record.ExceptionAsync(() => harness.Sender.Send(new AddManagedTenantLocaleCommand(before, "de", true, true, 7)));
            Assert.Same(injected, actual);
            Assert.Equal(2, failure.Saves);
            Assert.True(harness.Unit.IsRollbackOnly);
            await Assert.ThrowsAsync<InvalidOperationException>(() => outer.CompleteAsync());
        }
        Assert.Equal(before, await fixture.TenantVersionAsync());
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(app);
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, SchemaFixture.TenantA);
        Assert.Equal(0L, await ScalarAsync<long>(app, transaction, "SELECT count(*) FROM tenant_locales WHERE locale='de'"));
        Assert.Equal(1L, await ScalarAsync<long>(app, transaction, "SELECT count(*) FROM tenant_locales WHERE locale='en' AND is_default AND is_enabled"));
    }

    private sealed class AfterQueryCommit(Func<Task> commit) : DbCommandInterceptor
    {
        private Func<Task>? _commit = commit;
        public int Queries { get; private set; }
        public void Rearm(Func<Task> next) => _commit = next;
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            Queries++;
            var pending = _commit;
            _commit = null;
            if (pending is not null) await pending();
            return result;
        }
    }

    private sealed class FailSecondSave(Exception failure) : SaveChangesInterceptor
    {
        public int Saves { get; private set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context is TenancyDbContext && ++Saves == 2) throw failure;
            return ValueTask.FromResult(result);
        }
    }
}
