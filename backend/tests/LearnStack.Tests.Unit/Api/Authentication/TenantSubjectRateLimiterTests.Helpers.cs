using System.Threading.RateLimiting;
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using FluentAssertions;
using LearnStack.Api.Authentication;

namespace LearnStack.Tests.Unit.Api.Authentication;

public sealed partial class TenantSubjectRateLimiterTests
{
    private sealed record SubjectMeasurement(string Name, long Value, Dictionary<string, object?> Tags);

    private sealed class SubjectMetricCapture : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly ConcurrentQueue<SubjectMeasurement> _values = new();
        public SubjectMetricCapture()
        {
            _listener.InstrumentPublished = (instrument, observed) =>
            {
                if (instrument.Meter.Name == TenantSubjectAdmissionMetrics.MeterName
                    && instrument.Name is TenantSubjectAdmissionMetrics.RejectedCounterName
                        or TenantSubjectAdmissionMetrics.CleanupFailedCounterName)
                    observed.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
                _values.Enqueue(new SubjectMeasurement(instrument.Name, value,
                    tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value, StringComparer.Ordinal))));
            _listener.Start();
        }
        public SubjectMeasurement[] Rejections => _values.Where(value =>
            value.Name == TenantSubjectAdmissionMetrics.RejectedCounterName).ToArray();
        public SubjectMeasurement[] CleanupFailures => _values.Where(value =>
            value.Name == TenantSubjectAdmissionMetrics.CleanupFailedCounterName).ToArray();
        public void Dispose() => _listener.Dispose();
    }

    private sealed class TrackedBudget(int permits) : RateLimiter
    {
        private readonly int _permitLimit = permits;
        private int _remaining = permits;
        private TimeSpan? _idle;
        private int _attempts;
        private int _idleReads;
        private int _disposals;
        private int _completedDisposals;
        private int _asyncDisposals;
        public int Attempts => Volatile.Read(ref _attempts);
        public int IdleReads => Volatile.Read(ref _idleReads);
        public int Disposals => Volatile.Read(ref _disposals);
        public int CompletedDisposals => Volatile.Read(ref _completedDisposals);
        public int AsyncDisposals => Volatile.Read(ref _asyncDisposals);
        public Action? OnDispose { get; set; }
        public Action? OnIdleRead { get; set; }
        public TimeSpan RetryAfter { get; set; } = Window;
        public List<TrackedLease> Leases { get; } = [];
        public override TimeSpan? IdleDuration
        {
            get
            {
                Interlocked.Increment(ref _idleReads);
                OnIdleRead?.Invoke();
                return _idle;
            }
        }
        public void Replenish(TimeSpan fullQuotaIdle)
        {
            _remaining = _permitLimit;
            _idle = fullQuotaIdle;
        }
        public override RateLimiterStatistics? GetStatistics() => null;
        protected override RateLimitLease AttemptAcquireCore(int permitCount)
        {
            ObjectDisposedException.ThrowIf(Disposals != 0, this);
            Interlocked.Increment(ref _attempts);
            var acquired = _remaining >= permitCount;
            if (acquired)
            {
                _remaining -= permitCount;
                _idle = null;
            }
            var lease = new TrackedLease(acquired, RetryAfter);
            Leases.Add(lease);
            return lease;
        }
        protected override ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken) =>
            ValueTask.FromResult(AttemptAcquireCore(permitCount));
        protected override void Dispose(bool disposing)
        {
            if (disposing) DisposeBudget();
            base.Dispose(disposing);
        }
        protected override ValueTask DisposeAsyncCore()
        {
            Interlocked.Increment(ref _asyncDisposals);
            DisposeBudget();
            return base.DisposeAsyncCore();
        }
        private void DisposeBudget()
        {
            Interlocked.Increment(ref _disposals);
            OnDispose?.Invoke();
            Interlocked.Increment(ref _completedDisposals);
        }
    }

    private sealed class TrackedLease(bool acquired, TimeSpan retryAfter) : RateLimitLease
    {
        public int Disposals { get; private set; }
        public override bool IsAcquired
        {
            get
            {
                ObjectDisposedException.ThrowIf(Disposals != 0, this);
                return acquired;
            }
        }
        public override IEnumerable<string> MetadataNames => acquired ? [] : [MetadataName.RetryAfter.Name];
        public override bool TryGetMetadata(string metadataName, out object? value)
        {
            ObjectDisposedException.ThrowIf(Disposals != 0, this);
            value = !acquired && metadataName == MetadataName.RetryAfter.Name ? retryAfter : null;
            return value is not null;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) Disposals++;
            base.Dispose(disposing);
        }
    }

    private sealed class ManualClock : TimeProvider, IDisposable
    {
        private long _timestamp;
        private ManualTimer? _timer;
        public ManualTimer Timer => _timer ?? throw new InvalidOperationException("The owner has not created its timer.");
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);
        public void Advance(TimeSpan elapsed) => Interlocked.Add(ref _timestamp, elapsed.Ticks);
        public void Dispose() => _timer?.Dispose();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _timer.Should().BeNull();
            dueTime.Should().Be(TimeSpan.FromSeconds(30), "ADR-0066 fixes the first retirement pass at 30 seconds");
            period.Should().Be(TimeSpan.FromSeconds(30), "ADR-0066 fixes the retirement cadence at 30 seconds");
            _timer = new ManualTimer(callback, state);
            return _timer;
        }
    }

    private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
    {
        private readonly object _gate = new();
        private readonly TaskCompletionSource _joined = Completion();
        private bool _disposed;
        private int _callbacks;
        private int _asyncDisposals;
        public int AsyncDisposals => Volatile.Read(ref _asyncDisposals);
        public TaskCompletionSource DisposalRequested { get; } = Completion();
        public bool Fire()
        {
            lock (_gate)
            {
                if (_disposed) return false;
                _callbacks++;
            }
            try
            {
                callback(state);
                return true;
            }
            finally
            {
                lock (_gate)
                {
                    _callbacks--;
                    if (_disposed && _callbacks == 0) _joined.TrySetResult();
                }
            }
        }
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (_gate) return !_disposed;
        }
        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
                if (_callbacks == 0) _joined.TrySetResult();
            }
            DisposalRequested.TrySetResult();
        }
        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _asyncDisposals);
            Dispose();
            return new ValueTask(_joined.Task);
        }
    }
}
