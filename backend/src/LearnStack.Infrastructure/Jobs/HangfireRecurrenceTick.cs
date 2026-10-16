using Hangfire;

namespace LearnStack.Infrastructure.Jobs;

/// <summary>Finite tick reference only. Durable recurrence materialization belongs to Step 8.</summary>
[AutomaticRetry(Attempts = 0)]
public sealed class HangfireRecurrenceTick
{
    private readonly HangfireInvocationContract.BoundInvocation _invocation;

    internal HangfireRecurrenceTick(HangfireInvocationContract.BoundInvocation invocation) => _invocation = invocation;

    public Task RunAsync(string recurrenceKey, long revision) => _invocation.RunTickAsync(recurrenceKey, revision);
}
