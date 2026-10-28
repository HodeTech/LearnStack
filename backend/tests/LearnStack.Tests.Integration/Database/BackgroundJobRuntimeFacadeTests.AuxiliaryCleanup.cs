using System.Collections.Concurrent;
using System.Data;
using System.Diagnostics.Metrics;
using FluentAssertions;
using LearnStack.Application.Audit;
using LearnStack.Infrastructure.Audit;
using LearnStack.Infrastructure.Caching;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobRuntimeFacadeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Native_cache_waiter_timeout_joins_late_actual_auxiliary_cleanup_and_retains_disposal_faults(bool dirty)
    {
        await using var database = await DatabaseAsync();
        await using var blocker = await PostgresFixture.OpenAsync(database.MigrationConnectionString);
        await using var locked = await blocker.BeginTransactionAsync();
        await SchemaQueries.ExecuteAsync(blocker, locked, "LOCK TABLE audit_config IN ACCESS EXCLUSIVE MODE");
        var bounds = BusinessBounds() with { TransactionTimeout = TimeSpan.FromSeconds(4) };
        var auxiliary = new FacadeCleanupSignals();
        var callback = new FacadeCleanupSignals();
        var published = new TaskCompletionSource<JobApplicationSourceOwner>(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiterReturned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstJob = new TaskCompletionSource<BackgroundJobId>(TaskCreationOptions.RunContinuationsAsynchronously);
        var followOnJob = new TaskCompletionSource<BackgroundJobId>(TaskCreationOptions.RunContinuationsAsynchronously);
        var followOnStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var organizationTime = DateTimeOffset.UtcNow;
        var firstOrganization = Guid.CreateVersion7(organizationTime);
        var nextOrganization = Guid.CreateVersion7(organizationTime);
        nextOrganization.Should().NotBe(firstOrganization);
        nextOrganization.ToString("N")[..12].Should().Be(firstOrganization.ToString("N")[..12],
            "distinct UUIDv7 fixture ids deliberately share the timestamp prefix");
        var auditLogger = new AuxiliaryAuditLogger<PostgresAuditStore>();
        var reconcileLogger = new AuxiliaryAuditLogger<AuditExecutionCoordinator>();
        var calls = 0;
        var closes = 0;
        CancellationTokenRegistration privateCallback = default;
        NpgsqlConnection? borrowed = null;
        Task? actual = null;
        JobApplicationAuxiliaryResources? originalResources = null;
        StateChangeEventHandler? cleanup = null;
        var probe = new BackgroundJobFixtureProbe
        {
            Body = async (_, services, token) =>
            {
                var first = Interlocked.Increment(ref calls) == 1;
                var accessor = services.GetRequiredService<ITenantContextAccessor>();
                var context = accessor.Current.Should().BeOfType<JobTenantContext>().Subject;
                context.JobId.Should().NotBeNull("business execution always carries its admitted job identity");
                if (first) firstJob.TrySetResult(context.JobId!.Value);
                var originalJob = context.JobId == await firstJob.Task;
                if (!originalJob)
                {
                    context.JobId.Should().Be(await followOnJob.Task);
                    followOnStarted.TrySetResult();
                }
                var organization = originalJob ? firstOrganization : nextOrganization;
                var written = await services.GetRequiredService<ISender>().Send(Fixture.OrganizationRequest(
                    OrganizationId.From(organization), "job-aux-" + organization.ToString("N"), "Job auxiliary"), token);
                written.IsSuccess.Should().BeTrue();
                if (!first) return Result.Ok(None.Value);

                var owner = JobApplicationSourceOwner.Current
                    ?? throw new InvalidOperationException("The native business source owner is missing.");
                var classifier = new AuditConfigService(new InMemoryCacheService(new SystemClock(),
                    services.GetRequiredService<IMeterFactory>(), TimeSpan.FromSeconds(1)),
                    services.GetRequiredService<Lazy<NpgsqlDataSource>>(), NullLogger<AuditConfigService>.Instance, accessor);
                var classification = classifier.ClassifyAsync(accessor.Current!.TenantId,
                    new AuditCatalogEntry("tenancy", "tenancy.application-source.proof", OperationType.Create,
                        OperationClass.Should, typeof(object)), CancellationToken.None);
                await EventuallyAsync(() => Task.FromResult(AuxiliaryCleanupConnection(owner)?.State == ConnectionState.Open));
                var resources = AuxiliaryCleanupResources(owner)!;
                originalResources = resources;
                borrowed = AuxiliaryCleanupConnection(owner)!;
                actual = (Task)typeof(JobApplicationAuxiliaryResources).GetField("_actualTask", PrivateInstance)!.GetValue(resources)!;
                cleanup = (_, change) =>
                {
                    if (change.CurrentState != ConnectionState.Closed) return;
                    Interlocked.Increment(ref closes);
                    auxiliary.Entered.TrySetResult();
                    auxiliary.Release.Task.GetAwaiter().GetResult();
                    auxiliary.Joined.TrySetResult();
                    if (dirty) throw new InvalidOperationException("Deliberate actual auxiliary disposal failure.");
                };
                borrowed.StateChange += cleanup;
                privateCallback = token.Register(() =>
                {
                    callback.Entered.TrySetResult();
                    callback.Release.Task.GetAwaiter().GetResult();
                    callback.Joined.TrySetResult();
                });
                published.TrySetResult(owner);
                (await classification).Should().Be(AuditClassification.Should);
                waiterReturned.TrySetResult();
                return Result.Ok(None.Value);
            },
        };
        await using var host = Fixture.Host(database, probe, configure: services =>
        {
            services.RemoveAll<BackgroundJobRuntimeOptions>();
            services.AddSingleton(bounds);
            services.AddSingleton<ILogger<PostgresAuditStore>>(auditLogger);
            services.AddSingleton<ILogger<AuditExecutionCoordinator>>(reconcileLogger);
        });
        await using var source = Source(database, new(true, 52));
        var applicationSource = host.Services.GetRequiredService<ApplicationDataSourceLifetime>();
        var auditHealth = host.Services.GetRequiredService<IAuditHealth>();
        auditHealth.IsHealthy.Should().BeTrue();
        var auditMeter = host.Services.GetRequiredService<IMeterFactory>().Create(PostgresAuditStore.MeterName);
        var auditFailures = new ConcurrentQueue<(long Count, KeyValuePair<string, object?>[] Tags)>();
        using var auditListener = new MeterListener();
        auditListener.InstrumentPublished = (instrument, listener) =>
        {
            if (ReferenceEquals(instrument.Meter, auditMeter)
                && instrument.Name == PostgresAuditStore.StandaloneWriteFailureCounterName)
                listener.EnableMeasurementEvents(instrument);
        };
        auditListener.SetMeasurementEventCallback<long>((_, count, tags, _) => auditFailures.Enqueue((count, tags.ToArray())));
        auditListener.Start();
        using var globals = new GlobalFilterSnapshot();
        var runtime = Runtime(host.Services, source, bounds, new(true, 52));
        HangfireGeneration? generation = null;
        var verified = false;
        Exception? testFailure = null;
        var lockReleased = false;
        try
        {
            await runtime.StartAsync();
            var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString);
            var qualificationHint = await EnsureQualificationStartAsync(runtime, source, job, published.Task);
            var owner = await published.Task.WaitAsync(TestWait);
            await waiterReturned.Task.WaitAsync(TestWait);
            await auxiliary.Entered.Task.WaitAsync(TestWait);
            generation = ActualGeneration(runtime, "transactional");
            var execution = AuxiliaryCleanupExecution(runtime, job);
            var business = (Task<JobBusinessExecutionResult>)typeof(BackgroundJobExecutionControl)
                .GetField("_business", PrivateInstance)!.GetValue(execution)!;
            await EventuallyAsync(() => Task.FromResult((bool)typeof(JobApplicationSourceOwner)
                .GetField("_closed", PrivateInstance)!.GetValue(owner)!));
            business.IsCompleted.Should().BeFalse("the executor joins original auxiliary cleanup after its caller returned");
            actual!.IsCompleted.Should().BeFalse();
            owner.CleanupConfirmed.Should().BeFalse();
            await callback.Entered.Task.WaitAsync(TestWait);
            await EventuallyAsync(async () => await VendorAsync(source, job) is { } vendor
                && await StateAsync(source, vendor) == "Succeeded");
            if (qualificationHint is not null)
                await EventuallyAsync(async () => await StateAsync(source, qualificationHint) == "Succeeded");
            execution.Gate.IsClosed.Should().BeTrue();
            execution.Gate.CommitStarted.Should().BeFalse();
            generation.WorkSnapshot.Should().Be(new HangfireGenerationWorkSnapshot(0, 1, false));
            runtime.Snapshot.Control!.Executions.Should().Be(1);
            (await BackgroundJobRuntimeRows.CountOrganizationAsync(database.AppConnectionString, firstOrganization)).Should().Be(0);
            (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job)).Should().Be(1);
            await AssertAuxiliaryAuditAbsentAsync(database, firstOrganization);
            auditHealth.IsHealthy.Should().BeFalse("the real MUST standalone acquisition was refused");
            auditHealth.FailingOperation.Should().Be("jobfixture.organization.create");
            var alert = auditLogger.Entries.Should().ContainSingle().Subject;
            alert.Level.Should().Be(LogLevel.Critical);
            alert.EventId.Should().Be(new EventId(3, "LogStandaloneWriteFailed"));
            AssertAuxiliaryAuditDiagnostic(alert, firstOrganization);
            var warning = reconcileLogger.Entries.Should().ContainSingle().Subject;
            warning.Level.Should().Be(LogLevel.Warning, "reconciliation preserves the existing business failure");
            AssertAuxiliaryAuditDiagnostic(warning, firstOrganization);
            var counted = auditFailures.Should().ContainSingle().Subject;
            counted.Count.Should().Be(1);
            counted.Tags.Should().ContainSingle().Which
                .Should().Be(new KeyValuePair<string, object?>("operation", "jobfixture.organization.create"));
            AuxiliaryCleanupResources(owner).Should().BeSameAs(originalResources,
                "failed reconciliation cannot replace the held auxiliary acquisition");
            AuxiliaryCleanupConnection(owner).Should().BeSameAs(borrowed);
            typeof(JobApplicationAuxiliaryResources).GetField("_actualTask", PrivateInstance)!
                .GetValue(originalResources).Should().BeSameAs(actual);

            applicationSource.Dispose();
            source.Dispose();
            applicationSource.IsDisposalComplete.Should().BeFalse();
            source.IsDisposalComplete.Should().BeFalse();
            auxiliary.Release.TrySetResult();
            await locked.RollbackAsync();
            lockReleased = true;
            await actual.WaitAsync(TestWait).ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);
            actual.IsCompleted.Should().BeTrue();
            var completed = await business.WaitAsync(TestWait);
            completed.SourceOwner.Should().BeSameAs(owner);
            completed.CleanupFailed.Should().Be(dirty);
            completed.CommitAcknowledged.Should().BeFalse();
            completed.CommitStarted.Should().BeFalse();
            completed.DurableOutcomeUncertain.Should().BeFalse();
            completed.Decision.Disposition.Should().Be(BackgroundJobFailureDisposition.RetainOwnership);
            execution.Gate.IsClosed.Should().BeTrue("joining cleanup grants no new commit authority");
            owner.CleanupConfirmed.Should().Be(!dirty);
            closes.Should().Be(1);
            callback.Joined.Task.IsCompleted.Should().BeFalse();
            runtime.Snapshot.Control!.Executions.Should().Be(1, "private cancellation callbacks still belong to the invocation");
            generation.WorkSnapshot.Retained.Should().Be(1);
            callback.Release.TrySetResult();
            await callback.Joined.Task.WaitAsync(TestWait);

            if (dirty)
            {
                await EventuallyAsync(() => Task.FromResult((bool)typeof(BackgroundJobExecutionControl)
                    .GetField("_cleanupFault", PrivateInstance)!.GetValue(execution)!));
                runtime.RequestStop();
                await EventuallyAsync(() => Task.FromResult(generation.Snapshot.OuterJoined));
                using var finiteWait = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
                await FluentActions.Awaiting(() => runtime.StopAsync(finiteWait.Token))
                    .Should().ThrowAsync<OperationCanceledException>();
                runtime.Completion.IsCompleted.Should().BeFalse();
                generation.Completion.IsCompleted.Should().BeFalse();
                generation.WorkSnapshot.Should().Be(new HangfireGenerationWorkSnapshot(0, 1, true));
                runtime.Snapshot.Control!.Executions.Should().Be(1);
                applicationSource.IsDisposalComplete.Should().BeFalse();
                source.IsDisposalComplete.Should().BeFalse();
                AuxiliaryCleanupResources(owner).Should().NotBeNull("a real disposal fault cannot become a positive witness");
            }
            else
            {
                await EventuallyAsync(() => Task.FromResult(runtime.Snapshot.Control!.Executions == 0
                    && runtime.Snapshot.RetainedInvocations == 0));
                generation.WorkSnapshot.Should().Be(new HangfireGenerationWorkSnapshot(0, 0, false));
                AuxiliaryCleanupResources(owner).Should().BeNull();
                // Force the old canceled job's legitimate retry to finish
                // before submitting the independent follow-on job.
                await EventuallyAsync(async () => (await BackgroundJobRuntimeRows.ReadAsync(
                    database.AppConnectionString, job)) is { Phase: "completed", NormalAttempts: 2 });
                (await BackgroundJobRuntimeRows.CountOrganizationAsync(database.AppConnectionString, firstOrganization))
                    .Should().Be(1, "every retry retains the original job's fixture resource");
                (await BackgroundJobRuntimeRows.CountOrganizationAsync(database.AppConnectionString, nextOrganization))
                    .Should().Be(0, "the original retry cannot consume the independent job's fixture resource");
                var healthy = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString);
                followOnJob.TrySetResult(healthy);
                await EnsureQualificationStartAsync(runtime, source, healthy, followOnStarted.Task);
                try { await EventuallyAsync(() => NativeBusinessCompletedAsync(database, source, healthy)); }
                catch (TimeoutException)
                {
                    testOutput.WriteLine("Follow-on timeout: calls={0}; applicationDisposed={1}; privilegedDisposed={2}",
                        Volatile.Read(ref calls), applicationSource.IsDisposalComplete, source.IsDisposalComplete);
                    // Report only scheduling/status facts from the isolated
                    // fixture, preserving the original failure if reading fails.
                    try
                    {
                        using var diagnostic = new CancellationTokenSource(bounds.SqlTimeout);
                        await using var connection = await source.OpenConnectionAsync(diagnostic.Token);
                        await using var command = new NpgsqlCommand("""
                            SELECT j.phase,j.normal_attempts,j.wake_generation,j.ownership_kind,
                                j.handoff_check_at,clock_timestamp(),h.statename
                            FROM background_jobs j LEFT JOIN public.job h ON h.id::text=j.vendor_job_id
                            WHERE j.id=@id
                            """, connection) { CommandTimeout = 5 };
                        command.Parameters.AddWithValue("id", healthy.Value);
                        await using var row = await command.ExecuteReaderAsync(diagnostic.Token);
                        if (await row.ReadAsync(diagnostic.Token))
                            testOutput.WriteLine("Follow-on job: phase={0}; attempts={1}; generation={2}; owner={3}; repair={4}; now={5}; native={6}; runtime={7}",
                                row.GetString(0), row.GetInt32(1), row.GetInt64(2), row.IsDBNull(3) ? null : row.GetString(3),
                                row.GetFieldValue<DateTimeOffset>(4), row.GetFieldValue<DateTimeOffset>(5),
                                row.IsDBNull(6) ? null : row.GetString(6), runtime.Snapshot);
                    }
                    catch (Exception diagnosticFailure) when (diagnosticFailure.GetType() != typeof(OutOfMemoryException)
                        && diagnosticFailure.GetType() != typeof(StackOverflowException))
                    { testOutput.WriteLine("Follow-on status unavailable ({0}).", diagnosticFailure.GetType().Name); }
                    throw;
                }
                (await BackgroundJobRuntimeRows.CountOrganizationAsync(database.AppConnectionString, nextOrganization)).Should().Be(1);
                (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, healthy)).Should().Be(1);
                await runtime.StopAsync().WaitAsync(TestWait);
                await runtime.Completion.WaitAsync(TestWait);
                await generation.Completion.WaitAsync(TestWait);
                await ((Task)typeof(ApplicationDataSourceLifetime).GetField("_retentionObserver", PrivateInstance)!
                    .GetValue(applicationSource)!).WaitAsync(TestWait);
                await DisposePinnedSourceAsync(source);
                applicationSource.IsDisposalComplete.Should().BeTrue();
                runtime.Snapshot.Control!.Executions.Should().Be(0);
                runtime.Snapshot.RetainedInvocations.Should().Be(0);
            }
            verified = true;
        }
        catch (Exception failure) { testFailure = failure; }
        finally
        {
            await PreserveNativeBodyFailureAsync(testFailure, async () =>
            {
                auxiliary.Release.TrySetResult();
                callback.Release.TrySetResult();
                if (!lockReleased) await locked.RollbackAsync();
                if (actual is not null)
                {
                    await actual.WaitAsync(TestWait).ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);
                    actual.IsCompleted.Should().BeTrue("the fixture releases and joins its actual auxiliary operation");
                }
                if (borrowed is not null && cleanup is not null) borrowed.StateChange -= cleanup;
                privateCallback.Dispose();
                runtime.RequestStop();
                if (!dirty && verified)
                {
                    await runtime.StopAsync().WaitAsync(TestWait);
                    await runtime.Completion.WaitAsync(TestWait);
                    await DisposePinnedSourceAsync(source);
                }
                else
                {
                    // The disposal-fault witness deliberately cannot release its
                    // invocation. Join actual stock/control/shared I/O before the
                    // isolated database is removed, without inventing positive cleanup.
                    // The same finite cleanup preserves an earlier failed assertion
                    // when the regression under test permanently retained clean work.
                    using var finiteWait = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
                    try { await runtime.StopAsync(finiteWait.Token); }
                    catch (OperationCanceledException) when (finiteWait.IsCancellationRequested) { }
                    await EventuallyAsync(() => Task.FromResult(AuxiliaryCleanupStockJoined(runtime)
                        && runtime.Snapshot.Control!.CleanupConfirmed
                        && runtime.Snapshot.Maintenance.All(component => component.ActualJoined)));
                }
            });
        }
    }

    private static async Task AssertAuxiliaryAuditAbsentAsync(DisposableSchemaDatabase database, Guid organization)
    {
        await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var transaction = await connection.BeginTransactionAsync();
        await EducationSchemaSeed.AnnounceAsync(connection, transaction, SchemaFixture.TenantA, null);
        await SchemaQueries.AssertApplicationRoleAsync(connection, transaction);
        await using var read = new NpgsqlCommand("""
            SELECT count(*) FROM audit_log
            WHERE operation='jobfixture.organization.create' AND entity_id=@subject
            """, connection, transaction);
        read.Parameters.AddWithValue("subject", organization.ToString());
        (await read.ExecuteScalarAsync()).Should().Be(0L,
            "the rolled-back MUST row cannot be rewritten while the actual auxiliary cleanup is pending");
    }

    private static void AssertAuxiliaryAuditDiagnostic(AuxiliaryAuditLog entry, Guid organization)
    {
        entry.Exception.Should().BeNull("trusted job diagnostics never receive raw exception details");
        entry.Message.Should().Contain("jobfixture.organization.create").And.NotContain(organization.ToString())
            .And.NotContain(nameof(InvalidOperationException)).And.NotContain("refuses another acquisition")
            .And.NotContain("Deliberate actual auxiliary disposal failure");
        entry.Properties.Where(property => property.Key != "{OriginalFormat}").Should().ContainSingle().Which
            .Should().Be(new KeyValuePair<string, object?>("Operation", "jobfixture.organization.create"));
    }

    private sealed record AuxiliaryAuditLog(LogLevel Level, EventId EventId, string Message,
        Exception? Exception, KeyValuePair<string, object?>[] Properties);

    private sealed class AuxiliaryAuditLogger<T> : ILogger<T>
    {
        internal ConcurrentQueue<AuxiliaryAuditLog> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Enqueue(new(logLevel, eventId,
                formatter(state, exception), exception,
                ((IEnumerable<KeyValuePair<string, object?>>)state!).ToArray()));
    }

    private static JobApplicationAuxiliaryResources? AuxiliaryCleanupResources(JobApplicationSourceOwner owner) =>
        (JobApplicationAuxiliaryResources?)typeof(JobApplicationSourceOwner).GetField("_auxiliary", PrivateInstance)!.GetValue(owner);

    private static NpgsqlConnection? AuxiliaryCleanupConnection(JobApplicationSourceOwner owner) =>
        AuxiliaryCleanupResources(owner) is { } resources
            ? (NpgsqlConnection?)typeof(JobApplicationAuxiliaryResources).GetField("_connection", PrivateInstance)!.GetValue(resources) : null;

    private static BackgroundJobExecutionControl AuxiliaryCleanupExecution(BackgroundJobRuntime runtime, BackgroundJobId job)
    {
        var control = (BackgroundJobControl)typeof(BackgroundJobRuntime).GetField("_jobs", PrivateInstance)!.GetValue(runtime)!;
        var sync = typeof(BackgroundJobControl).GetField("_sync", PrivateInstance)!.GetValue(control)!;
        lock (sync)
            return ((Dictionary<Guid, BackgroundJobExecutionControl>)typeof(BackgroundJobControl)
                .GetField("_executions", PrivateInstance)!.GetValue(control)!)[job.Value];
    }

    private static bool AuxiliaryCleanupStockJoined(BackgroundJobRuntime runtime) =>
        ((IEnumerable<HangfirePoolOwner>)typeof(BackgroundJobRuntime).GetField("_pools", PrivateInstance)!
            .GetValue(runtime)!).All(pool => pool.Snapshot.Generation?.OuterJoined != false);
}
