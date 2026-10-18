using System.Diagnostics.Metrics;
using System.Reflection;
using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using Xunit;

namespace LearnStack.Tests.Unit.Infrastructure.Jobs;

public sealed partial class BackgroundJobObservationTests
{
    private static readonly string[] RequiredInstruments =
    [
        "learnstack_job_component_state", "learnstack_job_control_total", "learnstack_job_heartbeat_age_seconds",
        "learnstack_job_duration_seconds", "learnstack_job_failed_total", "learnstack_job_oldest_eligible_age_seconds",
        "learnstack_job_state_count", "learnstack_job_unknown_outcome_total",
    ];
    [Fact]
    public void Required_shared_observation_resolves_without_a_runtime_and_missing_registration_refuses_executor_activation()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<ITenantContextAccessor>());
        services.AddSingleton(Substitute.For<IAuditCatalog>());
        services.AddLearnStackBackgroundJobContracts();
        services.AddLearnStackBackgroundJobContracts();
        services.Count(item => item.ServiceType == typeof(BackgroundJobObservation)).Should().Be(1);
        using (var provider = services.BuildServiceProvider())
        {
            var observer = provider.GetRequiredService<BackgroundJobObservation>();
            provider.GetRequiredService<JobExecutor>().Observation.Should().BeSameAs(observer);
            provider.GetService<BackgroundJobRuntime>().Should().BeNull();
            using var probe = new Probe(observer);
            var points = probe.Collect();
            One(points, "learnstack_job_component_state", ("component", "runtime"), ("state", "not_bound")).Should().Be(1);
            points.Should().NotContain(point => point.Instrument == "learnstack_job_state_count",
                "absence of the API owner is not a healthy empty queue");
        }
        services.RemoveAll<BackgroundJobObservation>();
        using var missing = services.BuildServiceProvider();
        var activate = () => missing.GetRequiredService<JobExecutor>();
        activate.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task Actual_callbacks_preserve_sample_age_and_project_finite_zero_cells_with_separate_monotonic_freshness()
    {
        await using var fixture = new Fixture();
        fixture.Observe(BackgroundJobDurableObservationTests.EmptyPoints().Concat([
            new(BackgroundJobObservationFamily.Queue, "external", 0, 4, 2, 3),
            new(BackgroundJobObservationFamily.Deferred, "job", 1, 2, 0, 0),
            new(BackgroundJobObservationFamily.ReservedCapacity, "unknown", 0, 1, 0, 0),
            new(BackgroundJobObservationFamily.FanoutPending, "unknown", 3, 3, 0, 0),
        ]));
        fixture.Time.Advance(TimeSpan.FromSeconds(10));
        fixture.ObserveRenewals(acknowledged: 2, historyConfirmed: 3);
        fixture.ObserveRecurrence();
        using var probe = new Probe(fixture.Observer);
        var points = probe.Collect();
        One(points, "learnstack_job_control_total", ("operation", "lease_loss_renewal_refused"), ("pool", "none")).Should().Be(0);
        One(points, "learnstack_job_control_total", ("operation", "lease_loss_elapsed"), ("pool", "none")).Should().Be(0);
        fixture.ObserveLeaseLosses(2, 3);
        points = probe.Collect();
        One(points, "learnstack_job_control_total", ("operation", "lease_loss_renewal_refused"), ("pool", "none")).Should().Be(2);
        One(points, "learnstack_job_control_total", ("operation", "lease_loss_elapsed"), ("pool", "none")).Should().Be(3);
        One(points, "learnstack_job_control_total", ("operation", "renewal_acknowledged"), ("pool", "none")).Should().Be(2);
        One(points, "learnstack_job_control_total", ("operation", "renewal_history_confirmed"), ("pool", "none")).Should().Be(3);
        points.Where(point => point.Instrument == "learnstack_job_control_total").Select(point => point.Tags["operation"])
            .Distinct().Should().BeSubsetOf([
                "generation", "expiration_invocation", "fetched_heartbeat_invocation", "bridge_acknowledged", "bridge_failed",
                "bridge_deferred", "renewal_acknowledged", "renewal_history_confirmed", "retry_confirmed", "terminal_confirmed",
                "uncertainty_observation",
                "bridge_repair_prior_ack_hint_acknowledged", "bridge_repair_expired_token_acknowledged",
                "lease_loss_renewal_refused", "lease_loss_elapsed",
                "occurrence_acknowledged", "skipped_slot_acknowledged", "fanout_page_acknowledged",
                "fanout_route_examined_acknowledged", "fanout_recipient_inserted_acknowledged", "fanout_completed_acknowledged",
            ]);
        foreach (var (operation, expected) in new (string, long)[]
                 {
                     ("occurrence_acknowledged", 2), ("skipped_slot_acknowledged", 7), ("fanout_page_acknowledged", 3),
                     ("fanout_route_examined_acknowledged", 11), ("fanout_recipient_inserted_acknowledged", 5),
                     ("fanout_completed_acknowledged", 1),
                 })
            One(points, "learnstack_job_control_total", ("operation", operation), ("pool", "none")).Should().Be(expected);
        probe.Instruments.Order(StringComparer.Ordinal).Should().Equal(RequiredInstruments.Order(StringComparer.Ordinal));
        One(points, "learnstack_job_state_count", ("kind", "pending"), ("profile", "external")).Should().Be(4);
        One(points, "learnstack_job_state_count", ("kind", "deferred"), ("artifact", "job"), ("reason", "unknown")).Should().Be(2);
        One(points, "learnstack_job_state_count", ("kind", "reserved"), ("capacity_group", "unknown")).Should().Be(1);
        One(points, "learnstack_job_state_count", ("kind", "fanout_pending"), ("artifact", "recipient"), ("recurrence", "unknown")).Should().Be(3);
        One(points, "learnstack_job_oldest_eligible_age_seconds", ("profile", "external")).Should().Be(3,
            "the pre-acquisition freshness timestamp is not the SQL statement's age anchor");
        One(points, "learnstack_job_oldest_eligible_age_seconds", ("profile", "transactional")).Should().Be(0);
        One(points, "learnstack_job_heartbeat_age_seconds", ("component", "durable_observation")).Should().Be(10);
        points.Should().NotContain(point => point.Instrument == "learnstack_job_heartbeat_age_seconds"
            && point.Tags.GetValueOrDefault("component") == "metadata");
        points.Where(point => point.Instrument == "learnstack_job_component_state"
            && point.Tags.GetValueOrDefault("component") == "metadata" && point.Tags["state"] == "not_observed")
            .Should().HaveCount(3).And.OnlyContain(point => point.Value == 1);
        points.SelectMany(point => point.Tags.Keys).Distinct().Should().BeSubsetOf([
            "kind", "pool", "profile", "capacity_group", "recurrence", "artifact", "reason", "component", "state", "operation",
        ]);
        points.SelectMany(point => point.Tags.Values).Where(value => Guid.TryParse(value, out _)).Should().BeEmpty();
        fixture.Time.Advance(TimeSpan.FromSeconds(5));
        One(probe.Collect(), "learnstack_job_oldest_eligible_age_seconds", ("profile", "external")).Should().Be(3);
        fixture.Observe(BackgroundJobDurableObservationTests.EmptyPoints());
        var empty = probe.Collect();
        One(empty, "learnstack_job_state_count", ("kind", "deferred"), ("artifact", "job"), ("reason", "unknown")).Should().Be(0);
        One(empty, "learnstack_job_oldest_eligible_age_seconds", ("profile", "external")).Should().Be(0);
        fixture.Source.IsValueCreated.Should().BeFalse();
    }

    [Fact]
    public async Task Binding_refuses_replacement_and_disposing_telemetry_supplies_no_execution_or_cleanup_authority()
    {
        await using var fixture = new Fixture();
        var bindAgain = () => fixture.Observer.Bind(fixture.Runtime, BackgroundJobRecurrenceRegistry.CreateEmpty(), fixture.Time);
        bindAgain.Should().Throw<InvalidOperationException>();
        fixture.Observer.Dispose();
        fixture.Runtime.Snapshot.InstrumentationHealthy.Should().BeFalse();
        fixture.Runtime.Snapshot.IsHealthy.Should().BeFalse();
        fixture.Runtime.Completion.IsCompleted.Should().BeFalse();
        fixture.Gate.IsOpen.Should().BeTrue();
        fixture.Source.IsValueCreated.Should().BeFalse();
    }

    [Fact]
    public void Actual_stage_measurements_keep_elapsed_time_private_labels_and_uncertain_effects_distinct()
    {
        var time = new Clock();
        using var observer = new BackgroundJobObservation(BackgroundJobDefinitionRegistry.CreateEmpty(), time);
        using var probe = new Probe(observer);
        var started = observer.StartStage();
        time.Advance(TimeSpan.FromSeconds(7));
        observer.RecordStage(started, "raw:019712ac-1234-7000-8000-0000000000ab", "private-profile", JobObservedStage.Finalize,
            JobObservedOutcome.Acknowledged, true);
        var duration = probe.Current.Single();
        duration.Value.Should().Be(7);
        duration.Tags.Should().Contain("job_name", "unknown").And.Contain("profile", "unknown")
            .And.Contain("stage", "finalize").And.Contain("outcome", "Acknowledged").And.Contain("cleanup", "retained");
        observer.RecordStage(started, null, "external", JobObservedStage.Execute, JobObservedOutcome.Running, false);
        observer.RecordStage(started, null, "external", JobObservedStage.Execute, JobObservedOutcome.UnissuedProviderFault, false);
        probe.Current.Should().NotContain(point => point.Instrument == "learnstack_job_failed_total" || point.Instrument == "learnstack_job_unknown_outcome_total");
        observer.RecordStage(started, null, "external", JobObservedStage.Reconcile, JobObservedOutcome.ProviderFault, false);
        probe.Current.Should().ContainSingle(point => point.Instrument == "learnstack_job_unknown_outcome_total" && point.Value == 1);
        observer.RecordStage(started, null, "transactional", JobObservedStage.Transactional, JobObservedOutcome.Retry, false, "raw-provider-text");
        probe.Current.Single(point => point.Instrument == "learnstack_job_failed_total").Tags["reason"].Should().Be("stage_failed");
        probe.Current.SelectMany(point => point.Tags.Values).Should().NotContain(value => value.Contains("raw", StringComparison.Ordinal)
            || value.Contains("private", StringComparison.Ordinal));
        observer.IsHealthy.Should().BeTrue();
    }

    [Fact]
    public void Throwing_metric_listeners_fault_health_without_rethrowing_or_reemitting_the_completed_stage()
    {
        var time = new Clock();
        using var observer = new BackgroundJobObservation(BackgroundJobDefinitionRegistry.CreateEmpty(), time);
        using var probe = new Probe(observer, throwOnMeasurement: true);
        var started = observer.StartStage();
        var record = () => observer.RecordStage(started, null, "external", JobObservedStage.Outcome, JobObservedOutcome.UnknownCommit, false);
        record.Should().NotThrow();
        probe.Current.Should().HaveCount(2, "duration and uncertainty are each attempted once despite separate listener faults");
        probe.Current.Count(point => point.Instrument == "learnstack_job_duration_seconds").Should().Be(1);
        probe.Current.Count(point => point.Instrument == "learnstack_job_unknown_outcome_total").Should().Be(1);
        observer.IsHealthy.Should().BeFalse();
    }

    private static double One(IReadOnlyList<Sample> points, string instrument, params (string Key, string Value)[] tags) =>
        points.Single(point => point.Instrument == instrument && tags.All(tag => point.Tags.GetValueOrDefault(tag.Key) == tag.Value)).Value;

    private sealed record Sample(string Instrument, double Value, Dictionary<string, string> Tags);

    private sealed class Probe : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly List<Sample> _samples = [];
        internal HashSet<string> Instruments { get; } = new(StringComparer.Ordinal);
        internal Probe(BackgroundJobObservation observation, bool throwOnMeasurement = false)
        {
            var meter = typeof(BackgroundJobObservation).GetField("_meter", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(observation);
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (!ReferenceEquals(instrument.Meter, meter)) return;
                Instruments.Add(instrument.Name);
                listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            {
                _samples.Add(new(instrument.Name, value, tags.ToArray().ToDictionary(tag => tag.Key, tag => (string)tag.Value!)));
                if (throwOnMeasurement) throw new InvalidOperationException("Deliberate synchronous metric listener fault.");
            });
            _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            {
                _samples.Add(new(instrument.Name, value, tags.ToArray().ToDictionary(tag => tag.Key, tag => (string)tag.Value!)));
                if (throwOnMeasurement) throw new InvalidOperationException("Deliberate synchronous metric listener fault.");
            });
            _listener.Start();
        }
        internal Sample[] Current => _samples.ToArray();
        internal Sample[] Collect()
        {
            _samples.Clear();
            _listener.RecordObservableInstruments();
            return _samples.ToArray();
        }
        public void Dispose() => _listener.Dispose();
    }

    // Memory projection/ownership only. Cold durable truth uses the real PG controls.
    private sealed class Fixture : IAsyncDisposable
    {
        internal Clock Time { get; } = new();
        internal PrivilegedDataSource Source { get; } = new(() => throw new InvalidOperationException("A gauge cannot open a source."));
        internal BackgroundJobRuntimeGate Gate { get; }
        internal BackgroundJobObservation Observer { get; }
        internal BackgroundJobRuntime Runtime { get; }
        private readonly BackgroundJobControl _control;
        internal Fixture()
        {
            var definitions = BackgroundJobDefinitionRegistry.CreateEmpty();
            var bounds = new BackgroundJobRuntimeOptions();
            var ids = new SystemGuidFactory();
            Observer = new(definitions, Time);
            Gate = new(definitions.Infrastructure);
            var executor = new JobExecutor(Substitute.For<IServiceScopeFactory>(), Substitute.For<ITenantContextAccessor>(),
                new(definitions, definitions, Substitute.For<IAuditCatalog>()), bounds, Observer);
            Runtime = new(Source, definitions, BackgroundJobRecurrenceRegistry.CreateEmpty(), executor,
                new(Source, definitions, bounds, ids, Gate), Gate, bounds, ids, Time, new(false, 1), new());
            Runtime.BindMessagingLifetime(Task.CompletedTask, Task.CompletedTask);
            _control = (BackgroundJobControl)typeof(BackgroundJobRuntime).GetField("_jobs", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(Runtime)!;
        }
        internal void Observe(IEnumerable<BackgroundJobObservationPoint> points)
        {
            Set("_observation", BackgroundJobDurableObservation.Create(BackgroundJobDurableObservationTests.EmptyManifest(), points));
            Set("_observationObservedAt", Time.GetTimestamp());
        }
        internal void ObserveRenewals(long acknowledged, long historyConfirmed)
        {
            Set("_renewals", acknowledged);
            Set("_renewalsConfirmed", historyConfirmed);
        }
        internal void ObserveLeaseLosses(long refused, long elapsed)
        {
            Set("_leaseLossRenewalRefused", refused);
            Set("_leaseLossElapsed", elapsed);
        }
        internal void ObserveRecurrence()
        {
            var recurrence = typeof(BackgroundJobControl).GetField("_recurrence", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(_control)!;
            // Only listener projection; the real SQL owner is qualified separately.
            foreach (var (name, value) in new (string, long)[]
                     {
                         ("_occurrences", 2), ("_skippedSlots", 7), ("_pages", 3), ("_routesExamined", 11),
                         ("_recipientsInserted", 5), ("_fanoutsCompleted", 1),
                     })
                typeof(BackgroundJobRecurrenceControl).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(recurrence, value);
        }
        private void Set(string name, object value) => typeof(BackgroundJobControl)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_control, value);
        public async ValueTask DisposeAsync()
        {
            try { await Runtime.StopAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            finally { Observer.Dispose(); await Source.DisposeAsync(); }
        }
    }

    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        internal void Advance(TimeSpan elapsed) => Interlocked.Add(ref _ticks, elapsed.Ticks);
    }
}
