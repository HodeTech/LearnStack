using System.Reflection;
using Hangfire;
using Hangfire.Common;
using Hangfire.Server;
using LearnStack.SharedKernel.Jobs;

namespace LearnStack.Infrastructure.Jobs;

/// <summary>Exact closed wire targets and per-pool activation; never performs arbitrary CLR resolution.</summary>
public static class HangfireInvocationContract
{
    internal static MethodInfo JobMethod { get; } = typeof(HangfireJobWakeup).GetMethod(nameof(HangfireJobWakeup.RunAsync),
        BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly, [typeof(BackgroundJobId), typeof(long)])
        ?? throw Refused();
    internal static MethodInfo TickMethod { get; } = typeof(HangfireRecurrenceTick).GetMethod(nameof(HangfireRecurrenceTick.RunAsync),
        BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly, [typeof(string), typeof(long)])
        ?? throw Refused();

    public static Type ResolveType(string typeName) => typeName switch
    {
        HangfireStorageContract.JobWakeupType => typeof(HangfireJobWakeup),
        HangfireStorageContract.RecurrenceTickType => typeof(HangfireRecurrenceTick),
        "LearnStack.SharedKernel.Jobs.BackgroundJobId, LearnStack.SharedKernel" => typeof(BackgroundJobId),
        "System.Int64" => typeof(long),
        "System.String" => typeof(string),
        _ => throw Refused(),
    };

    public static JobActivator CreateActivator(BackgroundJobWorkerPoolRegistration pool,
        BackgroundJobInfrastructureRegistry topology, PostgresBackgroundJobRoutingStore routing,
        JobExecutor executor, BackgroundJobRecurrenceRegistry recurrences) =>
        CreateActivator(pool, topology, routing, executor, recurrences, null);

    internal static JobActivator CreateActivator(BackgroundJobWorkerPoolRegistration pool,
        BackgroundJobInfrastructureRegistry topology, PostgresBackgroundJobRoutingStore routing,
        JobExecutor executor, BackgroundJobRecurrenceRegistry recurrences, BackgroundJobControl? jobs)
    {
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentNullException.ThrowIfNull(routing);
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(recurrences);
        if (!topology.Pools.TryGetValue(pool.Name, out var registered) || !ReferenceEquals(pool, registered)) throw Refused();
        return new ClosedActivator(pool, routing, executor, recurrences, jobs);
    }

    internal static JobActivator BindGeneration(JobActivator activator, HangfireGeneration generation) =>
        activator is ClosedActivator closed ? closed.Bind(generation) : throw Refused();

