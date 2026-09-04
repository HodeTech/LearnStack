namespace LearnStack.SharedKernel.Tenancy;

/// <summary>
/// Which signals agreed to produce a tenant context — and therefore how far that
/// context is allowed to reach.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is an authority ceiling, not a provenance label.</b>
/// <see href="../../../../docs/decisions/0036-tenant-resolution-trusted-inputs.md">ADR-0036</see>
/// makes it the mechanism that keeps a forged <c>Host</c> harmless: a context
/// assembled from the host alone reaches only request types marked
/// <c>[PublicSurface]</c>, so the worst a forged host buys is the pages that
/// hostname already serves to anyone who types it — and only while the mapping row
/// is publicly live. Without the ceiling the trusted hop is a confused deputy,
/// because the edge derives its own assertion from the same string the visitor
/// chose, so the assertion comparison cannot catch it.
/// </para>
/// <para>
/// <b>The value names the signals that AGREED, not every port consulted.</b>
/// Matrix rows 7 and 10 ask <c>ITenantMembershipReader</c> and still carry
/// <see cref="HostAndClaim"/>: membership confirms a claim there, it does not
/// select anything. Only row 14 — where no host names a tenant at all — is
/// carried by membership, and only that row is
/// <see cref="ClaimAndMembership"/>.
/// </para>
/// <para>
/// There is deliberately <b>no member for the matrix rows whose Origin is "—"</b>.
/// Those rows resolve nothing: either the request is refused, or it legitimately
/// carries no tenant and runs under <see cref="UnresolvedTenantContext"/>, whose
/// <c>IsResolved</c> is <c>false</c>. A fifth member would be a resolved-looking
/// context with no authority behind it, which is the partially populated context
/// ADR-0036 § Rules forbids the factory from ever returning.
/// </para>
/// </remarks>
public enum TenantContextOrigin
{
    /// <summary>
    /// The host named the tenant and nothing else did — an anonymous page load.
    /// Reaches <c>[PublicSurface]</c> request types and nothing else.
    /// </summary>
    HostOnly,

    /// <summary>
    /// The host and a validated token claim named the same tenant. The ordinary
    /// authenticated request.
    /// </summary>
    HostAndClaim,

    /// <summary>
    /// No host named a tenant — a platform host — and a validated claim did,
    /// confirmed by an active membership. The Studio tenant switcher.
    /// </summary>
    ClaimAndMembership,

    /// <summary>
    /// Not an HTTP request at all: a background job's parameters or an integration
    /// event's envelope carried the tenant. There is no host and no token to
    /// reconcile, so the enqueuing side is where a missing tenant fails.
    /// </summary>
    Ambient,
}
