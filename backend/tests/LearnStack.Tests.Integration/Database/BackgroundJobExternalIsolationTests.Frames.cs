using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobExternalIsolationTests
{
    [Fact]
    public async Task Refused_external_complete_preserves_the_original_handle_for_real_commit_after_restoration()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        await using var source = NpgsqlDataSource.Create(database.AppConnectionString);
        await using var unit = new NpgsqlUnitOfWork(source, NullLogger<NpgsqlUnitOfWork>.Instance);
        await using var frame = await unit.BeginTransactionAsync();
        await unit.SetTenantContextAsync(new AnnouncedContext());
        var job = Guid.CreateVersion7();
        await BackgroundJobSchemaSeed.InsertJobAsync(unit.Connection, unit.Transaction!, job, SchemaFixture.TenantA, null);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task inherited;
        using (BackgroundJobExecutionAdmission.EnterExternal())
        {
            Func<Task> complete = () => frame.CompleteAsync();
            await complete.Should().ThrowAsync<InvalidOperationException>();
            unit.HasActiveTransaction.Should().BeTrue();
            unit.IsRollbackOnly.Should().BeFalse();
            inherited = Task.Run(async () =>
            {
                await release.Task;
                Func<Task> late = () => frame.CompleteAsync();
                await late.Should().ThrowAsync<InvalidOperationException>();
            });
        }
        release.SetResult();
        await inherited.WaitAsync(TimeSpan.FromSeconds(5));
        await frame.CompleteAsync();
        unit.HasActiveTransaction.Should().BeFalse("a denied call must not pre-resolve the original handle into false later success");
        await using var verify = await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var read = await verify.BeginTransactionAsync();
        await EducationSchemaSeed.AnnounceAsync(verify, read, SchemaFixture.TenantA, null);
        (await PolicyRowCountAsync(verify, read, job)).Should().Be(1, "the actual original COMMIT, not a handle's resolved bit, makes the row durable");
    }

    [Fact]
    public async Task Refused_external_owner_failure_cannot_rollback_live_joiners_or_poison_the_original_unit()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = NpgsqlDataSource.Create(database.AppConnectionString);
        await using var unit = new NpgsqlUnitOfWork(source, NullLogger<NpgsqlUnitOfWork>.Instance);
        await using var owner = await unit.BeginTransactionAsync();
        await using var joiner = await unit.BeginTransactionAsync();
        using (BackgroundJobExecutionAdmission.EnterExternal())
        {
            Func<Task> fail = () => owner.FailAsync();
            await fail.Should().ThrowAsync<InvalidOperationException>();
            Action poison = unit.MarkRollbackOnly;
            poison.Should().Throw<InvalidOperationException>();
            unit.HasActiveTransaction.Should().BeTrue();
            unit.IsRollbackOnly.Should().BeFalse();
        }
        await joiner.CompleteAsync();
        await owner.CompleteAsync();
        unit.HasActiveTransaction.Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_frame_disposal_under_external_denial_still_rolls_back_and_disposes_its_owned_transaction(bool leakedJoiner)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        await using var source = NpgsqlDataSource.Create(database.AppConnectionString);
        await using var unit = new NpgsqlUnitOfWork(source, NullLogger<NpgsqlUnitOfWork>.Instance);
        await using var frame = await unit.BeginTransactionAsync();
        await unit.SetTenantContextAsync(new AnnouncedContext());
        await using var joiner = leakedJoiner ? await unit.BeginTransactionAsync() : null;
        var job = Guid.CreateVersion7();
        await BackgroundJobSchemaSeed.InsertJobAsync(unit.Connection, unit.Transaction!, job, SchemaFixture.TenantA, null);
        using (BackgroundJobExecutionAdmission.EnterExternal()) await frame.DisposeAsync();
        unit.HasActiveTransaction.Should().BeFalse();
        unit.IsRollbackOnly.Should().BeTrue("owned disposal remains rollback cleanup rather than new operational authority");
        await using var verify = await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var read = await verify.BeginTransactionAsync();
        await EducationSchemaSeed.AnnounceAsync(verify, read, SchemaFixture.TenantA, null);
        (await PolicyRowCountAsync(verify, read, job)).Should().Be(0);
    }

    private static async Task<long> PolicyRowCountAsync(System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction, Guid job)
    {
        await using var command = new NpgsqlCommand("SELECT count(*) FROM background_jobs WHERE id=@job",
            (NpgsqlConnection)connection, (NpgsqlTransaction)transaction)
        { CommandTimeout = 5 };
        command.Parameters.AddWithValue("job", job);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
