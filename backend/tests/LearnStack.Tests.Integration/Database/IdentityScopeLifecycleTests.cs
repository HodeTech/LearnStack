using FluentAssertions;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Tenancy.Application.Contracts.IdentityLifecycle;
using LearnStack.Modules.Tenancy.Infrastructure.Persistence;
using LearnStack.SharedKernel.Errors;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class IdentityScopeLifecycleTests(SchemaFixture schema)
{
    [Theory]
    [InlineData("Trial", true)]
    [InlineData("Active", true)]
    [InlineData("Suspended", false)]
    [InlineData("Archived", false)]
    public async Task Tenant_facts_use_fresh_real_status_and_absent_org_has_no_organization_facts(string status, bool allows)
    {
        await using var source = ApplicationDataSource.Build(schema.Postgres.AppConnectionString);
        await using var unit = new NpgsqlUnitOfWork(source, NullLogger<NpgsqlUnitOfWork>.Instance);
        await using var frame = await unit.BeginTransactionAsync();
        var context = new Context(TenantId.From(SchemaFixture.TenantA), null);
        await unit.SetTenantContextAsync(context);
        var reader = new IdentityScopeLifecycleReader(unit);
        await ExecuteAsync(unit, "UPDATE public.tenants SET status = @status WHERE id = @tenant", ("status", status), ("tenant", context.TenantId.Value));
        (await reader.ReadAsync(context.TenantId, null)).Should().Be(new IdentityScopeLifecycle(true, allows, false, false));
        // A second fresh statement must see the actual current state, not the first result.
        await ExecuteAsync(unit, "UPDATE public.tenants SET status = 'Active' WHERE id = @tenant", ("tenant", context.TenantId.Value));
        (await reader.ReadAsync(context.TenantId, null)).TenantAllowsOrdinaryWork.Should().BeTrue();
        // Status changes are a fixture accommodation and roll back with this frame.
    }

    [Theory]
    [InlineData("Active", true)]
    [InlineData("Suspended", false)]
    [InlineData("Archived", false)]
    public async Task Organization_belonging_is_separate_from_current_work_eligibility(string status, bool allows)
    {
        await using var source = ApplicationDataSource.Build(schema.Postgres.AppConnectionString);
        await using var unit = new NpgsqlUnitOfWork(source, NullLogger<NpgsqlUnitOfWork>.Instance);
        await using var frame = await unit.BeginTransactionAsync();
        var context = new Context(TenantId.From(SchemaFixture.TenantA), OrganizationId.From(SchemaFixture.OrgA1));
        await unit.SetTenantContextAsync(context);
        await ExecuteAsync(unit, "UPDATE public.organizations SET status = @status WHERE id = @org", ("status", status), ("org", context.OrganizationId!.Value.Value));
        var facts = await new IdentityScopeLifecycleReader(unit).ReadAsync(context.TenantId, context.OrganizationId);
        facts.TenantExistsAndNotDeleted.Should().BeTrue();
        facts.OrganizationBelongsToTenant.Should().BeTrue();
        facts.OrganizationAllowsOrdinaryWork.Should().Be(allows);
    }

    [Fact]
    public async Task Missing_or_foreign_scope_facts_are_closed_and_cannot_retarget_the_announcement()
    {
        await using var source = ApplicationDataSource.Build(schema.Postgres.AppConnectionString);
        await using var unit = new NpgsqlUnitOfWork(source, NullLogger<NpgsqlUnitOfWork>.Instance);
        await using var frame = await unit.BeginTransactionAsync();
        var context = new Context(TenantId.From(SchemaFixture.TenantA), OrganizationId.From(SchemaFixture.OrgB1));
        await unit.SetTenantContextAsync(context);
        var reader = new IdentityScopeLifecycleReader(unit);
        var facts = await reader.ReadAsync(context.TenantId, context.OrganizationId);
        facts.OrganizationBelongsToTenant.Should().BeFalse();
        facts.OrganizationAllowsOrdinaryWork.Should().BeFalse();
        await FluentActions.Awaiting(() => reader.ReadAsync(TenantId.From(SchemaFixture.TenantB), context.OrganizationId))
            .Should().ThrowAsync<InfrastructureException>().WithMessage("Identity scope lifecycle is unavailable.");
        await FluentActions.Awaiting(() => reader.ReadAsync(context.TenantId, null))
            .Should().ThrowAsync<InfrastructureException>();
        unit.GetTenantContextAnnouncement(unit.Transaction)!.TenantId.Should().Be(context.TenantId);
    }

    [Fact]
    public async Task Actual_server_isolation_cannot_be_replaced_by_the_drivers_cached_mode()
    {
        await using var source = ApplicationDataSource.Build(schema.Postgres.AppConnectionString);
        await using var unit = new NpgsqlUnitOfWork(source, NullLogger<NpgsqlUnitOfWork>.Instance);
        await using var frame = await unit.BeginTransactionAsync();
        await ExecuteAsync(unit, "SET TRANSACTION ISOLATION LEVEL REPEATABLE READ");
        var context = new Context(TenantId.From(SchemaFixture.TenantA), null);
        await unit.SetTenantContextAsync(context);
        await FluentActions.Awaiting(() => new IdentityScopeLifecycleReader(unit).ReadAsync(context.TenantId, null))
            .Should().ThrowAsync<InfrastructureException>().WithMessage("Identity scope lifecycle is unavailable.");
    }

    [Fact]
    public async Task Missing_tenant_and_unannounced_owner_cannot_produce_live_facts()
    {
        await using var source = ApplicationDataSource.Build(schema.Postgres.AppConnectionString);
        await using var unit = new NpgsqlUnitOfWork(source, NullLogger<NpgsqlUnitOfWork>.Instance);
        var reader = new IdentityScopeLifecycleReader(unit);
        await FluentActions.Awaiting(() => reader.ReadAsync(TenantId.From(SchemaFixture.TenantA), null))
            .Should().ThrowAsync<InfrastructureException>();
        await using var frame = await unit.BeginTransactionAsync();
        var missing = TenantId.From(Guid.CreateVersion7());
        await unit.SetProvisioningTenantContextAsync(missing);
        (await reader.ReadAsync(missing, null)).Should().Be(new IdentityScopeLifecycle(false, false, false, false));
    }

    private static async Task ExecuteAsync(NpgsqlUnitOfWork unit, string sql, params (string Key, object Value)[] values)
    {
        await using var command = new NpgsqlCommand(sql, (NpgsqlConnection)unit.Connection, (NpgsqlTransaction)unit.Transaction!);
        foreach (var (key, value) in values) command.Parameters.AddWithValue(key, value);
        await command.ExecuteNonQueryAsync();
    }

    private sealed record Context(TenantId TenantId, OrganizationId? OrganizationId) : ITenantContext
    {
        public bool IsResolved => true;
        public UserId? UserId => LearnStack.SharedKernel.Identifiers.UserId.SystemActor;
        public string? CorrelationId => null;
        public string? ModuleName => "identity";
    }
}
