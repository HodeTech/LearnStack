namespace LearnStack.Infrastructure.Jobs;

internal sealed partial class BackgroundJobControl
{
    private async Task<bool> AdvanceTerminalNoStartAsync()
    {
        BackgroundJobTerminalNoStartOperation owner;
        lock (_sync)
        {
            if (_stopping || !_gate.TryAdmit()) return true;
            // Publish the owner before acquisition, source binding or app code
            // can execute any synchronous prefix.
            owner = _terminalNoStart ??= new(_executor, _gate.RunStop, _bounds, _time);
        }
        await owner.AdvanceAsync(_store, RetainedIds(), _stop.Token).ConfigureAwait(false);
        lock (_sync) _unknown += owner.TakeNewUnknownOutcomes();
        if (owner.PrivilegedCleanupFailed)
        {
            // A retained privileged connection seals this existing SQL lane;
            // it does not grant another independent privileged capacity slot.
            RetainCleanup();
            return false;
        }
        if (owner.Completion is { IsCompletedSuccessfully: true, Result: false }) RetainCleanup();
        if (owner.CanRelease)
        {
            lock (_sync)
            {
                if (!ReferenceEquals(_terminalNoStart, owner))
                    throw new InvalidOperationException("Only the actual terminal app owner releases its exclusion.");
                _terminalNoStart = null;
            }
            // No status release is issued here. Existing terminal selection
            // continues to require no occupied reservation and an expired or
            // null owner. Lease expiry is only that scheduling fence.
        }
        return true;
    }
}
