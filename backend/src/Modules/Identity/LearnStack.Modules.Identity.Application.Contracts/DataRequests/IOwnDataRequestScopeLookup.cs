using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.Modules.Identity.Application.Contracts.DataRequests;

/// <summary>The two closed modes of <c>fn_identity_own_data_request_scopes</c>.</summary>
public enum OwnDataRequestLookupMode { List = 1, Select = 2 }

/// <summary>
/// Preliminary retained-scope observation for the subject of a data request under a supplied host ceiling
/// (ADR-0106 § 9). It returns the groups in the host's tenant (and, on an organization host, only that
/// organization's group) in which the actor holds or held a Membership of any status or has any data request.
/// This is not credential, global-binding or host admission and supplies no tenant authority: the caller
/// supplies an actor it has already normalized from a verified credential, never one taken from a request.
/// </summary>
public interface IOwnDataRequestScopeLookup
{
    /// <param name="actor">The verified subject.</param>
    /// <param name="hostTenant">The independently classified real host's tenant.</param>
    /// <param name="hostOrganization">The real host's organization, or <see langword="null"/> for a tenant host.</param>
    /// <param name="mode"><see cref="OwnDataRequestLookupMode.List"/> pages the groups in order; <see cref="OwnDataRequestLookupMode.Select"/> proves one exact group.</param>
    /// <param name="selected">For <see cref="OwnDataRequestLookupMode.Select"/> only: the group to prove.</param>
    /// <param name="after">For <see cref="OwnDataRequestLookupMode.List"/> only: the last group of the previous page.</param>
    /// <param name="limit">1–20 for a list, exactly 1 for a selection.</param>
    /// <param name="cancellationToken">The request's cancellation.</param>
    Task<IReadOnlyList<DataRequestScopeGroup>> LookupAsync(UserId actor, TenantId hostTenant,
        OrganizationId? hostOrganization, OwnDataRequestLookupMode mode, DataRequestScopeGroup? selected,
        DataRequestScopeGroup? after, int limit, CancellationToken cancellationToken = default);
}
