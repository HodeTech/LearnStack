using System.Data.Common;
using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed partial class BackgroundJobExternalIsolationTests(SchemaFixture schema)
{
    [Fact]
    public async Task External_live_and_retained_flow_cannot_open_the_actual_app_unit_but_the_original_flow_remains_usable()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        await using var source = NpgsqlDataSource.Create(database.AppConnectionString);
        await using var unit = new NpgsqlUnitOfWork(source, NullLogger<NpgsqlUnitOfWork>.Instance);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task inherited;
        using (BackgroundJobExecutionAdmission.EnterExternal())
        {
            await AssertPhysicalEntryRefusedAsync(unit);
            inherited = Task.Run(async () =>
            {
                await release.Task;
                await AssertPhysicalEntryRefusedAsync(unit);
            });
        }
        release.SetResult();
        await inherited.WaitAsync(TimeSpan.FromSeconds(5));
        JobApplicationSourceOwner.IsExternal.Should().BeFalse();
        await using var transaction = await unit.BeginTransactionAsync(TransactionMode.ReadWrite);
        await unit.SetTenantContextAsync(new AnnouncedContext());
        await SchemaQueries.AssertApplicationRoleAsync(unit.Connection, unit.Transaction!);
        (await SchemaQueries.CountAsync(unit.Connection, "SELECT count(*) FROM tenants", unit.Transaction!)).Should().Be(1,
            "the actual app source and announced transaction must work after denial-only scope restoration");
    }

    private static async Task AssertPhysicalEntryRefusedAsync(NpgsqlUnitOfWork unit)
    {
        const string Refusal = "The job application-source owner refuses another acquisition or activation.";
        Func<DbConnection> connection = () => unit.Connection;
        connection.Should().Throw<InvalidOperationException>().WithMessage(Refusal);
        Func<DbTransaction?> transaction = () => unit.Transaction;
        transaction.Should().Throw<InvalidOperationException>().WithMessage(Refusal);
        Func<Task> begin = async () => await unit.BeginTransactionAsync(TransactionMode.ReadWrite);
        (await begin.Should().ThrowAsync<InvalidOperationException>()).WithMessage(Refusal);
        foreach (Func<Task> operation in new Func<Task>[]
        {
            () => unit.SetTenantContextAsync(new AnnouncedContext()),
            () => unit.SetProvisioningTenantContextAsync(TenantId.From(SchemaFixture.TenantA)),
            () => unit.CommitAsync(),
            () => unit.RollbackAsync(),
        })
            (await operation.Should().ThrowAsync<InvalidOperationException>()).WithMessage(Refusal);
        Action poison = unit.MarkRollbackOnly;
        poison.Should().Throw<InvalidOperationException>().WithMessage(Refusal);
        unit.HasActiveTransaction.Should().BeFalse();
        unit.IsRollbackOnly.Should().BeFalse();
    }

    private sealed class AnnouncedContext : ITenantContext
    {
        public bool IsResolved => true;
        public TenantId TenantId => TenantId.From(SchemaFixture.TenantA);
        public OrganizationId? OrganizationId => null;
        public UserId? UserId => null;
        public string? CorrelationId => null;
        public string? ModuleName => "fixture";
    }
}
