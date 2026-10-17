namespace LearnStack.Infrastructure.Jobs;

internal sealed partial class BackgroundJobControl
{
    private bool? _quarantinePresence;
    private bool? _controlQuarantinePresence;
    private long? _quarantineObservedAt;
    private long _quarantineReadAfter;
    private bool _quarantineFault = true;

    private bool QuarantineReadDue()
    {
        lock (_sync) return _time.GetTimestamp() >= _quarantineReadAfter;
    }

    // Runs only on the existing serialized control lane, including actual SQL
    // and resource cleanup. There is no health-query callback or extra slot.
    private async Task<bool> RefreshQuarantineAsync()
    {
        var issuedAt = _time.GetTimestamp();
        try
        {
            var operation = await _store.ReadQuarantinePresenceAsync(_stop.Token).ConfigureAwait(false);
            _controlOperation = operation;
            lock (_sync)
            {
                _quarantineFault = !operation.CleanupConfirmed || operation.Failed || operation.Value is null;
                if (!_quarantineFault)
                {
                    _quarantinePresence = operation.Value!.HasQuarantinedJobs;
                    _controlQuarantinePresence = operation.Value.HasQuarantinedControl;
                    _quarantineObservedAt = issuedAt;
                }
                _quarantineReadAfter = Later(_bounds.SnapshotInterval);
            }
            if (!operation.CleanupConfirmed) { SealControl(); return false; }
            _controlOperation = null;
            return true;
        }
        catch (Exception failure) when (Catchable(failure))
        {
            lock (_sync)
            {
                _quarantineFault = true;
                _quarantineReadAfter = Later(_bounds.SnapshotInterval);
            }
            throw;
        }
    }

    private bool QuarantineObservationHealthy()
    {
        // The cache cadence may exceed ProgressTimeout in valid configurations.
        // Keep each lane's progress check independent, and allow the preceding
        // read, one fresh control quantum, polling and the next bounded read.
        var maximumAge = _bounds.SnapshotInterval + _bounds.ProgressTimeout + _bounds.SqlTimeout * 2 + _bounds.ControlPoll;
        return !_cleanupFault && !_quarantineFault && _quarantinePresence.HasValue && _controlQuarantinePresence.HasValue
            && _quarantineObservedAt is { } observed
            && _time.GetElapsedTime(observed, _time.GetTimestamp()) <= maximumAge;
    }
}
