using System.Diagnostics;
using LearnStack.Infrastructure.Messaging;
using Npgsql;

namespace LearnStack.Api.Messaging;

/// <summary>One just-in-time claimant with bounded, task-owned publication overruns.</summary>
public sealed partial class OutboxProcessor : BackgroundService
{
    private readonly Lazy<NpgsqlDataSource> _source;
    private readonly OutboxDispatchStore _store;
    private readonly OutboxEnvelopeReader _reader;
    private readonly OutboxEventRegistry _registry;
    private readonly OutboxDispatchOptions _options;
    private readonly TimeProvider _time;
    private readonly IServiceScopeFactory _scopes;
    private readonly OutboxObservation _observation;
    private readonly ILogger<OutboxProcessor> _logger;
    private readonly object _attemptGate = new();
    private readonly List<OutboxPublishAttempt> _attempts = [];
    private OutboxClaimKind _nextClaimKind = OutboxClaimKind.Terminal;

    public OutboxProcessor(
        [FromKeyedServices(OutboxDispatcherDataSource.Key)] Lazy<NpgsqlDataSource> source,
        OutboxDispatchStore store, OutboxEnvelopeReader reader, OutboxEventRegistry registry,
        OutboxDispatchOptions options, TimeProvider time, IServiceScopeFactory scopes,
        OutboxObservation observation, ILogger<OutboxProcessor> logger)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(logger);
        _source = source;
        _store = store;
        _reader = reader;
        _registry = registry;
        _options = options;
        _time = time;
        _scopes = scopes;
        _observation = observation;
        _logger = logger;
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        // .NET 10 runs ExecuteAsync in the background. Startup refusal therefore
        // belongs here, before the host can report that it has started.
        try
        {
            _options.Validate();
            using var timeout = new CancellationTokenSource(_options.SqlTimeout, _time);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            await using var connection = await _source.Value.OpenConnectionAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception)
        {
            // No connection data, provider exception or inner exception escapes boot.
            throw new InvalidOperationException("Outbox dispatcher startup was refused. "
                + "Configure ConnectionStrings:OutboxDispatcher in Npgsql key/value form "
                + "for learnstack_outbox_admin and verify database connectivity.");
        }

        _observation.Started();
        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var blocked = RetainedPartitions();
                var didWork = false;
                try
                {
                    if (OutstandingCount < _options.MaxOutstandingPublishes)
                    {
                        var claim = await ClaimNextAsync(blocked, stoppingToken).ConfigureAwait(false);
                        if (claim is not null)
                        {
                            didWork = true;
                            await DispatchAsync(claim, stoppingToken).ConfigureAwait(false);
                        }

                        // Even an empty completed poll demonstrates real bounded work.
                        _observation.Progress();
                    }

                    await RefreshAsync(force: false, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception)
                {
                    _observation.LoopFailed();
                    LogIterationFailure(_logger);
                    didWork = false;
                }

                if (!didWork)
                    await Task.Delay(_options.IdlePoll, _time, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal hosted-service cancellation; live attempts own their cleanup.
        }
        finally
        {
            CancelAttempts();
            _observation.Stopped();
        }
    }

    private async Task<OutboxDispatchClaim?> ClaimNextAsync(string[] blocked, CancellationToken cancellationToken)
    {
        // Service every eligible class within three claims, even when terminal
        // audit keeps failing. Empty classes fall through without claiming work.
        for (var checkedKinds = 0; checkedKinds < 3; checkedKinds++)
        {
            var kind = _nextClaimKind;
            // Advance before acquisition/dispatch: a fault must not pin its class.
            _nextClaimKind = kind switch
            {
                OutboxClaimKind.Terminal => OutboxClaimKind.Recovery,
                OutboxClaimKind.Recovery => OutboxClaimKind.Normal,
                _ => OutboxClaimKind.Terminal,
            };
            var claim = kind switch
            {
                OutboxClaimKind.Terminal => await _store.TryClaimTerminalAsync(blocked, cancellationToken).ConfigureAwait(false),
                OutboxClaimKind.Recovery => await _store.TryAcquireRecoveryAsync(blocked, cancellationToken).ConfigureAwait(false),
                _ => await _store.TryClaimAsync(blocked, cancellationToken).ConfigureAwait(false),
            };
            if (claim is not null) return claim;
        }
        return null;
    }

    private async Task DispatchAsync(OutboxDispatchClaim claim, CancellationToken stoppingToken)
    {
        if (claim.Kind == OutboxClaimKind.Normal)
        {
            await DispatchNormalAsync(claim, stoppingToken).ConfigureAwait(false);
            return;
        }

        var label = claim.Kind == OutboxClaimKind.Terminal
            ? _reader.Read(claim).EventTypeLabel : _registry.Resolve(claim.Type).EventTypeLabel;
        using var activity = _observation.BeginAttempt(claim.CorrelationId, label);
        if (claim.Kind == OutboxClaimKind.Recovery)
        {
            Fence(await _store.FailAsync(claim, OutboxFailureReason.UnknownOutcome, stoppingToken).ConfigureAwait(false));
            activity.SetTag("outcome", "recovered");
            LogAttempt(activity, label);
            return;
        }

        try
        {
            // BeginAttempt always starts a W3C Activity, including without listeners.
            var terminalized = await _store.TryTerminalizeAsync(claim, activity.Id!, stoppingToken).ConfigureAwait(false);
            Fence(terminalized);
            if (terminalized)
            {
                _observation.TerminalAuditSucceeded();
                _observation.Deadletter(label);
            }
            activity.SetTag("outcome", terminalized ? "deadlettered" : "lease_lost");
        }
        catch (Exception)
        {
            _observation.TerminalAuditFailed();
            activity.SetTag("outcome", "terminal_audit_failed");
            throw;
        }
        finally
        {
            LogAttempt(activity, label);
        }
    }

