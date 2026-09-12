using FluentAssertions;
using LearnStack.Api.Composition;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Customization.Domain;
using LearnStack.Modules.Customization.Infrastructure.Persistence;
using LearnStack.Modules.Tenancy.Domain;
using LearnStack.Modules.Tenancy.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// The two properties
/// <see href="../../../../docs/decisions/0040-ambient-unit-of-work.md">ADR-0040</see> exists
/// for, observed rather than argued: two modules on one connection and one transaction.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why neither can be a unit test.</b> Both are claims about what a second connection would
/// and would not see. Against doubles, a context that opened its own connection passes every
/// single-module case in this repository — which is exactly the arrangement ADR-0040 exists to
/// forbid, and the reason the decision staged these two cases for the packet that had a second
/// module <c>DbContext</c> to use. Packet 8 shipped it.
/// </para>
/// <para>
/// <b>Nothing here commits.</b> Both cases roll back, which is what the second one is about and
/// what keeps the first from disturbing the row counts the schema cases assert on this shared
/// container.
/// </para>
/// <para>
/// As <c>learnstack_app</c>: the writes below pass Row Level Security because the unit of work
/// announced the tenant, not because the role may ignore it.
/// </para>
/// </remarks>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class AmbientUnitOfWorkTests
{
    private readonly SchemaFixture _schema;

    public AmbientUnitOfWorkTests(SchemaFixture schema) => _schema = schema;

    [Fact]
    public async Task A_Cross_Module_Read_Inside_The_Ambient_Transaction_Returns_Rows()
    {
        // One unit of work, two modules: Customization writes, Tenancy reads, and the row is
        // there before COMMIT because both contexts are enlisted on the same connection and the
        // same transaction. A context that opened its own connection would read committed data
        // only — and every single-module case in this suite would still pass.
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();

        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await using var frame = await unitOfWork.BeginTransactionAsync(CancellationToken.None);
        await unitOfWork.SetTenantContextAsync(TenantA, CancellationToken.None);

        var customization = scope.ServiceProvider.GetRequiredService<CustomizationDbContext>();
        var tenancy = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();

        var contentTypeId = TenantContentTypeId.From(Guid.CreateVersion7());
        customization.Add(ContentType(contentTypeId));
        await customization.SaveChangesAsync(CancellationToken.None);

        // Read through the OTHER module's context. It is the same connection and the same
        // transaction, so the uncommitted row is visible.
        var seenByTenancy = await CountAsync(tenancy, contentTypeId);

        seenByTenancy.Should().Be(1,
            "a row one module writes is visible to the other inside the ambient transaction "
            + "(ADR-0040 § Implementation Notes)");

        // And the control that stops this case agreeing with itself: a second connection sees
        // nothing, because nothing has committed. Without it, a context that committed on
        // SaveChanges would pass the assertion above for the wrong reason.
        (await CountOnItsOwnConnectionAsync(contentTypeId)).Should().Be(0,
            "the row is uncommitted, so the visibility above came from sharing the transaction");

        _ = frame;
    }

    [Fact]
    public async Task An_Outer_Failure_After_An_Inner_Write_Leaves_Zero_Rows_In_Both_Modules()
    {
        // One transaction, one outcome. Two connections would commit the inner write and roll
        // back the outer, which is the partial state the ambient unit of work makes impossible.
        await using var provider = BuildProvider();

        var contentTypeId = TenantContentTypeId.From(Guid.CreateVersion7());
        var domainId = TenantDomainId.From(Guid.CreateVersion7());
        var host = $"rollback-{Guid.CreateVersion7():N}.example.com";

        var failed = async () =>
        {
            await using var scope = provider.CreateAsyncScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await using var frame = await unitOfWork.BeginTransactionAsync(CancellationToken.None);
            await unitOfWork.SetTenantContextAsync(TenantA, CancellationToken.None);

            var customization = scope.ServiceProvider.GetRequiredService<CustomizationDbContext>();
            customization.Add(ContentType(contentTypeId));
            await customization.SaveChangesAsync(CancellationToken.None);

            var tenancy = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
            tenancy.Add(TenantDomain.CreateSubdomain(domainId, TenantA.TenantId, host, Clock, Actor));
            await tenancy.SaveChangesAsync(CancellationToken.None);

            // The outer frame fails after both writes and before the commit: no CommitAsync, and
            // the scope's disposal rolls the transaction back.
            throw new InvalidOperationException("the outer frame failed");
        };

        await failed.Should().ThrowAsync<InvalidOperationException>();

        (await CountOnItsOwnConnectionAsync(contentTypeId)).Should().Be(0,
            "the Customization write rolled back with the transaction that carried it");
        (await CountOnItsOwnConnectionAsync(host)).Should().Be(0,
            "and so did the Tenancy write, because there was only one transaction");
    }

    /// <summary>The composition root's shape: the ambient unit of work and both module contexts.</summary>
    private ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();

        services.AddSingleton(NpgsqlDataSource.Create(_schema.Postgres.AppConnectionString));
        services.AddLogging();
        services.AddSingleton<ITenantContextAccessor>(new StaticAccessor(TenantA));
        services.AddTransient<ITenantContext>(provider =>
            provider.GetRequiredService<ITenantContextAccessor>().Current ?? UnresolvedTenantContext.Instance);
        services.AddScoped<IUnitOfWork, NpgsqlUnitOfWork>();
        services.AddModuleDbContext<TenancyDbContext>();
        services.AddModuleDbContext<CustomizationDbContext>();

        return services.BuildServiceProvider();
    }

    private static TenantContentType ContentType(TenantContentTypeId id) =>
        TenantContentType.Create(
            id,
            TenantA.TenantId,
            $"probe-{id.Value:N}"[..24],
            1,
            LocalizedText.From(("en", "Probe")),
            """{"type":"object"}""",
            "default-card",
            Clock,
            Actor);

    /// <summary>Counts the row through a context's own connection.</summary>
    private static async Task<long> CountAsync(DbContext context, TenantContentTypeId id)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.Transaction = context.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = "SELECT count(*) FROM tenant_content_types WHERE id = @id";

        var parameter = command.CreateParameter();
        parameter.ParameterName = "id";
        parameter.Value = id.Value;
        command.Parameters.Add(parameter);

        return (long)(await command.ExecuteScalarAsync(CancellationToken.None))!;
    }

    /// <summary>Counts the row from a connection of its own, as <c>learnstack_app</c>.</summary>
    private async Task<long> CountOnItsOwnConnectionAsync(TenantContentTypeId id) =>
        await ScalarAsync("SELECT count(*) FROM tenant_content_types WHERE id = @value", id.Value);

    /// <summary>Counts a tenant domain from a connection of its own.</summary>
    private async Task<long> CountOnItsOwnConnectionAsync(string host) =>
        await ScalarAsync("SELECT count(*) FROM tenant_domains WHERE host = @value", host);

    private async Task<long> ScalarAsync(string sql, object value)
    {
        await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(
            _schema.Postgres.AppConnectionString);
        await using var transaction = await connection.BeginTransactionAsync(CancellationToken.None);

        await using (var announce = new NpgsqlCommand(
            "SELECT set_config('app.tenant_id', @tenant, true)", connection, transaction))
        {
            announce.Parameters.AddWithValue("tenant", SchemaFixture.TenantA.ToString());
            await announce.ExecuteNonQueryAsync(CancellationToken.None);
        }

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("value", value);

        var count = (long)(await command.ExecuteScalarAsync(CancellationToken.None))!;
        await transaction.RollbackAsync(CancellationToken.None);

        return count;
    }

    private static readonly IClock Clock =
        new FixedClock(new DateTimeOffset(2026, 9, 12, 9, 0, 0, TimeSpan.Zero));

    private static readonly UserId Actor = UserId.From(SchemaFixture.Actor);

    private static readonly ResolvedTenant TenantA = new(SchemaFixture.TenantA);

    private sealed class StaticAccessor(ITenantContext context) : ITenantContextAccessor
    {
        public ITenantContext? Current { get; set; } = context;
    }

    private sealed class ResolvedTenant(Guid tenant) : ITenantContext
    {
        public bool IsResolved => true;

        public TenantId TenantId => SharedKernel.Identifiers.TenantId.From(tenant);

        public OrganizationId? OrganizationId => null;

        public UserId? UserId => null;

        public string? CorrelationId => null;

        public string? ModuleName => "tenancy";
    }
}
