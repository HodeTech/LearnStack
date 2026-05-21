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
        CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}
