using FluentAssertions;
using LearnStack.Infrastructure.Jobs;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobRuntimeFacadeTests
{
    [Fact]
    public async Task Native_clean_control_lock_timeout_retains_the_permanent_refusal_until_terminal_audit()
    {
        await using var database = await DatabaseAsync();
        var bounds = NativeCommitFaultBounds() with
        {
            SqlTimeout = TimeSpan.FromSeconds(1),
            ControlPoll = TimeSpan.FromSeconds(1),
        };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishHandler = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var starts = 0;
        var probe = new BackgroundJobFixtureProbe
        {
            Body = async (_, services, token) =>
            {
                var unit = services.GetRequiredService<IUnitOfWork>();
                (await BackgroundJobRuntimeRows.ScalarInUnitAsync(unit, "SELECT current_user")).Should().Be("learnstack_app");
                Interlocked.Increment(ref starts);
                entered.TrySetResult();
                await finishHandler.Task.WaitAsync(token);
                return Result<None>.Fail(new Error(new LocalizedMessage("lockey_business_rule_violation")));
            },
        };
        await using var host = Fixture.Host(database, probe, configure: services =>
        {
            services.RemoveAll<BackgroundJobRuntimeOptions>();
            services.AddSingleton(bounds);
        });
        var physical = new BackgroundJobPhysicalPoolSettings(true, 52);
        var controlName = "native_control_failure_" + Guid.NewGuid().ToString("N");
        await using var source = Source(database, physical, controlName);
        using var globals = new GlobalFilterSnapshot();
        using var deadline = new CancellationTokenSource(TestWait);
        var runtime = Runtime(host.Services, source, bounds, physical);
        await using var locker = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        NpgsqlTransaction? locked = null;
        try
        {
            await runtime.StartAsync(deadline.Token);
            // Complete the initial renewal tick before submission. The next
            // 60-second tick cannot conceal this bounded control failure.
            await EventuallyQualificationAsync(() => Task.FromResult(runtime.Snapshot.IsHealthy), deadline.Token);
            var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString);
            await EnsureQualificationStartAsync(runtime, source, job, entered.Task, deadline.Token);
            await entered.Task.WaitAsync(deadline.Token);
            var execution = NativeCommitExecution(runtime, job);
            var identity = execution.Identity!;
            identity.Should().NotBeNull();
            var admitted = (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job))!;
            admitted.Phase.Should().Be("executing");
            admitted.NormalAttempts.Should().Be(1);
            locked = await locker.BeginTransactionAsync(deadline.Token);
            await LockNativeJobAsync(locker, locked, job, deadline.Token);
            finishHandler.TrySetResult();
            await WaitForNativeJobLockAsync(database, locker.ProcessID, "learnstack_outbox_admin", controlName, deadline.Token);
            var blockedProgress = NativeControlFailureObservation(runtime).Progress;
            // Confirm the SQL operation still waits after the progress sample.
            // A later progress value therefore proves its iteration returned.
            await WaitForNativeJobLockAsync(database, locker.ProcessID, "learnstack_outbox_admin", controlName, deadline.Token);

            var business = await NativeCommitBusinessTask(execution).WaitAsync(deadline.Token);
            business.KnownRollback.Should().BeTrue();
            business.CleanupFailed.Should().BeFalse();
            business.CommitStarted.Should().BeFalse();
            business.DurableOutcomeUncertain.Should().BeFalse();
            business.Decision.Should().Be(new BackgroundJobFailureDecision(BackgroundJobFailureDisposition.Terminal,
                "permanent_failure", BackgroundJobTerminalReason.PermanentFailure));

            BackgroundJobControlHealth? failed = null;
            await EventuallyQualificationAsync(() =>
            {
                var observation = NativeControlFailureObservation(runtime);
                if (!observation.Health.RecoveryHealthy && observation.Progress != blockedProgress)
                    failed = observation.Health;
                return Task.FromResult(failed is not null);
            }, deadline.Token);
            failed!.RecoveryHealthy.Should().BeFalse("the observed real SQL wait must fail before the lock is released");
            failed.PendingOutcomes.Should().Be(1, "a completed failed control iteration must retain its original request");
            failed.Executions.Should().Be(1, "the original execution remains excluded until its decision is durable");
            NativeCommitExecution(runtime, job).Should().BeSameAs(execution);
            execution.Identity.Should().BeSameAs(identity);
            execution.Gate.IsClosed.Should().BeTrue();
            probe.Constructions.Should().Equal("TenantRoot");
            Volatile.Read(ref starts).Should().Be(1);
            (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job)).Should().Be(1);
            var retained = (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job))!;
            retained.Phase.Should().Be("executing");
            retained.CurrentAttempt.Should().Be(admitted.CurrentAttempt);
            retained.OwnershipToken.Should().Be(admitted.OwnershipToken);

            // Expiry does not discard the live process's known decision. Its
            // retained identity excludes ordinary recovery, and the retry may
            // acquire a fresh recovery token without another normal admission.
            await using (var expiry = new NpgsqlCommand("""
                UPDATE background_jobs SET ownership_until=clock_timestamp()-interval '1 microsecond'
                WHERE id=@id AND ownership_token=@token
                RETURNING ownership_until < clock_timestamp()
                """, locker, locked))
            {
                expiry.Parameters.AddWithValue("id", job.Value);
                expiry.Parameters.AddWithValue("token", admitted.OwnershipToken!.Value);
                (await expiry.ExecuteScalarAsync(deadline.Token)).Should().Be(true);
            }
            await locked.CommitAsync(deadline.Token);
            await locked.DisposeAsync();
            locked = null;
            await EventuallyQualificationAsync(async () => runtime.Snapshot.Control is { Executions: 0, PendingOutcomes: 0 }
                && await NativePermanentControlSettledAsync(database, job, deadline.Token), deadline.Token);
            await AssertNativePermanentControlAuditAsync(database, job, admitted.CurrentAttempt!.Value, deadline.Token);

            // A later real wakeup must observe the terminal row without entering
            // another business constructor or consuming the normal budget.
            var duplicate = CreateQualificationWakeup(ActualProvider(runtime),
                Fixture.Registry.Infrastructure.Pools["transactional"], job);
            await EventuallyQualificationAsync(async () => await StateAsync(source, duplicate) == "Succeeded", deadline.Token);
            probe.Constructions.Should().Equal("TenantRoot");
            Volatile.Read(ref starts).Should().Be(1);
            (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job)).Should().Be(1);
            runtime.Snapshot.Control!.Retries.Should().Be(0);
            await AssertNativePermanentControlAuditAsync(database, job, admitted.CurrentAttempt!.Value, deadline.Token);
            await AssertNoNativeProviderRetryAsync(source, job, deadline.Token);
            await AssertGuardedPoolAsync(source, physical);
        }
        finally
        {
            finishHandler.TrySetResult();
            try
            {
                if (locked is not null)
                {
                    try { await locked.RollbackAsync(); }
                    finally { await locked.DisposeAsync(); }
                }
            }
            finally
            {
                using var cleanup = new CancellationTokenSource(TestWait);
                await runtime.StopAsync(cleanup.Token);
                await runtime.Completion.WaitAsync(cleanup.Token);
                await DisposePinnedSourceAsync(source);
            }
        }
    }

    private static (long? Progress, BackgroundJobControlHealth Health) NativeControlFailureObservation(BackgroundJobRuntime runtime)
    {
        var control = (BackgroundJobControl)typeof(BackgroundJobRuntime).GetField("_jobs", PrivateInstance)!.GetValue(runtime)!;
        var sync = typeof(BackgroundJobControl).GetField("_sync", PrivateInstance)!.GetValue(control)!;
        lock (sync)
            return ((long?)typeof(BackgroundJobControl).GetField("_controlProgress", PrivateInstance)!.GetValue(control), control.Snapshot);
    }

    private static async Task<bool> NativePermanentControlSettledAsync(DisposableSchemaDatabase database,
        BackgroundJobId job, CancellationToken cancellationToken)
    {
        await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.AppConnectionString, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await EducationSchemaSeed.AnnounceAsync(connection, transaction, SchemaFixture.TenantA, null);
        await using var command = new NpgsqlCommand("""
            SELECT phase='terminal_pending' AND terminal_reason='permanent_failure'
                AND dead_lettered_at IS NOT NULL AND processed_at IS NULL AND ownership_token IS NULL
                AND normal_attempts=1
            FROM background_jobs WHERE id=@id
            """, connection, transaction);
        command.Parameters.AddWithValue("id", job.Value);
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    private static async Task AssertNativePermanentControlAuditAsync(DisposableSchemaDatabase database,
        BackgroundJobId job, Guid attempt, CancellationToken cancellationToken)
    {
        await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.AppConnectionString, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await EducationSchemaSeed.AnnounceAsync(connection, transaction, SchemaFixture.TenantA, null);
        await using var command = new NpgsqlCommand("""
            SELECT operation_class, actor_user_id, metadata->>'reason', metadata->>'attempt_id',
                (metadata->>'normal_attempts')::integer
            FROM audit_log WHERE operation='platform.job.dead_letter' AND metadata->>'job_id'=@job
            """, connection, transaction);
        command.Parameters.AddWithValue("job", job.Value.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        (await reader.ReadAsync(cancellationToken)).Should().BeTrue();
        reader.GetString(0).Should().Be("Must");
        reader.GetGuid(1).Should().Be(UserId.SystemActor.Value);
        reader.GetString(2).Should().Be("permanent_failure");
        reader.GetString(3).Should().Be(attempt.ToString("D"));
        reader.GetInt32(4).Should().Be(1);
        (await reader.ReadAsync(cancellationToken)).Should().BeFalse("the terminal MUST audit commits exactly once");
    }
}
