using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Results;

namespace LearnStack.SharedKernel.Tenancy;

/// <summary>
/// The single entry point that turns a <see cref="TenantResolutionAttempt"/> into a
/// <see cref="TenantContext"/> — or refuses.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pure, total and synchronous.</b> Every question that needs a database was
/// answered before the attempt was assembled, so this is
/// <see href="../../../../docs/decisions/0036-tenant-resolution-trusted-inputs.md">ADR-0036</see>'s
/// reconciliation matrix expressed as a function — which means every row expressible
/// as a <see cref="TenantResolutionAttempt"/> is drivable from a unit test with no
/// container, no HTTP and no clock. That is <b>twelve</b> of the seventeen: rows 2, 3
/// and 6-15. The other five are decided elsewhere and belong there — row 1 at host
/// classification, rows 4 and 5 by an authentication outcome, row 16 by
/// <c>TenantAssertionMiddleware</c>, row 17 by <c>EventTenantContext.FromEnvelope</c>.
/// Pulling any of them in here would cost exactly the purity the rest depends on.
/// </para>
/// <para>
/// <b>It never returns a partially populated context</b>, which is the rule the
/// single entry point exists to hold. Any disagreement between signals is
/// <c>Result.Fail</c>, and no caller can assemble a <see cref="TenantContext"/>
/// another way.
/// </para>
/// <para>
/// <b>The error it returns is not the wire.</b> The refusal a client sees is a
/// bodyless <c>404</c> rendered by <c>UseStatusCodePages</c>, byte-identical to the
/// one an unresolvable host gets — because anything a client can tell apart confirms
/// to an anonymous caller that the tenant exists. The <see cref="Error"/> here names
/// the reason for a reader of the code and for the middleware's own logging; it
/// carries <c>lockey_not_found</c> rather than <c>lockey_tenant_mismatch</c> because
/// the wire result must match the anonymous case, and <c>tenant_mismatch</c> is the
/// authenticated code.
/// </para>
/// </remarks>
public static class TenantContextFactory
{
    /// <summary>The one refusal. Deliberately the same for every failing row.</summary>
    /// <remarks>
    /// One <see cref="Error"/> and not one per row: a caller who could tell row 8
    /// (a tenant that exists, claimed by a token for another) from row 10 (an
    /// organization no membership covers) would have an oracle over which tenants
    /// and organizations exist. The distinction that matters to an operator is
    /// carried by the middleware's log line, not by the response.
    /// </remarks>
    public static Error Refused { get; } = new(new LocalizedMessage("lockey_not_found"));

    /// <summary>
    /// Applies the reconciliation matrix. Returns the context, or
    /// <see cref="Refused"/>.
    /// </summary>
    /// <remarks>
    /// Callers must not invoke this for an attempt whose
    /// <see cref="TenantResolutionAttempt.NamesNoTenant"/> is <c>true</c> — rows 13
    /// and 15 resolve nothing, and "nothing" is not a failure to be rendered as one.
    /// The middleware leaves those requests on <see cref="UnresolvedTenantContext"/>
    /// and lets the pipeline decide, which is where
    /// <c>[AllowsUnresolvedTenantContext]</c> lives.
    /// </remarks>
    public static Result<TenantContext> Create(TenantResolutionAttempt attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);

        // Rows 13 and 15, defensively. Callers must not arrive here with NamesNoTenant
        // set — the middleware leaves those requests on UnresolvedTenantContext,
        // because "this host serves no tenant" is what a platform host legitimately is
        // and not an error. Refused is simply the safe answer for a caller that ignores
        // that contract; it is not the path traffic takes.
        if (attempt.NamesNoTenant)
        {
            return Result.Fail<TenantContext>(Refused);
        }

        // Neither shape is a row. Refused before the cross-check, because the
        // cross-check reasons about signals that agree and these signals are not
        // well-formed enough to disagree.
        if (attempt.HasIncoherentClaims)
        {
            return Result.Fail<TenantContext>(Refused);
        }

        // Rows 8, 11 and 12 — the cross-check. Evaluated before any port answer is
        // consulted, so a refused request never spends a database round trip.
        if (!attempt.ClaimAgreesWithHost)
        {
            return Result.Fail<TenantContext>(Refused);
        }

        // Rows 7, 10 and 14. `is not true` and not `== false`: an unanswered question
        // is a refusal, so a caller that forgot to ask cannot widen anything. This is
        // where DenyAllTenantMembershipReader makes rows 7 and 14 fail closed until
        // Phase 03.
        if (attempt.RequiresMembershipCheck && attempt.MembershipCovers is not true)
        {
            return Result.Fail<TenantContext>(Refused);
        }

        // Row 7's ∈ term, on the same fail-closed reading.
        if (attempt.RequiresOrganizationScopeCheck
            && attempt.ClaimedOrganizationBelongsToTenant is not true)
        {
            return Result.Fail<TenantContext>(Refused);
        }

        // The tenant is whichever signal named it; they agree by the check above.
        var tenantId = attempt.HostTenantId ?? attempt.ClaimTenantId!.Value;

        // The claim narrows, the host supplies the anonymous default. Row 7 takes the
        // claim's organization on a tenant-wide host; rows 3 and 9 take the host's.
        // The SAME member the resolver asks membership about, so the organization
        // granted and the organization vouched for are one expression.
        var organizationId = attempt.MembershipQuestionOrganizationId;

        return Result.Ok(new TenantContext(
            tenantId,
            organizationId,
            attempt.UserId,
            OriginFor(attempt),
            attempt.CorrelationId));
    }

    /// <summary>
    /// Which signals carried this context — the authority ceiling, decided once.
    /// </summary>
    private static TenantContextOrigin OriginFor(TenantResolutionAttempt attempt)
    {
        if (attempt.ClaimTenantId is null)
        {
            // Rows 2 and 3: the host, alone. The ceiling that makes a forged host
            // harmless.
            return TenantContextOrigin.HostOnly;
        }

        // Row 14: no host named a tenant, so membership is what carried it. Rows 6,
        // 7, 9 and 10 all have a host that agreed, and membership only confirms
        // there — which is why they stay HostAndClaim even when the reader was asked.
        return attempt.HostTenantId is null
            ? TenantContextOrigin.ClaimAndMembership
            : TenantContextOrigin.HostAndClaim;
    }
}
