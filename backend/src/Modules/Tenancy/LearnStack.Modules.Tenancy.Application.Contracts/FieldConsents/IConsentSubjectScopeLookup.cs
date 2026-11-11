using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;

namespace LearnStack.Modules.Tenancy.Application.Contracts.FieldConsents;

public enum ConsentSubjectLookupMode { List = 1, Select = 2 }

/// <summary>
/// Preliminary retained-scope observation under a supplied host ceiling. This is not
/// credential, global-binding or host admission and supplies no tenant authority.
/// </summary>
public interface IConsentSubjectScopeLookup
{
    Task<IReadOnlyList<ConsentSubjectScopeObservation>> LookupAsync(UserId actor, TenantId hostTenant,
        OrganizationId? hostOrganization, ConsentSubjectLookupMode mode, Guid? membershipId,
        Guid? afterMembership, int limit, CancellationToken cancellationToken = default);
}
