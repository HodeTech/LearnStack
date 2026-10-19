using System.Diagnostics;
using System.Reflection;
using FluentAssertions;
using LearnStack.Infrastructure.Jobs;
using Xunit;

namespace LearnStack.Tests.Unit.Infrastructure.Jobs;

public sealed partial class BackgroundJobObservationTests
{
    private static readonly string[] TraceOperations =
    [
        "job.admission", "job.execution", "job.external_execute", "job.external_reconcile", "job.external_read",
        "job.external_checkpoint", "job.capacity_cleanup", "job.recipient_submission", "job.terminal",
    ];

    [Theory]
    [InlineData(-1, false)]
    [InlineData(-1, true)]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public void Actual_trace_factory_returns_owned_unstarted_W3C_spans_with_trusted_parents_or_explicit_roots(int sampling, bool root)
    {
        using var observer = new BackgroundJobObservation(BackgroundJobDefinitionRegistry.CreateEmpty(), TimeProvider.System);
        var source = TraceSource(observer);
        using var listener = new ActivityListener
        {
            ShouldListenTo = candidate => sampling >= 0 && ReferenceEquals(candidate, source),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => (ActivitySamplingResult)sampling,
        };
        ActivitySource.AddActivityListener(listener);
        using var ambient = new Activity("foreign-tenant-activity").SetIdFormat(ActivityIdFormat.W3C).Start();
        var parent = root ? default : new ActivityContext(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(),
            ActivityTraceFlags.Recorded, isRemote: true);
        var previous = Activity.Current;
        try
        {
            for (var index = 0; index < TraceOperations.Length; index++)
            {
                Activity.Current = root ? null : ambient;
                var before = Activity.Current;
                using var activity = observer.CreateActivity((JobTraceOperation)index, parent);
                activity.Id.Should().BeNull("CreateActivity must not invoke Start before the concrete owner can assign it");
                Activity.Current.Should().BeSameAs(before);
                activity.OperationName.Should().Be(TraceOperations[index]);
                activity.Kind.Should().Be(ActivityKind.Internal);
                activity.Source.Name.Should().Be(sampling > 0 ? BackgroundJobObservation.InstrumentationName : string.Empty);
                activity.Start();
                activity.IdFormat.Should().Be(ActivityIdFormat.W3C);
                activity.TraceId.Should().NotBe(ambient.TraceId);
                activity.ParentSpanId.Should().Be(root ? default : parent.SpanId);
                if (root) activity.ParentId.Should().BeNull("a fresh root must not invent an all-zero parent identifier");
                if (!root) activity.TraceId.Should().Be(parent.TraceId);
                Activity.Current.Should().BeSameAs(activity);
            }
        }
        finally { Activity.Current = previous; }
        observer.IsHealthy.Should().BeTrue();
    }

    [Fact]
    public void Sampling_faults_mark_observation_unhealthy_and_return_an_unstarted_fallback_without_repeating_notification()
    {
        using var observer = new BackgroundJobObservation(BackgroundJobDefinitionRegistry.CreateEmpty(), TimeProvider.System);
        var source = TraceSource(observer);
        var samples = 0;
        using var listener = new ActivityListener
        {
            ShouldListenTo = candidate => ReferenceEquals(candidate, source),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
            {
                samples++;
                throw new InvalidOperationException("Deliberate sampler fault.");
            },
        };
        ActivitySource.AddActivityListener(listener);
        using var activity = observer.CreateActivity(JobTraceOperation.Admission,
            new(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded));
        activity.Id.Should().BeNull();
        activity.Source.Name.Should().BeEmpty();
        samples.Should().Be(1);
        observer.IsHealthy.Should().BeFalse();
    }

    private static ActivitySource TraceSource(BackgroundJobObservation observer) =>
        (ActivitySource)typeof(BackgroundJobObservation).GetField("_activities", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(observer)!;
}
