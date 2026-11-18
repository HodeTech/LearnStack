using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.Modules.Identity.Application.Contracts.Entry;

/// <summary>
/// ADR-0099 Amendment 6 (H): the native self read's host-alias source. The global self path ends before tenant
/// resolution, so this one enumerated setter announces the host tenant read-only, host-only, and reads exactly the
/// host tenant's Active, non-tombstone federation alias and that protocol's plan entitlement. It never writes and grants
/// nothing: the BFF compares a brokered ID token's <c>idp</c> with it before publishing credentials.
/// </summary>
public interface IFederationHostAliasReader
{
    /// <summary>
    /// The host tenant's Active, entitled alias, or <see langword="null"/> when there is none. Throws
    /// <see cref="LearnStack.SharedKernel.Errors.InfrastructureException"/> when it cannot decide, which the self read
    /// reports as unavailable: the BFF then treats the outcome as indeterminate and publishes nothing brokered.
    /// </summary>
    Task<string?> ReadActiveAliasAsync(TenantId hostTenant, CancellationToken cancellationToken = default);
}
