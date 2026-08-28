namespace LearnStack.Modules.Tenancy.Domain;

/// <summary>
/// A tenant's lifecycle state.
/// </summary>
/// <remarks>
/// Stored as <c>text</c> with a <c>CHECK</c> rather than a PostgreSQL <c>enum</c>
/// type or an <c>int</c>, per
/// <see href="../../../../../docs/standards/05-database.md">Database Standards
/// § Constraints</see>: an enum type's values can only be added, never removed or
/// reordered, and an <c>int</c> makes a database dump unreadable and a mistyped
/// value indistinguishable from a valid one.
/// </remarks>
public enum TenantStatus
{
    /// <summary>Provisioned, not yet paying. The default at creation.</summary>
    Trial = 0,

    /// <summary>Paying, or on a plan that needs no payment.</summary>
    Active = 1,

    /// <summary>Access withdrawn — billing failure, policy breach. Reversible.</summary>
    Suspended = 2,

    /// <summary>Ended. Retained for audit and retention obligations, never served.</summary>
    Archived = 3,
}

/// <summary>An organization's lifecycle state within its tenant.</summary>
public enum OrganizationStatus
{
    Active = 0,
    Suspended = 1,
    Archived = 2,
}

/// <summary>How a host came to belong to a tenant.</summary>
public enum TenantDomainKind
{
    /// <summary>
    /// A subdomain of the platform's own domain, always available and verified by
    /// construction because the platform controls the zone.
    /// </summary>
    Subdomain = 0,

    /// <summary>
    /// A domain the customer owns, which must be verified before it can serve.
    /// </summary>
    Custom = 1,
}

/// <summary>
/// Where a domain is in the verification lifecycle.
/// </summary>
/// <remarks>
/// The four states are fixed by
/// <see href="../../../../../docs/roadmap/phase-02a-kernel-tenancy.md">Phase 02a
/// Packet 6</see>. A <see cref="Subdomain"/> is created already
/// <see cref="Verified"/>; only a custom domain travels the whole path.
/// </remarks>
public enum TenantDomainStatus
{
    Requested = 0,
    Verifying = 1,
    Verified = 2,
    Failed = 3,
}
