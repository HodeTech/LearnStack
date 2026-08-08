using LearnStack.SharedKernel.Observability;

namespace LearnStack.Infrastructure.ErrorTracking;

/// <summary>
/// <see cref="IErrorTrackingProvider"/> implementation that discards capture
/// requests silently. Selected by the composition root when
/// <c>DeploymentMode.Development</c> — no external egress, and the local
/// developer sees the exception in their console / IDE without needing
/// Sentry running.
/// </summary>
internal sealed class NoOpErrorTracker : IErrorTrackingProvider
{
    public ValueTask CaptureAsync(
        Exception exception,
        CapturedContext context,
        CancellationToken cancellationToken = default)
    {
        // Guard for parity with the Sentry / LocalFile implementations so a
        // null argument fails the same way in Development as it would in
        // production — a contract bug surfaces locally instead of hiding
        // behind the no-op.
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(context);
        return ValueTask.CompletedTask;
    }
}
