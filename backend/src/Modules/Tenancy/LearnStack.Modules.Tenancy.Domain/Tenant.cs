using LearnStack.SharedKernel.Domain;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Time;

namespace LearnStack.Modules.Tenancy.Domain;

/// <summary>
/// The root of a customer's data. Every other tenant-owned row keys on its id.
/// </summary>
/// <remarks>
/// <para>
/// <b>Self-keyed, and that shapes its whole lifecycle.</b> <c>tenants</c> has no
/// <c>tenant_id</c> column — its primary key <i>is</i> the tenant id — so its Row
/// Level Security policy keys on <c>id</c>
/// (<see href="../../../../../docs/standards/05-database.md">Database Standards
/// § Table classes</see>). Two consequences follow, and both are binding:
/// </para>
/// <para>
/// <b>The id is never minted here.</b> The registry that owns the tenant assigns
/// it — the Hub in SaaS / Dedicated, configuration in Self-Hosted, the fixture in
/// a seed — and the provisioning transaction sets <c>app.tenant_id</c> to that
/// value before the <c>INSERT</c>, so <c>WITH CHECK</c> passes. A factory that
/// generated its own id could not satisfy its own policy.
/// </para>
/// <para>
/// <b>Enumerating tenants needs the platform role.</b> <c>SELECT … FROM tenants</c>
/// with no <c>app.tenant_id</c> returns zero rows, so every operator list screen
/// and every cross-tenant sweep goes through <c>EnterPlatformAdminScope(reason)</c>.
/// That is the intended cost: the application role cannot enumerate the customer
/// list.
/// </para>
/// </remarks>
public sealed class Tenant : AuditableEntity<TenantId>, IAggregateRoot<TenantId>
{
    private Tenant(TenantId id)
        : base(id)
    {
        Slug = null!;
        DisplayName = null!;
    }

    // EF materialization.
    private Tenant()
    {
        Slug = null!;
        DisplayName = null!;
    }

    /// <summary>
    /// Globally unique, URL-safe handle. Appears in hostnames.
    /// </summary>
    /// <remarks>
    /// Unique across the whole table rather than per tenant, and PostgreSQL
    /// enforces unique indexes with row security bypassed — so a duplicate-slug
    /// insert reveals that <i>some</i> tenant already holds the slug. Accepted
    /// here because slugs appear in hostnames and are public by construction. It
    /// is accepted nowhere else, which is why every other natural key is
    /// <c>UNIQUE (tenant_id, …)</c>.
    /// </remarks>
    public string Slug { get; private set; }

    /// <summary>Human-facing name.</summary>
    public string DisplayName { get; private set; }

    /// <summary>Lifecycle state.</summary>
    public TenantStatus Status { get; private set; }

    /// <summary>
    /// The organization a request falls back to when it names none.
    /// </summary>
    /// <remarks>
    /// Nullable, and null only inside the provisioning transaction. The foreign
    /// key is composite — <c>(id, default_organization_id) REFERENCES
    /// organizations (tenant_id, id)</c> — so it cannot point at another tenant's
    /// organization even though referential-integrity checks run with row
    /// security bypassed. Under <c>MATCH SIMPLE</c> the check is skipped entirely
    /// while the column is null, which is what makes the three-statement
    /// provisioning sequence work: insert the tenant, insert its organization,
    /// then <see cref="AssignDefaultOrganization"/>.
    /// </remarks>
    public OrganizationId? DefaultOrganizationId { get; private set; }

    /// <summary>
    /// Creates a tenant under an id its registry has already assigned.
    /// </summary>
    public static Tenant Create(
        TenantId id,
        string slug,
        string displayName,
        IClock clock,
        UserId createdBy)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

        if (!id.IsInitialized())
        {
            throw new ArgumentException(
                "A tenant id is assigned by the registry that owns the tenant, never minted here.",
                nameof(id));
        }

        var tenant = new Tenant(id)
        {
            Slug = slug,
            DisplayName = displayName,
            Status = TenantStatus.Trial,
        };

        tenant.MarkCreated(clock.UtcNow, createdBy);
        return tenant;
    }

    /// <summary>
    /// Points the tenant at its default organization, completing provisioning.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Create"/> because the organization cannot exist
    /// before its tenant: the composite foreign key would have nothing to
    /// reference. Both statements run in one transaction, so a tenant is never
    /// observable without a default organization.
    /// </remarks>
    public void AssignDefaultOrganization(OrganizationId organizationId, IClock clock, UserId updatedBy)
    {
        ArgumentNullException.ThrowIfNull(clock);

        if (!organizationId.IsInitialized())
        {
            throw new ArgumentException(
                "The default organization must be a real organization.", nameof(organizationId));
        }

        DefaultOrganizationId = organizationId;
        MarkUpdated(clock.UtcNow, updatedBy);
    }

    /// <summary>Moves the tenant to a new lifecycle state.</summary>
    public void ChangeStatus(TenantStatus status, IClock clock, UserId updatedBy)
    {
        ArgumentNullException.ThrowIfNull(clock);

        Status = status;
        MarkUpdated(clock.UtcNow, updatedBy);
    }
}
