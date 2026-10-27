using FluentAssertions;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobRuntimeFacadeTests
{
    [Fact]
    public async Task Native_lifecycle_qualification_resends_after_a_locked_hint_and_confirmed_handoff()
    {
        await using var database = await DatabaseAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var probe = new BackgroundJobFixtureProbe
        {
            Body = (_, _, _) =>
            {
                Interlocked.Increment(ref calls);
                entered.TrySetResult();
                return Task.FromResult(Result.Ok(None.Value));
            },
        };
        await using var host = Fixture.Host(database, probe);
        await using var source = Source(database);
        using var globals = new GlobalFilterSnapshot();
        using var deadline = new CancellationTokenSource(TestWait);
        var time = new HangfirePoolOwnerTests.ManualTimeProvider();
        var bounds = NativeCommitFaultBounds();
        var runtime = Runtime(host.Services, source, bounds, timeProvider: time);
        await using var locker = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        NpgsqlTransaction? held = null;
        try
        {
            await runtime.StartAsync(deadline.Token);
            await EventuallyQualificationAsync(() => Task.FromResult(runtime.Snapshot.IsHealthy
                && time.DelayCount(bounds.ControlPoll) == 3), deadline.Token);
            var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString);
            var bridge = new PostgresBackgroundJobBridgeStore(source, Fixture.Registry, bounds, new SystemGuidFactory());
            var claim = (await bridge.TryClaimAsync([], deadline.Token))!;
            claim.JobId.Should().Be(job);
            held = await locker.BeginTransactionAsync(deadline.Token);
            await LockNativeJobAsync(locker, held, job, deadline.Token);
            var skipped = CreateQualificationWakeup(ActualProvider(runtime), Fixture.Registry.Infrastructure.Pools["transactional"], job);
            await EventuallyQualificationAsync(async () => await StateAsync(source, skipped) == "Succeeded", deadline.Token);
            calls.Should().Be(0, "a successful native hint need not acquire the locked application row");
            (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job)).Should().Be(0);
            await held.RollbackAsync(deadline.Token);
            await held.DisposeAsync();
            held = null;
            (await bridge.AcknowledgeAsync(claim, skipped, deadline.Token)).Should().BeTrue();
            (await VendorAsync(source, job)).Should().Be(skipped);
            (await bridge.TryClaimAsync([], deadline.Token)).Should().BeNull("the accepted repair grace is still live");

            var hints = await EnsureQualificationCompletionAsync(runtime, source, job, deadline.Token);
            hints.Should().ContainSingle().Which.Generation.Should().Be(1);
            var resent = hints[0].Vendor;
            resent.Should().NotBe(skipped);
            await entered.Task.WaitAsync(deadline.Token);
            await EventuallyQualificationAsync(async () => await StateAsync(source, resent) == "Succeeded", deadline.Token);
            while (runtime.Snapshot.Control is not { Executions: 0, PendingOutcomes: 0 })
            {
                time.Advance(bounds.ControlPoll);
                await Task.Delay(TimeSpan.FromMilliseconds(25), deadline.Token);
            }
            calls.Should().Be(1);
            (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job)).Should().Be(1);
        }
        finally
        {
            if (held is not null) { await held.RollbackAsync(); await held.DisposeAsync(); }
            using var cleanup = new CancellationTokenSource(TestWait);
            await runtime.StopAsync(cleanup.Token);
            await runtime.Completion.WaitAsync(cleanup.Token);
            await DisposePinnedSourceAsync(source);
        }
    }

    [Fact]
    public async Task Native_completion_qualification_resends_the_locked_retry_generation_after_actual_rollback()
    {
        await using var database = await DatabaseAsync();
        var calls = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new BackgroundJobFixtureProbe
        {
            Body = async (_, services, token) =>
            {
                var context = services.GetRequiredService<ITenantContext>().Should().BeOfType<JobTenantContext>().Subject;
                var job = context.JobId ?? throw new InvalidOperationException("The actual business job identity is missing.");
                var entry = Interlocked.Increment(ref calls);
                entered.TrySetResult();
                (await BackgroundJobRuntimeRows.CountOrganizationAsync(database.AppConnectionString, job.Value)).Should().Be(0);
                var written = await services.GetRequiredService<ISender>().Send(Fixture.OrganizationRequest(
                    OrganizationId.From(job.Value), "job-handoff-" + job.Value.ToString("N")[..12], "Job handoff"), token);
                written.IsSuccess.Should().BeTrue();
                return entry == 1
                    ? Result<None>.Fail(new Error(LocalizedMessage.Of("lockey_dependency_unavailable")))
                    : Result.Ok(None.Value);
            },
        };
        await using var host = Fixture.Host(database, probe);
        await using var source = Source(database);
        using var globals = new GlobalFilterSnapshot();
        using var deadline = new CancellationTokenSource(TestWait);
        var time = new HangfirePoolOwnerTests.ManualTimeProvider();
        var bounds = NativeCommitFaultBounds();
        var runtime = Runtime(host.Services, source, bounds, timeProvider: time);
        await using var locker = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        NpgsqlTransaction? held = null;
        SemaphoreSlim? bridgeQuantum = null;
        var quantumHeld = false;
        try
        {
            await runtime.StartAsync(deadline.Token);
            await EventuallyQualificationAsync(() => Task.FromResult(runtime.Snapshot.IsHealthy
                && time.DelayCount(bounds.ControlPoll) == 3), deadline.Token);
            // This fixture qualifies native handoff, not bridge fairness. Hold
            // automatic bridge issuance while real control polling creates the
            // retry; the explicit store still commits each genuine bridge ACK.
            var actualBridge = (BackgroundJobBridge)typeof(BackgroundJobRuntime).GetField("_bridge", PrivateInstance)!.GetValue(runtime)!;
            bridgeQuantum = (SemaphoreSlim)typeof(BackgroundJobBridge).GetField("_quantum", PrivateInstance)!.GetValue(actualBridge)!;
            await bridgeQuantum.WaitAsync(deadline.Token);
            quantumHeld = true;
            var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString);
            var bridge = new PostgresBackgroundJobBridgeStore(source, Fixture.Registry, bounds, new SystemGuidFactory());
            var initial = (await bridge.TryClaimAsync([], deadline.Token))!;
            initial.WakeGeneration.Should().Be(1);
            var original = CreateQualificationWakeup(ActualProvider(runtime), Fixture.Registry.Infrastructure.Pools["transactional"], job);
            await entered.Task.WaitAsync(deadline.Token);
            (await bridge.AcknowledgeAsync(initial, original, deadline.Token)).Should().BeTrue();
            await EventuallyQualificationAsync(async () =>
            {
                time.Advance(bounds.ControlPoll);
                return (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job)) is { Phase: "pending", NormalAttempts: 1 }
                    && runtime.Snapshot.Control is { Executions: 0, PendingOutcomes: 0 };
            }, deadline.Token);
            await EventuallyQualificationAsync(async () => await StateAsync(source, original) == "Succeeded"
                && QualificationPoolOwners(runtime)["transactional"].Snapshot.Work is { Running: 0, Retained: 0, Closed: false }
                && (await ReadQualificationHandoffAsync(source, job, deadline.Token)).Eligible, deadline.Token);
            (await BackgroundJobRuntimeRows.CountOrganizationAsync(database.AppConnectionString, job.Value)).Should().Be(0,
                "the first genuine business write rolled back before control created its retry");

            var retry = (await bridge.TryClaimAsync([], deadline.Token))!;
            retry.WakeGeneration.Should().Be(2, "only the real rollback control transition creates this eligibility cycle");
            held = await locker.BeginTransactionAsync(deadline.Token);
            await LockNativeJobAsync(locker, held, job, deadline.Token);
            var skipped = CreateQualificationWakeup(ActualProvider(runtime), Fixture.Registry.Infrastructure.Pools["transactional"], job,
                retry.WakeGeneration);
            await EventuallyQualificationAsync(async () => await StateAsync(source, skipped) == "Succeeded", deadline.Token);
            calls.Should().Be(1, "the current-generation stock hint skipped the actual row lock");
            (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job)).Should().Be(1);
            await held.RollbackAsync(deadline.Token);
            await held.DisposeAsync();
            held = null;
            (await bridge.AcknowledgeAsync(retry, skipped, deadline.Token)).Should().BeTrue();
            (await bridge.TryClaimAsync([], deadline.Token)).Should().BeNull("the acknowledged retry hint still has its full repair grace");

            var hints = await EnsureQualificationCompletionAsync(runtime, source, job, deadline.Token);
            hints.Should().ContainSingle().Which.Generation.Should().Be(2);
            await EventuallyQualificationAsync(async () => await StateAsync(source, hints[0].Vendor) == "Succeeded", deadline.Token);
            calls.Should().Be(2);
            (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job)).Should().Match<BackgroundJobRuntimeRow>(
                row => row.Phase == "completed" && row.NormalAttempts == 2);
            (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job)).Should().Be(2);
            (await BackgroundJobRuntimeRows.CountOrganizationAsync(database.AppConnectionString, job.Value)).Should().Be(1);
            (await SuccessfulBacklogBusinessAuditsAsync(database, job)).Should().Be(1);
        }
        finally
        {
            if (held is not null) { await held.RollbackAsync(); await held.DisposeAsync(); }
            if (quantumHeld) bridgeQuantum!.Release();
            using var cleanup = new CancellationTokenSource(TestWait);
            await runtime.StopAsync(cleanup.Token);
            await runtime.Completion.WaitAsync(cleanup.Token);
            await DisposePinnedSourceAsync(source);
        }
    }

    private static async Task<IReadOnlyList<(long Generation, string Vendor)>> EnsureQualificationCompletionAsync(
        BackgroundJobRuntime runtime, PrivilegedDataSource source, BackgroundJobId job, CancellationToken cancellationToken)
    {
        // Completion fixtures do not measure repair grace. A successfully
        // consumed hint may have skipped ACK's row lock or old local cleanup.
        // Require the real bridge and native worker first, then supplement at
        // most once for each control-created generation, including retries.
        var hints = new List<(long Generation, string Vendor)>();
        var control = (BackgroundJobControl)typeof(BackgroundJobRuntime).GetField("_jobs", PrivateInstance)!.GetValue(runtime)!;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = await ReadQualificationHandoffAsync(source, job, cancellationToken);
            if (row.Phase == "completed") return hints.AsReadOnly();
            if (row.Eligible && row.Vendor is { } vendor && hints.All(hint => hint.Generation != row.Generation)
                && await StateAsync(source, vendor) == "Succeeded"
                && QualificationPoolOwners(runtime)["transactional"].Snapshot is
                { State: HangfirePoolOwnerState.Running, Work: { Running: 0, Retained: 0, Closed: false } }
                && control.CanRoute(job))
            {
                hints.Count.Should().BeLessThan(BackgroundJobRuntimeOptions.MaxNormalAttempts,
                    "fixture hints never create an unbounded retry policy");
                cancellationToken.ThrowIfCancellationRequested();
                hints.Add((row.Generation, CreateQualificationWakeup(ActualProvider(runtime),
                    Fixture.Registry.Infrastructure.Pools["transactional"], job, row.Generation)));
            }
            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
        }
    }

    private static async Task<QualificationHandoff> ReadQualificationHandoffAsync(PrivilegedDataSource source,
        BackgroundJobId job, CancellationToken cancellationToken)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT phase, wake_generation, vendor_job_id,
                phase='pending' AND profile='transactional' AND normal_attempts<6
                AND ownership_token IS NULL AND bridge_token IS NULL
                AND processed_at IS NULL AND dead_lettered_at IS NULL AND terminal_reason IS NULL
                AND available_after <= clock_timestamp() AND original_due_at <= clock_timestamp()
            FROM background_jobs WHERE id=@id
            """, connection) { CommandTimeout = 5 };
        command.Parameters.AddWithValue("id", job.Value);
        await using var row = await command.ExecuteReaderAsync(cancellationToken);
        (await row.ReadAsync(cancellationToken)).Should().BeTrue();
        return new(row.GetString(0), row.GetInt64(1), row.IsDBNull(2) ? null : row.GetString(2), row.GetBoolean(3));
    }

    private sealed record QualificationHandoff(string Phase, long Generation, string? Vendor, bool Eligible);

    private static async Task<string?> EnsureQualificationStartAsync(BackgroundJobRuntime runtime,
        PrivilegedDataSource source, LearnStack.SharedKernel.Jobs.BackgroundJobId job, Task started,
        CancellationToken cancellationToken = default)
    {
        // These cases qualify native execution/cleanup, not bridge repair timing.
        // An already-started body may itself hold the row needed by ACK. Otherwise
        // observe the committed ACK before sending an additional real stock hint.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TestWait);
        await EventuallyQualificationAsync(async () => started.IsCompleted
            || await VendorAsync(source, job, deadline.Token) is not null, deadline.Token);
        deadline.Token.ThrowIfCancellationRequested();
        return started.IsCompleted ? null : CreateQualificationWakeup(ActualProvider(runtime),
            Fixture.Registry.Infrastructure.Pools["transactional"], job);
    }
}
