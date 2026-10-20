using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Identifiers;
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
/// <b>The error is independent of HTTP classification.</b> Every factory refusal
/// carries <c>lockey_not_found</c>. The HTTP resolver uses validated attempt facts
/// to distinguish authenticated claim/host disagreement from membership refusal
/// through the shared Problem Details factory (ADR-0064). Anonymous refusals stay
/// indistinguishable from an unknown host. This pure factory performs no I/O.
/// </para>
/// </remarks>
public static class TenantContextFactory
{
    /// <summary>
    /// The one refusal — for every failing row, and for the pipeline's ceiling.
    /// </summary>
    /// <remarks>
    /// One pure refusal for every failed matrix row. The generic authority ceiling
    /// also uses it for a resolved context that cannot reach the requested surface.
    /// HTTP-specific authenticated mismatch classification belongs to the resolver,
    /// not to this shared error or the generic unresolved MediatR gate.
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

        // Validate every supplied signal before the no-tenant shortcut. Incoherent
        // actor/organization signals cannot become a legitimate platform request.
        if (attempt.HasIncoherentClaims
            || !StronglyTypedId.IsAssignedWhenPresent(attempt.HostTenantId)
            || !StronglyTypedId.IsAssignedWhenPresent(attempt.ClaimTenantId)
            || !StronglyTypedId.IsAssignedWhenPresent(attempt.HostOrganizationId)
            || !StronglyTypedId.IsAssignedWhenPresent(attempt.ClaimOrganizationId)
            || !StronglyTypedId.IsAssignedWhenPresent(attempt.UserId)
            || attempt.HostTenantId == TenantId.PlatformSentinel
            || attempt.ClaimTenantId == TenantId.PlatformSentinel
            || attempt.UserId == UserId.SystemActor
            || attempt.NamesNoTenant)
            return Result.Fail<TenantContext>(Refused);

        // Rows 8, 11 and 12 — the cross-check. Evaluated before any port answer is
        // consulted, so a refused request never spends a database round trip.
        if (!attempt.ClaimAgreesWithHost)
        {
            return Result.Fail<TenantContext>(Refused);
        }

        // Rows 7, 10 and 14. `is not true` and not `== false`: an unanswered question
        // is a refusal, so a caller that forgot to ask cannot widen anything. This is
        // where DenyAllTenantMembershipReader makes rows 7, 10 and 14 fail closed until
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
            attempt.CorrelationId,
            attempt.HostTenantId is { } hostTenant
                ? new HostScope(hostTenant, attempt.HostOrganizationId)
                : null));
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
