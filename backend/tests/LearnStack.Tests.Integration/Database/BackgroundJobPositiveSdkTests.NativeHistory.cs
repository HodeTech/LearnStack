using System.Globalization;
using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.PostgreSql;
using Hangfire.States;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobPositiveSdkTests
{
    [Theory]
    [InlineData("prepare", 2, BackgroundJobCheckpointKind.Prepare)]
    [InlineData("arm", 4, BackgroundJobCheckpointKind.Arm)]
    [InlineData("outcome", 5, BackgroundJobCheckpointKind.Outcome)]
    [InlineData("finalize", 9, BackgroundJobCheckpointKind.Finalize)]
    public async Task Native_lost_checkpoint_ACK_uses_the_exact_issued_history_and_never_repeats_the_provider(
        string kind, int passedCommits, BackgroundJobCheckpointKind boundary)
        => await NativeCheckpointHistoryAsync(kind, passedCommits, boundary, holdHistoryLock: false);

    [Fact]
    public async Task Native_history_retries_an_actual_skipped_row_until_exact_confirmation()
        => await NativeCheckpointHistoryAsync("outcome", 5, BackgroundJobCheckpointKind.Outcome, holdHistoryLock: true);

    private async Task NativeCheckpointHistoryAsync(string kind, int passedCommits,
        BackgroundJobCheckpointKind boundary, bool holdHistoryLock)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        await using var provider = await BackgroundJobProviderJournalDatabase.CreateAsync(schema.Postgres);
        var prepared = OrganizationId.From(Guid.CreateVersion7());
        var finalized = OrganizationId.From(Guid.CreateVersion7());
        await InstallTransactionObserverAsync(database, prepared, finalized);
        BackgroundJobId job;
        await using (var submit = Fixture.Host(database, provider.Settings))
        {
            var submitted = await Fixture.SubmitAsync(submit.Services,
                Fixture.Parameters(Guid.CreateVersion7(), prepared, finalized));
            submitted.IsSuccess.Should().BeTrue();
            job = submitted.Value;
        }
        var bounds = new BackgroundJobRuntimeOptions { HandoffRepairGrace = TimeSpan.FromSeconds(15) };
        await using var proxy = new CommitResponseDroppingProxy(database.AppConnectionString,
            commitsToPass: passedCommits, holdCommitResponse: true, lifetime: TimeSpan.FromMinutes(2));
        await using var host = Fixture.Host(database, provider.Settings, configure: services =>
        {
            services.RemoveAll<BackgroundJobRuntimeOptions>();
            services.AddSingleton(bounds);
        }).WithWebHostBuilder(builder => builder.UseSetting("ConnectionStrings:Default", proxy.ConnectionString));
        await using var historyProxy = new CommitResponseDroppingProxy(database.OutboxConnectionString,
            commitsToPass: int.MaxValue, lifetime: TimeSpan.FromMinutes(2),
            observedCheckpointJob: holdHistoryLock ? job.Value : null);
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(
            holdHistoryLock ? historyProxy.ConnectionString : database.OutboxConnectionString));
        using var globals = new NativeSdkGlobalFilterSnapshot();
        var runtime = NativeSdkRuntime(host.Services, source, bounds);
        NpgsqlConnection? historyBlocker = null;
        NpgsqlTransaction? historyLock = null;
        try
        {
            using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await runtime.StartAsync(startup.Token);
            await proxy.CommitResponseHeld.WaitAsync(startup.Token);
            proxy.CompletedCommits.Should().Be(passedCommits + 1, "the actual protocol counter identifies the intended transaction");
            proxy.DroppedCommitResponses.Should().Be(1);
            var invocation = NativeSdkInvocation(runtime);
            var execution = (BackgroundJobExecutionControl)NativeSdkField(invocation, "_execution");
            var gate = (BackgroundJobCommitGate)NativeSdkField(execution.RunGate, "_current");
            gate.Kind.Should().Be(boundary);
            gate.CommitStarted.Should().BeTrue();
            gate.CommitAcknowledged.Should().BeFalse();
            gate.CommitConfirmed.Should().BeFalse();
            runtime.Snapshot.Control!.Executions.Should().Be(1);
            if (kind == "outcome")
            {
                // The new generation is already durable, but the old native
                // operation still owns cleanup. An early real wakeup must not
                // leave a fresh routing lease behind this retained identity.
                var duplicate = NativeSdkWakeup(runtime, job, await GenerationAsync(database, job));
                using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                while (await NativeSdkVendorStateAsync(source, duplicate) != "Succeeded") await Task.Delay(10, wait.Token);
                var held = (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job))!;
                held.Phase.Should().Be("finalize_pending");
                held.OwnershipKind.Should().BeNull("held native cleanup must block before routing SQL can acquire the successor");
                held.NormalAttempts.Should().Be(1);
            }
            if (holdHistoryLock)
            {
                historyBlocker = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.OutboxConnectionString);
                historyLock = await historyBlocker.BeginTransactionAsync();
                await using var locked = new NpgsqlCommand("SELECT id FROM background_jobs WHERE id=@id FOR UPDATE", historyBlocker, historyLock)
                { CommandTimeout = 5 };
                locked.Parameters.AddWithValue("id", job.Value);
                (await locked.ExecuteScalarAsync()).Should().Be(job.Value);
                historyProxy.ObserveNextEmptyCheckpointLockRead();
            }
            proxy.ReleaseHeldResponse();
            if (holdHistoryLock)
            {
                await historyProxy.EmptyCheckpointLockRead.WaitAsync(TimeSpan.FromSeconds(15));
                gate.CommitConfirmed.Should().BeFalse("the actual skipped row supplies no committed history");
                await historyLock!.RollbackAsync();
                await historyLock.DisposeAsync();
                historyLock = null;
            }
            using var completion = new CancellationTokenSource(TimeSpan.FromSeconds(50));
            while ((await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job))!.Phase != "completed"
                || runtime.Snapshot.Control!.Executions != 0)
                await Task.Delay(20, completion.Token);
            var history = (Task<OwnedBackgroundJobControl<BackgroundJobCheckpointHistoryObservation>>)NativeSdkField(execution,
                kind switch
                {
                    "prepare" => "_prepareHistory",
                    "arm" => "_armHistory",
                    "outcome" => "_outcomeHistory",
                    "finalize" => "_finalizeHistory",
                    _ => throw new InvalidOperationException(),
                });
            var observed = await history.WaitAsync(completion.Token);
            observed.CleanupConfirmed.Should().BeTrue();
            observed.Failed.Should().BeFalse();
            observed.Value.Should().NotBeNull("a clean skipped row must be retried within the original history wait budget");
            observed.Value!.Disposition.Should().Be(BackgroundJobCheckpointHistoryDisposition.Committed);
            observed.Value.Confirmation.Should().NotBeNull();
            gate.CommitConfirmed.Should().BeTrue();
            gate.CommitAcknowledged.Should().BeFalse("durable observation never rewrites a missing physical ACK");
            gate.KnownRollback.Should().BeFalse();
            if (holdHistoryLock) historyProxy.CheckpointLockReads.Should().BeGreaterThanOrEqualTo(2);
            proxy.DroppedCommitResponses.Should().Be(1);
            (await BackgroundJobRuntimeRows.CountOrganizationAsync(database.AppConnectionString, prepared.Value)).Should().Be(1);
            (await BackgroundJobRuntimeRows.CountOrganizationAsync(database.AppConnectionString, finalized.Value)).Should().Be(1);
            await using var app = await PostgresFixture.OpenAsync(database.AppConnectionString);
            await using var transaction = await app.BeginTransactionAsync();
            await SchemaQueries.AssertApplicationRoleAsync(app, transaction);
            await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null);
            await using var command = new NpgsqlCommand("""
                SELECT operation_id,normal_attempts,finalize_attempts FROM background_jobs WHERE id=@id
                """, (NpgsqlConnection)app, (NpgsqlTransaction)transaction) { CommandTimeout = 5 };
            command.Parameters.AddWithValue("id", job.Value);
            await using (var row = await command.ExecuteReaderAsync())
            {
                (await row.ReadAsync()).Should().BeTrue();
                row.GetInt32(1).Should().Be(1);
                row.GetInt32(2).Should().Be(1);
                (await provider.CountsAsync(row.GetGuid(0))).Should().Be((1L, 0L, 1L));
            }
            await AssertObservedTransactionsAsync(app, transaction, failure: false);
        }
        finally
        {
            if (historyLock is not null) await historyLock.DisposeAsync();
            if (historyBlocker is not null) await historyBlocker.DisposeAsync();
            proxy.ReleaseHeldResponse();
            runtime.RequestStop();
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            await runtime.StopAsync(cleanup.Token);
            await runtime.Completion.WaitAsync(cleanup.Token);
        }
    }

    private static async Task EnsureNativeSdkStartAsync(BackgroundJobRuntime runtime, PrivilegedDataSource source,
        BackgroundJobId job, Task started, CancellationToken cancellationToken, Func<Task>? afterObservation = null)
    {
        // These isolated execution/cleanup fixtures do not measure the bridge's
        // repair grace. Handoff ACK and native Succeeded alone do not prove the
        // preceding hint's private work has actually released its ownership.
        long? generation = null;
        string? previous = null;
        object? capturedGeneration = null;
        while (!started.IsCompleted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long current;
            bool eligible;
            string vendor;
            await using (var connection = await source.OpenConnectionAsync(cancellationToken))
            await using (var command = new NpgsqlCommand("""
                SELECT wake_generation, vendor_job_id,
                    ownership_token IS NULL AND processed_at IS NULL AND dead_lettered_at IS NULL
                    AND terminal_reason IS NULL AND original_due_at <= clock_timestamp()
                    AND ((phase IN ('pending','finalize_pending') AND available_after <= clock_timestamp())
                        OR (phase='reconciling' AND probe_after IS NOT NULL AND probe_after <= clock_timestamp()))
                FROM background_jobs WHERE id=@id AND vendor_job_id IS NOT NULL
                """, connection) { CommandTimeout = 5 })
            {
                command.Parameters.AddWithValue("id", job.Value);
                await using var row = await command.ExecuteReaderAsync(cancellationToken);
                if (!await row.ReadAsync(cancellationToken))
                {
                    await Task.Delay(25, cancellationToken);
                    continue;
                }
                current = row.GetInt64(0);
                vendor = row.GetString(1);
                eligible = row.GetBoolean(2);
            }
            generation ??= current;
            current.Should().Be(generation.Value, "fixture hints cannot reset the durable generation");
            previous ??= vendor;
            var state = await NativeSdkVendorStateAsync(source, previous, cancellationToken);
            state.Should().NotBe("Failed").And.NotBe("Deleted", "fixture startup never retries failed native work");
            var pool = NativeSdkExternalPool(runtime);
            HangfirePoolOwnerSnapshot snapshot;
            object? currentGeneration;
            lock (NativeSdkField(pool, "_sync"))
            {
                snapshot = pool.Snapshot;
                currentGeneration = pool.GetType().GetField("_generation",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(pool);
            }
            if (snapshot.State != HangfirePoolOwnerState.Running || snapshot.Work is null || currentGeneration is null)
            {
                await Task.Delay(25, cancellationToken);
                continue;
            }
            capturedGeneration ??= currentGeneration;
            ReferenceEquals(capturedGeneration, currentGeneration).Should().BeTrue();
            if (state == "Succeeded" && snapshot.State == HangfirePoolOwnerState.Running
                && snapshot.Work is { Running: 0, Retained: 0, Closed: false }
                && ((BackgroundJobControl)NativeSdkField(runtime, "_jobs")).CanRoute(job) && eligible)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (started.IsCompleted) return;
                _ = NativeSdkWakeup(runtime, job, current);
                if (afterObservation is not null) await afterObservation();
                return; // One fixture hint, only after the preceding native operation actually released its ownership.
            }
            if (afterObservation is not null)
            {
                await afterObservation();
                afterObservation = null;
            }
            await Task.Delay(25, cancellationToken);
        }
    }

    private static HangfirePoolOwner NativeSdkExternalPool(BackgroundJobRuntime runtime)
    {
        lock (NativeSdkField(runtime, "_sync"))
            return ((IEnumerable<HangfirePoolOwner>)NativeSdkField(runtime, "_pools"))
                .Single(pool => pool.Snapshot.Pool == "external");
    }

    private static string NativeSdkWakeup(BackgroundJobRuntime runtime, BackgroundJobId job, long generation)
    {
        var provider = (PostgreSqlStorage)NativeSdkField(NativeSdkField(runtime, "_bootstrap"), "_provider");
        var culture = CultureInfo.CurrentCulture;
        var uiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
            return new BackgroundJobClient(provider, HangfireRuntimeFilters.Create()) { RetryAttempts = 0 }
                .Create(new Job(typeof(HangfireJobWakeup), HangfireInvocationContract.JobMethod, [job, generation], "external"),
                    new EnqueuedState("external")) ?? throw new InvalidOperationException("Native SDK wakeup creation failed.");
        }
        finally { CultureInfo.CurrentCulture = culture; CultureInfo.CurrentUICulture = uiCulture; }
    }

    private static async Task<string?> NativeSdkVendorStateAsync(PrivilegedDataSource source, string vendor, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SELECT statename FROM public.job WHERE id=@id", connection) { CommandTimeout = 5 };
        command.Parameters.AddWithValue("id", long.Parse(vendor, CultureInfo.InvariantCulture));
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }
}
