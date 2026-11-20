using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using LearnStack.Infrastructure.PlatformOperator;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// Captures the operator recovery counters while it lives. The counters are process-wide, so a test reads the
/// difference its own calls made and filters by operation, and no other test calls the recovery owner.
/// </summary>
internal sealed class OperatorRecoveryMeterProbe : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly ConcurrentBag<(string Counter, string Operation, string? Outcome, long Value)> _measurements = [];

    public OperatorRecoveryMeterProbe()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == PlatformOperatorMetrics.MeterName
                && instrument.Name is PlatformOperatorMetrics.RecoveryCounterName or PlatformOperatorMetrics.ResultCounterName)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            string? operation = null, outcome = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "operation") operation = tag.Value?.ToString();
                if (tag.Key == "outcome") outcome = tag.Value?.ToString();
            }

            _measurements.Add((instrument.Name, operation ?? string.Empty, outcome, value));
        });
        _listener.Start();
    }

    /// <summary>The recoveries counted for the operation under the outcome label.</summary>
    public long Recoveries(string operation, string outcome) => _measurements
        .Where(m => m.Counter == PlatformOperatorMetrics.RecoveryCounterName && m.Operation == operation && m.Outcome == outcome)
        .Sum(m => m.Value);

    /// <summary>The MUST result rows counted for the operation.</summary>
    public long Results(string operation) => _measurements
        .Where(m => m.Counter == PlatformOperatorMetrics.ResultCounterName && m.Operation == operation)
        .Sum(m => m.Value);

    public void Dispose() => _listener.Dispose();
}
