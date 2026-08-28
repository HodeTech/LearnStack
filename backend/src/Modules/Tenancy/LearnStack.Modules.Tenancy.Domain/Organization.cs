using LearnStack.SharedKernel.Domain;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Time;

namespace LearnStack.Modules.Tenancy.Domain;

/// <summary>
/// A sub-unit within a tenant — a branch, studio, campus, department or cohort —
/// per <see href="../../../../../docs/decisions/0017-tenant-organization-hierarchy.md">ADR-0017</see>.
/// </summary>
/// <remarks>
/// <para>
/// Declared here and nowhere else. ADR-0017's original sample placed it in
/// <c>LearnStack.Modules.Identity.Domain.Entities</c>; Amendment 2 (2026-08-10)
/// moved it to Tenancy, and Identity holds <see cref="OrganizationId"/> by value
/// and reads organization data through an application contract. The architecture
/// rule <c>Organization_Aggregate_Declared_In_Tenancy_Domain</c> —
/// <c>TenancyConventionTests</c>, introduced with this aggregate — is what keeps a
/// second declaration from appearing.
/// </para>
/// <para>
/// The hierarchy is strictly two levels. <see cref="ReportingParentId"/> is a
/// self-reference for reporting only — it is not an isolation boundary, nothing
/// resolves through it, and no policy reads it. A row's scope is its own
/// <see cref="OrganizationId"/>, never its parent's.
/// </para>
/// <para>
/// <b>Branding is not here.</b> ADR-0017's sample carries an
/// <c>OrganizationBranding?</c> override; the value object and the token merge
/// belong to <see href="../../../../../docs/roadmap/phase-06-renderer-admin-studio.md">Phase 06</see>,
/// so the column arrives with them rather than as an unused <c>jsonb</c> nobody
/// writes.
/// </para>
/// </remarks>
public sealed class Organization : AuditableEntity<OrganizationId>, IAggregateRoot<OrganizationId>
{
    private Organization(OrganizationId id)
        : base(id)
    {
        Slug = null!;
        DisplayName = null!;
    }

    // EF materialization.
    private Organization()
    {
        Slug = null!;
        DisplayName = null!;
    }

    /// <summary>The tenant this organization belongs to. Immutable.</summary>
    /// <remarks>
    /// An organization never moves between tenants, and neither does a row that
    /// names it: its audit rows, its storage prefix and its cache-key prefix are
    /// all tenant-qualified, so re-parenting would orphan three subsystems at
    /// once.
    /// </remarks>
    public TenantId TenantId { get; private set; }

    /// <summary>URL-safe handle, unique within the tenant.</summary>
    public string Slug { get; private set; }

    /// <summary>Human-facing name. Not translated — an organization has one name.</summary>
    public string DisplayName { get; private set; }

    /// <summary>
    /// The organization's own subdomain, when it serves one.
    /// </summary>
    /// <remarks>
    /// Advisory here: what actually resolves a request is a
    /// <c>platform_host_to_tenant</c> row, which is read before any tenant context
    /// exists. This column records intent; the mapping table records resolution.
    /// <b>No write path yet</b> — <see cref="Create"/> does not take it and no
    /// mutator sets it. It arrives with the host lifecycle in
    /// <see href="../../../../../docs/roadmap/phase-02c-hub-foundation.md">Phase
    /// 02c</see>, which is what decides when a subdomain is claimed.
    /// </remarks>
    public string? CustomSubdomain { get; private set; }

    /// <summary>Lifecycle state within the tenant.</summary>
    public OrganizationStatus Status { get; private set; }

    /// <summary>
    /// A reporting-only parent, for tenants whose branches roll up.
    /// </summary>
    /// <remarks>
    /// Not enforced as a hierarchy: no cycle check, no depth limit, and nothing
    /// resolves through it. ADR-0017 keeps the isolation model flat precisely so
    /// that a policy never has to walk a tree.
    /// <b>No write path yet</b>, for the same reason as
    /// <see cref="CustomSubdomain"/>: reporting roll-up is an admin operation and
    /// arrives with the organization admin surface in
    /// <see href="../../../../../docs/roadmap/phase-03-identity-admin.md">Phase
    /// 03</see>. The composite foreign key and its index ship now because both are
    /// one-way doors; the mutator is additive.
    /// </remarks>
    public OrganizationId? ReportingParentId { get; private set; }

    /// <summary>
    /// Creates an organization inside a tenant.
    /// </summary>
    /// <remarks>
    /// The id is supplied rather than minted here so the caller's
    /// <c>IGuidFactory</c> is the single source of identifiers and a test can pin
    /// it — per Standards 02 § Time.
    /// </remarks>
    public static Organization Create(
        OrganizationId id,
        TenantId tenantId,
        string slug,
        string displayName,
        IClock clock,
        UserId createdBy)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        EnsureWithinMappedLengths(slug, displayName);

        if (!id.IsInitialized())
        {
            throw new ArgumentException(
                "The identifier was never assigned; construct it through its factory.",
                nameof(id));
        }

        if (!tenantId.IsInitialized())
        {
            throw new ArgumentException(
                "An organization belongs to a tenant; the tenant id was never assigned.",
                nameof(tenantId));
        }

        var organization = new Organization(id)
        {
            TenantId = tenantId,
            Slug = slug,
            DisplayName = displayName,
            Status = OrganizationStatus.Active,
        };

        organization.MarkCreated(clock.UtcNow, createdBy);
        return organization;
    }

    /// <summary>Renames the organization.</summary>
    public void Rename(string displayName, IClock clock, UserId updatedBy)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        EnsureWithinMappedLengths(Slug, displayName);

        DisplayName = displayName;
        MarkUpdated(clock.UtcNow, updatedBy);
    }

    /// <summary>
    /// The two bounds the EF configuration maps, asserted where the value is set.
    /// </summary>
    /// <remarks>
    /// The database rejects a longer value with <c>22001</c>, three layers from
    /// the call that produced it and with no property name. These are the same
    /// numbers <c>OrganizationConfiguration</c> declares — 63 for the slug, which
    /// is a DNS label, and 200 for the display name — and they are asserted here
    /// so the failure names what is wrong.
    /// </remarks>
    private static void EnsureWithinMappedLengths(string slug, string displayName)
    {
        if (slug.Length > 63)
        {
            throw new ArgumentException(
                $"Slug is {slug.Length} characters; the column holds 63, the DNS label limit.",
                nameof(slug));
        }

        if (displayName.Length > 200)
        {
            throw new ArgumentException(
                $"DisplayName is {displayName.Length} characters; the column holds 200.",
                nameof(displayName));
        }
    }

    /// <summary>Moves the organization to a new lifecycle state.</summary>
    public void ChangeStatus(OrganizationStatus status, IClock clock, UserId updatedBy)
    {
        ArgumentNullException.ThrowIfNull(clock);

        Status = status;
        MarkUpdated(clock.UtcNow, updatedBy);
    }
}
