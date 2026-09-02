using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.SharedKernel.Tenancy;

/// <summary>
/// Everything the reconciliation matrix is allowed to look at, gathered before any
/// context exists.
/// </summary>
/// <remarks>
/// <para>
/// <b>It carries answers, never ports.</b> Rows 7, 10 and 14 need a question
/// answered by a database — does this organization belong to this tenant, does an
/// active membership cover this pair — and the answers arrive here as
/// <see cref="bool"/>? rather than the factory holding
/// <c>IOrganizationScopeValidator</c> and <c>ITenantMembershipReader</c>. That is
/// what lets
/// <see href="../../../../docs/decisions/0036-tenant-resolution-trusted-inputs.md">ADR-0036</see>'s
/// signature — <c>Create(TenantResolutionAttempt) → Result&lt;TenantContext&gt;</c>,
/// with no <c>Async</c> and no <c>CancellationToken</c> — be literally true, and it
/// makes the whole matrix a pure total function a unit test can drive row by row
/// without a container. The middleware does the I/O; the factory does the decision.
/// </para>
/// <para>
/// <b>What is deliberately absent, each for its own reason.</b> No host string: it
/// is attacker-authored on every anonymous request and host classification keeps it
/// at <c>Debug</c> precisely so it never reaches a retained sink. No
/// <c>X-Tenant-Id</c> / <c>X-Organization-Id</c>: assertions select nothing —
/// admitting them here would make
/// <c>Tenant_Headers_Are_Never_A_Resolution_Source</c> false by construction, and
/// they are compared downstream against what this produced. No host class: the
/// three live classes are already determined by which of the two host-side
/// identifiers are present, and the enum lives in an assembly the kernel cannot
/// see. No module name: the resolver runs before routing has selected an endpoint.
/// </para>
/// <para>
/// <c>UnknownHost</c> never arrives here either — host classification answers it
/// <c>404</c> before a context is attempted at all.
/// </para>
/// </remarks>
public sealed record TenantResolutionAttempt
{
    /// <summary>The tenant the host mapping named. <c>null</c> on a platform host.</summary>
    public TenantId? HostTenantId { get; init; }

    /// <summary>
    /// The organization the host mapping named, when the row carries one.
    /// </summary>
    /// <remarks>
    /// The anonymous organization scope <b>is</b> this value, per ADR-0036 — not the
    /// tenant's organization count. A tenant that wants its default organization's
    /// content on its public site seeds <c>organization_id</c> into its
    /// <c>platform_host_to_tenant</c> row, which removes a code branch and makes the
    /// behaviour visible, seedable and auditable as data.
    /// </remarks>
    public OrganizationId? HostOrganizationId { get; init; }

    /// <summary>
    /// Whether a token was validated for this request. Constant <c>false</c> until
    /// Phase 02b registers authentication.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="ClaimTenantId"/> because rows 13 and 15 differ only
    /// in this: both resolve no tenant, and only one of them has a principal. An
    /// <b>invalid</b> token never arrives — it is a <c>401</c> before the outcome is
    /// consumed, because a rejected token must never be treated as absence.
    /// </remarks>
    public bool HasValidatedPrincipal { get; init; }

    /// <summary>The tenant a validated claim named.</summary>
    public TenantId? ClaimTenantId { get; init; }

    /// <summary>The organization a validated claim named.</summary>
    public OrganizationId? ClaimOrganizationId { get; init; }

    /// <summary>The actor, when one is authenticated.</summary>
    public UserId? UserId { get; init; }

    /// <summary>
    /// Whether an active membership covers the claimed pair — <c>null</c> when the
    /// question was not asked, which is every row that does not need it.
    /// </summary>
    public bool? MembershipCovers { get; init; }

    /// <summary>
    /// Whether the claimed organization belongs to the resolved tenant —
    /// <c>null</c> when the question was not asked.
    /// </summary>
    public bool? ClaimedOrganizationBelongsToTenant { get; init; }

    /// <summary>The correlation id, carried through and decided nowhere here.</summary>
    public string? CorrelationId { get; init; }

    /// <summary>
    /// Rows 13 and 15: no authoritative signal names a tenant, which is not a
    /// failure.
    /// </summary>
    public bool NamesNoTenant => HostTenantId is null && ClaimTenantId is null;

    /// <summary>
    /// <c>false</c> on rows 8, 11 and 12 — the disagreements — and on nothing else.
    /// </summary>
    /// <remarks>
    /// A signal that is absent cannot disagree, which is why each term is guarded on
    /// both sides being present. The organization term matters on its own: row 11 is
    /// an org-host and a claim naming a <i>different</i> organization of the same
    /// tenant, and ADR-0036 settles it as a mismatch rather than a scope change —
    /// an earlier draft let the host win, on a citation that turned out to describe
    /// <c>?org=</c> search parameters, which this ADR trusts for nothing.
    /// </remarks>
    public bool ClaimAgreesWithHost =>
        ClaimTenantId is null
        || HostTenantId is null
        || (ClaimTenantId == HostTenantId
            && (ClaimOrganizationId is null
                || HostOrganizationId is null
                || ClaimOrganizationId == HostOrganizationId));

    /// <summary>
    /// Rows 7, 10 and 14 — a claim that goes beyond what the host already vouches
    /// for, and nothing else.
    /// </summary>
    public bool RequiresMembershipCheck =>
        ClaimTenantId is not null
        && ClaimAgreesWithHost
        && (HostTenantId is null || ClaimOrganizationId != HostOrganizationId);

    /// <summary>
    /// Row 7 only: a claim naming an organization the host did not, on a host that
    /// did name the tenant.
    /// </summary>
    /// <remarks>
    /// Row 7 is the one row of the matrix carrying an <c>∈</c> term. Row 14 names
    /// membership alone, and that is not an omission: a membership record is per
    /// <c>(tenant, organization)</c>, so an active membership covering
    /// <c>(T_j, O_j)</c> already establishes that <c>O_j</c> belongs to <c>T_j</c>.
    /// Adding the structural check there would be an addition beyond the matrix, and
    /// the sequencing matters more than the redundancy: membership is asked first,
    /// so a row-14 attempt never announces a caller-supplied, unvouched-for tenant
    /// id to PostgreSQL through <c>set_config</c>.
    /// </remarks>
    public bool RequiresOrganizationScopeCheck =>
        HostTenantId is not null
        && ClaimOrganizationId is not null
        && ClaimAgreesWithHost
        && ClaimOrganizationId != HostOrganizationId;
}
