using System.Reflection;
using FluentAssertions;
using Hangfire;
using Hangfire.PostgreSql;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class HangfireGenerationTests
{
    private static readonly TimeSpan WorkCallbackWait = TimeSpan.FromMinutes(1);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_bound_routing_or_scope_cleanup_outlives_stock_join_and_blocks_replacement(bool holdScopeCleanup)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var blocked = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString);
        var positive = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString);
        var fixture = BackgroundJobRuntimeFixture.Shared;
        var probe = new BackgroundJobFixtureProbe();
        var roots = fixture.RootConstructions;
        var gate = new BackgroundJobRuntimeGate(fixture.Registry.Infrastructure);
        await using var source = Source(database);
        var cleanup = new WorkCleanupSignals();
        var observation = new BackgroundJobAdmissionObservation
        {
            OnRead = (_query, services) =>
            {
                if (holdScopeCleanup) services.GetRequiredService<WorkHeldReadCleanup>();
                return Task.CompletedTask;
            },
        };
        await using var host = fixture.Host(database, probe, configure: services =>
        {
            observation.Configure(services);
            services.AddScoped(_ => new WorkHeldReadCleanup(cleanup));
            RegisterWorkRouting(services, source, fixture, gate);
        });
        using var globals = new GlobalFilterSnapshot();
        var filters = HangfireRuntimeFilters.Create();
        var provider = HangfireStorageBootstrap.Create(source, fixture.Registry.Infrastructure, new HangfireBoundedLogProvider(), gate);
        var pool = fixture.Registry.Infrastructure.Pools["transactional"];
        var owner = WorkOwner(host.Services, provider, new(provider), filters, pool, gate);
        await using var locker = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.MigrationConnectionString);
        await using var transaction = await locker.BeginTransactionAsync();
        var released = false;
        Task? running = null;
        owner.Start();
        try
        {
            await EventuallyAsync(() => Task.FromResult(owner.Snapshot.State == HangfirePoolOwnerState.Running));
            var generation = WorkOwnerGeneration(owner);
            if (!holdScopeCleanup)
                await SchemaQueries.ExecuteAsync(locker, transaction,
                    "LOCK TABLE public.background_jobs IN ACCESS EXCLUSIVE MODE");
            using var contextConnection = provider.GetConnection();
            var activator = WorkActivator(host.Services, pool, fixture.Registry.Infrastructure, generation, gate);
            using var scope = activator.BeginScope(ActivationContext(contextConnection, pool, blocked));
            var invocation = (HangfireJobWakeup)scope.Resolve(typeof(HangfireJobWakeup));
            // A test-owned scope invokes the real closed BoundInvocation. It is
            // independent of stock child threads, so their actual join cannot
            // stand in for this routing/scope-cleanup task's completion.
            running = invocation.RunAsync(blocked, 1);
            if (holdScopeCleanup) await cleanup.Entered.Task.WaitAsync(TestWait);
            else await EventuallyAsync(() => HasBlockedRoutingAsync(source));
            generation.WorkSnapshot.Should().Be(new HangfireGenerationWorkSnapshot(1, 0, false));
            var privateToken = WorkEntry(generation).Token;

            generation.RequestDrain(HangfireGenerationReason.HeartbeatTransient);
            await EventuallyAsync(() => Task.FromResult(generation.Snapshot.OuterJoined));
            generation.Snapshot.NormalChildrenJoined.Should().BeTrue();
            generation.Snapshot.CleanupComplete.Should().BeFalse();
            generation.WorkSnapshot.Should().Be(new HangfireGenerationWorkSnapshot(1, 0, true));
            privateToken.IsCancellationRequested.Should().BeFalse("local drain closes starts without canceling admitted work");
            privateToken.WaitHandle.Should().NotBeNull();
            running.IsCompleted.Should().BeFalse();
            generation.Completion.IsCompleted.Should().BeFalse("the actual stock join has not joined owned framework work");
            owner.Completion.IsCompleted.Should().BeFalse();
            owner.Snapshot.Generations.Should().Be(1);
            owner.Snapshot.State.Should().Be(HangfirePoolOwnerState.Draining);
            WorkOwnerGeneration(owner).Should().BeSameAs(generation);
            await AssertServersAsync(source);

            await transaction.RollbackAsync();
            released = true;
            cleanup.Release.TrySetResult();
            await running.WaitAsync(TestWait);
            await generation.Completion.WaitAsync(TestWait);
            AssertJoined(generation);
            generation.WorkSnapshot.Should().Be(new HangfireGenerationWorkSnapshot(0, 0, true));
            var tokenHandle = () => privateToken.WaitHandle;
            tokenHandle.Should().Throw<ObjectDisposedException>("only actual positive cleanup disposes the private source");
            await AssertPendingJobAsync(database, blocked, expectsRouting: true);
            if (holdScopeCleanup)
            {
                cleanup.Joined.Task.IsCompletedSuccessfully.Should().BeTrue();
                observation.ReadScopeDisposed.Should().BeTrue();
            }

            // The real five-second replacement backoff and a native wakeup
            // demonstrate recovery after, rather than during, actual cleanup.
            await EventuallyAsync(() => Task.FromResult(owner.Snapshot is
            { State: HangfirePoolOwnerState.Running, Generations: 2 }));
            WorkOwnerGeneration(owner).Should().NotBeSameAs(generation);
            var vendor = CreateWakeup(provider, filters, pool, positive);
            await EventuallyAsync(async () => await StateAsync(source, vendor) == "Succeeded");
            (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, positive)).Should().Be(1);
            probe.Constructions.Should().BeEmpty("Step 4 commits admission without constructing business handlers");
            fixture.RootConstructions.Should().Be(roots);
        }
        finally
        {
            using var callerBudget = new CancellationTokenSource(TestWait);
            if (!released) await transaction.RollbackAsync(callerBudget.Token);
            cleanup.Release.TrySetResult();
            gate.StopStarts();
            owner.RequestStop();
            await Task.WhenAll((running ?? Task.CompletedTask).WaitAsync(callerBudget.Token),
                owner.StopAsync(callerBudget.Token));
        }
    }

    [Fact]
    public async Task A_native_admission_preserves_private_tokens_on_local_drain_and_retains_host_cancellation_callbacks()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString);
        var fixture = BackgroundJobRuntimeFixture.Shared;
        var probe = new BackgroundJobFixtureProbe();
        var roots = fixture.RootConstructions;
        var gate = new BackgroundJobRuntimeGate(fixture.Registry.Infrastructure);
        await using var source = Source(database);
        var admission = new WorkAdmissionSignals();
        var observation = new BackgroundJobAdmissionObservation
        {
            OnRead = async (_, _) =>
            {
                admission.ReadEntered.TrySetResult();
                await admission.ReadRelease.Task.ConfigureAwait(false);
            },
        };
        await using var host = fixture.Host(database, probe, configure: services =>
        {
            observation.Configure(services);
            services.AddSingleton(admission);
            services.AddScoped<AdmitBackgroundJobAttemptCommandHandler>();
            services.RemoveAll<IRequestHandler<AdmitBackgroundJobAttemptCommand, Result<None>>>();
            services.AddScoped<IRequestHandler<AdmitBackgroundJobAttemptCommand, Result<None>>, WorkObservedAdmission>();
            RegisterWorkRouting(services, source, fixture, gate);
        });
        using var globals = new GlobalFilterSnapshot();
        var filters = HangfireRuntimeFilters.Create();
        var provider = HangfireStorageBootstrap.Create(source, fixture.Registry.Infrastructure, new HangfireBoundedLogProvider(), gate);
        var pool = fixture.Registry.Infrastructure.Pools["transactional"];
        var generation = Generation(host.Services, provider, new(provider), filters, pool, gate);
        using var callbackRelease = new ManualResetEventSlim();
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration registration = default;
        generation.Start();
        try
        {
            (await generation.Ready.WaitAsync(TestWait)).Should().BeTrue();
            var vendor = CreateWakeup(provider, filters, pool, job);
            await admission.ReadEntered.Task.WaitAsync(TestWait);
            var owned = WorkEntry(generation);
            var privateToken = owned.Token;
            registration = privateToken.Register(() =>
            {
                callbackEntered.TrySetResult();
                if (!callbackRelease.Wait(WorkCallbackWait)) throw new TimeoutException("The owned private callback was not released.");
            });
            // Register before the next admission source links to private work.
            // Its closed propagation can therefore occur before this deliberately
            // blocking callback; no callback-order-independent SLA is asserted.
            admission.ReadRelease.TrySetResult();
            await admission.Entered.Task.WaitAsync(TestWait);
            generation.RequestDrain(HangfireGenerationReason.HeartbeatTransient);
            generation.WorkSnapshot.Should().Be(new HangfireGenerationWorkSnapshot(1, 0, true));
            privateToken.IsCancellationRequested.Should().BeFalse();
            admission.Token.IsCancellationRequested.Should().BeFalse("the actual app admission source links only to private work and its SQL deadline");
            callbackEntered.Task.IsCompleted.Should().BeFalse();
            (await StateAsync(source, vendor)).Should().Be("Processing");

            gate.StopStarts();
            await Task.Run(generation.RequestHostStop).WaitAsync(TestWait);
            await callbackEntered.Task.WaitAsync(TestWait);
            privateToken.IsCancellationRequested.Should().BeTrue();
            admission.Release.TrySetResult();
            await EventuallyAsync(() => Task.FromResult(admission.Token.IsCancellationRequested
                && generation.WorkSnapshot.Retained == 1));
            generation.WorkSnapshot.Should().Be(new HangfireGenerationWorkSnapshot(0, 1, true));
            generation.Completion.IsCompleted.Should().BeFalse();
            privateToken.WaitHandle.Should().NotBeNull("the actual blocked cancellation callback still owns its source");

            using var callerBudget = new CancellationTokenSource();
            await callerBudget.CancelAsync();
            Func<Task> wait = () => generation.StopAsync(callerBudget.Token);
            await wait.Should().ThrowAsync<OperationCanceledException>();
            generation.Completion.IsCompleted.Should().BeFalse();
            callbackRelease.Set();
            await generation.Completion.WaitAsync(TestWait);
            AssertJoined(generation);
            generation.WorkSnapshot.Should().Be(new HangfireGenerationWorkSnapshot(0, 0, true));
            var tokenHandle = () => privateToken.WaitHandle;
            tokenHandle.Should().Throw<ObjectDisposedException>();
            await AssertPendingJobAsync(database, job, expectsRouting: true);
            probe.Constructions.Should().BeEmpty();
            fixture.RootConstructions.Should().Be(roots);
        }
        finally
        {
            admission.ReadRelease.TrySetResult();
            admission.Release.TrySetResult();
            callbackRelease.Set();
            using var callerBudget = new CancellationTokenSource(TestWait);
            gate.StopStarts();
            await generation.StopAsync(callerBudget.Token);
            await registration.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_throwing_read_scope_stays_strongly_owned_after_wrapper_return_and_prevents_replacement()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString);
        var fixture = BackgroundJobRuntimeFixture.Shared;
        var probe = new BackgroundJobFixtureProbe();
        var roots = fixture.RootConstructions;
        var gate = new BackgroundJobRuntimeGate(fixture.Registry.Infrastructure);
        await using var source = Source(database);
        var observed = new WorkFailedCleanupSignals();
        await using var host = fixture.Host(database, probe, configure: services =>
        {
            services.AddSingleton(observed);
            services.AddScoped<ReadBackgroundJobEnvelopeQueryHandler>();
            services.RemoveAll<IRequestHandler<ReadBackgroundJobEnvelopeQuery, Result<BackgroundJobEnvelope>>>();
            services.AddScoped<IRequestHandler<ReadBackgroundJobEnvelopeQuery, Result<BackgroundJobEnvelope>>, WorkCleanupObservedRead>();
            services.AddScoped<WorkThrowingReadCleanup>();
            RegisterWorkRouting(services, source, fixture, gate);
        });
        using var globals = new GlobalFilterSnapshot();
        var filters = HangfireRuntimeFilters.Create();
        var provider = HangfireStorageBootstrap.Create(source, fixture.Registry.Infrastructure, new HangfireBoundedLogProvider(), gate);
        var pool = fixture.Registry.Infrastructure.Pools["transactional"];
        var owner = WorkOwner(host.Services, provider, new(provider), filters, pool, gate);
        HangfireGeneration? generation = null;
        HangfireGenerationWork.Invocation? retainedWork = null;
        Task? running = null;
        owner.Start();
        try
        {
            await EventuallyAsync(() => Task.FromResult(owner.Snapshot.State == HangfirePoolOwnerState.Running));
            generation = WorkOwnerGeneration(owner);
            using var contextConnection = provider.GetConnection();
            var activator = WorkActivator(host.Services, pool, fixture.Registry.Infrastructure, generation, gate);
            using var scope = activator.BeginScope(ActivationContext(contextConnection, pool, job));
            var wrapper = (HangfireJobWakeup)scope.Resolve(typeof(HangfireJobWakeup));
            running = wrapper.RunAsync(job, 1);
            await running.WaitAsync(TestWait);
            running.IsCompletedSuccessfully.Should().BeTrue("a returned transport wrapper cannot erase failed actual cleanup");
            generation.WorkSnapshot.Should().Be(new HangfireGenerationWorkSnapshot(0, 1, false));
            retainedWork = WorkEntry(generation);
            var admission = WorkAdmissionTask(retainedWork);
            admission.IsCompletedSuccessfully.Should().BeTrue();
            var result = await admission;
            result.Outcome.Should().Be(JobStartExecutionOutcome.Failed);
            result.Receipt.Should().BeNull();
            result.CleanupFailed.Should().BeTrue();
            var retained = result.RetainedCleanup.Should().ContainSingle().Which;
            var retainedScope = retained.Scope
                ?? throw new InvalidOperationException("The failed actual read scope is not retained.");
            retainedScope.ServiceProvider.Should().BeSameAs(observed.Provider);
            retained.Unit.Should().BeSameAs(observed.Unit);
            retained.Deadline.Should().BeSameAs(observed.Deadline);
            WorkScopeDisposables(retainedScope.ServiceProvider).Should()
                .Contain(item => ReferenceEquals(item, observed.Throwing), "the exact throwing disposable remains owned by the retained scope");
            observed.DisposeCalls.Should().Be(1);
            observed.CleanupSucceeded.Should().BeFalse();

            generation.RequestDrain(HangfireGenerationReason.HeartbeatTransient);
            await EventuallyAsync(() => Task.FromResult(generation.Snapshot.OuterJoined));
            generation.Snapshot.NormalChildrenJoined.Should().BeTrue();
            generation.Snapshot.CleanupComplete.Should().BeFalse();
            generation.WorkSnapshot.Should().Be(new HangfireGenerationWorkSnapshot(0, 1, true));
            generation.Completion.IsCompleted.Should().BeFalse();
            owner.Completion.IsCompleted.Should().BeFalse();
            owner.Snapshot.Generations.Should().Be(1);
            WorkOwnerGeneration(owner).Should().BeSameAs(generation);
            WorkAdmissionTask(retainedWork).Should().BeSameAs(admission);
            retainedWork.Token.IsCancellationRequested.Should().BeFalse();
            retainedWork.Token.WaitHandle.Should().NotBeNull();
            await AssertPendingJobAsync(database, job, expectsRouting: true);
            await AssertServersAsync(source);
            probe.Constructions.Should().BeEmpty();
            fixture.RootConstructions.Should().Be(roots);
        }
        finally
        {
            using var callerBudget = new CancellationTokenSource(TestWait);
            gate.StopStarts();
            owner.RequestStop();
            if (running is not null) await running.WaitAsync(callerBudget.Token);
            // Fixture teardown only: this synthetic disposable deliberately
            // succeeds on its second explicit call. Join it and the exact unit
            // before confirming cleanup on the test-owned framework entry.
            // This supplies no production repair/recovery API or fault inference.
            if (observed.Throwing is { } throwing)
                await throwing.DisposeAsync().AsTask().WaitAsync(callerBudget.Token);
            if (observed.Unit is { } unit)
                await unit.DisposeAsync().AsTask().WaitAsync(callerBudget.Token);
            if (generation?.WorkSnapshot.Retained == 1)
            {
                retainedWork ??= WorkEntry(generation);
                await retainedWork.CompleteAsync(cleanupConfirmed: true).WaitAsync(callerBudget.Token);
            }
            await owner.StopAsync(callerBudget.Token);
        }
    }

    [Fact]
    public async Task Four_retained_framework_entries_refuse_native_app_starts_without_spending_attempts()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString);
        var fixture = BackgroundJobRuntimeFixture.Shared;
        var probe = new BackgroundJobFixtureProbe();
        var roots = fixture.RootConstructions;
        var gate = new BackgroundJobRuntimeGate(fixture.Registry.Infrastructure);
        await using var source = Source(database);
        await using var host = fixture.Host(database, probe,
            configure: services => RegisterWorkRouting(services, source, fixture, gate));
        using var globals = new GlobalFilterSnapshot();
        var filters = HangfireRuntimeFilters.Create();
        var provider = HangfireStorageBootstrap.Create(source, fixture.Registry.Infrastructure, new HangfireBoundedLogProvider(), gate);
        var pool = fixture.Registry.Infrastructure.Pools["transactional"];
        var generation = Generation(host.Services, provider, new(provider), filters, pool, gate);
        var held = new List<HangfireGenerationWork.Invocation>();
        generation.Start();
        try
        {
            (await generation.Ready.WaitAsync(TestWait)).Should().BeTrue();
            // Step 5 has no business overrun producer yet. These are explicitly
            // test-owned framework occupancy entries on an actual generation;
            // the refused and restored app starts use real native workers/SQL.
            for (var index = 0; index < BackgroundJobRuntimeOptions.RetainedStopThreshold; index++)
            {
                var entry = generation.TryBeginWork()
                    ?? throw new InvalidOperationException("The ready fixture could not own framework work.");
                held.Add(entry);
                await entry.CompleteAsync(cleanupConfirmed: false);
            }
            generation.WorkSnapshot.Should().Be(new HangfireGenerationWorkSnapshot(0, 4, false));
            generation.TryAdmitInvocation().Should().BeFalse();
            generation.TryBeginWork().Should().BeNull("four retained entries close starts despite a spare workers-plus-four slot");
            gate.IsOpen.Should().BeTrue();
            var refused = CreateWakeup(provider, filters, pool, job);
            await EventuallyAsync(async () => await StateAsync(source, refused) == "Failed");
            await AssertPendingJobAsync(database, job, expectsRouting: false);

            await held[0].CompleteAsync(cleanupConfirmed: true);
            held.RemoveAt(0);
            generation.TryAdmitInvocation().Should().BeTrue();
            var positive = CreateWakeup(provider, filters, pool, job);
            await EventuallyAsync(async () => await StateAsync(source, positive) == "Succeeded");
            (await BackgroundJobRuntimeRows.CountAttemptsAsync(database.AppConnectionString, job)).Should().Be(1);
            generation.WorkSnapshot.Should().Be(new HangfireGenerationWorkSnapshot(0, 3, false));
            probe.Constructions.Should().BeEmpty();
            fixture.RootConstructions.Should().Be(roots);

            generation.RequestDrain(HangfireGenerationReason.HeartbeatTransient);
            await EventuallyAsync(() => Task.FromResult(generation.Snapshot.OuterJoined));
            generation.Snapshot.NormalChildrenJoined.Should().BeTrue();
            generation.Completion.IsCompleted.Should().BeFalse("actual stock completion cannot release retained framework ownership");
            generation.WorkSnapshot.Should().Be(new HangfireGenerationWorkSnapshot(0, 3, true));
            foreach (var entry in held) entry.Token.IsCancellationRequested.Should().BeFalse();
            await Task.WhenAll(held.Select(entry => entry.CompleteAsync(cleanupConfirmed: true))).WaitAsync(TestWait);
            held.Clear();
            await generation.Completion.WaitAsync(TestWait);
            AssertJoined(generation);
            await AssertServersAsync(source);
        }
        finally
        {
            using var callerBudget = new CancellationTokenSource(TestWait);
            gate.StopStarts();
            generation.RequestHostStop();
            await Task.WhenAll(held.Select(entry => entry.CompleteAsync(cleanupConfirmed: true))).WaitAsync(callerBudget.Token);
            await generation.StopAsync(callerBudget.Token);
        }
    }

    private static void RegisterWorkRouting(IServiceCollection services, PrivilegedDataSource source,
        BackgroundJobRuntimeFixture fixture, BackgroundJobRuntimeGate gate)
    {
        services.RemoveAll<PostgresBackgroundJobRoutingStore>();
        services.AddSingleton(new PostgresBackgroundJobRoutingStore(source, fixture.Registry,
            new BackgroundJobRuntimeOptions(), new SystemGuidFactory(), gate));
    }

    private static HangfirePoolOwner WorkOwner(IServiceProvider services, PostgreSqlStorage provider,
        HangfireComponentInventory inventory, HangfireRuntimeFilters filters,
        BackgroundJobWorkerPoolRegistration pool, BackgroundJobRuntimeGate gate) =>
        new(provider, inventory, filters, HangfireInvocationContract.CreateActivator(pool,
                services.GetRequiredService<BackgroundJobDefinitionRegistry>().Infrastructure,
                services.GetRequiredService<PostgresBackgroundJobRoutingStore>(), services.GetRequiredService<JobExecutor>(),
                BackgroundJobRecurrenceRegistry.CreateEmpty(), gate), pool, gate,
            services.GetRequiredService<BackgroundJobRuntimeOptions>(), TimeProvider.System);

    private static JobActivator WorkActivator(IServiceProvider services, BackgroundJobWorkerPoolRegistration pool,
        BackgroundJobInfrastructureRegistry topology, HangfireGeneration generation, BackgroundJobRuntimeGate gate) =>
        HangfireInvocationContract.BindGeneration(HangfireInvocationContract.CreateActivator(pool, topology,
            services.GetRequiredService<PostgresBackgroundJobRoutingStore>(), services.GetRequiredService<JobExecutor>(),
            BackgroundJobRecurrenceRegistry.CreateEmpty(), gate), generation);

    private static HangfireGeneration WorkOwnerGeneration(HangfirePoolOwner owner) =>
        typeof(HangfirePoolOwner).GetField("_generation", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(owner) as HangfireGeneration
        ?? throw new InvalidOperationException("The actual generation has not been published.");

    private static HangfireGenerationWork.Invocation WorkEntry(HangfireGeneration generation)
    {
        // Identity-only observation under the actual work owner's memory lock.
        // No field, token, task, completion result or generation is replaced.
        var work = typeof(HangfireGeneration).GetField("_work", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(generation) as HangfireGenerationWork
            ?? throw new InvalidOperationException("The actual generation work owner is missing.");
        var sync = typeof(HangfireGenerationWork).GetField("_sync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(work) ?? throw new InvalidOperationException("The actual work synchronization is missing.");
        lock (sync)
        {
            var active = typeof(HangfireGenerationWork).GetField("_active", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(work) as IEnumerable<HangfireGenerationWork.Invocation>
                ?? throw new InvalidOperationException("The actual work inventory is missing.");
            return active.Single();
        }
    }

    private static Task<JobStartExecutionResult> WorkAdmissionTask(HangfireGenerationWork.Invocation invocation) =>
        typeof(HangfireGenerationWork.Invocation).GetField("_admission", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(invocation) as Task<JobStartExecutionResult>
        ?? throw new InvalidOperationException("The actual admission task is not retained.");

    private static IEnumerable<object> WorkScopeDisposables(IServiceProvider provider) =>
        provider.GetType().GetField("_disposables", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(provider) as IEnumerable<object>
        ?? throw new InvalidOperationException("The actual failed DI scope's disposables are not retained.");

    private static CancellationTokenSource WorkCancellationSource(CancellationToken token) =>
        typeof(CancellationToken).GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(field => field.FieldType == typeof(CancellationTokenSource)).GetValue(token) as CancellationTokenSource
        ?? throw new InvalidOperationException("The actual read deadline source is missing.");

    private sealed class WorkCleanupSignals
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Joined { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class WorkHeldReadCleanup(WorkCleanupSignals state) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            state.Entered.TrySetResult();
            await state.Release.Task.ConfigureAwait(false);
            state.Joined.TrySetResult();
        }
    }

    private sealed class WorkAdmissionSignals
    {
        internal TaskCompletionSource ReadEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReadRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationToken Token { get; set; }
    }

    private sealed class WorkObservedAdmission(AdmitBackgroundJobAttemptCommandHandler actual, WorkAdmissionSignals state)
        : IRequestHandler<AdmitBackgroundJobAttemptCommand, Result<None>>
    {
        public async Task<Result<None>> Handle(AdmitBackgroundJobAttemptCommand request, CancellationToken cancellationToken)
        {
            state.Token = cancellationToken;
            state.Entered.TrySetResult();
            await state.Release.Task.ConfigureAwait(false);
            return await actual.Handle(request, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class WorkFailedCleanupSignals
    {
        internal IServiceProvider? Provider { get; set; }
        internal IUnitOfWork? Unit { get; set; }
        internal CancellationTokenSource? Deadline { get; set; }
        internal WorkThrowingReadCleanup? Throwing { get; set; }
        internal int DisposeCalls { get; set; }
        internal bool CleanupSucceeded { get; set; }
    }

    private sealed class WorkCleanupObservedRead(ReadBackgroundJobEnvelopeQueryHandler actual,
        IServiceProvider services, IUnitOfWork unit, WorkFailedCleanupSignals state)
        : IRequestHandler<ReadBackgroundJobEnvelopeQuery, Result<BackgroundJobEnvelope>>
    {
        public async Task<Result<BackgroundJobEnvelope>> Handle(ReadBackgroundJobEnvelopeQuery request,
            CancellationToken cancellationToken)
        {
            state.Provider = services;
            state.Unit = unit;
            state.Deadline = WorkCancellationSource(cancellationToken);
            state.Throwing = services.GetRequiredService<WorkThrowingReadCleanup>();
            (await BackgroundJobRuntimeRows.ScalarInUnitAsync(unit, "SELECT current_user")).Should().Be("learnstack_app");
            return await actual.Handle(request, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class WorkThrowingReadCleanup(WorkFailedCleanupSignals state) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            state.DisposeCalls++;
            if (state.DisposeCalls == 1)
            {
                state.Unit!.Transaction.Should().BeNull("the actual read transaction resolved before scope disposal");
                throw new InvalidOperationException("Synthetic scoped disposal failure for ownership regression.");
            }
            state.CleanupSucceeded = true;
            return ValueTask.CompletedTask;
        }
    }
}
