using FluentAssertions;
using LearnStack.Infrastructure.Jobs;
using LearnStack.SharedKernel.Jobs;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace LearnStack.Tests.Unit.Infrastructure.Jobs;

public sealed class BackgroundJobDurableObservationTests
{
    private static readonly string[] Artifacts = ["job", "recurrence", "occurrence", "recipient"];

    [Fact]
    public void Complete_samples_copy_their_finite_points_and_keep_deferred_and_quarantine_separate()
    {
        var points = EmptyPoints().ToList();
        points.Add(new(BackgroundJobObservationFamily.Deferred, "recipient", 1, 2, 0, 0));
        var observed = BackgroundJobDurableObservation.Create(EmptyManifest(), points);
        points.Clear();
        observed.Points.Should().HaveCount(5);
        observed.HasDeferredWork.Should().BeTrue();
        observed.HasQuarantinedJobs.Should().BeFalse();
        observed.HasQuarantinedControl.Should().BeFalse();
        var copy = observed.Points.ToArray();
        copy[0] = copy[0] with { Count = 9 };
        observed.HasQuarantinedJobs.Should().BeFalse();

        var quarantined = BackgroundJobDurableObservation.Create(EmptyManifest(),
            EmptyPoints().Select(point => point.Label == "occurrence" ? point with { Count = 1 } : point));
        quarantined.HasQuarantinedControl.Should().BeTrue();
        quarantined.HasQuarantinedJobs.Should().BeFalse();
        quarantined.HasDeferredWork.Should().BeFalse();
    }

