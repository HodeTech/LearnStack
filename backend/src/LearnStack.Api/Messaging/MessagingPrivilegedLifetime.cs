using LearnStack.Infrastructure.Messaging;
using Npgsql;

namespace LearnStack.Api.Messaging;

/// <summary>Positive completion of one fixed messaging processor's privileged source use.</summary>
internal sealed class MessagingPrivilegedLifetime
{
    private readonly object _gate = new();
    private readonly TaskCompletionSource _completion;
    private readonly OutboxDispatchStore? _outboxStore;
    private readonly DeliveryDispatchStore? _deliveryStore;
    private bool _startAdmitted;
    private bool _startSettled;
    private bool _stopping;
    private bool _executionReserved;
    private bool _executionJoined;
    private NpgsqlConnection? _startupConnection;
    private Task? _startupDisposal;
    private Task? _execution;
    private Task? _executionObserver;

    private MessagingPrivilegedLifetime() =>
        // The source pin owns this task, so its state must keep actual unresolved
        // connections and execution tasks rooted even after DI drops the service.
        _completion = new(this, TaskCreationOptions.RunContinuationsAsynchronously);

    internal MessagingPrivilegedLifetime(OutboxDispatchStore store) : this() =>
        _outboxStore = store ?? throw new ArgumentNullException(nameof(store));

    internal MessagingPrivilegedLifetime(DeliveryDispatchStore store) : this() =>
        _deliveryStore = store ?? throw new ArgumentNullException(nameof(store));

    internal Task Completion => _completion.Task;

    internal bool IsStopping
    {
        get { lock (_gate) return _stopping; }
    }

    internal void BeginStart()
    {
        lock (_gate)
        {
            if (_stopping || _startAdmitted)
                throw new InvalidOperationException("A messaging processor cannot start again or after stopping.");
            _startAdmitted = true;
        }
    }

    internal async Task DisposeStartupConnectionAsync(NpgsqlConnection connection)
    {
        // Retain the concrete connection before invoking disposal: even a
        // synchronous disposal failure cannot lose its unresolved owner.
        lock (_gate) _startupConnection = connection;
        var disposal = connection.DisposeAsync().AsTask();
        lock (_gate) _startupDisposal = disposal;
        await disposal.ConfigureAwait(false);
        lock (_gate)
        {
            _startupConnection = null;
            _startupDisposal = null;
        }
    }

    internal bool TryReserveExecution()
    {
        lock (_gate)
        {
            if (_stopping) return false;
            _executionReserved = true;
            return true;
        }
    }

    internal void ObserveExecution(Task execution)
    {
        lock (_gate) _execution = execution;
        var observer = ObserveExecutionAsync(execution);
        lock (_gate) _executionObserver = observer;
    }

    private async Task ObserveExecutionAsync(Task execution)
    {
        // BackgroundService.StopAsync joins only within its caller's budget.
        // ExecuteTask instead includes actual ExecuteAsync return, including
        // its finally, or cancellation before the scheduled body could run.
        await execution.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (_outboxStore?.HasUnconfirmedCleanup == true || _deliveryStore?.HasUnconfirmedCleanup == true)
            return;
        lock (_gate)
        {
            _executionJoined = true;
            TryComplete();
        }
    }

    internal void FinishStart()
    {
        lock (_gate)
        {
            _startSettled = true;
            TryComplete();
        }
    }

    internal void SealStopping()
    {
        lock (_gate)
        {
            _stopping = true;
            TryComplete();
        }
    }

    private void TryComplete()
    {
        if (!_startAdmitted)
        {
            if (_stopping) _completion.TrySetResult();
            return;
        }

        // A reservation stays unresolved if base.StartAsync fails before
        // publishing its actual task. Absence of a handle is not no-issued proof.
        if (_startSettled && _startupConnection is null
            && (!_executionReserved || _executionJoined))
            _completion.TrySetResult();
    }
}
