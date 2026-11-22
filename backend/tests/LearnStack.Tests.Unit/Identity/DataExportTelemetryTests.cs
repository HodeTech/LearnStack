using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using FluentAssertions;
using LearnStack.Modules.Identity.Application.DataRequests;
using Xunit;

namespace LearnStack.Tests.Unit.Identity;

/// <summary>Standard 10: the export workflow's instruments carry one closed <c>outcome</c> label and nothing else.</summary>
public sealed class DataExportTelemetryTests
{
    [Fact]
    public void Each_export_and_purge_outcome_is_one_closed_label_and_only_a_stored_bundle_records_its_size()
    {
        var measurements = new ConcurrentQueue<(string Instrument, double Value, string[] Tags)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meter) =>
            {
                if (instrument.Meter.Name == DataExportTelemetry.MeterName)
                {
                    meter.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            measurements.Enqueue((instrument.Name, value, [.. Tags(tags)])));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            measurements.Enqueue((instrument.Name, value, [.. Tags(tags)])));
        listener.Start();

        DataExportTelemetry.Export(DataExportOutcome.Completed, TimeSpan.FromSeconds(2), 4_321, 2);
        DataExportTelemetry.Export(DataExportOutcome.FailedTooLarge, TimeSpan.FromSeconds(1), 9_999, 1);
        DataExportTelemetry.Export(DataExportOutcome.NoOp, TimeSpan.Zero);
        DataExportTelemetry.Export(DataExportOutcome.Failed, TimeSpan.Zero);
        DataExportTelemetry.Purge(DataExportPurgeOutcome.Purged);
        DataExportTelemetry.Purge(DataExportPurgeOutcome.NoOp);
        DataExportTelemetry.Purge(DataExportPurgeOutcome.NotDue);

        var recorded = measurements.ToList();
        recorded.Where(entry => entry.Instrument == DataExportTelemetry.ExportCounterName).Select(entry => entry.Tags.Single())
            .Should().Equal("outcome=completed", "outcome=failed_too_large", "outcome=noop", "outcome=failed");
        recorded.Where(entry => entry.Instrument == DataExportTelemetry.DurationHistogramName).Select(entry => entry.Value)
            .Should().Equal(2d, 1d, 0d, 0d);
        recorded.Where(entry => entry.Instrument == DataExportTelemetry.BundleBytesHistogramName)
            .Should().ContainSingle("a refused bundle stores nothing and records no size").Which.Value.Should().Be(4_321);
        recorded.Where(entry => entry.Instrument == DataExportTelemetry.TruncationCounterName)
            .Should().ContainSingle().Which.Value.Should().Be(2);
        recorded.Where(entry => entry.Instrument == DataExportTelemetry.PurgeCounterName).Select(entry => entry.Tags.Single())
            .Should().Equal("outcome=purged", "outcome=noop", "outcome=not_due");
        recorded.Where(entry => entry.Instrument is DataExportTelemetry.BundleBytesHistogramName or DataExportTelemetry.TruncationCounterName)
            .Should().OnlyContain(entry => entry.Tags.Length == 0, "the size and truncations carry no label at all");
        DataExportTelemetry.MeterName.Should().StartWith("learnstack.", "the host subscribes to learnstack.*");
    }

    private static List<string> Tags(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var labels = new List<string>();
        foreach (var tag in tags)
        {
            labels.Add(tag.Key + "=" + tag.Value);
        }

        return labels;
    }
}
