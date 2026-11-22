using FluentAssertions;
using LearnStack.Application.DataProtection;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Identifiers;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LearnStack.Tests.Unit.SharedKernel.DataProtection;

/// <summary>The SharedKernel data-request ports (ADR-0106 § 7, ADR-0107 § 1 and § 4) and the participant seam.</summary>
public sealed class DataRequestPortTests
{
    private static readonly TenantId Tenant = TenantId.From(Guid.CreateVersion7());
    private static readonly UserId Subject = UserId.From(Guid.CreateVersion7());

    [Fact]
    public void A_scope_group_is_a_real_tenant_and_an_optional_real_organization()
    {
        new DataRequestScopeGroup(Tenant, null).IsTenantWide.Should().BeTrue();
        new DataRequestScopeGroup(Tenant, OrganizationId.From(Guid.CreateVersion7())).IsTenantWide.Should().BeFalse();
        FluentActions.Invoking(() => new DataRequestScopeGroup(TenantId.PlatformSentinel, null)).Should().Throw<ArgumentException>(
            "a tenant request never carries the platform sentinel");
        FluentActions.Invoking(() => new DataRequestScopeGroup(TenantId.From(Guid.Empty), null)).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => new DataRequestScopeGroup(Tenant, OrganizationId.From(Guid.Empty))).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_participant_subject_carries_a_bounded_frozen_snapshot_and_prints_nothing()
    {
        var subject = new UserDataSubject(Guid.CreateVersion7(), DataRequestKind.Export, DataRequestOrigin.Self, Subject,
            new DataRequestScopeGroup(Tenant, null), [Guid.CreateVersion7()]);
        subject.ToString().Should().Be(nameof(UserDataSubject));
        subject.MembershipIds.Should().ContainSingle();

        var group = new DataRequestScopeGroup(Tenant, null);
        FluentActions.Invoking(() => new UserDataSubject(Guid.CreateVersion7(), DataRequestKind.Export, DataRequestOrigin.Self,
            Subject, group, [])).Should().Throw<ArgumentException>();
        var duplicate = Guid.CreateVersion7();
        FluentActions.Invoking(() => new UserDataSubject(Guid.CreateVersion7(), DataRequestKind.Export, DataRequestOrigin.Self,
            Subject, group, [duplicate, duplicate])).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => new UserDataSubject(Guid.CreateVersion7(), DataRequestKind.Export, DataRequestOrigin.Self,
            UserId.SystemActor, group, [Guid.CreateVersion7()])).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => new UserDataSubject(Guid.Empty, DataRequestKind.Export, DataRequestOrigin.Self,
            Subject, group, [Guid.CreateVersion7()])).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Eligibility_queries_name_their_request_only_at_execution_and_decisions_are_closed()
    {
        var group = new DataRequestScopeGroup(Tenant, null);
        _ = new DataRequestEligibilityQuery(DataRequestKind.Erasure, DataRequestOrigin.Self, Subject, group, DataRequestEligibilityStage.Creation);
        _ = new DataRequestEligibilityQuery(DataRequestKind.Erasure, DataRequestOrigin.Self, Subject, group,
            DataRequestEligibilityStage.Execution, Guid.CreateVersion7());
        FluentActions.Invoking(() => new DataRequestEligibilityQuery(DataRequestKind.Erasure, DataRequestOrigin.Self, Subject, group,
            DataRequestEligibilityStage.Execution)).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => new DataRequestEligibilityQuery(DataRequestKind.Erasure, DataRequestOrigin.Self, Subject, group,
            DataRequestEligibilityStage.Creation, Guid.CreateVersion7())).Should().Throw<ArgumentException>();

        DataRequestEligibilityDecision.Eligible.IsEligible.Should().BeTrue();
        DataRequestEligibilityDecision.Refused(DataRequestIneligibility.BlockedHold).Refusal.Should().Be(DataRequestRefusal.BlockedHold);
        DataRequestEligibilityDecision.Refused(DataRequestIneligibility.BlockedLastAdministrator).Refusal
            .Should().Be(DataRequestRefusal.BlockedLastAdministrator);
        DataRequestEligibilityDecision.Refused(DataRequestIneligibility.RequestAlreadyActive).Refusal.Should().BeNull(
            "an already-active request is a 409 response, not a recorded refusal");
        FluentActions.Invoking(() => DataRequestEligibilityDecision.Refused((DataRequestIneligibility)9))
            .Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task The_default_hold_source_reports_no_hold()
    {
        var query = new DataRequestHoldQuery(DataRequestKind.Closure, Subject, new DataRequestScopeGroup(Tenant, null));
        (await new NoHoldSource().HasHoldAsync(query, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public void Artifact_store_values_carry_the_chunk_bounds_and_print_no_content()
    {
        var put = new DataExportArtifactPut(Guid.CreateVersion7(), Guid.CreateVersion7(), Subject,
            new DataRequestScopeGroup(Tenant, null), DataRequestLimits.ChunkPlaintextBytes + 1);
        put.ChunkCount.Should().Be(2);
        FluentActions.Invoking(() => new DataExportArtifactPut(Guid.CreateVersion7(), Guid.CreateVersion7(), Subject,
            new DataRequestScopeGroup(Tenant, null), DataRequestLimits.MaximumBundleBytes + 1)).Should().Throw<ArgumentOutOfRangeException>();

        var chunk = new DataExportPlaintextChunk(0, 1, new byte[] { 1, 2, 3 });
        chunk.ToString().Should().Be(nameof(DataExportPlaintextChunk));
        FluentActions.Invoking(() => new DataExportPlaintextChunk(1, 1, new byte[] { 1 })).Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => new DataExportPlaintextChunk(0, 1, new byte[DataRequestLimits.ChunkPlaintextBytes + 1]))
            .Should().Throw<ArgumentOutOfRangeException>();
        DataRequestLimits.MaximumChunkCount.Should().Be(512);
        DataRequestLimits.ChunkPlaintextBytes.Should().Be(131072);
        DataRequestLimits.NonceBytes.Should().Be(12);
        DataRequestLimits.TagBytes.Should().Be(16);
    }

    [Fact]
    public void The_registry_orders_participants_by_key_and_refuses_duplicates_and_malformed_keys()
    {
        var registry = new UserDataParticipantRegistry([new Participant("tenancy"), new Participant("identity")]);
        registry.Participants.Select(participant => participant.Key).Should().Equal("identity", "tenancy");
        registry.TryGet("identity", out var found).Should().BeTrue();
        found!.Key.Should().Be("identity");
        registry.TryGet("Identity", out _).Should().BeFalse("keys are ordinal");

        FluentActions.Invoking(() => new UserDataParticipantRegistry([new Participant("identity"), new Participant("identity")]))
            .Should().Throw<InvalidOperationException>("two participants writing one progress key would skip unfinished work");
        FluentActions.Invoking(() => new UserDataParticipantRegistry([new Participant("Identity-Module")]))
            .Should().Throw<InvalidOperationException>();
        new UserDataParticipantRegistry([]).Participants.Should().BeEmpty("no module registers a participant yet");
    }

    [Fact]
    public async Task The_composition_seam_collects_module_registrations_and_defaults_the_hold_source()
    {
        var services = new ServiceCollection();
        services.AddLearnStackUserDataProtection();
        services.AddLearnStackUserDataProtection();
        services.AddScoped<IUserDataParticipant>(_ => new Participant("identity"));
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        await using var scope = provider.CreateAsyncScope();

        scope.ServiceProvider.GetRequiredService<IUserDataParticipantRegistry>().Participants.Should().ContainSingle()
            .Which.Key.Should().Be("identity");
        scope.ServiceProvider.GetRequiredService<IDataRequestHoldSource>().Should().BeOfType<NoHoldSource>();
        services.Count(descriptor => descriptor.ServiceType == typeof(IUserDataParticipantRegistry)).Should().Be(1);
    }

    [Fact]
    public async Task A_participant_without_an_erasure_erases_nothing()
    {
        IUserDataParticipant participant = new Participant("audit");
        var subject = new UserDataSubject(Guid.CreateVersion7(), DataRequestKind.Erasure, DataRequestOrigin.Self, Subject,
            new DataRequestScopeGroup(Tenant, null), [Guid.CreateVersion7()]);
        await participant.EraseAsync(subject, CancellationToken.None);
        (await participant.DescribeRetainedAsync(subject, CancellationToken.None)).Should().ContainSingle()
            .Which.Kind.Should().Be(RetainedReferenceKind.AuditActorReference);
    }

    private sealed class Participant(string key) : IUserDataParticipant
    {
        public string Key => key;

        public Task ExportAsync(UserDataSubject subject, IUserDataExportWriter writer, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<IReadOnlyCollection<RetainedReferenceCount>> DescribeRetainedAsync(UserDataSubject subject,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyCollection<RetainedReferenceCount>>([new(RetainedReferenceKind.AuditActorReference, 1)]);
    }
}
