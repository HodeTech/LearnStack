using System.Diagnostics.CodeAnalysis;
using LearnStack.SharedKernel.Messaging;

namespace LearnStack.Api.Messaging;

/// <summary>Owns callback construction, cancellation callbacks and scope cleanup as one task.</summary>
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The owned task disposes its cancellation source only after callback and scope cleanup; caller disposal could invalidate a live attempt.")]
internal sealed class OutboxPublishAttempt
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cancellation = new();
    private Task? _cancelCallbacks;
    private bool _finishing;

    public OutboxPublishAttempt(IServiceScopeFactory scopes, IntegrationEventEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(envelope);
        PartitionKey = envelope.PartitionKey;
        // Resolution and even the synchronous prefix of PublishAsync belong to the
        // retained task. Neither may monopolize the dispatcher's bounded wait.
        Completion = Task.Run(() => PublishAsync(scopes, envelope), CancellationToken.None);
    }

    public string PartitionKey { get; }
    public Task<bool> Completion { get; }

    public void RequestCancellation()
    {
        lock (_gate)
        {
            if (_finishing || _cancelCallbacks is not null) return;
            // CancelAsync changes token state immediately but runs registered
            // callbacks asynchronously. A blocking registration cannot block StopAsync.
            _cancelCallbacks = _cancellation.CancelAsync();
        }
    }

    private async Task<bool> PublishAsync(IServiceScopeFactory scopes, IntegrationEventEnvelope envelope)
    {
        AsyncServiceScope? scope = null;
        var succeeded = false;
        try
        {
            scope = scopes.CreateAsyncScope();
            var bus = scope.Value.ServiceProvider.GetRequiredService<IEventBus>();
            await bus.PublishAsync(envelope, _cancellation.Token).ConfigureAwait(false);
            succeeded = !_cancellation.IsCancellationRequested;
        }
        catch (Exception)
        {
            // Provider diagnostics and payloads never cross this dispatch boundary.
            succeeded = false;
        }
        finally
        {
            Task? callbacks;
            lock (_gate)
            {
                _finishing = true;
                callbacks = _cancelCallbacks;
            }

            if (callbacks is not null)
            {
                try { await callbacks.ConfigureAwait(false); }
                catch (Exception) { succeeded = false; }
            }

            try
            {
                if (scope is { } ownedScope) await ownedScope.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                succeeded = false;
            }
            finally
            {
                _cancellation.Dispose();
            }
        }

        return succeeded;
    }
}
