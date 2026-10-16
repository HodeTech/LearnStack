using System.Data;
using System.Diagnostics.Metrics;
using FluentAssertions;
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
        var firstOrganization = Guid.CreateVersion7();
        var nextOrganization = Guid.CreateVersion7();
        var calls = 0;
        var closes = 0;
        CancellationTokenRegistration privateCallback = default;
        NpgsqlConnection? borrowed = null;
        Task? actual = null;
        StateChangeEventHandler? cleanup = null;
        var probe = new BackgroundJobFixtureProbe
        {
            Body = async (_, services, token) =>
            {
                var first = Interlocked.Increment(ref calls) == 1;
                var organization = first ? firstOrganization : nextOrganization;
                var written = await services.GetRequiredService<ISender>().Send(new CreateOrganizationCommand(
                    OrganizationId.From(organization), "job-aux-" + organization.ToString("N")[..12], "Job auxiliary"), token);
                written.IsSuccess.Should().BeTrue();
                if (!first) return Result.Ok(None.Value);

                var owner = JobApplicationSourceOwner.Current
                    ?? throw new InvalidOperationException("The native business source owner is missing.");
                var accessor = services.GetRequiredService<ITenantContextAccessor>();
                var classifier = new AuditConfigService(new InMemoryCacheService(new SystemClock(),
                    services.GetRequiredService<IMeterFactory>(), TimeSpan.FromSeconds(1)),
                    services.GetRequiredService<Lazy<NpgsqlDataSource>>(), NullLogger<AuditConfigService>.Instance, accessor);
                var classification = classifier.ClassifyAsync(accessor.Current!.TenantId,
                    new AuditCatalogEntry("tenancy", "tenancy.application-source.proof", OperationType.Create,
                        OperationClass.Should, typeof(object)), CancellationToken.None);
                await EventuallyAsync(() => Task.FromResult(AuxiliaryCleanupConnection(owner)?.State == ConnectionState.Open));
                var resources = AuxiliaryCleanupResources(owner)!;
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
        });
        await using var source = Source(database, new(true, 50));
        var applicationSource = host.Services.GetRequiredService<ApplicationDataSourceLifetime>();
        using var globals = new GlobalFilterSnapshot();
        var runtime = Runtime(host.Services, source, bounds, new(true, 50));
        HangfireGeneration? generation = null;
        var verified = false;
        var lockReleased = false;
        try
        {
            await runtime.StartAsync();
            var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString);
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
            execution.Gate.IsClosed.Should().BeTrue();
            execution.Gate.CommitStarted.Should().BeFalse();
            generation.WorkSnapshot.Should().Be(new HangfireGenerationWorkSnapshot(0, 1, false));
            runtime.Snapshot.Control!.Executions.Should().Be(1);
            (await BackgroundJobRuntimeRows.CountOrganizationAsync(database.AppConnectionString, firstOrganization)).Should().Be(0);
            (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job)).Should().Be(1);

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
                var healthy = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString);
                await EventuallyAsync(() => NativeBusinessCompletedAsync(database, source, healthy));
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
        finally
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
        }
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
