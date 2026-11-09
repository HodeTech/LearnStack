using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.Modules.Tenancy.Domain;

public enum MembershipFieldConsentTransitionDecision { Append, Unchanged, Stale, GrantCooldown, SequenceExhausted }

/// <summary>
/// Evaluates a requested transition against rows freshly loaded on the exclusive tenant
/// fence. The caller supplies the last grant for the same exact tuple from durable storage.
/// </summary>
public static class MembershipFieldConsentTransition
{
    public const long MaximumSequence = 9007199254740991L;
    public static readonly TimeSpan GrantCooldown = TimeSpan.FromSeconds(60);

    public static MembershipFieldConsentTransitionDecision Decide(
        MembershipFieldConsent? current, MembershipFieldConsent? latestGrant,
        long expectedSequence, MembershipFieldConsentId? expectedId,
        MembershipFieldConsentState requestedState, DateTimeOffset now)
    {
        if (!Enum.IsDefined(requestedState) || expectedSequence < 0 || expectedSequence > MaximumSequence
            || (expectedSequence == 0) != (expectedId is null)
            || (expectedId is { } id && (!id.IsInitialized() || id.Value == Guid.Empty)))
            return MembershipFieldConsentTransitionDecision.Stale;
        if ((current?.Sequence ?? 0) != expectedSequence || current?.Id != expectedId)
            return MembershipFieldConsentTransitionDecision.Stale;
        if (current is null && requestedState == MembershipFieldConsentState.Withdrawn)
            return MembershipFieldConsentTransitionDecision.Stale;
        if (current is not null && latestGrant is null)
            throw new ArgumentException("A current consent requires its durable latest grant.", nameof(latestGrant));
        if (current is not null && current.State == requestedState)
            return MembershipFieldConsentTransitionDecision.Unchanged;
        if (current?.Sequence == MaximumSequence)
            return MembershipFieldConsentTransitionDecision.SequenceExhausted;
        if (latestGrant is not null && (current is null || latestGrant.State != MembershipFieldConsentState.Granted
            || latestGrant.Sequence > current.Sequence || !SameTuple(current, latestGrant)))
            throw new ArgumentException("The latest grant must belong to the current tuple.", nameof(latestGrant));
        if (requestedState == MembershipFieldConsentState.Granted && latestGrant is not null
            && now - latestGrant.CreatedAt < GrantCooldown)
            return MembershipFieldConsentTransitionDecision.GrantCooldown;
        return MembershipFieldConsentTransitionDecision.Append;
    }

    private static bool SameTuple(MembershipFieldConsent a, MembershipFieldConsent b) =>
        a.TenantId == b.TenantId && a.OrganizationId == b.OrganizationId && a.MembershipId == b.MembershipId
        && a.SubjectUserId == b.SubjectUserId && a.DefinitionId == b.DefinitionId
        && a.DefinitionVersion == b.DefinitionVersion && a.PurposeKey == b.PurposeKey;
}