    internal static void Validate(Job job, BackgroundJobWorkerPoolRegistration pool,
        BackgroundJobRecurrenceRegistry recurrences)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.Queue is null || !pool.Queues.Contains(job.Queue, StringComparer.Ordinal)
            || job.Method.IsStatic || job.Method.IsGenericMethod || !job.Method.IsPublic
            || job.Method.DeclaringType != job.Type || job.Args.Count != 2
            || job.Args[1] is not long generation || generation < 1) throw Refused();
        if (job.Type == typeof(HangfireJobWakeup) && job.Method == JobMethod
            && job.Args[0] is BackgroundJobId id && id.IsInitialized() && id.Value.Version == 7
            && pool.Kind != BackgroundJobWorkerPoolKind.Control) return;
        if (job.Type == typeof(HangfireRecurrenceTick) && job.Method == TickMethod
            && job.Args[0] is string key && pool.Kind == BackgroundJobWorkerPoolKind.Control
            && recurrences.TryGet(key, out var recurrence) && recurrence.Registration.Revision == generation) return;
        throw Refused();
    }

    private static InvalidOperationException Refused() => new("The Hangfire invocation contract refused activation.");

    private sealed class ClosedActivator(BackgroundJobWorkerPoolRegistration pool,
        PostgresBackgroundJobRoutingStore routing, JobExecutor executor,
        BackgroundJobRecurrenceRegistry recurrences, BackgroundJobControl? jobs,
        HangfireGeneration? generation = null) : JobActivator
    {
        internal ClosedActivator Bind(HangfireGeneration owner) => generation is null
            ? new(pool, routing, executor, recurrences, jobs, owner) : throw Refused();

        public override JobActivatorScope BeginScope(PerformContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            var job = context.BackgroundJob.Job;
            Validate(job, pool, recurrences);
            if (generation?.TryAdmitInvocation() == false) throw Refused();
            return new ClosedScope(new BoundInvocation(job, routing, executor, generation, jobs));
        }

        public override object ActivateJob(Type jobType) => throw Refused();
    }

    private sealed class ClosedScope(BoundInvocation invocation) : JobActivatorScope
    {
        private object? _instance;
        public override object Resolve(Type type)
        {
            if (!invocation.IsOpen || type != invocation.Type) throw Refused();
            return _instance ??= type == typeof(HangfireJobWakeup)
                ? new HangfireJobWakeup(invocation) : new HangfireRecurrenceTick(invocation);
        }

        public override void DisposeScope() => invocation.Close();
    }

    internal sealed class BoundInvocation(Job job, PostgresBackgroundJobRoutingStore routing, JobExecutor executor,
        HangfireGeneration? owner = null, BackgroundJobControl? jobs = null)
    {
        private readonly TaskCompletionSource<Task> _operationPublished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task? _operationObserver;
        private Task<bool>? _wrapperWait;
        private bool _cleanupConfirmed;
        private int _entered;
        private int _closed;
        internal Type Type => job.Type;
        internal bool IsOpen => Volatile.Read(ref _closed) == 0;
        internal void Close() => Interlocked.Exchange(ref _closed, 1);

        internal async Task RunJobAsync(BackgroundJobId id, long generation)
        {
            if (Volatile.Read(ref _closed) != 0 || Type != typeof(HangfireJobWakeup) || job.Args[0] is not BackgroundJobId bound || bound != id
                || job.Args[1] is not long expected || expected != generation
                || Interlocked.Exchange(ref _entered, 1) != 0) throw Refused();
            if (owner?.TryAdmitInvocation() == false) return;
            var work = owner?.TryBeginWork();
            if (owner is not null && work is null) return;
            if (work is null)
            {
                // Unbound activation is used only by direct contract fixtures.
                await RunFrameworkAsync(id, generation, null).ConfigureAwait(false);
                return;
            }

            // The active generation roots this exact operation before either
            // scheduling or a possibly blocking synchronous framework prefix.
            work.HoldInvocation(this);
            _operationObserver = ObserveOperationAsync(work);
            _wrapperWait = WaitForOperationAsync(work, owner!.InvocationWait, owner.InvocationTime);
            try
            {
                Task operation;
                if (ExecutionContext.IsFlowSuppressed()) operation = Task.Run(() => RunFrameworkAsync(id, generation, work));
                else
                {
                    using (ExecutionContext.SuppressFlow())
                        operation = Task.Run(() => RunFrameworkAsync(id, generation, work));
                }
                _operationPublished.TrySetResult(operation);
            }
            catch (Exception failure) when (Catchable(failure))
            {
                // No returned task is not a positive no-issued-work witness.
                // The prebound observer and concrete invocation stay retained.
                work.ExpireWait();
            }
            if (await _wrapperWait.ConfigureAwait(false))
                await (await _operationPublished.Task.ConfigureAwait(false)).ConfigureAwait(false);
        }

        private async Task ObserveOperationAsync(HangfireGenerationWork.Invocation work)
        {
            var operation = await _operationPublished.Task.ConfigureAwait(false);
            // Observe faults even after the native wrapper has stopped waiting.
            // Only its actual return and positive cleanup release the work entry.
            await operation.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await work.CompleteAsync(_cleanupConfirmed).ConfigureAwait(false);
        }

        private async Task<bool> WaitForOperationAsync(HangfireGenerationWork.Invocation work,
            TimeSpan timeout, TimeProvider time)
        {
            try
            {
                await _operationObserver!.WaitAsync(timeout, time).ConfigureAwait(false);
                return true;
            }
            catch (Exception failure) when (Catchable(failure))
            {
                // Close later admission and account retained work before giving
                // the stock slot back; cancellation callbacks remain owned.
                work.ExpireWait();
                return false;
            }
        }

        private async Task RunFrameworkAsync(BackgroundJobId id, long generation, HangfireGenerationWork.Invocation? work)
        {
            var cleanupConfirmed = false;
            BackgroundJobExecutionControl? execution = null;
            try
            {
                if (!MayContinue(work)) { cleanupConfirmed = true; return; }
                var token = work?.Token ?? CancellationToken.None;
                var route = routing.TryClaimOwnedAsync(id, generation, token);
                work?.HoldRouting(route);
                var read = await route.ConfigureAwait(false);
                cleanupConfirmed = read.CleanupConfirmed;
                if (!cleanupConfirmed || read.Failed || read.Claim is not { } claim) return;
                if (claim.Queue != job.Queue) throw Refused();
                if (!MayContinue(work)) return;
                // Direct unbound contract fixtures exercise framework admission
                // alone. Actual facade generations always bind the closed owner.
                if (jobs is not null && work is not null)
                {
                    execution = jobs.TryBegin(claim, work);
                    if (execution is null) return;
                    work.HoldExecution(execution);
                }
                var admission = execution is null
                    ? executor.ExecuteAsync(claim, startGate: () => MayContinue(work), cancellationToken: token)
                    : executor.ExecutePublishedAsync(claim, execution.Publication, () => MayContinue(work), token);
                work?.HoldAdmission(admission);
                execution?.HoldAdmission(admission);
                cleanupConfirmed = false;
                var result = await admission.ConfigureAwait(false);
                cleanupConfirmed = !result.CleanupFailed;
                if (!cleanupConfirmed || execution is null) return;
                await execution.WaitForPublicationAsync().ConfigureAwait(false);
                if (result.Outcome == JobStartExecutionOutcome.Started && result.Receipt is { } receipt
                    && !execution.Gate.IsClosed && !execution.Token.IsCancellationRequested)
                {
                    // A local generation drain closes new admission, not an
                    // already acknowledged business execution or its renewal.
                    var business = executor.ExecuteBusinessAsync(receipt, execution.Gate, execution.Token);
                    execution.HoldBusiness(business);
                    work!.HoldBusiness(business);
                    cleanupConfirmed = false;
                    var completed = await business.ConfigureAwait(false);
                    BackgroundJobActualCleanup? actualCleanup = null;
                    if (!completed.CommitAcknowledged && !completed.CleanupFailed && receipt.Envelope.CapacityGroup is not null)
                    {
                        actualCleanup = await work.PrepareCapacityCleanupAsync(execution).ConfigureAwait(false);
                        if (actualCleanup is null) return;
                    }
                    cleanupConfirmed = await jobs!.FinishBusinessAsync(execution, completed, actualCleanup).ConfigureAwait(false);
                }
                else
                {
                    BackgroundJobActualCleanup? actualCleanup = null;
                    if (result.Outcome is JobStartExecutionOutcome.Started or JobStartExecutionOutcome.Indeterminate
                        && (result.Receipt?.Envelope ?? result.ValidatedEnvelope) is { Profile: "transactional", CapacityGroup: not null })
                    {
                        cleanupConfirmed = false;
                        actualCleanup = await work!.PrepareCapacityCleanupAsync(execution).ConfigureAwait(false);
                        if (actualCleanup is null) return;
                    }
                    cleanupConfirmed = await jobs!.FinishAdmissionAsync(execution, result, actualCleanup).ConfigureAwait(false);
                }
            }
            finally
            {
                if (execution is not null)
                    cleanupConfirmed = await execution.JoinAsync(cleanupConfirmed).ConfigureAwait(false);
                _cleanupConfirmed = cleanupConfirmed;
            }
        }

        private bool MayContinue(HangfireGenerationWork.Invocation? work) => owner?.TryAdmitInvocation() != false
            && work?.TryAdmitContinuation() != false;

        private static bool Catchable(Exception failure) => failure.GetType() != typeof(OutOfMemoryException)
            && failure.GetType() != typeof(StackOverflowException);

        internal Task RunTickAsync(string key, long revision)
        {
            if (Volatile.Read(ref _closed) != 0 || Type != typeof(HangfireRecurrenceTick) || job.Args[0] is not string bound || bound != key
                || job.Args[1] is not long expected || expected != revision
                || Interlocked.Exchange(ref _entered, 1) != 0) throw Refused();
            // Refuse rather than claim successful materialization before Step 8.
            throw new InvalidOperationException("Durable recurrence materialization is not active.");
        }
    }
}
