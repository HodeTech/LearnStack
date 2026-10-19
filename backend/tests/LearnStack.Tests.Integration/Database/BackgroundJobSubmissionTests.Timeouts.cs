using System.Diagnostics;
using FluentAssertions;
using LearnStack.Infrastructure.Jobs;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobSubmissionTests
{
    [Fact]
    public async Task A_blocked_typed_INSERT_honors_the_SQL_budget_with_an_unlimited_default_and_poisons_before_absorption()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var appConnection = new NpgsqlConnectionStringBuilder(database.AppConnectionString)
        { CommandTimeout = 0, CancellationTimeout = 1000 }.ConnectionString;
        var options = new BackgroundJobRuntimeOptions { SqlTimeout = TimeSpan.FromSeconds(1) };
        options.Validate();
        var probe = new BackgroundJobFixtureProbe();
        await using var host = Fixture.Host(database, probe,
            configure: services => services.AddSingleton(options), appConnectionString: appConnection);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var absorbed = false;
        await using (var scope = host.Services.CreateAsyncScope())
        await using (var locker = await PostgresFixture.OpenAsync(database.AppConnectionString))
        await using (var transaction = await locker.BeginTransactionAsync())
        {
            await EducationSchemaSeed.AnnounceAsync(locker, transaction, SchemaFixture.TenantA, null);
            await SchemaQueries.ExecuteAsync(locker, transaction,
                "SELECT id FROM tenants WHERE id = @tenant FOR UPDATE", ("tenant", SchemaFixture.TenantA));
            using var proofDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            probe.Body = async (_, services, cancellationToken) =>
            {
                var unit = services.GetRequiredService<IUnitOfWork>();
                using (var inherited = unit.Connection.CreateCommand())
                    inherited.CommandTimeout.Should().Be(0, "the actual app connection has an unlimited command default");
                entered.TrySetResult();
                Exception? failure = null;
                try
                {
                    await ScheduleWithCancellationAsync(services, cancellationToken);
                }
                catch (Exception exception) when (exception is OperationCanceledException or NpgsqlException)
                {
                    failure = exception;
                }
                failure.Should().NotBeNull("the blocked INSERT must fail before the independent proof deadline");
                proofDeadline.IsCancellationRequested.Should().BeFalse();
                unit.IsRollbackOnly.Should().BeTrue("the scheduler poisons before the caller can absorb its SQL fault");
                CurrentInvocation(services).HasProducedWork.Should().BeFalse("a timed-out INSERT never records successful production");
                absorbed = true;
                return Result.Ok(None.Value);
            };
            var running = scope.ServiceProvider.GetRequiredService<ISender>()
                .Send(Fixture.Request("Submit"), proofDeadline.Token);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await WaitForJobInsertLockAsync(appConnection);
                await FluentActions.Awaiting(() => running.WaitAsync(TimeSpan.FromSeconds(6))).Should()
                    .ThrowAsync<InvalidOperationException>().WithMessage("*rollback-only*");
                absorbed.Should().BeTrue();
            }
            finally
            {
                await transaction.RollbackAsync();
                await proofDeadline.CancelAsync();
                try { await running.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (Exception) when (running.IsCompleted)
                {
                    // Join the actual owner without replacing the assertion or
                    // its expected rollback-only failure during proof cleanup.
                }
            }
        }
        (await CountSubmittedJobsAsync(appConnection)).Should().Be(0,
            "an independent application connection must observe no timed-out intent");

        BackgroundJobId? successful = null;
        probe.Body = async (_, services, _) =>
        {
            successful = await Fixture.EnqueueAsync(services, dueAt: BackgroundJobSchemaSeed.Slot);
            return Result.Ok(None.Value);
        };
        await using var fresh = host.Services.CreateAsyncScope();
        (await Fixture.SendAsync(fresh.ServiceProvider)).IsSuccess.Should().BeTrue(
            "a fresh owner must insert after the foreign-key lock has actually been released");
        (await BackgroundJobRuntimeRows.ReadAsync(appConnection, successful!.Value)).Should().NotBeNull();
        (await CountSubmittedJobsAsync(appConnection)).Should().Be(1);
        AssertClean(fresh.ServiceProvider);
    }

    private static Task<BackgroundJobId> ScheduleWithCancellationAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var parameters = Fixture.Parameters();
        var typedKey = Activator.CreateInstance(typeof(BackgroundJobKey<>).MakeGenericType(Fixture.ParameterType),
            BackgroundJobRuntimeFixture.TenantKey)!;
        return (Task<BackgroundJobId>)typeof(IBackgroundJobScheduler).GetMethod(nameof(IBackgroundJobScheduler.ScheduleAsync))!
            .MakeGenericMethod(Fixture.ParameterType).Invoke(services.GetRequiredService<IBackgroundJobScheduler>(),
                [typedKey, parameters, BackgroundJobSchemaSeed.Slot, cancellationToken])!;
    }

    private static async Task WaitForJobInsertLockAsync(string connectionString)
    {
        await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(connectionString);
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(3))
        {
            await using var command = new NpgsqlCommand("""
                SELECT count(*) FROM pg_stat_activity WHERE datname = current_database()
                    AND usename = 'learnstack_app' AND wait_event_type = 'Lock'
                    AND query LIKE '%INSERT INTO background_jobs%'
                """, connection) { CommandTimeout = 1 };
            if ((long)(await command.ExecuteScalarAsync())! > 0) return;
            await Task.Delay(TimeSpan.FromMilliseconds(25));
        }
        throw new TimeoutException("The real typed submission did not reach its foreign-key lock wait.");
    }

    private static async Task<long> CountSubmittedJobsAsync(string connectionString)
    {
        await using var connection = await PostgresFixture.OpenAsync(connectionString);
        await using var transaction = await connection.BeginTransactionAsync();
        await EducationSchemaSeed.AnnounceAsync(connection, transaction, SchemaFixture.TenantA, null);
        await using var command = new NpgsqlCommand("SELECT count(*) FROM background_jobs",
            (NpgsqlConnection)connection, (NpgsqlTransaction)transaction)
        { CommandTimeout = 1 };
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
