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
        BackgroundJobRecurrenceRegistry recurrences) : JobActivator
    {
        public override JobActivatorScope BeginScope(PerformContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            var job = context.BackgroundJob.Job;
            Validate(job, pool, recurrences);
            return new ClosedScope(new BoundInvocation(job, routing, executor));
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

    internal sealed class BoundInvocation(Job job, PostgresBackgroundJobRoutingStore routing, JobExecutor executor)
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
            var claim = await routing.TryClaimAsync(id, generation).ConfigureAwait(false);
            if (claim is null) return;
            if (claim.Queue != job.Queue) throw Refused();
            // Step 4 supplies transport and committed admission only. Step 5
            // extends JobExecutor with the business participant/completion owner.
            _ = await executor.ExecuteAsync(claim).ConfigureAwait(false);
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
