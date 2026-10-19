using System.Text.Json;
using FluentAssertions;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Tenancy.Application.Contracts.IntegrationEvents;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Messaging;
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
    [Fact]
    public async Task Native_lost_business_ACK_retains_the_actual_identity_until_a_locked_fresh_probe_resolves_without_duplicate_effects()
    {
        await using var database = await DatabaseAsync();
        var bounds = NativeCommitFaultBounds();
        await using var proxy = new CommitResponseDroppingProxy(database.AppConnectionString,
            commitsToPass: 2, holdCommitResponse: true);
        var appSettings = new NpgsqlConnectionStringBuilder(proxy.ConnectionString) { Pooling = true, MaxPoolSize = 100 };
        var effects = new NativeCommitEffects();
        var observation = new BackgroundJobAdmissionObservation();
        var probe = new BackgroundJobFixtureProbe
        {
            Body = async (_, services, token) =>
            {
                // These are observed protocol completions from the actual app
                // source, not an assumption about which COMMIT is being lost.
                proxy.CompletedCommits.Should().Be(2);
                proxy.DroppedCommitResponses.Should().Be(0);
                observation.ReadScopeDisposed.Should().BeTrue();
                observation.StartScopeDisposed.Should().BeTrue();
                await StageNativeCommitEffectsAsync(services, effects, token);
                return Result.Ok(None.Value);
            },
        };
        await using var host = Fixture.Host(database, probe, configure: services =>
        {
            observation.Configure(services);
            services.RemoveAll<BackgroundJobRuntimeOptions>();
            services.AddSingleton(bounds);
        }, appConnectionString: appSettings.ConnectionString);
        var physical = new BackgroundJobPhysicalPoolSettings(true, 52);
        var controlName = "native_commit_probe_" + Guid.NewGuid().ToString("N");
        await using var source = Source(database, physical, controlName);
        using var globals = new GlobalFilterSnapshot();
        using var deadline = new CancellationTokenSource(TestWait);
        var runtime = Runtime(host.Services, source, bounds, physical);
        await using var locker = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        NpgsqlTransaction? locked = null;
        Exception? testFailure = null;
        try
        {
            await runtime.StartAsync(deadline.Token);
            await EventuallyQualificationAsync(() => Task.FromResult(runtime.Snapshot.IsHealthy), deadline.Token);
            var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString);
            await EnsureQualificationStartAsync(runtime, source, job, proxy.CommitResponseHeld, deadline.Token);
            await proxy.CommitResponseHeld.WaitAsync(deadline.Token);
            proxy.CompletedCommits.Should().Be(3);
            proxy.DroppedCommitResponses.Should().Be(1);
            var execution = NativeCommitExecution(runtime, job);
            var identity = execution.Identity!;
            identity.Should().NotBeNull();
            execution.Gate.CommitStarted.Should().BeTrue();
            execution.Gate.CommitAcknowledged.Should().BeFalse();
            (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job))!.Phase.Should().Be("completed");
            await AssertNativeCommitEffectsAsync(database, effects, committed: true);

            // Establish the lock after PostgreSQL durably committed, while its
            // acknowledgement is still held. The ensuing real probe must wait.
            locked = await locker.BeginTransactionAsync(deadline.Token);
            await LockNativeJobAsync(locker, locked, job, deadline.Token);
            proxy.ReleaseHeldResponse();
            await WaitForNativeJobLockAsync(database, locker.ProcessID, "learnstack_outbox_admin",
                controlName, deadline.Token);
            var business = await NativeCommitBusinessTask(execution).WaitAsync(deadline.Token);
            business.CommitStarted.Should().BeTrue();
            business.CommitAcknowledged.Should().BeFalse();
            business.DurableOutcomeUncertain.Should().BeTrue();
            business.KnownRollback.Should().BeFalse();
            business.Decision.Disposition.Should().Be(BackgroundJobFailureDisposition.Reconcile);
            business.CleanupFailed.Should().BeFalse();
            NativeCommitExecution(runtime, job).Should().BeSameAs(execution);
            execution.Identity.Should().BeSameAs(identity);
            runtime.Snapshot.Control!.Executions.Should().Be(1);
            runtime.Snapshot.Control.PendingOutcomes.Should().Be(1);
            (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job)).Should().Be(1);

            await locked.RollbackAsync(deadline.Token);
            await locked.DisposeAsync();
            locked = null;
            await EventuallyQualificationAsync(async () => runtime.Snapshot.Control is { Executions: 0, PendingOutcomes: 0 }
                && await NativeBusinessCompletedAsync(database, source, job), deadline.Token);
            runtime.Snapshot.Control!.Retries.Should().Be(0);

            // A second actual stock wakeup exercises the same opaque identity
            // after reconciliation and cleanup; the completed app row refuses it.
            var duplicate = CreateQualificationWakeup(ActualProvider(runtime),
                Fixture.Registry.Infrastructure.Pools["transactional"], job);
            await EventuallyQualificationAsync(async () => await StateAsync(source, duplicate) == "Succeeded", deadline.Token);
            await AssertNativeCommitEffectsAsync(database, effects, committed: true);
            probe.Constructions.Should().Equal("TenantRoot");
            (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job)).Should().Be(1);
            await AssertNoNativeProviderRetryAsync(source, job, deadline.Token);
            await AssertGuardedPoolAsync(source, physical);
        }
        catch (Exception failure) { testFailure = failure; }
        finally
        {
            await PreserveNativeBodyFailureAsync(testFailure, async () =>
            {
                proxy.ReleaseHeldResponse();
                if (locked is not null) { await locked.RollbackAsync(); await locked.DisposeAsync(); }
                using var cleanup = new CancellationTokenSource(TestWait);
                await runtime.StopAsync(cleanup.Token);
                await runtime.Completion.WaitAsync(cleanup.Token);
                await DisposePinnedSourceAsync(source);
            });
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Native_final_completion_rechecks_database_expiry_after_its_real_app_row_lock_and_preserves_atomic_effects(bool expire)
    {
        await using var database = await DatabaseAsync();
        var bounds = NativeCommitFaultBounds();
        var effects = new NativeCommitEffects();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var beginEffects = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var effectsStaged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishHandler = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var appName = "native_final_lock_" + Guid.NewGuid().ToString("N");
        var appSettings = new NpgsqlConnectionStringBuilder(database.AppConnectionString)
        {
            ApplicationName = appName,
            Pooling = true,
            MaxPoolSize = 100,
        };
        var probe = new BackgroundJobFixtureProbe
        {
            Body = async (_, services, token) =>
            {
                entered.TrySetResult();
                await beginEffects.Task.WaitAsync(token);
                await StageNativeCommitEffectsAsync(services, effects, token);
                effectsStaged.TrySetResult();
                await finishHandler.Task.WaitAsync(token);
                return Result.Ok(None.Value);
            },
        };
        await using var host = Fixture.Host(database, probe, configure: services =>
        {
            services.RemoveAll<BackgroundJobRuntimeOptions>();
            services.AddSingleton(bounds);
        }, appConnectionString: appSettings.ConnectionString);
        var physical = new BackgroundJobPhysicalPoolSettings(true, 52);
        await using var source = Source(database, physical);
        using var globals = new GlobalFilterSnapshot();
        using var deadline = new CancellationTokenSource(TestWait);
        var runtime = Runtime(host.Services, source, bounds, physical);
        await using var locker = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        NpgsqlTransaction? locked = null;
        Exception? testFailure = null;
        try
        {
            await runtime.StartAsync(deadline.Token);
            // A completed first renewal tick precedes submission. The valid
            // 60-second next tick is beyond this bounded 30-second scenario, so
            // SKIP LOCKED renewal refusal cannot close the gate under test.
            await EventuallyQualificationAsync(() => Task.FromResult(runtime.Snapshot.IsHealthy), deadline.Token);
            var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString);
            await EnsureQualificationStartAsync(runtime, source, job, entered.Task, deadline.Token);
            await entered.Task.WaitAsync(deadline.Token);
            // The child FK takes KEY SHARE on its parent. Hold staging until
            // ACK has acquired its stronger row lock and actually completed.
            effects.Child.Should().BeNull();
            // Observe the first real handoff, then join only the bridge. This
            // isolates final-fence/rollback state from a later retry hint ACK,
            // which legitimately clears last_error while the row is pending.
            await EventuallyQualificationAsync(async () => await VendorAsync(source, job) is not null, deadline.Token);
            var bridge = (BackgroundJobBridge)typeof(BackgroundJobRuntime)
                .GetField("_bridge", PrivateInstance)!.GetValue(runtime)!;
            await bridge.StopAsync(deadline.Token);
            bridge.Snapshot.Outstanding.Should().Be(0);
            beginEffects.TrySetResult();
            await effectsStaged.Task.WaitAsync(deadline.Token);
            var execution = NativeCommitExecution(runtime, job);
            var admitted = (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job))!;
            admitted.Phase.Should().Be("executing");
            await AssertNativeCommitEffectsAsync(database, effects, committed: false);
            locked = await locker.BeginTransactionAsync(deadline.Token);
            await LockNativeJobAsync(locker, locked, job, deadline.Token);
            finishHandler.TrySetResult();
            await WaitForNativeJobLockAsync(database, locker.ProcessID, "learnstack_app", appName, deadline.Token);
            execution.Gate.IsClosed.Should().BeFalse();
            execution.Gate.CommitStarted.Should().BeFalse();

            if (expire)
            {
                // Expire only after proving the actual final-fence wait. The
                // lock hides this change from recovery until the waiter resumes.
                await using var expiry = new NpgsqlCommand("""
                    UPDATE background_jobs SET ownership_until=clock_timestamp()-interval '1 microsecond'
                    WHERE id=@id AND ownership_token=@token
                    RETURNING ownership_until < clock_timestamp()
                    """, locker, locked);
                expiry.Parameters.AddWithValue("id", job.Value);
                expiry.Parameters.AddWithValue("token", admitted.OwnershipToken!.Value);
                (await expiry.ExecuteScalarAsync(deadline.Token)).Should().Be(true);
            }
            else
            {
                await using var live = new NpgsqlCommand("""
                    SELECT ownership_until > clock_timestamp() AND attempt_deadline > clock_timestamp()
                    FROM background_jobs WHERE id=@id
                    """, locker, locked);
                live.Parameters.AddWithValue("id", job.Value);
                (await live.ExecuteScalarAsync(deadline.Token)).Should().Be(true);
            }
            NativeCommitExecution(runtime, job).Should().BeSameAs(execution);
            (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job))!.CurrentAttempt.Should().Be(admitted.CurrentAttempt);
            await locked.CommitAsync(deadline.Token);
            await locked.DisposeAsync();
            locked = null;
            var business = await NativeCommitBusinessTask(execution).WaitAsync(deadline.Token);
            business.CommitAcknowledged.Should().Be(!expire);
            business.CommitStarted.Should().Be(!expire);
            business.KnownRollback.Should().Be(expire);
            business.DurableOutcomeUncertain.Should().BeFalse();
            business.CleanupFailed.Should().BeFalse();
            business.Decision.Disposition.Should().Be(expire
                ? BackgroundJobFailureDisposition.Retry : BackgroundJobFailureDisposition.Succeeded);

            await EventuallyQualificationAsync(async () => runtime.Snapshot.Control!.Executions == 0
                && (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job))!.Phase == (expire ? "pending" : "completed"), deadline.Token);
            // Close new issuance after observing the real retry transition, so
            // the next counted attempt cannot obscure the first rollback proof.
            runtime.RequestStop();
            if (expire) await AssertNativeRetryDispositionAsync(database, job, deadline.Token);
            else runtime.Snapshot.Control!.Retries.Should().Be(0);
            await AssertNativeCommitEffectsAsync(database, effects, committed: !expire);
            (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job)).Should().Be(1);
            probe.Constructions.Should().Equal("TenantRoot");
            await AssertNoNativeProviderRetryAsync(source, job, deadline.Token);
        }
        catch (Exception failure) { testFailure = failure; }
        finally
        {
            await PreserveNativeBodyFailureAsync(testFailure, async () =>
            {
                beginEffects.TrySetResult();
                finishHandler.TrySetResult();
                if (locked is not null) { await locked.RollbackAsync(); await locked.DisposeAsync(); }
                using var cleanup = new CancellationTokenSource(TestWait);
                await runtime.StopAsync(cleanup.Token);
                await runtime.Completion.WaitAsync(cleanup.Token);
                await DisposePinnedSourceAsync(source);
            });
        }
    }

    private static BackgroundJobRuntimeOptions NativeCommitFaultBounds() => new()
    {
        LeaseDuration = TimeSpan.FromMinutes(3),
        RenewalInterval = TimeSpan.FromSeconds(60),
        ProgressTimeout = TimeSpan.FromMinutes(4),
        ControlPoll = TimeSpan.FromMilliseconds(50),
        RecoveryProbeInterval = TimeSpan.FromSeconds(5),
    };

    private sealed class NativeCommitEffects
    {
        internal Guid Organization { get; } = Guid.CreateVersion7();
        internal Guid Outgoing { get; } = Guid.CreateVersion7();
        internal BackgroundJobId? Child { get; set; }
    }

    private static async Task StageNativeCommitEffectsAsync(IServiceProvider services, NativeCommitEffects effects,
        CancellationToken cancellationToken)
    {
        var unit = services.GetRequiredService<IUnitOfWork>();
        (await BackgroundJobRuntimeRows.ScalarInUnitAsync(unit, "SELECT current_user")).Should().Be("learnstack_app");
        (await services.GetRequiredService<ISender>().Send(new CreateOrganizationCommand(
            OrganizationId.From(effects.Organization), "job-native-" + effects.Organization.ToString("N")[..12], "Native job commit"),
            cancellationToken)).IsSuccess.Should().BeTrue();
        effects.Child = await Fixture.EnqueueAsync(services, dueAt: DateTimeOffset.UtcNow.AddHours(1));
        await services.GetRequiredService<IOutbox>().EnqueueAsync(new TenantCreatedIntegrationEventV1
        {
            EventId = effects.Outgoing,
            OccurredAt = DateTimeOffset.UtcNow,
            TenantId = SchemaFixture.TenantA,
        }, cancellationToken);
    }

    private static async Task AssertNativeCommitEffectsAsync(DisposableSchemaDatabase database,
        NativeCommitEffects effects, bool committed)
    {
        await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var transaction = await connection.BeginTransactionAsync();
        await EducationSchemaSeed.AnnounceAsync(connection, transaction, SchemaFixture.TenantA, null);
        await using var count = new NpgsqlCommand("""
            SELECT (SELECT count(*) FROM organizations WHERE id=@organization),
                (SELECT count(*) FROM background_jobs WHERE id=@child),
                (SELECT count(*) FROM outbox_messages WHERE id=@outgoing),
                (SELECT count(*) FROM audit_log WHERE entity_id=@subject
                    AND operation='tenancy.organization.create' AND outcome='success')
            """, connection, transaction);
        count.Parameters.AddWithValue("organization", effects.Organization);
        count.Parameters.AddWithValue("child", effects.Child!.Value.Value);
        count.Parameters.AddWithValue("outgoing", effects.Outgoing);
        count.Parameters.AddWithValue("subject", effects.Organization.ToString());
        await using var reader = await count.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        for (var index = 0; index < 4; index++) reader.GetInt64(index).Should().Be(committed ? 1 : 0);
    }

    private static BackgroundJobExecutionControl NativeCommitExecution(BackgroundJobRuntime runtime, BackgroundJobId job)
    {
        var control = (BackgroundJobControl)typeof(BackgroundJobRuntime).GetField("_jobs", PrivateInstance)!.GetValue(runtime)!;
        var sync = typeof(BackgroundJobControl).GetField("_sync", PrivateInstance)!.GetValue(control)!;
        lock (sync)
            return ((Dictionary<Guid, BackgroundJobExecutionControl>)typeof(BackgroundJobControl)
                .GetField("_executions", PrivateInstance)!.GetValue(control)!)[job.Value];
    }

    private static Task<JobBusinessExecutionResult> NativeCommitBusinessTask(BackgroundJobExecutionControl execution) =>
        (Task<JobBusinessExecutionResult>)typeof(BackgroundJobExecutionControl).GetField("_business", PrivateInstance)!.GetValue(execution)!;

    private static async Task LockNativeJobAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        BackgroundJobId job, CancellationToken cancellationToken)
    {
        // The staged child's composite parent FK holds KEY SHARE. NO KEY
        // UPDATE can coexist with that genuine business lock while still
        // blocking the completion/probe's stronger FOR UPDATE acquisition.
        await using var command = new NpgsqlCommand("SELECT id FROM background_jobs WHERE id=@id FOR NO KEY UPDATE", connection, transaction);
        command.Parameters.AddWithValue("id", job.Value);
        (await command.ExecuteScalarAsync(cancellationToken)).Should().Be(job.Value);
    }

    private static async Task WaitForNativeJobLockAsync(DisposableSchemaDatabase database, int blocker,
        string role, string applicationName, CancellationToken cancellationToken)
    {
        // The independent observer authenticates as the waiter's role so
        // pg_stat_activity exposes that backend's actual query and lock wait.
        await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(
            role == "learnstack_app" ? database.AppConnectionString : database.OutboxConnectionString, cancellationToken);
        await EventuallyQualificationAsync(async () =>
        {
            await using var command = new NpgsqlCommand("""
                SELECT count(*) FROM pg_stat_activity WHERE datname=current_database()
                    AND usename=@role AND application_name=@application AND wait_event_type='Lock'
                    AND @blocker=ANY(pg_blocking_pids(pid))
                    AND query LIKE '%FROM background_jobs%' AND query LIKE '%FOR UPDATE%'
                """, connection);
            command.Parameters.AddWithValue("role", role);
            command.Parameters.AddWithValue("application", applicationName);
            command.Parameters.AddWithValue("blocker", blocker);
            return (long)(await command.ExecuteScalarAsync(cancellationToken))! == 1;
        }, cancellationToken);
    }

    private static async Task AssertNoNativeProviderRetryAsync(PrivilegedDataSource source, BackgroundJobId job,
        CancellationToken cancellationToken)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT count(*), (SELECT count(*) FROM public.job j JOIN public.jobparameter p ON p.jobid=j.id
                WHERE j.arguments->>0=@job AND p.name='RetryCount' AND p.value <> '0')
            FROM public.job WHERE arguments->>0=@job
            """, connection);
        command.Parameters.AddWithValue("job", JsonSerializer.Serialize(job.Value));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        (await reader.ReadAsync(cancellationToken)).Should().BeTrue();
        reader.GetInt64(0).Should().BeGreaterThan(0, "an actual opaque native wakeup must exist");
        reader.GetInt64(1).Should().Be(0);
    }

    private static async Task AssertNativeRetryDispositionAsync(DisposableSchemaDatabase database, BackgroundJobId job,
        CancellationToken cancellationToken)
    {
        await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.AppConnectionString, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await EducationSchemaSeed.AnnounceAsync(connection, transaction, SchemaFixture.TenantA, null);
        await using var command = new NpgsqlCommand("""
            SELECT phase, normal_attempts, wake_generation, last_error, ownership_token IS NULL,
                processed_at IS NULL, dead_lettered_at IS NULL, terminal_reason IS NULL
            FROM background_jobs WHERE id=@id
            """, connection, transaction);
        command.Parameters.AddWithValue("id", job.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        (await reader.ReadAsync(cancellationToken)).Should().BeTrue();
        reader.GetString(0).Should().Be("pending");
        reader.GetInt32(1).Should().Be(1);
        reader.GetInt64(2).Should().Be(2);
        reader.GetString(3).Should().Be("unexpected_fault");
        for (var index = 4; index < 8; index++) reader.GetBoolean(index).Should().BeTrue();
    }
}
