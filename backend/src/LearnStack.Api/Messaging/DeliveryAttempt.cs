using LearnStack.Infrastructure.Messaging;

namespace LearnStack.Api.Messaging;

/// <summary>The retained task owns construction, callbacks and the actual application scope.</summary>
internal sealed class DeliveryAttempt
{
    private readonly DeliveryExecutionControl _control = new();

    public DeliveryAttempt(DeliveryExecutor executor, DeliveryClaim claim)
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(claim);
        PartitionKey = claim.PartitionKey;
        Completion = Task.Run(() => executor.ExecuteControlledAsync(claim, _control), CancellationToken.None);
        // Observe even an unexpected fault outside the executor's owned-scope
        // boundary after the scheduler has already stopped waiting.
        _ = Completion.ContinueWith(static completed => { _ = completed.Exception; },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    public string PartitionKey { get; }
    public bool CommitAcknowledged => _control.Gate.CommitAcknowledged;
    public bool CommitStarted => _control.Gate.CommitStarted;
    public Task<DeliveryExecutionResult> Completion { get; }
    public void RequestCancellation() => _control.RequestCancellation();
}
