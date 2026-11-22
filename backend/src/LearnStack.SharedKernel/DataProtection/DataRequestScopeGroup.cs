using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.SharedKernel.DataProtection;

/// <summary>
/// The unit every tenant data request is bound to: one tenant and either one organization or
/// none (ADR-0106 § 2).
/// </summary>
/// <remarks>
/// <para>
/// The group is the context the request was filed in, and every write the request causes runs
/// under that same announcement: the organization row policies admit a write of an
/// organization-bound row only under that organization, so a request that spanned groups would
/// need an RLS exception. A person with Memberships in several organizations files one request
/// per group.
/// </para>
/// <para>
/// A closure is global, but its row still lives in its originating group (ADR-0107 § 7).
/// </para>
/// </remarks>
public sealed record DataRequestScopeGroup
{
    /// <summary>Builds a group, refusing the nil and platform-sentinel tenants and a nil organization.</summary>
    /// <exception cref="ArgumentException">A member is unassigned or names no real scope.</exception>
    public DataRequestScopeGroup(TenantId tenantId, OrganizationId? organizationId)
    {
        if (!StronglyTypedId.IsAssigned(tenantId) || tenantId == TenantId.PlatformSentinel)
        {
            throw new ArgumentException("A data request group needs a real tenant.", nameof(tenantId));
        }

        if (organizationId is { } organization && !StronglyTypedId.IsAssigned(organization))
        {
            throw new ArgumentException("A data request group's organization must be assigned.", nameof(organizationId));
        }

        TenantId = tenantId;
        OrganizationId = organizationId;
    }

    public TenantId TenantId { get; }

    /// <summary>The group's organization; <see langword="null"/> is the tenant-wide group.</summary>
    public OrganizationId? OrganizationId { get; }

    /// <summary>Whether this is the tenant-wide group.</summary>
    public bool IsTenantWide => OrganizationId is null;
}
