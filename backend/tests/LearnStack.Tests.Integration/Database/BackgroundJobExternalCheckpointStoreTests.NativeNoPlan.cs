using System.Text.Json;
using FluentAssertions;
using Hangfire;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using MediatR;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobExternalCheckpointStoreTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Actual_native_NoPlan_owner_requests_terminal_cleanup_and_one_MUST_without_another_attempt(
        bool sixthRecovery, bool currentNoStart)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var bounds = new BackgroundJobRuntimeOptions
        {
            LeaseDuration = TimeSpan.FromSeconds(15),
            RenewalInterval = TimeSpan.FromSeconds(1),
            SqlTimeout = TimeSpan.FromSeconds(3),
            ProbeTimeout = TimeSpan.FromSeconds(3),
            ProgressTimeout = TimeSpan.FromSeconds(30),
            ControlPoll = TimeSpan.FromMilliseconds(50),
            HandoffRepairGrace = TimeSpan.FromSeconds(15),
        };
        bounds.Validate();
        var calls = new NativeNoPlanCalls();
        await using var host = NoPlanRefusalHost(database,
            admissionWindow: sixthRecovery ? TimeSpan.FromSeconds(2) : null).WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<BackgroundJobRuntimeOptions>();
                services.AddSingleton(bounds);
                services.AddSingleton(calls);
                services.AddTransient(typeof(IPipelineBehavior<,>), typeof(NativeNoPlanRequestObservation<,>));
                var definition = Fixture.Registry.Resolve(BackgroundJobRuntimeFixture.ExternalKey).Definition!;
                Fixture.Registry.Infrastructure.TryGetAdapter(definition.ProfileRegistration.ExternalPortType!, out var adapter)
                    .Should().BeTrue();
                var registered = adapter ?? throw new InvalidOperationException("The original adapter registration is missing.");
                services.RemoveAll(registered.PortType);
                services.AddTransient(registered.PortType, provider =>
                {
                    Interlocked.Increment(ref calls.ProviderResolutions);
                    // Keep the original frozen throwing adapter; this observes resolution only.
                    return ActivatorUtilities.CreateInstance(provider, registered.ImplementationType);
                });
            }));
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        BackgroundJobId job;
        if (sixthRecovery)
        {
            // Six real app admissions, with explicit store-fixture retry eligibility.
            // This does not claim six native backoffs; only the final disposition below is native.
            var setupGate = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
            var setup = new PostgresBackgroundJobControlStore(source, Fixture.Registry, bounds, new SystemGuidFactory(), setupGate);
            var normal = await NoPlanOrdinalAsync(database, host.Services, 6, setup, setupGate);
            job = normal.Envelope.JobId;
            await ExpireSelectionLeaseAsync(database, normal);
            normal.SourceOwner.CloseIssuance();
            normal.SourceOwner.CleanupConfirmed.Should().BeTrue();
            if (currentNoStart)
            {
                var acquired = await setup.AcquireExternalRecoveryAsync(job, await RecoveryGenerationAsync(database, job),
                    normal.Envelope.Queue, CancellationToken.None);
                acquired.ThrowIfFailed();
                acquired.CommitAcknowledged.Should().BeTrue();
                acquired.CleanupConfirmed.Should().BeTrue();
                var binding = BindRecovery(acquired, setupGate);
                var executor = host.Services.GetRequiredService<JobExecutor>();
                var read = await executor.ReadExternalStateAsync(binding, CancellationToken.None);
                read.CleanupFailed.Should().BeFalse();
                var handle = read.Handle ?? throw new InvalidOperationException("The actual setup read was not acknowledged.");
                handle.Snapshot.PlanJson.Should().BeNull();
                var written = await executor.RecordExternalNoStartAsync(binding, handle,
                    binding.Run.OpenBoundary(BackgroundJobCheckpointKind.NoStart), CancellationToken.None);
                written.CommitAcknowledged.Should().BeTrue();
                written.CleanupFailed.Should().BeFalse();
                binding.Run.Close();
                binding.SourceOwner.CloseIssuance();
                binding.SourceOwner.CleanupConfirmed.Should().BeTrue();
                // Only pre-runtime fixture ownership is expired, after actual app/source cleanup.
                await ExpireSelectionLeaseAsync(database, normal);
            }
        }
        else job = await ExternalRetryJobAsync(database);
        var before = await ReadNativeNoPlanFactsAsync(database, job);
        before.Reason.Should().BeNull();
        before.DeadLetteredAt.Should().BeNull();
        before.Ordinals.Should().Equal(Enumerable.Range(1, sixthRecovery ? 6 : 0));
        before.NoStarts.Should().Be(sixthRecovery ? currentNoStart ? 6 : 5 : 0);
        before.Reservations.Should().Be(sixthRecovery ? 6 : 0);
        before.Released.Should().Be(before.NoStarts);
        before.NoPlanOrEffect.Should().BeTrue();
        before.CleanLedger.Should().BeTrue();
        (await ReadNativeNoPlanAuditAsync(database, job)).Should().BeNull();
        var ordinal = sixthRecovery ? 6 : 1;
        var reason = sixthRecovery ? "retry_exhausted" : "permanent_failure";
        using var globals = new NativeNoPlanGlobalFilterSnapshot();
        var gate = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        var ids = new SystemGuidFactory();
        var runtime = new BackgroundJobRuntime(source, Fixture.Registry, BackgroundJobRecurrenceRegistry.CreateEmpty(),
            host.Services.GetRequiredService<JobExecutor>(), new(source, Fixture.Registry, bounds, ids, gate), gate,
            bounds, ids, TimeProvider.System, new(false, 100), new HangfireBoundedLogProvider(),
            host.Services.GetRequiredService<ApplicationDataSourceLifetime>());
        runtime.BindMessagingLifetime(Task.CompletedTask, Task.CompletedTask);
        DateTimeOffset? actualCleanupLease = null;
        try
        {
            using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await runtime.StartAsync(startup.Token);
            if (!currentNoStart)
            {
                NativeNoPlanFacts? cleaned = null;
                await WaitTerminalOwnerConditionAsync(async () =>
                {
                    cleaned = await ReadNativeNoPlanFactsAsync(database, job);
                    return cleaned.Reason == reason && cleaned.NoStarts == ordinal && cleaned.Released == ordinal;
                }, TimeSpan.FromSeconds(45));
                var noStart = cleaned ?? throw new InvalidOperationException("Native no-start was not observed.");
                noStart.DeadLetteredAt.Should().BeNull();
                noStart.OwnershipUntil.Should().NotBeNull();
                noStart.DbNow.Should().BeBefore(noStart.OwnershipUntil!.Value,
                    "the native app NoStart leaves its real scheduling lease; no fixture expiry follows native startup");
                actualCleanupLease = noStart.OwnershipUntil;
                (await ReadNativeNoPlanAuditAsync(database, job)).Should().BeNull();
            }
            await WaitTerminalOwnerConditionAsync(async () =>
                (await ReadNativeNoPlanFactsAsync(database, job)).DeadLetteredAt is not null, TimeSpan.FromSeconds(60));
            await WaitTerminalOwnerConditionAsync(() => Task.FromResult(runtime.Snapshot.Control is
            { Executions: 0, PendingOutcomes: 0 } && runtime.Snapshot.RetainedInvocations == 0), TimeSpan.FromSeconds(15));
        }
        finally
        {
            runtime.RequestStop();
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            await runtime.StopAsync(cleanup.Token);
            await runtime.Completion.WaitAsync(cleanup.Token);
        }
        var stopped = runtime.Snapshot;
        var control = stopped.Control ?? throw new InvalidOperationException("The actual native control is missing.");
        stopped.CleanupConfirmed.Should().BeTrue();
        control.CleanupConfirmed.Should().BeTrue();
        control.Executions.Should().Be(0);
        control.PendingOutcomes.Should().Be(0);
        stopped.RetainedInvocations.Should().Be(0);
        var after = await ReadNativeNoPlanFactsAsync(database, job);
        after.Phase.Should().Be("terminal_pending");
        after.Reason.Should().Be(reason);
        after.NormalAttempts.Should().Be(ordinal);
        after.FinalizeAttempts.Should().Be(0);
        after.Ordinals.Should().Equal(Enumerable.Range(1, ordinal));
        after.NoStarts.Should().Be(ordinal);
        after.Reservations.Should().Be(ordinal);
        after.Released.Should().Be(ordinal);
        after.NoPlanOrEffect.Should().BeTrue();
        after.CleanLedger.Should().BeTrue();
        after.DeadLetteredAt.Should().NotBeNull();
        if (actualCleanupLease is { } lease) after.DeadLetteredAt!.Value.Should().BeOnOrAfter(lease);
        if (sixthRecovery) after.Attempt.Should().Be(before.Attempt);
        var metadata = await ReadNativeNoPlanAuditAsync(database, job);
        metadata.Should().NotBeNull();
        using var audit = JsonDocument.Parse(metadata!);
        audit.RootElement.GetProperty("job_id").GetGuid().Should().Be(job.Value);
        audit.RootElement.GetProperty("attempt_id").GetGuid().Should().Be(after.Attempt!.Value);
        audit.RootElement.GetProperty("normal_attempts").GetInt32().Should().Be(ordinal);
        audit.RootElement.GetProperty("reason").GetString().Should().Be(reason);
        audit.RootElement.GetProperty("acquisition_token").GetGuid().Should().NotBe(Guid.Empty);
        audit.RootElement.TryGetProperty("failure_policy", out _).Should().BeFalse();
        calls.Prepare.Should().Be(sixthRecovery ? 0 : 1);
        calls.Finalize.Should().Be(0);
        calls.ProviderResolutions.Should().Be(0);
        calls.TerminalNoStart.Should().Be(currentNoStart ? 0 : 1);
    }

    private static async Task<NativeNoPlanFacts> ReadNativeNoPlanFactsAsync(DisposableSchemaDatabase database, BackgroundJobId job)
    {
        await using var app = await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await SchemaQueries.AssertApplicationRoleAsync(app, transaction);
        await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null);
        await using var command = new NpgsqlCommand("""
            SELECT j.phase,j.terminal_reason,j.normal_attempts,j.finalize_attempts,j.current_attempt_id,
                j.ownership_until,j.dead_lettered_at,clock_timestamp(),
                j.plan IS NULL AND j.plan_fingerprint IS NULL AND j.prepared_at IS NULL AND j.operation_id IS NULL
                    AND j.final_outcome_attempt_id IS NULL AND j.finalization_status IS NULL AND j.finalization_reason IS NULL
                    AND NOT j.business_finalization_incomplete AND j.processed_at IS NULL,
                ARRAY(SELECT a.ordinal FROM background_job_attempts a WHERE a.job_id=j.id ORDER BY a.ordinal),
                (SELECT count(*) FROM background_job_attempts a WHERE a.job_id=j.id AND a.no_start_at IS NOT NULL),
                (SELECT count(*) FROM background_job_reservations r WHERE r.job_id=j.id),
                (SELECT count(*) FROM background_job_reservations r WHERE r.job_id=j.id
                    AND r.released_at IS NOT NULL AND r.release_reason='never_started'),
                NOT EXISTS (SELECT 1 FROM background_job_attempts a WHERE a.job_id=j.id AND
                    (a.stage<>'normal' OR a.plan_fingerprint IS NOT NULL OR a.operation_id IS NOT NULL
                    OR a.execute_intent_at IS NOT NULL OR a.outcome IS NOT NULL OR a.outcome_kind IS NOT NULL
                    OR a.outcome_at IS NOT NULL OR a.failure_policy IS NOT NULL OR a.quiescence_id IS NOT NULL
                    OR a.quiescence_kind IS NOT NULL OR a.quiescent_at IS NOT NULL
                    OR a.final_outcome_attempt_id IS NOT NULL OR a.transactional_cleanup_at IS NOT NULL))
            FROM background_jobs j WHERE j.id=@id
            """, (NpgsqlConnection)app, (NpgsqlTransaction)transaction) { CommandTimeout = 5 };
        command.Parameters.AddWithValue("id", job.Value);
        await using var row = await command.ExecuteReaderAsync();
        (await row.ReadAsync()).Should().BeTrue();
        return new(row.GetString(0), row.IsDBNull(1) ? null : row.GetString(1), row.GetInt32(2), row.GetInt32(3),
            row.IsDBNull(4) ? null : row.GetGuid(4), row.IsDBNull(5) ? null : row.GetFieldValue<DateTimeOffset>(5),
            row.IsDBNull(6) ? null : row.GetFieldValue<DateTimeOffset>(6), row.GetFieldValue<DateTimeOffset>(7),
            row.GetBoolean(8), row.GetFieldValue<int[]>(9), row.GetInt64(10), row.GetInt64(11), row.GetInt64(12), row.GetBoolean(13));
    }

    private static async Task<string?> ReadNativeNoPlanAuditAsync(DisposableSchemaDatabase database, BackgroundJobId job)
    {
        await using var app = await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await SchemaQueries.AssertApplicationRoleAsync(app, transaction);
        await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null);
        await using var command = new NpgsqlCommand("""
            SELECT metadata::text FROM audit_log WHERE metadata->>'job_id'=@id AND operation='platform.job.dead_letter'
            """, (NpgsqlConnection)app, (NpgsqlTransaction)transaction) { CommandTimeout = 5 };
        command.Parameters.AddWithValue("id", job.Value.ToString("D"));
        await using var row = await command.ExecuteReaderAsync();
        if (!await row.ReadAsync()) return null;
        var metadata = row.GetString(0);
        (await row.ReadAsync()).Should().BeFalse("a native terminal disposition commits exactly one real MUST audit");
        return metadata;
    }

    private sealed record NativeNoPlanFacts(string Phase, string? Reason, int NormalAttempts, int FinalizeAttempts,
        Guid? Attempt, DateTimeOffset? OwnershipUntil, DateTimeOffset? DeadLetteredAt, DateTimeOffset DbNow,
        bool NoPlanOrEffect, int[] Ordinals, long NoStarts, long Reservations, long Released, bool CleanLedger);
    private sealed class NativeNoPlanCalls { internal int Prepare, Finalize, ProviderResolutions, TerminalNoStart; }
    private sealed class NativeNoPlanRequestObservation<TRequest, TResponse>(NativeNoPlanCalls calls)
        : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
    {
        public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
        {
            if (typeof(TRequest) == Fixture.RequestType("Prepare")) Interlocked.Increment(ref calls.Prepare);
            if (typeof(TRequest) == Fixture.RequestType("Finalize")) Interlocked.Increment(ref calls.Finalize);
            if (request is RecordBackgroundJobOutcomeCommand { IsTerminalNoStart: true })
                Interlocked.Increment(ref calls.TerminalNoStart);
            return await next();
        }
    }
    private sealed class NativeNoPlanGlobalFilterSnapshot : IDisposable
    {
        private readonly Hangfire.Common.JobFilter[] _filters = GlobalJobFilters.Filters.ToArray();
        public void Dispose()
        {
            GlobalJobFilters.Filters.Clear();
            foreach (var filter in _filters) GlobalJobFilters.Filters.Add(filter.Instance, filter.Order);
        }
    }
}
