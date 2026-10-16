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
        JobExecutor executor, BackgroundJobRecurrenceRegistry recurrences)
    {
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentNullException.ThrowIfNull(routing);
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(recurrences);
        if (!topology.Pools.TryGetValue(pool.Name, out var registered) || !ReferenceEquals(pool, registered)) throw Refused();
        return new ClosedActivator(pool, routing, executor, recurrences);
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
        BackgroundJobRecurrenceRegistry recurrences, HangfireGeneration? generation = null) : JobActivator
    {
        internal ClosedActivator Bind(HangfireGeneration owner) => generation is null
            ? new(pool, routing, executor, recurrences, owner) : throw Refused();

        public override JobActivatorScope BeginScope(PerformContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            var job = context.BackgroundJob.Job;
            Validate(job, pool, recurrences);
            if (generation?.TryAdmitInvocation() == false) throw Refused();
            return new ClosedScope(new BoundInvocation(job, routing, executor, generation));
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
        HangfireGeneration? owner = null)
    {
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
            var cleanupConfirmed = false;
            try
            {
                var token = work?.Token ?? CancellationToken.None;
                var route = routing.TryClaimOwnedAsync(id, generation, token);
                work?.HoldRouting(route);
                var read = await route.ConfigureAwait(false);
                cleanupConfirmed = read.CleanupConfirmed;
                if (!cleanupConfirmed || read.Failed || read.Claim is not { } claim) return;
                if (claim.Queue != job.Queue) throw Refused();
                if (owner?.TryAdmitInvocation() == false) return;
                // Step 4 supplies transport and committed admission only. Step 5
                // extends JobExecutor with the business participant/completion owner.
                var admission = executor.ExecuteAsync(claim, startGate: owner is null ? null : owner.TryAdmitInvocation,
                    cancellationToken: token);
                work?.HoldAdmission(admission);
                cleanupConfirmed = false;
                var result = await admission.ConfigureAwait(false);
                cleanupConfirmed = !result.CleanupFailed;
            }
            finally
            {
                if (work is not null) await work.CompleteAsync(cleanupConfirmed).ConfigureAwait(false);
            }
        }

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
