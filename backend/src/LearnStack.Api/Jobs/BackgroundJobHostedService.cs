using LearnStack.Api.Messaging;
using LearnStack.Infrastructure.Jobs;

namespace LearnStack.Api.Jobs;

/// <summary>Owns API activation and binds the actual shared-source messaging lifetimes.</summary>
public sealed class BackgroundJobHostedService : IHostedService, IDisposable
{
    private readonly BackgroundJobRuntime _runtime;

    public BackgroundJobHostedService(BackgroundJobRuntime runtime, OutboxProcessor outbox, DeliveryProcessor delivery)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(outbox);
        ArgumentNullException.ThrowIfNull(delivery);
        _runtime = runtime;
        runtime.BindMessagingLifetime(outbox.PrivilegedCompletion, delivery.PrivilegedCompletion);
    }

    public Task StartAsync(CancellationToken cancellationToken) => _runtime.StartAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => _runtime.StopAsync(cancellationToken);

    // Host construction/startup can fail before this service's StartAsync.
    // DI disposal still seals that positive no-issued-work path.
    public void Dispose() => _runtime.RequestStop();
}
