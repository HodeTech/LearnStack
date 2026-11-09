using LearnStack.Modules.Tenancy.Domain;
using LearnStack.SharedKernel.Identifiers;
using Xunit;

namespace LearnStack.Tests.Unit.Tenancy;

public sealed class MembershipFieldConsentTransitionTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly TenantId Tenant = TenantId.From(Guid.CreateVersion7());
    private static readonly UserId Subject = UserId.From(Guid.CreateVersion7());
    private static readonly Guid Member = Guid.CreateVersion7();
    private static readonly Guid Definition = Guid.CreateVersion7();

    [Fact]
    public void Withdrawal_is_immediate_but_regrant_observes_durable_grant_cooldown()
    {
        var grant = Act(1, MembershipFieldConsentState.Granted, null, At);
        Assert.Equal(MembershipFieldConsentTransitionDecision.Append,
            MembershipFieldConsentTransition.Decide(grant, grant, 1, grant.Id, MembershipFieldConsentState.Withdrawn, At));
        var withdrawal = Act(2, MembershipFieldConsentState.Withdrawn, grant, At.AddSeconds(1));
        Assert.Equal(MembershipFieldConsentTransitionDecision.GrantCooldown,
            MembershipFieldConsentTransition.Decide(withdrawal, grant, 2, withdrawal.Id, MembershipFieldConsentState.Granted, At.AddSeconds(59)));
        Assert.Equal(MembershipFieldConsentTransitionDecision.Append,
            MembershipFieldConsentTransition.Decide(withdrawal, grant, 2, withdrawal.Id, MembershipFieldConsentState.Granted, At.AddSeconds(60)));
    }

    [Fact]
    public void Same_state_converges_and_stale_predecessor_cannot_override_withdrawal()
    {
        var grant = Act(1, MembershipFieldConsentState.Granted, null, At);
        var withdrawal = Act(2, MembershipFieldConsentState.Withdrawn, grant, At.AddSeconds(1));
        Assert.Equal(MembershipFieldConsentTransitionDecision.Unchanged,
            MembershipFieldConsentTransition.Decide(withdrawal, grant, 2, withdrawal.Id, MembershipFieldConsentState.Withdrawn, At.AddSeconds(1)));
        Assert.Equal(MembershipFieldConsentTransitionDecision.Stale,
            MembershipFieldConsentTransition.Decide(withdrawal, grant, 1, grant.Id, MembershipFieldConsentState.Granted, At.AddMinutes(2)));
    }

    private static MembershipFieldConsent Act(long sequence, MembershipFieldConsentState state,
        MembershipFieldConsent? previous, DateTimeOffset at) => MembershipFieldConsent.Create(
        MembershipFieldConsentId.From(Guid.CreateVersion7()), Tenant, null, Member, Subject, Subject,
        Definition, 1, "health-purpose", "en", new string('a', 64), sequence, state,
        previous?.Id, sequence - 1, at);
}
