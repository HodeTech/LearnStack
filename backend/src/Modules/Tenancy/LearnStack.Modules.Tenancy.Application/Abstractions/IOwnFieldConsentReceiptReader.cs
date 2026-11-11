using LearnStack.Modules.Tenancy.Domain;
using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.Modules.Tenancy.Application.Abstractions;

/// <summary>One retained consent audit subject, restricted to admitted own memberships.</summary>
public interface IOwnFieldConsentReceiptReader
{
    Task<MembershipFieldConsent?> ReadAsync(UserId actor, IReadOnlyCollection<Guid> memberships, Guid evidenceId,
        CancellationToken cancellationToken = default);
}
