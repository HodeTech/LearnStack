using LearnStack.Application.Jobs;
using FluentAssertions;
using LearnStack.Infrastructure.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobAdmissionTests
{
    [Fact]
    public async Task Closing_the_runtime_gate_during_the_actual_application_row_lock_spends_no_attempt()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var blocked = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString);
        var positive = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString);
        var probe = new BackgroundJobFixtureProbe();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observation = new BackgroundJobAdmissionObservation
        {
            OnStart = (_, _) => { entered.TrySetResult(); return Task.CompletedTask; },
        };
        await using var host = Fixture.Host(database, probe, configure: observation.Configure);
        var routing = host.Services.GetRequiredService<PostgresBackgroundJobRoutingStore>();
        var claim = (await routing.TryClaimAsync(blocked, 1))!;
        var gate = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        await using var locker = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        await using var transaction = await locker.BeginTransactionAsync();
        await SchemaQueries.ExecuteAsync(locker, transaction,
            "SELECT id FROM background_jobs WHERE id=@id FOR UPDATE", ("id", blocked.Value));
        var executor = host.Services.GetRequiredService<JobExecutor>();
        var running = executor.ExecuteAsync(claim, new BackgroundJobRunGate(gate.RunStop), startGate: gate.TryAdmit);
        var released = false;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await WaitForApplicationRowLockAsync(database.AppConnectionString);
            gate.StopStarts();
            await transaction.RollbackAsync();
            released = true;
            var refused = await running.WaitAsync(TimeSpan.FromSeconds(10));
            refused.Receipt.Should().BeNull();
            refused.Outcome.Should().Be(JobStartExecutionOutcome.Failed);
            refused.CleanupFailed.Should().BeFalse();
            observation.ReadScopeDisposed.Should().BeTrue();
            observation.StartScopeDisposed.Should().BeTrue();
        }
        finally
        {
            if (!released) await transaction.RollbackAsync();
            await running.WaitAsync(TimeSpan.FromSeconds(10));
        }

        var row = (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, blocked))!;
        row.Phase.Should().Be("pending");
        row.NormalAttempts.Should().Be(0);
        row.CurrentAttempt.Should().BeNull();
        (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, blocked)).Should().Be(0);
        var positiveClaim = (await routing.TryClaimAsync(positive, 1))!;
        var openGate = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        var started = await executor.ExecuteAsync(positiveClaim, new BackgroundJobRunGate(openGate.RunStop), startGate: openGate.TryAdmit);
        started.Outcome.Should().Be(JobStartExecutionOutcome.Started);
        started.Receipt.Should().NotBeNull("the same actual application admission path remains eligible while open");
        (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, positive)).Should().Be(1);
        probe.Constructions.Should().BeEmpty("Step 4 records admission without activating a business root");
    }
}
