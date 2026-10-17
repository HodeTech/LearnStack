using System.Data;
using System.Diagnostics.Metrics;
using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Audit;
using LearnStack.Infrastructure.Caching;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Persistence;
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
    public async Task Native_grouped_rollback_checkpoints_only_after_original_auxiliary_and_private_callback_cleanup(bool dirty)
    {
        await using var database = await DatabaseAsync();
        await using var blocker = await PostgresFixture.OpenAsync(database.MigrationConnectionString);
        await using var locked = await blocker.BeginTransactionAsync();
        await SchemaQueries.ExecuteAsync(blocker, locked, "LOCK TABLE audit_config IN ACCESS EXCLUSIVE MODE");
        var bounds = BusinessBounds() with { TransactionTimeout = TimeSpan.FromSeconds(4) };
        var auxiliary = new FacadeCleanupSignals();
        var callback = new FacadeCleanupSignals();
        var checkpoint = new CapacityCleanupCheckpointState(callback);
        var published = new TaskCompletionSource<JobApplicationSourceOwner>(TaskCreationOptions.RunContinuationsAsynchronously);
        var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration privateCallback = default;
        NpgsqlConnection? borrowed = null;
        StateChangeEventHandler? disposal = null;
        Task? actualAuxiliary = null;
        var closes = 0;
        var calls = 0;
        var probe = new BackgroundJobFixtureProbe
        {
            Body = async (request, services, token) =>
            {
                request.Should().Be("CapacityRoot");
                if (Interlocked.Increment(ref calls) != 1) return Result.Ok(None.Value);
                var owner = JobApplicationSourceOwner.Current
                    ?? throw new InvalidOperationException("The native grouped owner is missing.");
                checkpoint.OriginalOwner = owner;
                checkpoint.OriginalUnit = services.GetRequiredService<IUnitOfWork>();
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
                actualAuxiliary = (Task)typeof(JobApplicationAuxiliaryResources).GetField("_actualTask", PrivateInstance)!
                    .GetValue(resources)!;
                checkpoint.OriginalAuxiliary = actualAuxiliary;
                disposal = (_, change) =>
                {
                    if (change.CurrentState != ConnectionState.Closed) return;
                    Interlocked.Increment(ref closes);
                    auxiliary.Entered.TrySetResult();
                    auxiliary.Release.Task.GetAwaiter().GetResult();
                    auxiliary.Joined.TrySetResult();
                    if (dirty) throw new InvalidOperationException("Deliberate grouped auxiliary disposal failure.");
                };
                borrowed.StateChange += disposal;
                privateCallback = token.Register(() =>
                {
                    callback.Entered.TrySetResult();
                    callback.Release.Task.GetAwaiter().GetResult();
                    // The callback inherited the old business binding, which
                    // cannot issue another helper during a later checkpoint.
                    var acquire = () => owner.BeginStandaloneAudit();
                    acquire.Should().Throw<InvalidOperationException>();
                    callback.Joined.TrySetResult();
                });
                published.TrySetResult(owner);
                (await classification).Should().Be(AuditClassification.Should);
                returned.TrySetResult();
                return Result<None>.Fail(BackgroundJobExecutionAdmission.Refused);
            },
        };
        await using var host = Fixture.Host(database, probe, configure: services =>
        {
            services.RemoveAll<BackgroundJobDefinitionRegistry>();
            services.AddSingleton(Fixture.CapacityRegistry());
            services.RemoveAll<BackgroundJobRuntimeOptions>();
            services.AddSingleton(bounds);
            services.AddSingleton(checkpoint);
            services.AddTransient<IPipelineBehavior<RecordBackgroundJobCapacityCleanupCommand, Result<None>>,
                CapacityCleanupCheckpointObserver>();
        });
        await using var source = Source(database, new(true, 50));
        var applicationSource = host.Services.GetRequiredService<ApplicationDataSourceLifetime>();
        using var globals = new GlobalFilterSnapshot();
        var runtime = Runtime(host.Services, source, bounds, new(true, 50));
        var lockReleased = false;
        var verified = false;
        try
        {
            await runtime.StartAsync();
            var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString,
                BackgroundJobRuntimeFixture.CapacityKey, capacityGroup: BackgroundJobRuntimeFixture.CapacityGroup);
            var originalOwner = await published.Task.WaitAsync(TestWait);
            await returned.Task.WaitAsync(TestWait);
            await auxiliary.Entered.Task.WaitAsync(TestWait);
            await callback.Entered.Task.WaitAsync(TestWait);
            var execution = AuxiliaryCleanupExecution(runtime, job);
            var generation = ActualGeneration(runtime, "transactional");
            var business = (Task<JobBusinessExecutionResult>)typeof(BackgroundJobExecutionControl)
                .GetField("_business", PrivateInstance)!.GetValue(execution)!;
            business.IsCompleted.Should().BeFalse();
            actualAuxiliary!.IsCompleted.Should().BeFalse();
            checkpoint.Entered.Task.IsCompleted.Should().BeFalse();
            (await ReadCapacityCleanupEvidenceAsync(database, job)).Should().Be((false, false, (string?)null));
            runtime.Snapshot.Control!.Executions.Should().Be(1);
            originalOwner.CleanupConfirmed.Should().BeFalse();

            auxiliary.Release.TrySetResult();
            await locked.RollbackAsync();
            lockReleased = true;
            await actualAuxiliary.WaitAsync(TestWait)
                .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);
            var completed = await business.WaitAsync(TestWait);
            completed.SourceOwner.Should().BeSameAs(originalOwner);
            completed.CleanupFailed.Should().Be(dirty);
            completed.CommitAcknowledged.Should().BeFalse();
            completed.KnownRollback.Should().BeTrue();
            closes.Should().Be(1);
            callback.Joined.Task.IsCompleted.Should().BeFalse();
            // Keep the actual callback held over several independent control
            // polls, so an early checkpoint is observed before its barrier opens.
            var early = await Task.WhenAny(checkpoint.Entered.Task, Task.Delay(TimeSpan.FromMilliseconds(300)));
            early.Should().NotBeSameAs(checkpoint.Entered.Task);
            (await ReadCapacityCleanupEvidenceAsync(database, job)).Should().Be((false, false, (string?)null));
            runtime.Snapshot.Control!.Executions.Should().Be(1);
            callback.Release.TrySetResult();
            await callback.Joined.Task.WaitAsync(TestWait);

            if (dirty)
            {
                await EventuallyAsync(() => Task.FromResult(execution.CleanupFault));
                checkpoint.Entered.Task.IsCompleted.Should().BeFalse();
                originalOwner.CleanupConfirmed.Should().BeFalse();
                (await ReadCapacityCleanupEvidenceAsync(database, job)).Should().Be((false, false, (string?)null));
                applicationSource.Dispose();
                source.Dispose();
                runtime.RequestStop();
                await EventuallyAsync(() => Task.FromResult(generation.Snapshot.OuterJoined));
                using var finite = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
                await FluentActions.Awaiting(() => runtime.StopAsync(finite.Token)).Should().ThrowAsync<OperationCanceledException>();
                runtime.Completion.IsCompleted.Should().BeFalse();
                applicationSource.IsDisposalComplete.Should().BeFalse();
                source.IsDisposalComplete.Should().BeFalse();
            }
            else
            {
                (await checkpoint.Entered.Task.WaitAsync(TestWait)).Should().BeSameAs(originalOwner);
                await checkpoint.Completed.Task.WaitAsync(TestWait);
                await EventuallyAsync(async () => (await ReadCapacityCleanupEvidenceAsync(database, job))
                    == (true, true, "transactional_rollback"));
                await EventuallyAsync(() => Task.FromResult(runtime.Snapshot.Control!.Executions == 0));
                originalOwner.CleanupConfirmed.Should().BeTrue();
                var reopen = () => originalOwner.Enter();
                reopen.Should().Throw<InvalidOperationException>("only the final native join closes the original owner");
                await runtime.StopAsync().WaitAsync(TestWait);
                await runtime.Completion.WaitAsync(TestWait);
            }
            verified = true;
        }
        finally
        {
            auxiliary.Release.TrySetResult();
            callback.Release.TrySetResult();
            if (!lockReleased) await locked.RollbackAsync();
            if (actualAuxiliary is not null)
                await actualAuxiliary.WaitAsync(TestWait)
                    .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);
            if (borrowed is not null && disposal is not null) borrowed.StateChange -= disposal;
            await privateCallback.DisposeAsync();
            runtime.RequestStop();
            if (!dirty && verified)
            {
                await runtime.StopAsync().WaitAsync(TestWait);
                await runtime.Completion.WaitAsync(TestWait);
                await DisposePinnedSourceAsync(source);
            }
            else
            {
                using var finite = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
                try { await runtime.StopAsync(finite.Token); }
                catch (OperationCanceledException) when (finite.IsCancellationRequested) { }
                await EventuallyAsync(() => Task.FromResult(AuxiliaryCleanupStockJoined(runtime)
                    && runtime.Snapshot.Control!.CleanupConfirmed
                    && runtime.Snapshot.Maintenance.All(component => component.ActualJoined)));
            }
        }
    }

    private sealed class CapacityCleanupCheckpointState(FacadeCleanupSignals callback)
    {
        internal JobApplicationSourceOwner? OriginalOwner { get; set; }
        internal IUnitOfWork? OriginalUnit { get; set; }
        internal Task? OriginalAuxiliary { get; set; }
        internal FacadeCleanupSignals Callback { get; } = callback;
        internal TaskCompletionSource<JobApplicationSourceOwner> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class CapacityCleanupCheckpointObserver(CapacityCleanupCheckpointState state, IUnitOfWork unit)
        : IPipelineBehavior<RecordBackgroundJobCapacityCleanupCommand, Result<None>>
    {
        public async Task<Result<None>> Handle(RecordBackgroundJobCapacityCleanupCommand request,
            RequestHandlerDelegate<Result<None>> next, CancellationToken cancellationToken)
        {
            var owner = JobApplicationSourceOwner.Current!;
            state.Entered.TrySetResult(owner);
            try
            {
                owner.Should().BeSameAs(state.OriginalOwner);
                request.SourceOwner.Should().BeSameAs(owner);
                request.Evidence.Should().Be(BackgroundJobCapacityCleanupEvidence.TransactionalRollback);
                unit.Should().NotBeSameAs(state.OriginalUnit);
                state.OriginalAuxiliary!.IsCompleted.Should().BeTrue();
                AuxiliaryCleanupResources(owner).Should().BeNull();
                state.Callback.Joined.Task.IsCompletedSuccessfully.Should().BeTrue();
                request.RequireOwner(unit);
                var result = await next();
                state.Completed.TrySetResult();
                return result;
            }
            catch (Exception failure)
            {
                state.Completed.TrySetException(new InvalidOperationException(
                    $"Capacity checkpoint observer failed: type={failure.GetType().FullName};sqlstate={(failure as PostgresException)?.SqlState ?? "none"};constraint={(failure as PostgresException)?.ConstraintName ?? "none"};stack={failure.StackTrace}"));
                throw;
            }
        }
    }

    private static async Task<(bool Cleanup, bool Released, string? Reason)> ReadCapacityCleanupEvidenceAsync(
        DisposableSchemaDatabase database, BackgroundJobId job)
    {
        await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var transaction = await connection.BeginTransactionAsync();
        await EducationSchemaSeed.AnnounceAsync(connection, transaction, SchemaFixture.TenantA, null);
        await using var command = new NpgsqlCommand("""
            SELECT a.transactional_cleanup_at IS NOT NULL, r.released_at IS NOT NULL, r.release_reason
            FROM background_job_attempts a JOIN background_job_reservations r
              ON r.tenant_id=a.tenant_id AND r.job_id=a.job_id AND r.attempt_id=a.id
            WHERE a.job_id=@job AND a.stage='normal' AND a.ordinal=1
            """, connection, transaction);
        command.Parameters.AddWithValue("job", job.Value);
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        return (reader.GetBoolean(0), reader.GetBoolean(1), reader.IsDBNull(2) ? null : reader.GetString(2));
    }
}
