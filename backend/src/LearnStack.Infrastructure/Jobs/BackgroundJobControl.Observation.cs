namespace LearnStack.Infrastructure.Jobs;

internal sealed partial class BackgroundJobControl
{
    private BackgroundJobDurableObservation? _observation;
    private long? _observationObservedAt;
    private long _observationReadAfter;
    private bool _observationFault = true;

    private bool ObservationReadDue()
    {
        lock (_sync) return _time.GetTimestamp() >= _observationReadAfter;
    }

    // The existing serialized control lane owns the complete statement, rollback
    // and actual resource cleanup. Health/gauges neither query nor add a slot.
    private async Task<bool> RefreshObservationAsync()
    {
        var issuedAt = _time.GetTimestamp();
        try
        {
            var operation = await _store.ReadDurableObservationAsync(_observationManifest, RetainedIds(), _stop.Token)
                .ConfigureAwait(false);
            _controlOperation = operation;
            lock (_sync)
            {
                _observationFault = !operation.CleanupConfirmed || operation.Failed || operation.Value is null;
                if (!_observationFault)
                {
                    _observation = operation.Value;
                    _observationObservedAt = issuedAt;
                }
                _observationReadAfter = Later(_bounds.SnapshotInterval);
            }
            if (!operation.CleanupConfirmed) { SealControl(); return false; }
            _controlOperation = null;
            return true;
        }
        catch (Exception failure) when (Catchable(failure))
        {
            lock (_sync)
            {
                _observationFault = true;
                _observationReadAfter = Later(_bounds.SnapshotInterval);
            }
            throw;
        }
    }

    private bool ObservationHealthy()
    {
        // A supported cache cadence may exceed the independent lane deadline.
        // Account for its previous read, a control quantum, polling and next read.
        var maximumAge = _bounds.SnapshotInterval + _bounds.ProgressTimeout + _bounds.SqlTimeout * 2 + _bounds.ControlPoll;
        return !_cleanupFault && !_observationFault && _observation is not null
            && _observationObservedAt is { } observed
            && _time.GetElapsedTime(observed, _time.GetTimestamp()) <= maximumAge;
    }
}
