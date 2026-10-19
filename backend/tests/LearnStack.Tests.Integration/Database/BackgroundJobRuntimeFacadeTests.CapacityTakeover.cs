using System.Runtime.ExceptionServices;
using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Entitlements;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobRuntimeFacadeTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Native_capacity_recovery_reprobes_settled_history_after_peer_takes_over_between_probe_and_acquisition(
        bool peerKeepsLiveLease, bool loseRecoveryAck)
    {
        await using var database = await DatabaseAsync();
        var bounds = NativeCommitFaultBounds();
        var state = new CapacityTakeoverState();
        var allowance = new CapacityEntitlementProvider(1);
        var probe = new BackgroundJobFixtureProbe
        {
            Body = (_, _, _) =>
            {
                state.OriginalOwner = JobApplicationSourceOwner.Current!;
                return Task.FromResult(Result<None>.Fail(new Error(LocalizedMessage.Of("lockey_dependency_unavailable"))));
            },
        };
        await using var host = Fixture.Host(database, probe, configure: services =>
        {
            services.RemoveAll<BackgroundJobDefinitionRegistry>();
            services.AddSingleton(Fixture.CapacityRegistry(planLimit: LimitKeys.MaxOrganizations));
            services.RemoveAll<IEntitlementProvider>();
            services.AddSingleton<IEntitlementProvider>(allowance);
            services.RemoveAll<BackgroundJobRuntimeOptions>();
            services.AddSingleton(bounds);
            services.AddSingleton(state);
            services.AddScoped<CapacityCheckpointDisposalHold>();
            services.AddTransient<IPipelineBehavior<RecordBackgroundJobCapacityCleanupCommand, Result<None>>, CapacityTakeoverObserver>();
        });
        await using var proxy = new CommitResponseDroppingProxy(database.OutboxConnectionString,
            commitsToPass: int.MaxValue, lifetime: TimeSpan.FromMinutes(2));
        var physical = new BackgroundJobPhysicalPoolSettings(true, 52);
        var settings = new NpgsqlConnectionStringBuilder(proxy.ConnectionString) { Pooling = true, MaxPoolSize = 52 };
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(settings.ConnectionString));
        await using var peerSource = Source(database, physical);
        using var globals = new GlobalFilterSnapshot();
        using var deadline = new CancellationTokenSource(TestWait);
        var runtime = Runtime(host.Services, source, bounds, physical);
        Exception? testFailure = null;
        var stage = "startup";
        try
        {
            await runtime.StartAsync(deadline.Token);
            await EventuallyQualificationAsync(() => Task.FromResult(runtime.Snapshot.IsHealthy), deadline.Token);
            var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString,
                BackgroundJobRuntimeFixture.CapacityKey, capacityGroup: BackgroundJobRuntimeFixture.CapacityGroup);
            stage = "native handoff or start";
            await EnsureQualificationStartAsync(runtime, source, job, state.Disposal.Entered.Task, deadline.Token);
            stage = "checkpoint disposal";
            await state.Disposal.Entered.Task.WaitAsync(deadline.Token);
            allowance.Limit = 0; // Keep the peer's legitimate retry observable without another business admission.
            state.Command!.CommitAcknowledged.Should().BeTrue("the actual app checkpoint committed before its scope cleanup is held");
            var execution = NativeCommitExecution(runtime, job);
            var original = execution.Identity!;
            var generation = ActualGeneration(runtime, "transactional");
            var checkpoint = (BackgroundJobCapacityCheckpointOperation)typeof(BackgroundJobExecutionControl)
                .GetField("_capacityCheckpoint", PrivateInstance)!.GetValue(execution)!;
            await AssertNativeCapacityReleaseAsync(database, job, "transactional_rollback", cleanup: true);
            await ExpireCapacityRecoveryAsync(database, job, deadline.Token);
            if (loseRecoveryAck) proxy.HoldNextCapacityRecoveryCommitResponse();
            else proxy.HoldNextCapacityProbeRollbackResponse();
            state.Disposal.Release.TrySetResult();
            stage = loseRecoveryAck ? "recovery acquisition COMMIT" : "historical probe rollback";
            await (loseRecoveryAck ? proxy.CapacityRecoveryCommitHeld : proxy.CapacityRollbackHeld).WaitAsync(deadline.Token);
            if (loseRecoveryAck)
            {
                // The second actual recovery acquisition committed, but its
                // ACK is withheld. A peer can legitimately recover that lease.
                var acquired = (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job))!;
                acquired.OwnershipToken.Should().NotBe(checkpoint.Recovery.RecoveryToken);
                acquired.OwnershipKind.Should().Be("recovery");
                await ExpireCapacityRecoveryAsync(database, job, deadline.Token);
            }
            checkpoint.Completion.IsCompletedSuccessfully.Should().BeTrue();
            checkpoint.Recovery.RecoveryToken.Should().NotBe(Guid.Empty);
            NativeCommitExecution(runtime, job).Should().BeSameAs(execution);
            runtime.Snapshot.Control!.PendingOutcomes.Should().Be(1);
            var registry = host.Services.GetRequiredService<BackgroundJobDefinitionRegistry>();
            var peer = new PostgresBackgroundJobControlStore(peerSource, registry, bounds, new SystemGuidFactory());
            if (peerKeepsLiveLease)
            {
                // A privileged fixture peer establishes an exact live lease
                // after the probe unlocked. It does not forge app evidence.
                await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.OutboxConnectionString, deadline.Token);
                var peerToken = Guid.NewGuid();
                await using var acquire = new NpgsqlCommand("""
                    UPDATE background_jobs SET ownership_token=@token, ownership_until=clock_timestamp()+interval '1 minute'
                    WHERE id=@id AND phase='reconciling' AND ownership_kind='recovery' AND ownership_until<=clock_timestamp()
                        AND ownership_token=@previous AND wake_generation=@generation AND current_attempt_id=@attempt
                    RETURNING ownership_token
                    """, connection);
                acquire.Parameters.AddWithValue("id", job.Value);
                acquire.Parameters.AddWithValue("token", peerToken);
                acquire.Parameters.AddWithValue("previous", checkpoint.Recovery.RecoveryToken);
                acquire.Parameters.AddWithValue("generation", checkpoint.Recovery.WakeGeneration);
                acquire.Parameters.AddWithValue("attempt", checkpoint.Recovery.AttemptId.Value);
                (await acquire.ExecuteScalarAsync(deadline.Token)).Should().Be(peerToken);
            }
            else await RouteCapacityFromPeerAsync(peer, job, deadline.Token);
            proxy.ReleaseCapacityRollbackResponse();
            proxy.ReleaseCapacityRecoveryCommitResponse();
            stage = "peer live lease or routing";

            if (peerKeepsLiveLease)
            {
                // Require a second actual locked history probe, not merely an
                // idle wait. Live peer ownership still prevents local settlement.
                await EventuallyQualificationAsync(() => Task.FromResult(proxy.CompletedCapacityRollbacks >= 2), deadline.Token);
                await Task.Delay(bounds.ControlPoll + bounds.ControlPoll, deadline.Token);
                runtime.Snapshot.Control!.PendingOutcomes.Should().Be(1);
                NativeCommitExecution(runtime, job).Should().BeSameAs(execution);
                (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job))!.Phase.Should().Be("reconciling");
                await ExpireCapacityRecoveryAsync(database, job, deadline.Token);
                await RouteCapacityFromPeerAsync(peer, job, deadline.Token);
            }

            stage = "original invocation join";
            await EventuallyQualificationAsync(() => Task.FromResult(runtime.Snapshot.Control is { Executions: 0, PendingOutcomes: 0 }
                && generation.WorkSnapshot is { Running: 0, Retained: 0 }), deadline.Token);
            var released = (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job))!;
            (await ReadCapacityNoStartAsync(database, job)).Generation.Should().Be(2);
            released.NormalAttempts.Should().Be(1);
            released.CurrentAttempt.Should().Be(original.AttemptId.Value);
            await AssertNativeCapacityReleaseAsync(database, job, "transactional_rollback", cleanup: true);
            (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job)).Should().Be(1);
            probe.Constructions.Should().Equal("CapacityRoot");
            var reopen = () => state.OriginalOwner!.Enter();
            reopen.Should().Throw<InvalidOperationException>("the settled original invocation sealed its source owner");
            state.Checkpoints.Should().Be(1, "re-probing immutable history never issues another app checkpoint");
            if (loseRecoveryAck) runtime.Snapshot.Control!.UnknownOutcomes.Should().BeGreaterThan(0);
        }
        catch (Exception failure)
        {
            testFailure = new InvalidOperationException($"Native takeover failed at {stage}; checkpoints={state.Checkpoints};"
                + $"checkpointAck={state.Command?.CommitAcknowledged};disposal={state.Disposal.Joined.Task.Status};runtime={runtime.Snapshot}", failure);
        }
        finally
        {
            state.Disposal.Release.TrySetResult();
            proxy.ReleaseCapacityRollbackResponse();
            proxy.ReleaseCapacityRecoveryCommitResponse();
            using var cleanup = new CancellationTokenSource(TestWait);
            try
            {
                await runtime.StopAsync(cleanup.Token);
                await runtime.Completion.WaitAsync(cleanup.Token);
                await DisposePinnedSourceAsync(source);
            }
            catch (Exception failure) when (testFailure is not null)
            {
                testFailure = new AggregateException(testFailure, failure);
            }
        }
        if (testFailure is not null) ExceptionDispatchInfo.Capture(testFailure).Throw();
    }

    private static async Task ExpireCapacityRecoveryAsync(DisposableSchemaDatabase database,
        LearnStack.SharedKernel.Jobs.BackgroundJobId job, CancellationToken token)
    {
        await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.OutboxConnectionString, token);
        await using var command = new NpgsqlCommand("""
            UPDATE background_jobs SET ownership_until=clock_timestamp()-interval '1 microsecond'
            WHERE id=@id AND phase='reconciling' AND ownership_kind='recovery'
            RETURNING ownership_until<clock_timestamp()
            """, connection);
        command.Parameters.AddWithValue("id", job.Value);
        (await command.ExecuteScalarAsync(token)).Should().Be(true);
    }

    private static async Task RouteCapacityFromPeerAsync(PostgresBackgroundJobControlStore peer,
        LearnStack.SharedKernel.Jobs.BackgroundJobId job, CancellationToken token)
    {
        var routed = await peer.RunRecoveryQuantumAsync(BackgroundJobControlLane.Recovery, [], token);
        routed.ThrowIfFailed();
        routed.CommitAcknowledged.Should().BeTrue();
        routed.Value.Should().ContainSingle(change => change.JobId == job.Value && change.Phase == "pending"
            && change.WakeGeneration == 2, "the peer routes through the real fenced recovery transaction");
    }

    private sealed class CapacityTakeoverState
    {
        internal FacadeCleanupSignals Disposal { get; } = new();
        internal RecordBackgroundJobCapacityCleanupCommand? Command { get; set; }
        internal JobApplicationSourceOwner? OriginalOwner { get; set; }
        internal int Checkpoints;
    }

    private sealed class CapacityTakeoverObserver(CapacityTakeoverState state, IServiceProvider services)
        : IPipelineBehavior<RecordBackgroundJobCapacityCleanupCommand, Result<None>>
    {
        public async Task<Result<None>> Handle(RecordBackgroundJobCapacityCleanupCommand request,
            RequestHandlerDelegate<Result<None>> next, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref state.Checkpoints);
            state.Command = request;
            _ = services.GetRequiredService<CapacityCheckpointDisposalHold>();
            return await next();
        }
    }

    private sealed class CapacityCheckpointDisposalHold(CapacityTakeoverState state) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            state.Command!.CommitAcknowledged.Should().BeTrue();
            state.Disposal.Entered.TrySetResult();
            await state.Disposal.Release.Task.ConfigureAwait(false);
            state.Disposal.Joined.TrySetResult();
        }
    }
}
