using FluentAssertions;
using LearnStack.Modules.Tenancy.Application.Contracts.Account;
using LearnStack.Modules.Tenancy.Application.Contracts.PublicReads;
using LearnStack.Modules.Tenancy.Domain;
using LearnStack.Modules.Tenancy.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using LearnStack.Tools.Seeder;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class AccountSiteQueryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_account_query_physically_refuses_SQL_and_EF_writes_with_writable_controls(bool useEf)
    {
        var context = Context(SeedData.English, false);
        var mutation = new WriteObservation(TenantDomainId.From(Guid.CreateVersion7()), useEf, context);
        await using var host = CreateHost(context, services =>
        {
            services.AddSingleton(mutation);
            services.AddScoped<IPublicTenantConfigurationReader, WritingConfigurationReader>();
        });
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var services = scope.ServiceProvider;
            var unit = services.GetRequiredService<IUnitOfWork>();
            unit.HasActiveTransaction.Should().BeFalse();
            var act = async () => await services.GetRequiredService<ISender>().Send(new GetAccountSiteQuery());
            var failure = (await act.Should().ThrowAsync<Exception>()).Which;
            var database = failure as PostgresException ?? failure.InnerException as PostgresException;
            database.Should().NotBeNull("the barrier must reject the actual database write, not an earlier substitute");
            database!.SqlState.Should().Be(PostgresErrorCodes.ReadOnlySqlTransaction);
            mutation.ReachedWrite.Should().BeTrue();
            unit.HasActiveTransaction.Should().BeFalse();
            unit.Mode.Should().BeNull();
            unit.IsRollbackOnly.Should().BeTrue();
        }
        (await DomainCount(mutation.Id, context)).Should().Be(0);

        // The identical mutation on the same app-role graph must succeed under
        // a writable owner. Neither a missing grant nor inert fixture explains
        // the ReadOnly failure. Roll back to preserve every shared fixture row.
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var services = scope.ServiceProvider;
            var unit = services.GetRequiredService<IUnitOfWork>();
            await using var owner = await unit.BeginTransactionAsync(TransactionMode.ReadWrite);
            await unit.SetTenantContextAsync(context);
            await SchemaQueries.AssertApplicationRoleAsync(unit.Connection, unit.Transaction);
            await WriteDomain(services, unit, mutation);
            (await CountInFrame(unit, mutation.Id)).Should().Be(1);
            await owner.FailAsync();
        }
        (await DomainCount(mutation.Id, context)).Should().Be(0);
    }

    [Fact]
    public async Task Actual_account_query_cannot_join_a_writable_outer_owner_or_reach_its_reader()
    {
        var context = Context(SeedData.English, false);
        var observation = new RefusedReadObservation();
        await using var host = CreateHost(context, services =>
        {
            services.AddSingleton(observation);
            services.AddScoped<IPublicTenantConfigurationReader, ForbiddenConfigurationReader>();
        });
        await using var scope = host.Services.CreateAsyncScope();
        var unit = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await using var owner = await unit.BeginTransactionAsync(TransactionMode.ReadWrite);
        await unit.SetTenantContextAsync(context);
        await SchemaQueries.AssertApplicationRoleAsync(unit.Connection, unit.Transaction);
        var transaction = unit.Transaction;
        var send = async () => await scope.ServiceProvider.GetRequiredService<ISender>().Send(new GetAccountSiteQuery());
        await send.Should().ThrowAsync<InvalidOperationException>().WithMessage("*mixed-mode*");
        observation.Reads.Should().Be(0);
        unit.Transaction.Should().BeSameAs(transaction);
        unit.Mode.Should().Be(TransactionMode.ReadWrite);
        unit.IsRollbackOnly.Should().BeTrue();
        var complete = async () => await owner.CompleteAsync();
        await complete.Should().ThrowAsync<InvalidOperationException>().WithMessage("*rollback-only*");
        unit.HasActiveTransaction.Should().BeFalse();
        unit.Mode.Should().BeNull();
    }

    private async Task<long> DomainCount(TenantDomainId id, TenantContext context)
    {
        await using var connection = new NpgsqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SchemaQueries.AssertApplicationRoleAsync(connection, transaction);
        await SchemaQueries.SetTenantAsync(connection, transaction, context.TenantId.Value);
        await using var command = new NpgsqlCommand("SELECT count(*) FROM tenant_domains WHERE id=@id", connection, transaction);
        command.Parameters.AddWithValue("id", id.Value);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<long> CountInFrame(IUnitOfWork unit, TenantDomainId id)
    {
        await using var command = new NpgsqlCommand("SELECT count(*) FROM tenant_domains WHERE id=@id",
            (NpgsqlConnection)unit.Connection, (NpgsqlTransaction?)unit.Transaction);
        command.Parameters.AddWithValue("id", id.Value);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task WriteDomain(IServiceProvider services, IUnitOfWork unit, WriteObservation mutation)
    {
        var host = "account-readonly-" + mutation.Id.Value.ToString("N") + ".example.com";
        if (mutation.UseEf)
        {
            var db = services.GetRequiredService<TenancyDbContext>();
            db.Add(TenantDomain.CreateSubdomain(mutation.Id, mutation.Context.TenantId, host, new SystemClock(), Actor));
            await db.SaveChangesAsync();
            return;
        }
        await using var command = new NpgsqlCommand("""
            INSERT INTO tenant_domains (id,tenant_id,host,kind,status,created_at,created_by,row_version)
            VALUES (@id,@tenant,@host,'Subdomain','Verified',now(),@actor,0)
            """, (NpgsqlConnection)unit.Connection, (NpgsqlTransaction?)unit.Transaction);
        command.Parameters.AddWithValue("id", mutation.Id.Value);
        command.Parameters.AddWithValue("tenant", mutation.Context.TenantId.Value);
        command.Parameters.AddWithValue("host", host);
        command.Parameters.AddWithValue("actor", Actor.Value);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class WriteObservation(TenantDomainId id, bool useEf, ITenantContext context)
    {
        public TenantDomainId Id { get; } = id;
        public bool UseEf { get; } = useEf;
        public ITenantContext Context { get; } = context;
        public bool ReachedWrite { get; set; }
    }

    private sealed class WritingConfigurationReader(
        IUnitOfWork unit, IServiceProvider services, WriteObservation observation) : IPublicTenantConfigurationReader
    {
        public async Task<Result<PublicTenantConfiguration>> ReadAsync(CancellationToken cancellationToken)
        {
            unit.Mode.Should().Be(TransactionMode.ReadOnly);
            await SchemaQueries.AssertApplicationRoleAsync(unit.Connection, unit.Transaction);
            await using var command = new NpgsqlCommand("SHOW transaction_read_only",
                (NpgsqlConnection)unit.Connection, (NpgsqlTransaction?)unit.Transaction);
            (await command.ExecuteScalarAsync(cancellationToken)).Should().Be("on");
            observation.ReachedWrite = true;
            await WriteDomain(services, unit, observation);
            throw new InvalidOperationException("A physical ReadOnly account query unexpectedly wrote.");
        }
    }
}
