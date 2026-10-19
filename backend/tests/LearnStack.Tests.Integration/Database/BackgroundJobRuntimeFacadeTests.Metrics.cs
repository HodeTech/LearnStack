using System.Diagnostics.Metrics;
using System.Collections.Concurrent;
using System.Reflection;
using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Jobs;
using LearnStack.SharedKernel.Identifiers;
using Npgsql;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobRuntimeFacadeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_runtime_projects_disjoint_bridge_repair_ACKs_without_reclassifying_unmarked_work(bool priorHint)
    {
        await using var database = await DatabaseAsync();
        var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString, due: DateTimeOffset.UtcNow.AddHours(1));
        await using var host = Fixture.Host(database, new());
        await using var source = Source(database);
        using var globals = new GlobalFilterSnapshot();
        var bounds = new BackgroundJobRuntimeOptions();
        var setup = new PostgresBackgroundJobBridgeStore(source, Fixture.Registry, bounds, new SystemGuidFactory());
        var first = (await setup.TryClaimAsync([]))!;
        first.Kind.Should().Be(BackgroundJobBridgeClaimKind.Unmarked);
        if (priorHint) (await setup.AcknowledgeAsync(first, "123")).Should().BeTrue();
        await using (var connection = await source.OpenConnectionAsync())
        await using (var due = new NpgsqlCommand(priorHint
            ? "UPDATE background_jobs SET handoff_check_at=clock_timestamp()-interval '1 second' WHERE id=@id"
            : "UPDATE background_jobs SET bridge_until=clock_timestamp()-interval '1 second',handoff_check_at=clock_timestamp()-interval '1 second' WHERE id=@id", connection))
        {
            due.Parameters.AddWithValue("id", job.Value);
            (await due.ExecuteNonQueryAsync()).Should().Be(1);
        }
        var runtime = Runtime(host.Services, source, bounds);
        using var probe = new BackgroundJobMetricProbe(host.Services.GetRequiredService<BackgroundJobObservation>());
        try
        {
            await runtime.StartAsync();
            await EventuallyAsync(() => Task.FromResult(runtime.Snapshot.Bridge?.Acknowledged == 1));
            var bridge = runtime.Snapshot.Bridge!;
            bridge.PriorHintRepairsAcknowledged.Should().Be(priorHint ? 1 : 0);
            bridge.ExpiredTokenRepairsAcknowledged.Should().Be(priorHint ? 0 : 1);
            for (var scrape = 0; scrape < 2; scrape++)
            {
                var points = probe.Read();
                BackgroundJobMetricProbe.One(points, "learnstack_job_control_total", ("operation", "bridge_repair_prior_ack_hint_acknowledged"), ("pool", "none"))
                    .Should().Be(priorHint ? 1 : 0);
                BackgroundJobMetricProbe.One(points, "learnstack_job_control_total", ("operation", "bridge_repair_expired_token_acknowledged"), ("pool", "none"))
                    .Should().Be(priorHint ? 0 : 1);
                points.Where(point => point.Name == "learnstack_job_control_total")
                    .Should().OnlyContain(point => point.Tags.Count == 2 && point.Tags.ContainsKey("operation") && point.Tags.ContainsKey("pool"));
            }
            var row = (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job))!;
            row.NormalAttempts.Should().Be(0);
            (await VendorAsync(source, job)).Should().NotBeNull();
        }
        finally
        {
            using var deadline = new CancellationTokenSource(TestWait);
            await runtime.StopAsync(deadline.Token);
            await runtime.Completion.WaitAsync(TestWait);
        }
    }

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

/// <summary>Actual synchronous stage emissions only; collection never invokes gauges.</summary>
internal sealed class BackgroundJobStageProbe : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly ConcurrentQueue<BackgroundJobMetricProbe.Point> _points = new();

    internal BackgroundJobStageProbe(BackgroundJobObservation observation, bool throwOnMeasurement = false)
    {
        var meter = typeof(BackgroundJobObservation).GetField("_meter", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(observation);
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (!instrument.IsObservable && ReferenceEquals(instrument.Meter, meter)) listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            _points.Enqueue(new(instrument.Name, value, tags.ToArray().ToDictionary(tag => tag.Key, tag => (string)tag.Value!)));
            if (throwOnMeasurement) throw new InvalidOperationException("Deliberate job metric listener fault.");
        });
        _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
        {
            _points.Enqueue(new(instrument.Name, value, tags.ToArray().ToDictionary(tag => tag.Key, tag => (string)tag.Value!)));
            if (throwOnMeasurement) throw new InvalidOperationException("Deliberate job metric listener fault.");
        });
        _listener.Start();
    }

    internal BackgroundJobMetricProbe.Point[] Points => _points.ToArray();
    internal BackgroundJobMetricProbe.Point Duration(string stage) => Points.Single(point => point.Name == "learnstack_job_duration_seconds"
        && point.Tags["stage"] == stage);
    public void Dispose() => _listener.Dispose();
}
