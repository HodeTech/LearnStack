using System.Collections.Concurrent;
using System.Diagnostics;
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
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobRuntimeFacadeTests
{
    [Fact]
    public async Task Terminal_audit_backlog_remains_faulted_beyond_two_leases_while_native_business_commits()
    {
        await using var database = await DatabaseAsync();
        var bounds = BusinessBounds();
        var failed = new List<Guid>();
        for (var index = 0; index < 100; index++)
            failed.Add((await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString)).Value);
        await using (var owner = await PostgresFixture.OpenAsync(database.MigrationConnectionString))
        {
            await using (var setup = await owner.BeginTransactionAsync())
            {
                await EducationSchemaSeed.AnnounceAsync(owner, setup, SchemaFixture.TenantA, null);
                await SchemaQueries.ExecuteAsync(owner, setup, """
                    UPDATE background_jobs SET phase='terminal_pending', terminal_reason='invalid_envelope'
                    WHERE id=ANY(@ids)
                    """, ("ids", failed.ToArray()));
                await setup.CommitAsync();
            }
            // Only the terminalizer loses audit permission. Ordinary business
            // still runs as learnstack_app with its real MUST writer available.
            await SchemaQueries.ExecuteAsync(owner, null, "REVOKE INSERT ON audit_log FROM learnstack_outbox_admin");
        }
        var bodyEntries = new ConcurrentDictionary<Guid, int>();
        var controlledRefusals = new ConcurrentDictionary<Guid, byte>();
        var probe = new BackgroundJobFixtureProbe
        {
            Body = async (request, services, token) =>
            {
                request.Should().Be("TenantRoot");
                var context = services.GetRequiredService<ITenantContext>().Should().BeOfType<JobTenantContext>().Subject;
                var job = context.JobId ?? throw new InvalidOperationException("The actual business job identity is missing.");
                var organization = job.Value;
                var entries = bodyEntries.AddOrUpdate(job.Value, 1, (_, previous) => previous + 1);
                if (entries > 1)
                    (await BackgroundJobRuntimeRows.CountOrganizationAsync(database.AppConnectionString, organization)).Should().Be(0,
                        "a new attempt must not see its predecessor's rolled-back business write");
                var written = await services.GetRequiredService<ISender>().Send(Fixture.OrganizationRequest(
                    OrganizationId.From(organization), "job-fair-" + organization.ToString("N")[..12], "Job fairness"), token);
                written.IsSuccess.Should().BeTrue();
                // The real nested write succeeds before this one controlled refusal.
                // Handler entries include rolled-back attempts; the logical job's
                // stable organization and successful MUST audit witness its effect.
                if (controlledRefusals.TryAdd(job.Value, 0))
                    return Result<None>.Fail(new Error(LocalizedMessage.Of("lockey_dependency_unavailable")));
                return Result.Ok(None.Value);
            },
        };
        await using var host = Fixture.Host(database, probe, configure: services =>
        {
            services.RemoveAll<BackgroundJobRuntimeOptions>();
            services.AddSingleton(bounds);
        });
        var physical = new BackgroundJobPhysicalPoolSettings(true, 52);
        await using var source = Source(database, physical);
        using var globals = new GlobalFilterSnapshot();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(100));
        var runtime = Runtime(host.Services, source, bounds, physical);
        try
        {
            (await TerminalBacklogAsync(database)).Total.Should().Be(100, "the real announced setup must create a nonempty backlog");
            await runtime.StartAsync(deadline.Token);
            await EventuallyQualificationAsync(async () => (await TerminalBacklogAsync(database)).BackedOff > 0, deadline.Token);
            var firstFailure = await TerminalBacklogAsync(database);
            var elapsed = Stopwatch.StartNew();
            var first = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString);
            await EnsureQualificationCompletionAsync(runtime, source, first, deadline.Token);

            // A one-row or sub-lease happy case cannot expose priority starvation.
            // Keep the real permission fault and all 100 records for two full
            // leases, then demand another actual native business completion.
            await Task.Delay(bounds.LeaseDuration + bounds.LeaseDuration + TimeSpan.FromSeconds(1), deadline.Token);
            var second = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString);
            await EnsureQualificationCompletionAsync(runtime, source, second, deadline.Token);
            elapsed.Elapsed.Should().BeGreaterThan(bounds.LeaseDuration + bounds.LeaseDuration);
            var after = await TerminalBacklogAsync(database);
            after.Total.Should().Be(100);
            after.Dead.Should().Be(0, "failed MUST audit never commits terminal state alone");
            after.Attempts.Should().Be(0, "terminal requests cannot enter business execution");
            after.LatestBackoff.Should().BeAfter(firstFailure.LatestBackoff + bounds.LeaseDuration,
                "actual terminal retries and their audit failure continued during healthy work");
            after.AuditPermitted.Should().BeFalse();
            bodyEntries.Keys.Should().BeEquivalentTo([first.Value, second.Value]);
            controlledRefusals.Keys.Should().BeEquivalentTo([first.Value, second.Value]);
            foreach (var job in new[] { first, second })
            {
                (await BackgroundJobRuntimeRows.CountOrganizationAsync(database.AppConnectionString, job.Value)).Should().Be(1);
                (await SuccessfulBacklogBusinessAuditsAsync(database, job)).Should().Be(1);
                var completed = (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job))!;
                completed.Phase.Should().Be("completed");
                completed.NormalAttempts.Should().BeInRange(2, BackgroundJobRuntimeOptions.MaxNormalAttempts);
                (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job)).Should().Be(completed.NormalAttempts,
                    "the durable attempt ledger and completed job summary must agree");
                bodyEntries[job.Value].Should().BeInRange(2, completed.NormalAttempts,
                    "each job retries its controlled rollback, and every body entry needs a committed admission");
            }
            await AssertGuardedPoolAsync(source, physical);
        }
        finally
        {
            runtime.RequestStop();
            await using (var owner = await PostgresFixture.OpenAsync(database.MigrationConnectionString))
                await SchemaQueries.ExecuteAsync(owner, null, "GRANT INSERT ON audit_log TO learnstack_outbox_admin");
            using var cleanup = new CancellationTokenSource(TestWait);
            await runtime.StopAsync(cleanup.Token);
            await runtime.Completion.WaitAsync(cleanup.Token);
            await DisposePinnedSourceAsync(source);
        }
    }

    private static async Task<long> SuccessfulBacklogBusinessAuditsAsync(DisposableSchemaDatabase database, BackgroundJobId job)
    {
        await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var transaction = await connection.BeginTransactionAsync();
        await EducationSchemaSeed.AnnounceAsync(connection, transaction, SchemaFixture.TenantA, null);
        await using var command = new NpgsqlCommand("""
            SELECT count(*) FROM audit_log
            WHERE entity_id=@id AND operation='jobfixture.organization.create' AND operation_class='Must'
                AND outcome='success' AND metadata->>'job_id'=@id
            """, connection, transaction);
        command.Parameters.AddWithValue("id", job.Value.ToString());
        return (long)(await command.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task Admitted_native_business_renews_across_local_drain_and_commits_after_two_initial_leases()
    {
        await using var database = await DatabaseAsync();
        var bounds = BusinessBounds() with { TransactionTimeout = TimeSpan.FromSeconds(60) };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var organization = Guid.CreateVersion7();
        var probe = new BackgroundJobFixtureProbe
        {
            Body = async (_, services, token) =>
            {
                var written = await services.GetRequiredService<ISender>().Send(Fixture.OrganizationRequest(
                    OrganizationId.From(organization), "job-renew-" + organization.ToString("N")[..12], "Job renewal"), token);
                written.IsSuccess.Should().BeTrue();
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
                return Result.Ok(None.Value);
            },
        };
        await using var host = Fixture.Host(database, probe, configure: services =>
        {
            services.RemoveAll<BackgroundJobRuntimeOptions>();
            services.AddSingleton(bounds);
        });
        var physical = new BackgroundJobPhysicalPoolSettings(true, 52);
        await using var source = Source(database, physical);
        using var globals = new GlobalFilterSnapshot();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(100));
        var runtime = Runtime(host.Services, source, bounds, physical);
        try
        {
            await runtime.StartAsync(deadline.Token);
            var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString);
            await EnsureQualificationStartAsync(runtime, source, job, entered.Task, deadline.Token);
            await entered.Task.WaitAsync(deadline.Token);
            var admitted = (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job))!;
            admitted.Phase.Should().Be("executing");
            var generation = ActualGeneration(runtime, "transactional");
            generation.RequestDrain(HangfireGenerationReason.HeartbeatTransient);
            generation.TryAdmitInvocation().Should().BeFalse();
            await Task.Delay(bounds.LeaseDuration + bounds.LeaseDuration + TimeSpan.FromSeconds(1), deadline.Token);
            var renewed = (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job))!;
            renewed.OwnershipToken.Should().Be(admitted.OwnershipToken);
            renewed.CurrentAttempt.Should().Be(admitted.CurrentAttempt);
            renewed.OwnershipUntil.Should().BeAfter(admitted.OwnershipUntil!.Value + bounds.LeaseDuration);
            renewed.NormalAttempts.Should().Be(1);
            runtime.Snapshot.Control!.Renewals.Should().BeGreaterThan(0);
            generation.Completion.IsCompleted.Should().BeFalse("local drain cannot replace the still-running generation");
            (await BackgroundJobRuntimeRows.CountOrganizationAsync(database.AppConnectionString, organization)).Should().Be(0);
            release.TrySetResult();
            await EventuallyQualificationAsync(async () => (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job))!.Phase == "completed", deadline.Token);
            (await BackgroundJobRuntimeRows.CountOrganizationAsync(database.AppConnectionString, organization)).Should().Be(1);
            var observedAttempts = await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job);
            if (observedAttempts == 0)
                throw new InvalidOperationException("Native renewal attempt ledger is missing its admitted row.");
            if (observedAttempts > 1)
                throw new InvalidOperationException("Native renewal attempt ledger contains an additional admission.");
            observedAttempts.Should().Be(1);
            await generation.Completion.WaitAsync(deadline.Token);
            probe.Constructions.Should().Equal("TenantRoot");
        }
        finally
        {
            release.TrySetResult();
            using var cleanup = new CancellationTokenSource(TestWait);
            await runtime.StopAsync(cleanup.Token);
            await runtime.Completion.WaitAsync(cleanup.Token);
            await DisposePinnedSourceAsync(source);
        }
    }

    private static BackgroundJobRuntimeOptions BusinessBounds() => new()
    {
        SqlTimeout = TimeSpan.FromSeconds(1),
        LeaseDuration = TimeSpan.FromSeconds(15),
        RenewalInterval = TimeSpan.FromSeconds(1),
        TransactionTimeout = TimeSpan.FromSeconds(10),
        ControlPoll = TimeSpan.FromMilliseconds(50),
        RecoveryProbeInterval = TimeSpan.FromSeconds(5),
    };

    [Fact]
    public async Task Native_business_overrun_and_private_callback_keep_both_sources_until_actual_cleanup_after_host_stop()
    {
        await using var database = await DatabaseAsync();
        var bounds = new BackgroundJobRuntimeOptions { TransactionTimeout = TimeSpan.FromSeconds(2) };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bodyRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bodyReturned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callback = new FacadeCleanupSignals();
        var organization = Guid.CreateVersion7();
        var probe = new BackgroundJobFixtureProbe
        {
            Body = async (_, services, token) =>
            {
                var written = await services.GetRequiredService<ISender>().Send(Fixture.OrganizationRequest(
                    OrganizationId.From(organization), "job-stop-" + organization.ToString("N")[..12], "Job stop"), token);
                written.IsSuccess.Should().BeTrue();
                using var registration = token.Register(() =>
                {
                    callback.Entered.TrySetResult();
                    callback.Release.Task.GetAwaiter().GetResult();
                    callback.Joined.TrySetResult();
                });
                entered.TrySetResult();
                // Deliberately non-cooperative fixture work and callback hold
                // actual native business ownership; neither is a cleanup witness.
                await bodyRelease.Task;
                bodyReturned.TrySetResult();
                return Result.Ok(None.Value);
            },
        };
        await using var host = Fixture.Host(database, probe, configure: services =>
        {
            services.RemoveAll<BackgroundJobRuntimeOptions>();
            services.AddSingleton(bounds);
        });
        await using var source = Source(database, new(true, 52));
        var applicationSource = host.Services.GetRequiredService<ApplicationDataSourceLifetime>();
        using var globals = new GlobalFilterSnapshot();
        var runtime = Runtime(host.Services, source, bounds, new(true, 52));
        using var deadline = new CancellationTokenSource(TestWait);
        try
        {
            await runtime.StartAsync(deadline.Token);
            var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString);
            await EnsureQualificationStartAsync(runtime, source, job, entered.Task, deadline.Token);
            await entered.Task.WaitAsync(deadline.Token);
            await callback.Entered.Task.WaitAsync(deadline.Token);
            await EventuallyQualificationAsync(async () => await VendorAsync(source, job) is { } vendor
                && await StateAsync(source, vendor) == "Succeeded", deadline.Token);
            runtime.Snapshot.RetainedInvocations.Should().Be(1);
            runtime.Snapshot.Control!.Executions.Should().Be(1);
            applicationSource.Dispose();
            source.Dispose();
            runtime.RequestStop();
            using var finiteWait = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            await FluentActions.Awaiting(() => runtime.StopAsync(finiteWait.Token))
                .Should().ThrowAsync<OperationCanceledException>();
            runtime.Completion.IsCompleted.Should().BeFalse();
            applicationSource.IsDisposalComplete.Should().BeFalse();
            source.IsDisposalComplete.Should().BeFalse();
            bodyRelease.TrySetResult();
            await bodyReturned.Task.WaitAsync(deadline.Token);
            callback.Joined.Task.IsCompleted.Should().BeFalse();
            runtime.Completion.IsCompleted.Should().BeFalse("returned handler code has not joined its actual callback");
            applicationSource.IsDisposalComplete.Should().BeFalse();
            callback.Release.TrySetResult();
            await runtime.Completion.WaitAsync(deadline.Token);
            await ((Task)typeof(ApplicationDataSourceLifetime).GetField("_retentionObserver", PrivateInstance)!
                .GetValue(applicationSource)!).WaitAsync(deadline.Token);
            await DisposePinnedSourceAsync(source);
            applicationSource.IsDisposalComplete.Should().BeTrue();
            source.IsDisposalComplete.Should().BeTrue();
            callback.Joined.Task.IsCompletedSuccessfully.Should().BeTrue();
            runtime.Snapshot.Control!.Executions.Should().Be(0);
            (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job))!.Phase.Should().Be("executing");
            (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job)).Should().Be(1);
            (await BackgroundJobRuntimeRows.CountOrganizationAsync(database.AppConnectionString, organization)).Should().Be(0,
                "late success cannot commit through the already closed private gate");
        }
        finally
        {
            bodyRelease.TrySetResult();
            callback.Release.TrySetResult();
            using var cleanup = new CancellationTokenSource(TestWait);
            await runtime.StopAsync(cleanup.Token);
            await runtime.Completion.WaitAsync(cleanup.Token);
            await DisposePinnedSourceAsync(source);
        }
    }

    private static async Task<(long Total, long BackedOff, long Dead, long Attempts, DateTimeOffset LatestBackoff, bool AuditPermitted)>
        TerminalBacklogAsync(DisposableSchemaDatabase database)
    {
        await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var transaction = await connection.BeginTransactionAsync();
        await EducationSchemaSeed.AnnounceAsync(connection, transaction, SchemaFixture.TenantA, null);
        await using var command = new NpgsqlCommand("""
            SELECT count(*), count(*) FILTER (WHERE last_error='audit_unavailable'),
                count(*) FILTER (WHERE dead_lettered_at IS NOT NULL), sum(normal_attempts)::bigint,
                max(available_after), has_table_privilege('learnstack_outbox_admin', 'audit_log', 'INSERT')
            FROM background_jobs WHERE terminal_reason='invalid_envelope'
            """, connection, transaction);
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3),
            reader.GetFieldValue<DateTimeOffset>(4), reader.GetBoolean(5));
    }
}
