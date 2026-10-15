using FluentAssertions;
using LearnStack.Infrastructure.Jobs;
using LearnStack.SharedKernel.Jobs;
using Xunit;

namespace LearnStack.Tests.Unit.Infrastructure.Jobs;

public sealed class BackgroundJobRecurrenceTests
{
    private static JobModuleFixture Fixture => JobModuleFixture.Shared;
    private static readonly DateTimeOffset Anchor = new(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Identical_manifests_hash_identically_and_each_changed_field_changes_the_hash()
    {
        var original = Resolve(Registration());
        Resolve(Registration()).ManifestHash.Should().Be(original.ManifestHash);
        original.ManifestHash.Should().HaveLength(64);
        foreach (var changed in new[]
        {
            Registration(key: "jobsfixture.other"), Registration(revision: 2),
            Registration(activation: Anchor.AddDays(1)), Registration(anchor: Anchor.AddHours(1)),
            Registration(period: TimeSpan.FromHours(1)), Registration(jitter: TimeSpan.FromMinutes(1)),
            Registration(policy: BackgroundJobRecipientPolicy.ProvisionedNonArchivedTenants),
            Registration(schedule: BackgroundJobRecurrenceSchedule.Daily),
        }) Resolve(changed).ManifestHash.Should().NotBe(original.ManifestHash);
    }

    [Theory]
    [InlineData(BackgroundJobRecipientPolicy.ProvisionedNonArchivedTenants, BackgroundJobScope.TenantWide)]
    [InlineData(BackgroundJobRecipientPolicy.ServingTenants, BackgroundJobScope.TenantWide)]
    [InlineData(BackgroundJobRecipientPolicy.ServingOrganizations, BackgroundJobScope.Organization)]
    public void Recipient_policy_binds_exact_target_scope(BackgroundJobRecipientPolicy policy, BackgroundJobScope scope)
    {
        Resolve(Registration(policy: policy), scope).Scope.Should().Be(scope);
        var wrong = scope == BackgroundJobScope.TenantWide ? BackgroundJobScope.Organization : BackgroundJobScope.TenantWide;
        var action = () => Resolve(Registration(policy: policy), wrong);
        action.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Target_must_exist_and_use_the_fixed_occurrence_contract()
    {
        var missing = () => BackgroundJobRecurrenceRegistry.Create([Registration()], BackgroundJobDefinitionRegistry.CreateEmpty(), [Fixture.Module]);
        missing.Should().Throw<ArgumentException>();
        var ordinary = () => BackgroundJobRecurrenceRegistry.Create([Registration()], Fixture.Registry(Fixture.Transactional()), [Fixture.Module]);
        ordinary.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Binary_cannot_declare_multiple_revisions_for_one_recurrence_key()
    {
        var action = () => BackgroundJobRecurrenceRegistry.Create([Registration(), Registration(revision: 2)],
            Fixture.Registry(Fixture.Transactional(root: "OccurrenceRoot")), [Fixture.Module]);
        action.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("activation")]
    [InlineData("anchor")]
    [InlineData("period")]
    [InlineData("jitter")]
    public void Submicrosecond_facts_are_refused_before_freezing_a_durable_manifest_hash(string field)
    {
        var registration = field switch
        {
            "activation" => Registration(activation: Anchor.AddTicks(1)),
            "anchor" => Registration(anchor: Anchor.AddTicks(1)),
            "period" => Registration(period: TimeSpan.FromDays(1) + TimeSpan.FromTicks(1)),
            _ => Registration(jitter: TimeSpan.FromTicks(1)),
        };
        var action = () => Resolve(registration);
        action.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Microsecond_facts_are_accepted_and_remain_part_of_the_fingerprint()
    {
        var original = Resolve(Registration()).ManifestHash;
        foreach (var registration in new[]
                 {
                     Registration(activation: Anchor.AddTicks(10)), Registration(anchor: Anchor.AddTicks(10)),
                     Registration(period: TimeSpan.FromDays(1) + TimeSpan.FromTicks(10)),
                     Registration(jitter: TimeSpan.FromTicks(10)),
                 })
            Resolve(registration).ManifestHash.Should().NotBe(original);
    }

    [Fact]
    public void Interval_and_jitter_boundaries_are_inclusive()
    {
        Resolve(Registration(period: TimeSpan.FromMinutes(1), jitter: TimeSpan.FromMinutes(1)));
        Resolve(Registration(period: TimeSpan.FromDays(366), jitter: TimeSpan.FromHours(2)));
        var invalid = new[]
        {
            Registration(key: "jobsfixture.scan.v1"), Registration(key: "other.scan"), Registration(revision: 0),
            Registration(activation: Anchor.ToOffset(TimeSpan.FromHours(1))),
            Registration(anchor: Anchor.ToOffset(TimeSpan.FromHours(1))),
            Registration(period: TimeSpan.FromMinutes(1) - TimeSpan.FromTicks(1)),
            Registration(period: TimeSpan.FromDays(366) + TimeSpan.FromTicks(1)),
            Registration(jitter: TimeSpan.FromTicks(-1)), Registration(jitter: TimeSpan.FromHours(2) + TimeSpan.FromTicks(1)),
            Registration(period: TimeSpan.FromMinutes(1), jitter: TimeSpan.FromMinutes(1) + TimeSpan.FromTicks(1)),
            Registration(schedule: BackgroundJobRecurrenceSchedule.Daily, period: TimeSpan.FromHours(12)),
            Registration(policy: (BackgroundJobRecipientPolicy)99), Registration(schedule: (BackgroundJobRecurrenceSchedule)99),
        };
        foreach (var registration in invalid)
        {
            var action = () => Resolve(registration);
            action.Should().Throw<ArgumentException>();
        }
    }

    [Fact]
    public void Pending_targets_are_preserved_and_unknown_recurrences_have_no_terminal_disposition()
    {
        var registry = BackgroundJobRecurrenceRegistry.Create([Registration()], Fixture.Registry(
            Fixture.Transactional(root: "OccurrenceRoot", readiness: BackgroundJobDefinitionReadiness.Pending)), [Fixture.Module]);
        registry.TryGet("jobsfixture.scan", out _).Should().BeTrue();
        registry.TryGet("jobsfixture.future", out var future).Should().BeFalse();
        future.Should().BeNull();
    }

    private static BackgroundJobRecurrenceDescriptor Resolve(BackgroundJobRecurrenceRegistration registration,
        BackgroundJobScope scope = BackgroundJobScope.TenantWide)
    {
        var registry = BackgroundJobRecurrenceRegistry.Create([registration],
            Fixture.Registry(Fixture.Transactional(root: "OccurrenceRoot", scope: scope)), [Fixture.Module]);
        registry.TryGet(registration.Key, out var descriptor).Should().BeTrue();
        return descriptor!;
    }

    private static BackgroundJobRecurrenceRegistration Registration(string key = "jobsfixture.scan", long revision = 1,
        DateTimeOffset? activation = null, DateTimeOffset? anchor = null, TimeSpan? period = null, TimeSpan? jitter = null,
        BackgroundJobRecipientPolicy policy = BackgroundJobRecipientPolicy.ServingTenants,
        BackgroundJobRecurrenceSchedule schedule = BackgroundJobRecurrenceSchedule.Interval) =>
        new(key, Fixture.Declaration, revision, activation ?? Anchor, new("jobsfixture.run.v1"), schedule,
            anchor ?? Anchor, period ?? TimeSpan.FromDays(1), jitter ?? TimeSpan.Zero, policy);
}
