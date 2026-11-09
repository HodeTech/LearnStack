using LearnStack.Modules.Tenancy.Application.Contracts.FieldConsents;
using LearnStack.Modules.Tenancy.Domain;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Errors;
using LearnStack.SharedKernel.Persistence;

namespace LearnStack.Modules.Tenancy.Application.Abstractions;

public sealed record MembershipFieldConsentAppendState(MembershipFieldConsent? Current, MembershipFieldConsent? LatestGrant);

/// <summary>A single immutable consent root on the actual exclusive tenant plan.</summary>
public interface IMembershipFieldConsentWriteStore : IAggregateWriteStore<MembershipFieldConsent, MembershipFieldConsentId>
{
    Task<MembershipFieldConsentAppendState> ReadForAppendAsync(IdentityMutationLockPlan plan,
        MembershipFieldConsentTuple tuple, CancellationToken cancellationToken = default);
}

/// <summary>A fixed storage failure that never carries provider text or consent data.</summary>
public sealed class MembershipFieldConsentStorageException()
    : InfrastructureException("Consent evidence could not be persisted.");
