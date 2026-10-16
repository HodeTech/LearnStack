using Hangfire;
using LearnStack.SharedKernel.Jobs;

namespace LearnStack.Infrastructure.Jobs;

/// <summary>Opaque transport entry, constructed only by the closed invocation-bound activator.</summary>
[AutomaticRetry(Attempts = 0)]
public sealed class HangfireJobWakeup
{
    private readonly HangfireInvocationContract.BoundInvocation _invocation;

    internal HangfireJobWakeup(HangfireInvocationContract.BoundInvocation invocation) => _invocation = invocation;

    public Task RunAsync(BackgroundJobId jobId, long wakeGeneration) => _invocation.RunJobAsync(jobId, wakeGeneration);
}
