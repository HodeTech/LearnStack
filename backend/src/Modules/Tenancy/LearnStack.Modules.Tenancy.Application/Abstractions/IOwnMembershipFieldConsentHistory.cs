using LearnStack.Modules.Tenancy.Application.Contracts.FieldConsents;
using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.Modules.Tenancy.Application.Abstractions;

/// <summary>Exact retained evidence and latest tuple pages within an already admitted owner scope.</summary>
public interface IOwnMembershipFieldConsentHistory
{
    Task<MembershipFieldConsentDetails?> ReadEvidenceAsync(UserId subject, Guid membershipId, Guid evidenceId,
        CancellationToken cancellationToken = default);
    Task<MembershipFieldConsentDetails?> ReadExactAsync(UserId subject, Guid membershipId, Guid definitionId,
        int definitionVersion, CancellationToken cancellationToken = default);
    Task<MembershipFieldConsentPage?> ListLatestAsync(UserId subject, Guid membershipId, string? cursor, int limit,
        CancellationToken cancellationToken = default);
}
