using Hangfire;
using Hangfire.Common;
using Hangfire.States;

namespace LearnStack.Infrastructure.Jobs;

/// <summary>Stock creation of opaque wakeups only; its synchronous call remains bridge-owned.</summary>
public sealed class HangfireWakeupClient
{
    private readonly IBackgroundJobClient _client;

    public HangfireWakeupClient(JobStorage storage, IJobFilterProvider filters)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(filters);
        _client = new BackgroundJobClient(storage, filters) { RetryAttempts = 0 };
    }

    internal HangfireWakeupClient(IBackgroundJobClient client) => _client = client;

    internal string? Create(BackgroundJobBridgeClaim claim)
    {
        var job = new Job(typeof(HangfireJobWakeup), HangfireInvocationContract.JobMethod,
            [claim.JobId, claim.WakeGeneration], claim.Queue);
        IState state = claim.AvailableAfter > claim.DatabaseNow
            ? new ScheduledState(claim.AvailableAfter.UtcDateTime) : new EnqueuedState(claim.Queue);
        return _client.Create(job, state);
    }
}
