using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using MediatR;

namespace LearnStack.Application.Pipeline;

/// <summary>
/// MediatR pipeline behavior — step 4 of the canonical 8-step order
/// (ADR-0032 § Sub-decision 2). Asserts that the upstream resolution stage
/// populated <see cref="ITenantContext"/>, and that the context it produced reaches
/// as far as this request type. Two refusals with two codes: an unresolved context
/// short-circuits with <c>Result.Fail(tenant_mismatch)</c> unless the request carries
/// <c>[AllowsUnresolvedTenantContext]</c>, and a resolved context whose
/// <c>TenantContextOrigin</c> exceeds what the request type permits short-circuits
/// with <c>Result.Fail(not_found)</c> — a different code because the second refusal
/// must be indistinguishable on the wire from an unresolvable host, and
/// <c>tenant_mismatch</c> is the authenticated code. It does <strong>not</strong>
/// set the PostgreSQL RLS session variables: <c>SET LOCAL</c> is transaction-local and
/// this behavior runs at step 4, before any transaction exists. They are issued by
/// <c>TransactionBehavior</c> as the first statement inside the transaction at step 6
/// — see Security Standards § Tenant Context, the single authority for this
/// placement. Packet 6 shipped both halves: <c>TransactionBehavior</c> opens the
/// ambient transaction and calls <c>IUnitOfWork.SetTenantContextAsync</c> inside
/// it. Packet 7 step 5 added <c>TenantResolverMiddleware</c>, which is what now
/// gives that setter a tenant to write.
/// </summary>
/// <remarks>
/// <para>
/// <b>The ceiling is an allow-list, and that is load-bearing rather than stylistic.</b>
/// <see cref="ITenantContext.Origin"/> is a <i>nullable default interface member</i>,
/// so an implementation that states nothing carries <c>null</c> — and
/// <c>Origin != HostOnly</c> is <c>true</c> for <c>null</c>. Written as a negation this
/// gate would hand exactly the contexts that never thought about authority the run of
/// the API. Written as an allow-list over stated origins, an unstated one reaches
/// nothing, and so does any member added later whose ceiling nobody decided.
/// </para>
/// <para>
/// <b>The two gates are nested, not sequential.</b> The unresolved branch returns; it
/// never falls through to the ceiling. It cannot: an unresolved context states no
/// origin, so the fail-closed allow-list would refuse it — and that would <c>404</c>
/// precisely the rows 13 and 15 requests <c>[AllowsUnresolvedTenantContext]</c> exists
/// to admit. Fusing them the other way is worse: a marked request exempted from
/// <i>both</i> gates lets an anonymous caller reach a provisioning command by typing a
/// live tenant's hostname.
/// </para>
/// <para>
/// The RLS session variables are still issued by <c>TransactionBehavior</c> inside the
/// transaction at step 6, never from this behavior.
/// </para>
/// </remarks>
public sealed class TenantContextBehavior<TRequest, TResponse>(
    ITenantContext tenantContext)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
    where TResponse : IResultBase
{
    private static readonly Error TenantMismatchError = new(
        new LocalizedMessage("lockey_tenant_mismatch"));

    // Read once per closed generic rather than once per request: TRequest is fixed for
    // the lifetime of this type, so the reflection runs in the type initializer and
    // every request pays a static field read.
    //
    // inherit: false, matching the attributes' own Inherited = false. Reading with
    // inherit: true against a non-inherited attribute is not an error and not a
    // widening — it is a silent mismatch between what the marker declares and what the
    // reader looks for, and the day someone makes the attribute inheritable the reader
    // would not follow.
    private static readonly bool AllowsUnresolved = typeof(TRequest)
        .IsDefined(typeof(AllowsUnresolvedTenantContextAttribute), inherit: false);

    private static readonly bool IsPublicSurface = typeof(TRequest)
        .IsDefined(typeof(PublicSurfaceAttribute), inherit: false);

    public Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(next);

        // Gate 1 — the assertion. Returns either way: an unresolved context states no
        // origin, so there is no ceiling to apply, and falling through to one would
        // refuse the very requests the marker admits.
        if (!tenantContext.IsResolved)
        {
            return AllowsUnresolved
                ? next()
                : Task.FromResult(Result.FailFor<TResponse>(TenantMismatchError));
        }

        // Gate 2 — the authority ceiling. [AllowsUnresolvedTenantContext] is not an
        // exemption from this one: a provisioning command addressed to a live tenant's
        // own hostname resolves HostOnly, and refusing it there is the whole point.
        if (!PermittedUnder(tenantContext.Origin))
        {
            // TenantContextFactory.Refused, not a second literal. The refusal must be
            // byte-identical on the wire to an unresolvable host's 404, and sharing the
            // one Error makes that a compile-time fact rather than a coincidence two
            // tests happen to agree on.
            return Task.FromResult(Result.FailFor<TResponse>(TenantContextFactory.Refused));
        }

        // Nothing to do here for RLS, and nothing left undone elsewhere:
        // TransactionBehavior issues the set_config pair at step 6. RLS was enforced and
        // fail-closed before Packet 7 too — an unresolved context writes the empty
        // string, so every predicate is NULL and every tenant-owned table returns zero
        // rows. What Packet 7 added is a non-NULL predicate: TenantResolverMiddleware
        // now gives that setter a tenant to write.
        //
        // Why it belongs there and not here: the GUCs are transaction-local
        // (set_config('app.tenant_id', ..., true)) and this behavior runs at
        // step 4 with no transaction open, so the value would be discarded
        // before the query it protects ever runs. TransactionBehavior at step 6
        // issues them as the first statement inside the transaction — Security
        // Standards § Tenant Context. A DbConnectionInterceptor cannot do it
        // either: it fires at connection open, which precedes BEGIN.

        return next();
    }

    /// <summary>
    /// Whether a context carrying <paramref name="origin"/> may reach this request.
    /// </summary>
    /// <remarks>
    /// Exhaustive by construction. <c>HostOnly</c> is the one origin ADR-0036 narrows,
    /// and it narrows to <c>[PublicSurface]</c>. The three that carry an authenticated
    /// principal or an envelope reach an unmarked request type; what narrows those is
    /// authorization at step 5, not this gate — and <c>Ambient</c> in particular must be
    /// admitted or every integration-event consumer stops running, because
    /// <c>EventTenantContext</c> resolves with exactly that origin.
    /// </remarks>
    private static bool PermittedUnder(TenantContextOrigin? origin) => origin switch
    {
        TenantContextOrigin.HostOnly => IsPublicSurface,
        TenantContextOrigin.HostAndClaim => true,
        TenantContextOrigin.ClaimAndMembership => true,
        TenantContextOrigin.Ambient => true,

        // null — an implementation that states no origin — and any member added later
        // without deciding its ceiling. Fail-closed, which is the whole reason this is
        // a switch over stated values rather than a comparison against one of them.
        _ => false,
    };
}
