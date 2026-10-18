using System.Diagnostics.Metrics;
using System.Reflection;
using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobRuntimeFacadeTests
{
    [Fact]
    public async Task Actual_native_pool_named_none_keeps_aggregate_and_pool_metrics_distinct_without_private_labels()
    {
        await using var database = await DatabaseAsync();
        var topology = BackgroundJobInfrastructureRegistry.Create([
            new("none", BackgroundJobWorkerPoolKind.Transactional, 1, ["transactional"]),
            new("external", BackgroundJobWorkerPoolKind.External, 2, ["external"]),
            new("control", BackgroundJobWorkerPoolKind.Control, 1, ["control"]),
        ], [], [], []);
        var definitions = BackgroundJobDefinitionRegistry.Create([], BackgroundJobFrameworkRequests.All, [], infrastructure: topology);
        await using var host = Fixture.Host(database, new(), configure: services =>
        {
            services.RemoveAll<BackgroundJobDefinitionRegistry>();
            services.AddSingleton(definitions);
        });
        await using var source = Source(database);
        using var globals = new GlobalFilterSnapshot();
        var runtime = Runtime(host.Services, source);
        using var probe = new BackgroundJobMetricProbe(host.Services.GetRequiredService<BackgroundJobObservation>());
        try
        {
            await runtime.StartAsync();
            await EventuallyAsync(() => Task.FromResult(runtime.Snapshot.IsHealthy));
            var points = probe.Read();
            points.GroupBy(point => point.Name + ":" + string.Join(";", point.Tags.OrderBy(tag => tag.Key, StringComparer.Ordinal)
                    .Select(tag => tag.Key + "=" + tag.Value)), StringComparer.Ordinal)
                .Should().OnlyContain(group => group.Count() == 1, "a legal pool name cannot alias an aggregate's full metric identity");
            BackgroundJobMetricProbe.One(points, "learnstack_job_state_count", ("kind", "retained_total"), ("pool", "none")).Should().Be(0);
            BackgroundJobMetricProbe.One(points, "learnstack_job_state_count", ("kind", "retained"), ("pool", "none")).Should().Be(0);
            BackgroundJobMetricProbe.One(points, "learnstack_job_state_count", ("kind", "pending"), ("profile", "transactional")).Should().Be(0);
            BackgroundJobMetricProbe.One(points, "learnstack_job_heartbeat_age_seconds", ("component", "metadata"), ("pool", "none")).Should().BeGreaterThanOrEqualTo(0);
            points.SelectMany(point => point.Tags.Keys).Should().NotContain(key => key.Contains("tenant", StringComparison.Ordinal)
                || key.Contains("trace", StringComparison.Ordinal) || key.Contains("organization", StringComparison.Ordinal));
        }
        finally
        {
            using var deadline = new CancellationTokenSource(TestWait);
            await runtime.StopAsync(deadline.Token);
            await runtime.Completion.WaitAsync(TestWait);
        }
    }
}

internal sealed class BackgroundJobMetricProbe : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly List<Point> _points = [];
    internal BackgroundJobMetricProbe(BackgroundJobObservation observation)
    {
        var meter = typeof(BackgroundJobObservation).GetField("_meter", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(observation);
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.IsObservable && ReferenceEquals(instrument.Meter, meter)) listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            _points.Add(new(instrument.Name, value, tags.ToArray().ToDictionary(tag => tag.Key, tag => (string)tag.Value!))));
        _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            _points.Add(new(instrument.Name, value, tags.ToArray().ToDictionary(tag => tag.Key, tag => (string)tag.Value!))));
        _listener.Start();
    }
    internal Point[] Read()
    {
        _points.Clear();
        _listener.RecordObservableInstruments();
        return _points.ToArray();
    }
    internal static double One(IEnumerable<Point> points, string name, params (string Key, string Value)[] tags) =>
        points.Single(point => point.Name == name && tags.All(tag => point.Tags.GetValueOrDefault(tag.Key) == tag.Value)).Value;
    public void Dispose() => _listener.Dispose();
    internal sealed record Point(string Name, double Value, Dictionary<string, string> Tags);
}
