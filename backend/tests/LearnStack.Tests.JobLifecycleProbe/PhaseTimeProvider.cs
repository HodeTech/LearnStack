namespace LearnStack.Tests.JobLifecycleProbe;

/// <summary>Advances only the existing generation observation clock; Core delays remain real.</summary>
internal sealed class PhaseTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly HashSet<PhaseTimer> _timers = [];
    private long _ticks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() { lock (_gate) return _ticks; }
    public override DateTimeOffset GetUtcNow() { lock (_gate) return DateTimeOffset.UnixEpoch.AddTicks(_ticks); }
    internal bool HasStartupDeadline
    {
        get { lock (_gate) return _timers.Any(timer => timer.Duration == TimeSpan.FromSeconds(75)); }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        GenerationPhaseDriver.Require(period == Timeout.InfiniteTimeSpan && dueTime > TimeSpan.Zero);
        lock (_gate)
        {
            var timer = new PhaseTimer(this, callback, state, dueTime, _ticks + dueTime.Ticks);
            _timers.Add(timer);
            return timer;
        }
    }

    internal void Advance(TimeSpan duration)
    {
        GenerationPhaseDriver.Require(duration > TimeSpan.Zero);
        PhaseTimer[] due;
        lock (_gate)
        {
            _ticks += duration.Ticks;
            due = _timers.Where(timer => timer.Due <= _ticks).ToArray();
        }
        foreach (var timer in due) timer.Fire();
    }

    private sealed class PhaseTimer(PhaseTimeProvider owner, TimerCallback callback, object? state,
        TimeSpan duration, long due) : ITimer
    {
        private bool _disposed;
        internal TimeSpan Duration { get; private set; } = duration;
        internal long Due { get; private set; } = due;

        internal void Fire()
        {
            lock (owner._gate)
            {
                if (_disposed || !owner._timers.Remove(this)) return;
            }
            callback(state);
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            GenerationPhaseDriver.Require(period == Timeout.InfiniteTimeSpan);
            lock (owner._gate)
            {
                if (_disposed) return false;
                Duration = dueTime;
                Due = owner._ticks + dueTime.Ticks;
                owner._timers.Add(this);
                return true;
            }
        }

        public void Dispose()
        {
            lock (owner._gate)
            {
                _disposed = true;
                owner._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
