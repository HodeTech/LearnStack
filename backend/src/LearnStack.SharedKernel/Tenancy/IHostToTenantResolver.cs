using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.SharedKernel.Tenancy;

/// <summary>
/// Answers "which tenant is this host?" by reading
/// <c>platform_host_to_tenant</c> and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <b>Never the Hub.</b> An anonymous page load must not depend on a control
/// plane being reachable, so this port reads the local table and no other source
/// (<see href="../../../../docs/decisions/0034-hub-contract-surface-invariant.md">ADR-0034</see>).
/// The Hub writes that table through
/// <c>PUT /api/internal/tenants/{id}/host-mappings</c>; the resolver only reads
/// what is already there.
/// </para>
/// <para>
/// <b>It runs before any tenant context exists</b> — that is what it is for — so
/// it cannot use a module <c>DbContext</c> or the ambient
/// <c>IUnitOfWork</c>. Its implementation opens a short read-only transaction of
/// its own and is the single setter of <c>app.resolving_host</c>, the fourth and
/// last canonical session variable
/// (<see href="../../../../docs/standards/11-security.md">Security Standards
/// § Tenant Context</see>).
/// </para>
/// </remarks>
public interface IHostToTenantResolver
{
    /// <summary>
    /// The tenant behind <paramref name="host"/>, or <c>null</c> when no active,
    /// publicly-live mapping exists.
    /// </summary>
    /// <param name="host">
    /// The <b>effective</b> host, already normalized by
    /// <c>EffectiveHost.Normalize</c> — lowercase, punycoded, no port, no trailing
    /// dot. It is not re-normalized here:
    /// <see href="../../../../docs/decisions/0036-tenant-resolution-trusted-inputs.md">ADR-0036</see>
    /// puts that computation in exactly one place and
    /// <c>Effective_Host_Computed_In_One_Place</c> fails a second one. A caller
    /// passing a raw <c>Host</c> header gets a cache key and a policy predicate
    /// that match no row, which is a 404 rather than a wider read.
    /// </param>
    /// <param name="cancellationToken">Cancels this caller's wait, never the lookup.</param>
    Task<HostResolution?> ResolveAsync(string host, CancellationToken cancellationToken = default);
}

/// <summary>
/// What a host resolves to: a tenant, and the organization when the host serves
/// one.
/// </summary>
/// <remarks>
/// <b>The organization is the mapping row's, not a count.</b> A tenant that wants
/// its default organization's content on its public site seeds
/// <c>organization_id</c> into its <c>platform_host_to_tenant</c> row; the
/// resolver never infers a scope from how many organizations a tenant has
/// (ADR-0036 § The anonymous organization scope). <c>null</c> means the host is
/// tenant-wide.
/// </remarks>
public sealed record HostResolution(TenantId TenantId, OrganizationId? OrganizationId);
