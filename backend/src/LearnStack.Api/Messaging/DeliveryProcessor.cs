using System.Diagnostics;
using LearnStack.Infrastructure.Messaging;
using Npgsql;

namespace LearnStack.Api.Messaging;

/// <summary>One just-in-time claimant with bounded, task-owned execution overruns.</summary>
public sealed partial class DeliveryProcessor : BackgroundService
{
    private readonly Lazy<NpgsqlDataSource> _source;
    private readonly DeliveryDispatchStore _store;
    private readonly DeliveryExecutor _executor;
    private readonly OutboxEventRegistry _registry;
    private readonly DeliveryDispatchOptions _options;
    private readonly TimeProvider _time;
    private readonly DeliveryObservation _observation;
    private readonly ILogger<DeliveryProcessor> _logger;
    private readonly object _attemptGate = new();
    private readonly List<DeliveryAttempt> _attempts = [];
    private long? _lastRefreshTimestamp;

    public DeliveryProcessor(
        [FromKeyedServices(OutboxDispatcherDataSource.Key)] Lazy<NpgsqlDataSource> source,
        DeliveryDispatchStore store, DeliveryExecutor executor, OutboxEventRegistry registry,
        DeliveryDispatchOptions options, TimeProvider time,
        DeliveryObservation observation, ILogger<DeliveryProcessor> logger)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(logger);
        _source = source;
        _store = store;
        _executor = executor;
        _registry = registry;
        _options = options;
        _time = time;
        _observation = observation;
        _logger = logger;
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        // .NET 10 runs ExecuteAsync in the background. Startup refusal therefore
        // belongs here, before the host can report that it has started.
        _options.Validate();
        try
        {
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
            throw new InvalidOperationException("Delivery dispatcher startup was refused. "
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
                    if (OutstandingCount < _options.MaxOutstandingExecutions)
                    {
                        // Terminal requests and expired acquisitions never spend
                        // another execution attempt, and take priority over new work.
                        var claim = await _store.TryClaimTerminalAsync(blocked, stoppingToken).ConfigureAwait(false)
                            ?? await _store.TryAcquireRecoveryAsync(blocked, stoppingToken).ConfigureAwait(false)
                            ?? await _store.TryClaimAsync(blocked, stoppingToken).ConfigureAwait(false);
                        if (claim is not null)
                        {
                            didWork = true;
                            await DispatchAsync(claim, stoppingToken).ConfigureAwait(false);
                        }

                        // Even an empty completed poll demonstrates real bounded work.
                        _observation.Progress();
                    }

                    await RefreshAsync(stoppingToken).ConfigureAwait(false);
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

    private async Task DispatchAsync(DeliveryClaim claim, CancellationToken stoppingToken)
    {
        if (claim.Kind == DeliveryClaimKind.Normal)
        {
            await DispatchNormalAsync(claim, stoppingToken).ConfigureAwait(false);
            return;
        }

        var label = _registry.Resolve(claim.Type).EventTypeLabel;
        using var activity = _observation.BeginAttempt(label, claim.SubscriptionId);
        if (claim.Kind == DeliveryClaimKind.Recovery)
        {
            Fence(await _store.FailAsync(claim, DeliveryFailureReason.UnknownOutcome, stoppingToken).ConfigureAwait(false));
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
                _observation.Deadletter(label, claim.SubscriptionId);
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

    private async Task DispatchNormalAsync(DeliveryClaim claim, CancellationToken stoppingToken)
    {
        var resolution = _registry.Resolve(claim.Type);
        var label = resolution.EventTypeLabel;
        using var activity = _observation.BeginAttempt(label, claim.SubscriptionId);
        var started = _time.GetTimestamp();
        try
        {
            var terminal = resolution.Disposition switch
            {
                OutboxEventDisposition.Unknown => DeliveryTerminalReason.UnknownType,
                OutboxEventDisposition.Retired => DeliveryTerminalReason.RetiredType,
                _ => _registry.IsActiveOrdinarySubscription(claim.Type, claim.SubscriptionId)
                    ? (DeliveryTerminalReason?)null : DeliveryTerminalReason.InvalidSubscription,
            };
            if (terminal is { } reason)
            {
                Fence(await _store.RequestTerminalAsync(claim, reason, stoppingToken).ConfigureAwait(false));
                activity.SetTag("outcome", "terminal_requested");
                return;
            }

            var attempt = new DeliveryAttempt(_executor, claim);
            lock (_attemptGate) _attempts.Add(attempt);
            DeliveryExecutionResult result;
            try
            {
                result = await attempt.Completion.WaitAsync(_options.ExecutionTimeout, _time, stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                attempt.RequestCancellation();
                RetainedPartitions();
                // Acknowledged business durability survives a hung cleanup.
                if (attempt.CommitAcknowledged)
                {
                    activity.SetTag("outcome", "completed_cleanup_timeout");
                    return;
                }
                _observation.Failed(label, claim.SubscriptionId);
                activity.SetTag("outcome", "execution_timeout");
                Fence(await _store.FailAsync(claim, attempt.CommitStarted
                    ? DeliveryFailureReason.UnknownOutcome : DeliveryFailureReason.ExecutionTimeout,
                    stoppingToken).ConfigureAwait(false));
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                attempt.RequestCancellation();
                RetainedPartitions();
                activity.SetTag("outcome", "shutdown");
                throw;
            }

            RetainedPartitions();
            stoppingToken.ThrowIfCancellationRequested();
            if (result.CommitAcknowledged)
            {
                if (result.Outcome == DeliveryExecutionOutcome.Duplicate)
                    _observation.Deduplicated(label, claim.SubscriptionId);
                activity.SetTag("outcome", result.CleanupFailed ? "completed_cleanup_failed"
                    : result.Outcome == DeliveryExecutionOutcome.Duplicate ? "duplicate" : "completed");
                return;
            }
            if (result.Outcome == DeliveryExecutionOutcome.LostClaim)
            {
                _observation.LeaseLost();
                activity.SetTag("outcome", "lease_lost");
                return;
            }
            if (result.Outcome is DeliveryExecutionOutcome.InvalidEnvelope or DeliveryExecutionOutcome.InvalidSubscription)
            {
                Fence(await _store.RequestTerminalAsync(claim,
                    result.Outcome == DeliveryExecutionOutcome.InvalidEnvelope
                        ? DeliveryTerminalReason.InvalidEnvelope : DeliveryTerminalReason.InvalidSubscription,
                    stoppingToken).ConfigureAwait(false));
                activity.SetTag("outcome", "terminal_requested");
                return;
            }
            _observation.Failed(label, claim.SubscriptionId);
            Fence(await _store.FailAsync(claim, result.Outcome == DeliveryExecutionOutcome.Indeterminate
                ? DeliveryFailureReason.UnknownOutcome : DeliveryFailureReason.ExecutionFailed,
                stoppingToken).ConfigureAwait(false));
            activity.SetTag("outcome", result.Outcome == DeliveryExecutionOutcome.Indeterminate
                ? "unknown_outcome" : "execution_failed");
        }
        finally
        {
            _observation.Duration(label, claim.SubscriptionId, _time.GetElapsedTime(started, _time.GetTimestamp()));
            LogAttempt(activity, label);
        }
    }

    private void LogAttempt(Activity activity, string label)
    {
        if (_logger.IsEnabled(LogLevel.Information))
        {
            var outcome = activity.GetTagItem("outcome") as string ?? "storage_failure";
            var subscription = activity.GetTagItem("subscription") as string ?? "invalid";
            LogAttemptFinished(_logger, activity.Id, label, subscription, outcome);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Delivery dispatch iteration failed with outcome iteration_failed.")]
    private static partial void LogIterationFailure(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Delivery dispatch attempt {CorrelationId} finished with event type {EventType}, subscription {Subscription} and outcome {Outcome}.")]
    private static partial void LogAttemptFinished(ILogger logger, string? correlationId, string eventType, string subscription, string outcome);

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var last = _lastRefreshTimestamp;
        if (last is { } timestamp
            && _time.GetElapsedTime(timestamp, _time.GetTimestamp()) < _options.SnapshotInterval) return;
        _lastRefreshTimestamp = _time.GetTimestamp();
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

    private DeliveryAttempt[] CancelAttempts()
    {
        DeliveryAttempt[] attempts;
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
