using System.Text.Json;
using FluentAssertions;
using LearnStack.Modules.Identity.Application.Contracts.IntegrationEvents;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Messaging;
using LearnStack.SharedKernel.Tenancy;
using Xunit;

namespace LearnStack.Tests.Unit.SharedKernel.Audit;

public sealed class ProviderEventAuditEvidenceTests
{
    [Fact]
    public void Source_failure_is_separate_from_successful_observation_and_affected_person_is_not_the_actor()
    {
        var (evidence, intent) = Pair();
        evidence.BindTo(intent);
        var row = AuditDraftComposer.InTransaction(intent, []);
        row.Outcome.Should().Be(AuditOutcome.Success);
        row.ActorUserId.Should().Be(UserId.SystemActor);
        row.EntityId.Should().Be(evidence.AffectedUserId.Value.ToString("D"));
        row.EntityType.Should().Be("User");
        row.Timestamp.Should().Be(intent.DeclaredAt);
        row.BeforeState.Should().BeNull(); row.AfterState.Should().BeNull(); row.Changes.Should().BeNull();
        using var metadata = JsonDocument.Parse(row.Metadata!);
        metadata.RootElement.GetProperty("provider_event").GetProperty("provider_outcome").GetString().Should().Be("error");
        metadata.RootElement.GetProperty("provider_event").GetProperty("scope").GetString().Should().Be("provider_account_history");
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("actor")]
    [InlineData("org")]
    [InlineData("operation")]
    [InlineData("causation")]
    [InlineData("class")]
    [InlineData("type")]
    public void Evidence_cannot_be_attached_to_a_different_authority_shape(string defect)
    {
        var (evidence, original) = Pair();
        var changed = defect switch
        {
            "tenant" => original with { TenantId = TenantId.From(Guid.CreateVersion7()) },
            "actor" => original with { ActorUserId = UserId.From(Guid.CreateVersion7()) },
            "org" => original with { OrganizationId = OrganizationId.From(Guid.CreateVersion7()) },
            "operation" => original with { Operation = "audit.event.read" },
            "causation" => original with { CausationId = Guid.CreateVersion7() },
            "class" => original with { OperationClass = OperationClass.May },
            "type" => original with { EntityType = typeof(object) },
            _ => throw new ArgumentOutOfRangeException(nameof(defect)),
        };
        Assert.Throws<InvalidOperationException>(() => evidence.BindTo(changed));
    }

    [Fact]
    public void Evidence_is_single_use_and_an_unbound_or_missing_attachment_fails_closed()
    {
        var (evidence, intent) = Pair();
        Assert.Throws<InvalidOperationException>(() => AuditDraftComposer.InTransaction(intent, []));
        Assert.Throws<InvalidOperationException>(() => AuditDraftComposer.InTransaction(intent with { ProviderEvent = null }, []));
        evidence.BindTo(intent);
        Assert.Throws<InvalidOperationException>(() => evidence.BindTo(intent));
        Assert.Throws<InvalidOperationException>(() => AuditDraftComposer.InTransaction(intent with { Id = AuditEntryId.From(Guid.CreateVersion7()) }, []));
        Assert.Throws<InvalidOperationException>(() => AuditDraftComposer.InTransaction(intent, [new("User", "changed", null, "{}", [])]));
    }

    private static (ProviderEventAuditEvidence Evidence, AuditIntent Intent) Pair()
    {
        var at = DateTimeOffset.UnixEpoch.AddMinutes(10);
        var fact = new UserProviderEventObservedIntegrationEventV1
        { EventId = Guid.CreateVersion7(), OccurredAt = at, TenantId = Guid.CreateVersion7(), TargetId = Guid.CreateVersion7() };
        var context = EventTenantContext.FromEnvelope(new IntegrationEventEnvelope(fact,
            "00-11111111111111111111111111111111-2222222222222222-01"), "audit");
        var evidence = Minter.Create(context, fact.TargetId, Guid.CreateVersion7(), Guid.NewGuid(), IdentityAuthorityId.From(Guid.CreateVersion7()),
            "learnstack", UserId.From(Guid.CreateVersion7()), IdentityProviderEventKind.LoginFailure, at.AddMinutes(-1));
        var intent = new AuditIntent(AuditEntryId.From(Guid.CreateVersion7()), context.TenantId, null, UserId.SystemActor,
            context.CorrelationId, "audit", "audit.provider_event.observe", OperationType.SecurityEvent, OperationClass.Must,
            typeof(ProviderEventAuditEvidence), at)
        { ProviderEvent = evidence, CausationId = fact.EventId };
        return (evidence, intent);
    }
    private sealed class Minter : ProviderEventAuditEvidence.MintingSource
    {
        internal static ProviderEventAuditEvidence Create(EventTenantContext context, Guid target, Guid receipt, Guid providerEvent,
            IdentityAuthorityId authority, string realm, UserId affected, IdentityProviderEventKind kind, DateTimeOffset at) =>
            Observe(context, target, receipt, providerEvent, authority, realm, affected, kind, at);
    }
}