    private async Task DispatchNormalAsync(OutboxDispatchClaim claim, CancellationToken stoppingToken)
    {
        var read = _reader.Read(claim);
        using var activity = _observation.BeginAttempt(claim.CorrelationId, read.EventTypeLabel);
        var started = _time.GetTimestamp();
        try
        {
            if (read.TerminalReason is { } terminal)
            {
                Fence(await _store.RequestTerminalAsync(claim, terminal, stoppingToken).ConfigureAwait(false));
                activity.SetTag("outcome", "terminal_requested");
                return;
            }

            if (read.Disposition == OutboxEventDisposition.IntentionallyUnsubscribed)
            {
                Fence(await _store.CompleteAsync(claim, stoppingToken).ConfigureAwait(false));
                activity.SetTag("outcome", "intentionally_unsubscribed");
                return;
            }

            // The immutable registry excludes Pending identities in SQL, before
            // acquisition. Refuse publication if this invariant ever changes.
            if (read.Disposition != OutboxEventDisposition.Active || read.Envelope is null)
                throw new InvalidOperationException("Outbox publication was not admitted.");

            var attempt = new OutboxPublishAttempt(_scopes, read.Envelope);
            lock (_attemptGate) _attempts.Add(attempt);
            bool succeeded;
            try
            {
                succeeded = await attempt.Completion.WaitAsync(_options.PublishTimeout, _time, stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                attempt.RequestCancellation();
                RetainedPartitions();
                _observation.Failed(read.EventTypeLabel);
                activity.SetTag("outcome", "publish_timeout");
                // This is the only durable timeout transition. A late callback's
                // result is observed solely by its owned task, never acknowledged.
                Fence(await _store.FailAsync(claim, OutboxFailureReason.PublishTimeout, stoppingToken).ConfigureAwait(false));
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                attempt.RequestCancellation();
                RetainedPartitions();
                activity.SetTag("outcome", "shutdown");
                // Expired-lease recovery owns this unknown result after shutdown.
                throw;
            }

            RetainedPartitions();
            stoppingToken.ThrowIfCancellationRequested();
            if (succeeded)
            {
                var completed = await _store.CompleteAsync(claim, stoppingToken).ConfigureAwait(false);
                Fence(completed);
                activity.SetTag("outcome", completed ? "published" : "lease_lost");
            }
            else
            {
                _observation.Failed(read.EventTypeLabel);
                Fence(await _store.FailAsync(claim, OutboxFailureReason.PublishFailed, stoppingToken).ConfigureAwait(false));
                activity.SetTag("outcome", "publish_failed");
            }
        }
        finally
        {
            _observation.Duration(read.EventTypeLabel, _time.GetElapsedTime(started, _time.GetTimestamp()));
            LogAttempt(activity, read.EventTypeLabel);
        }
    }

    private void LogAttempt(Activity activity, string label)
    {
        if (_logger.IsEnabled(LogLevel.Information))
        {
            var outcome = activity.GetTagItem("outcome") as string ?? "storage_failure";
            LogAttemptFinished(_logger, activity.Id, label, outcome);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbox dispatch iteration failed with outcome iteration_failed.")]
    private static partial void LogIterationFailure(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Outbox dispatch attempt {CorrelationId} finished with event type {EventType} and outcome {Outcome}.")]
    private static partial void LogAttemptFinished(ILogger logger, string? correlationId, string eventType, string outcome);

    private async Task RefreshAsync(bool force, CancellationToken cancellationToken)
    {
        var last = _observation.Snapshot.LastSnapshotTimestamp;
        if (!force && last is { } timestamp
            && _time.GetElapsedTime(timestamp, _time.GetTimestamp()) < _options.SnapshotInterval) return;
        try
        {
            var queue = await _store.ReadSnapshotAsync(RetainedPartitions(), cancellationToken).ConfigureAwait(false);
            _observation.Refreshed(queue);
        }
        catch (Exception)
        {
            _observation.RefreshFailed();
            throw;
        }
    }

    private void Fence(bool changed)
    {
        if (!changed) _observation.LeaseLost();
    }

    private int OutstandingCount
    {
        get { lock (_attemptGate) return _attempts.Count; }
    }

    private string[] RetainedPartitions()
    {
        lock (_attemptGate)
        {
            // Completion includes cancellation callbacks and asynchronous scope
            // disposal; only then may this partition be acquired locally again.
            _attempts.RemoveAll(attempt => attempt.Completion.IsCompleted);
            _observation.Outstanding(_attempts.Count);
            return _attempts.Select(attempt => attempt.PartitionKey).Distinct(StringComparer.Ordinal).ToArray();
        }
    }

    private OutboxPublishAttempt[] CancelAttempts()
    {
        OutboxPublishAttempt[] attempts;
        lock (_attemptGate) attempts = _attempts.ToArray();
        foreach (var attempt in attempts) attempt.RequestCancellation();
        return attempts;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // The base service and callback drain share one overall budget. Never add
        // a second shutdown wait after the host has already exhausted the first.
        using var timeout = new CancellationTokenSource(_options.ShutdownTimeout, _time);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            await base.StopAsync(deadline.Token).ConfigureAwait(false);
            var attempts = CancelAttempts();
            await Task.WhenAll(attempts.Select(attempt => attempt.Completion)).WaitAsync(deadline.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            CancelAttempts();
        }
        finally
        {
            RetainedPartitions();
            _observation.Stopped();
        }
    }
}