    [Fact]
    public void Missing_duplicate_nonfinite_negative_and_unknown_label_points_refuse_the_entire_sample()
    {
        var invalid = new[]
        {
            new BackgroundJobObservationPoint((BackgroundJobObservationFamily)99, "job", 0, 0, 0, 0),
            new(BackgroundJobObservationFamily.Queue, "tenant:019712ac-1234-7000-8000-0000000000ab", 0, 1, 0, 0),
            new(BackgroundJobObservationFamily.FanoutPending, "unknown.valid_looking_key", 1, 1, 0, 0),
            new(BackgroundJobObservationFamily.ReservedCapacity, "unknown.valid_looking_key", 0, 1, 0, 0),
            new(BackgroundJobObservationFamily.Deferred, "job", 0, 1, 0, 0),
            new(BackgroundJobObservationFamily.Queue, "external", 0, -1, 0, 0),
            new(BackgroundJobObservationFamily.Queue, "external", 0, 1, 2, 1),
            new(BackgroundJobObservationFamily.Queue, "external", 0, 1, 0, 1),
            new(BackgroundJobObservationFamily.Queue, "external", 0, 1, 1, double.NaN),
            new(BackgroundJobObservationFamily.Queue, "external", 0, 1, 1, double.PositiveInfinity),
            new(BackgroundJobObservationFamily.Queue, "external", 0, 1, 1, -1),
            new(BackgroundJobObservationFamily.ReservedCapacity, "unknown", 0, 1, 1, 0),
        };
        foreach (var point in invalid)
        {
            var act = () => BackgroundJobDurableObservation.Create(EmptyManifest(), EmptyPoints().Append(point));
            act.Should().Throw<InvalidOperationException>();
        }
        var missing = () => BackgroundJobDurableObservation.Create(EmptyManifest(), EmptyPoints().Skip(1));
        missing.Should().Throw<InvalidOperationException>();
        var duplicate = () => BackgroundJobDurableObservation.Create(EmptyManifest(), EmptyPoints().Append(EmptyPoints().First()));
        duplicate.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Frozen_nonempty_manifests_bind_equal_length_typed_metadata_arrays_without_constructing_an_adapter()
    {
        var fixture = JobModuleFixture.Shared;
        var definitions = fixture.Registry(fixture.Transactional(root: "OccurrenceRoot"), fixture.External());
        var anchor = new DateTimeOffset(2026, 10, 18, 0, 0, 0, TimeSpan.Zero);
        var registration = new BackgroundJobRecurrenceRegistration("jobsfixture.scan", fixture.Declaration, 3,
            anchor, new("jobsfixture.run.v1"), BackgroundJobRecurrenceSchedule.Interval, anchor,
            TimeSpan.FromDays(1), TimeSpan.FromMinutes(1), BackgroundJobRecipientPolicy.ServingTenants);
        var recurrences = BackgroundJobRecurrenceRegistry.Create([registration], definitions, [fixture.Module]);
        var manifest = BackgroundJobObservationManifest.Create(definitions, recurrences);
        using var command = new NpgsqlCommand();
        var retained = Guid.NewGuid();
        manifest.Bind(command, [retained]);
        foreach (NpgsqlParameter parameter in command.Parameters)
            parameter.NpgsqlDbType.HasFlag(NpgsqlDbType.Array).Should().BeTrue(parameter.ParameterName);
        var definitionParameters = command.Parameters.Cast<NpgsqlParameter>()
            .Where(parameter => parameter.ParameterName.StartsWith("d_", StringComparison.Ordinal)).ToArray();
        definitionParameters.Should().HaveCount(8);
        foreach (var parameter in definitionParameters)
            parameter.Value.Should().BeAssignableTo<Array>().Which.Length.Should().Be(2);
        command.Parameters["d_keys"].Value.Should().BeOfType<string[]>().Which.Should().Equal("jobsfixture.external.v1", "jobsfixture.run.v1");
        command.Parameters["d_groups"].Value.Should().BeOfType<string?[]>().Which.Should().Equal("jobsfixture.resource", null);
        command.Parameters["d_occurrence"].Value.Should().BeOfType<bool[]>().Which.Should().Equal(false, true);
        command.Parameters["r_revisions"].Value.Should().BeOfType<long[]>().Which.Should().Equal(3);
        command.Parameters["retained_jobs"].Value.Should().BeOfType<Guid[]>().Which.Should().Equal(retained);
        manifest.Allows(new(BackgroundJobObservationFamily.ReservedCapacity, "jobsfixture.resource", 0, 1, 0, 0)).Should().BeTrue();
        manifest.Allows(new(BackgroundJobObservationFamily.FanoutPending, "jobsfixture.scan", 2, 1, 0, 0)).Should().BeTrue();
        manifest.Allows(new(BackgroundJobObservationFamily.FanoutPending, "jobsfixture.other", 2, 1, 0, 0)).Should().BeFalse();
    }

    [Fact]
    public void Empty_manifests_keep_explicit_array_types_and_reject_forged_retained_identities()
    {
        using var command = new NpgsqlCommand();
        var manifest = EmptyManifest();
        manifest.Bind(command, []);
        foreach (NpgsqlParameter parameter in command.Parameters)
        {
            parameter.NpgsqlDbType.HasFlag(NpgsqlDbType.Array).Should().BeTrue();
            parameter.Value.Should().BeAssignableTo<Array>().Which.Length.Should().Be(0);
        }
        var empty = () => manifest.Bind(command, [Guid.Empty]);
        empty.Should().Throw<ArgumentException>();
        var id = Guid.NewGuid();
        var duplicate = () => manifest.Bind(command, [id, id]);
        duplicate.Should().Throw<ArgumentException>();
    }

    internal static BackgroundJobObservationManifest EmptyManifest() =>
        BackgroundJobObservationManifest.Create(BackgroundJobDefinitionRegistry.CreateEmpty(), BackgroundJobRecurrenceRegistry.CreateEmpty());

    internal static IEnumerable<BackgroundJobObservationPoint> EmptyPoints() =>
        Artifacts.Select(label =>
            new BackgroundJobObservationPoint(BackgroundJobObservationFamily.Quarantine, label, 0, 0, 0, 0));
}
