using System.Runtime.ExceptionServices;
using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Entitlements;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobRuntimeFacadeTests
{
    [Fact]
    public async Task Native_rolled_back_capacity_admission_joins_after_peer_clears_the_original_routing_token()
    {
        await using var database = await DatabaseAsync();
        var bounds = NativeCommitFaultBounds();
        var state = new CapacityNoStartState();
        var allowance = new CapacityEntitlementProvider(1);
        var probe = new BackgroundJobFixtureProbe();
        var reads = 0;
        var definitions = Fixture.CapacityRegistry(planLimit: LimitKeys.MaxOrganizations);
        await using var proxy = new CommitResponseDroppingProxy(database.AppConnectionString,
            commitsToPass: 1, lifetime: TimeSpan.FromMinutes(2), dropCommitRequest: true);
        BackgroundJobRuntime? boundRuntime = null;
        var observation = new BackgroundJobAdmissionObservation
        {
            OnRead = (_, _) => { Interlocked.Increment(ref reads); return Task.CompletedTask; },
            OnStart = (command, services) =>
            {
                if (state.Command is not null) return Task.CompletedTask;
                state.Command = command;
                state.OriginalOwner = JobApplicationSourceOwner.Current!;
                state.OriginalUnit = services.GetRequiredService<IUnitOfWork>();
                state.Execution = NativeCommitExecution(boundRuntime!, command.Claim.JobId);
                _ = services.GetRequiredService<CapacityNoStartAdmissionDisposal>();
                return Task.CompletedTask;
            },
        };
        await using var host = Fixture.Host(database, probe, configure: services =>
        {
            services.RemoveAll<BackgroundJobDefinitionRegistry>();
            services.AddSingleton(definitions);
            services.RemoveAll<IEntitlementProvider>();
            services.AddSingleton<IEntitlementProvider>(allowance);
            services.RemoveAll<BackgroundJobRuntimeOptions>();
            services.AddSingleton(bounds);
            services.AddSingleton(state);
            observation.Configure(services);
            services.AddScoped<CapacityNoStartAdmissionDisposal>();
        }, appConnectionString: new NpgsqlConnectionStringBuilder(proxy.ConnectionString)
        { Pooling = true, MaxPoolSize = 100 }.ConnectionString);
        var physical = new BackgroundJobPhysicalPoolSettings(true, 52);
        await using var source = Source(database, physical);
        await using var peerSource = Source(database, physical);
        using var globals = new GlobalFilterSnapshot();
        using var deadline = new CancellationTokenSource(TestWait);
        var time = new HangfirePoolOwnerTests.ManualTimeProvider();
        var runtime = Runtime(host.Services, source, bounds, physical, time);
        boundRuntime = runtime;
        Exception? testFailure = null;
        var stage = "startup";
        try
        {
            await runtime.StartAsync(deadline.Token);
            await EventuallyQualificationAsync(() => Task.FromResult(runtime.Snapshot.IsHealthy), deadline.Token);
            // This proof owns native admission, not bridge ACK timing. Park its
            // empty polling quantum before exposing the row, as in the lifetime
            // controls. A valid ACK row lock can otherwise make a hint skip the
            // row until the unrelated sixty-second repair grace.
            await EventuallyQualificationAsync(() => Task.FromResult(time.DelayCount(bounds.ControlPoll) == 3), deadline.Token);
            runtime.Snapshot.Bridge!.Acknowledged.Should().Be(0);
            var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString,
                BackgroundJobRuntimeFixture.CapacityKey, capacityGroup: BackgroundJobRuntimeFixture.CapacityGroup);
            _ = CreateQualificationWakeup(ActualProvider(runtime), definitions.Infrastructure.Pools["transactional"], job);
            stage = "actual admission rollback and scope disposal";
            await proxy.CommitRequestDropped.WaitAsync(deadline.Token);
            await state.Disposal.Entered.Task.WaitAsync(deadline.Token);
            proxy.DroppedCommitRequests.Should().Be(1);
            proxy.CompletedCommits.Should().Be(1, "only the actual read COMMIT reached PostgreSQL");
            runtime.Snapshot.Bridge!.Acknowledged.Should().Be(0, "the actual wakeup was explicit while bridge polling remained parked");
            state.Command!.CommitStarted.Should().BeTrue();
            state.Command.Receipt.Should().BeNull();
            var execution = state.Execution!;
            var generation = ActualGeneration(runtime, "transactional");
            var admission = CapacityNoStartAdmissionTask(execution);
            admission.IsCompleted.Should().BeFalse("the actual original scope still owns its cleanup");
            (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job)).Should().Be(0);
            allowance.Limit = 0; // Subsequent native hints must not obscure the original absent admission.
            stage = "peer routing cleanup";
            await using (var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.OutboxConnectionString, deadline.Token))
            await using (var expire = new NpgsqlCommand("""
                UPDATE background_jobs SET ownership_until=clock_timestamp()-interval '1 microsecond'
                WHERE id=@id AND ownership_token=@original AND ownership_kind='routing' AND phase='pending'
                RETURNING normal_attempts
                """, connection))
            {
                expire.Parameters.AddWithValue("id", job.Value);
                expire.Parameters.AddWithValue("original", state.Command.Claim.Token);
                (await expire.ExecuteScalarAsync(deadline.Token)).Should().Be(0);
            }
            var peer = new PostgresBackgroundJobControlStore(peerSource, definitions, bounds, new SystemGuidFactory());
            var cleared = await peer.RunRecoveryQuantumAsync(BackgroundJobControlLane.Recovery, [], deadline.Token);
            cleared.ThrowIfFailed();
            cleared.CommitAcknowledged.Should().BeTrue();
            cleared.Value.Should().ContainSingle(change => change.JobId == job.Value && change.Phase == "pending"
                && change.WakeGeneration == 1, "the real peer clears routing without spending a normal start");
            stage = "original invocation join";
            state.Disposal.Release.TrySetResult();
            var result = await admission.WaitAsync(deadline.Token);
            result.Outcome.Should().Be(JobStartExecutionOutcome.Indeterminate);
            result.CleanupFailed.Should().BeFalse();
            result.Receipt.Should().BeNull();
            result.SourceOwner.Should().BeSameAs(state.OriginalOwner);
            while (runtime.Snapshot.Control is not { Executions: 0, PendingOutcomes: 0 }
                || generation.WorkSnapshot is not { Running: 0, Retained: 0 })
            {
                // Advance only owner polling. Real SQL, tasks, callbacks and
                // scope disposal still have to join under the original deadline.
                time.Advance(bounds.ControlPoll);
                await Task.Delay(TimeSpan.FromMilliseconds(25), deadline.Token);
            }
            (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job)).Should().Be(0);
            probe.Constructions.Should().BeEmpty();
            var reopen = () => state.OriginalOwner!.Enter();
            reopen.Should().Throw<InvalidOperationException>();
            await using var app = await PostgresFixture.OpenAsync(database.AppConnectionString, deadline.Token);
            await using var transaction = await app.BeginTransactionAsync(deadline.Token);
            await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null);
            (await SchemaQueries.CountAsync(app, "SELECT count(*) FROM background_job_reservations", transaction)).Should().Be(0L);
        }
        catch (Exception failure)
        {
            testFailure = new InvalidOperationException(
                $"Admission rollback takeover failed at {stage};dropped={proxy.DroppedCommitRequests};"
                + $"connections={proxy.AcceptedConnections};commits={proxy.CompletedCommits};reads={Volatile.Read(ref reads)};"
                + $"command={state.Command is not null};committing={state.Command?.CommitStarted};"
                + $"receipt={state.Command?.Receipt is not null};polls={time.DelayCount(bounds.ControlPoll)};runtime={runtime.Snapshot}", failure);
        }
        finally
        {
            state.Disposal.Release.TrySetResult();
            using var cleanup = new CancellationTokenSource(TestWait);
            try
            {
                await runtime.StopAsync(cleanup.Token);
                await runtime.Completion.WaitAsync(cleanup.Token);
                await DisposePinnedSourceAsync(source);
            }
            catch (Exception failure) when (testFailure is not null) { testFailure = new AggregateException(testFailure, failure); }
        }
        if (testFailure is not null) ExceptionDispatchInfo.Capture(testFailure).Throw();
    }
}
