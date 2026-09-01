using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Tenancy;

namespace LearnStack.Api.Tenancy;

/// <summary>
/// Which of the three serving classes a request's effective host falls into.
/// </summary>
/// <remarks>
/// The fourth class <c>UnknownHost</c> has no member here on purpose: it never
/// reaches a downstream reader, because
/// <see cref="HostClassificationMiddleware"/> answers it <c>404</c> before any
/// handler runs (ADR-0036 § The reconciliation matrix, row 1). Representing it
/// would invite a branch on it somewhere that cannot be reached.
/// </remarks>
public enum HostClass
{
    /// <summary>A row with <c>organization_id IS NULL</c> — the tenant's own site.</summary>
    Tenant,

    /// <summary>The same, with an organization — one branch's site.</summary>
    Organization,

    /// <summary>
    /// A host in <c>Tenancy:PlatformHosts</c>. The Studio / Portal entry host; it
    /// maps to no tenant, so it needs no row.
    /// </summary>
    Platform,
}

/// <summary>
/// What <see cref="HostClassificationMiddleware"/> decided about this request.
/// </summary>
/// <remarks>
/// <para>
/// Stored on <c>HttpContext.Features</c> rather than <c>Items</c>: a feature is
/// typed, has one writer, and is what the resolver middleware reads one step
/// later. ADR-0036 § Rules splits the single step architecture/27 once described
/// as "the resolver first, before JWT validation" into two — classification
/// before authentication, context construction after it — and this record is what
/// crosses between them.
/// </para>
/// <para>
/// <b>It is not a tenant context and must never be mistaken for one.</b> A
/// classification says which host was addressed; it carries no authority. The
/// authority ceiling is <c>TenantContextOrigin</c>, and the context itself is
/// built only by <c>TenantContextFactory</c>.
/// </para>
/// </remarks>
public sealed record HostClassification
{
    private HostClassification(
        HostClass @class, string host, TenantId? tenantId, OrganizationId? organizationId)
    {
        Class = @class;
        Host = host;
        TenantId = tenantId;
        OrganizationId = organizationId;
    }

    public HostClass Class { get; }

    /// <summary>The normalized effective host this decision was made about.</summary>
    public string Host { get; }

    /// <summary><c>null</c> only for <see cref="HostClass.Platform"/>.</summary>
    public TenantId? TenantId { get; }

    /// <summary>Set only for <see cref="HostClass.Organization"/>.</summary>
    public OrganizationId? OrganizationId { get; }

    /// <summary>A host that maps to no tenant, by configuration.</summary>
    public static HostClassification Platform(string host)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        return new HostClassification(HostClass.Platform, host, null, null);
    }

    /// <summary>A host that resolved, to a tenant and possibly to an organization.</summary>
    public static HostClassification ForResolution(string host, HostResolution resolution)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentNullException.ThrowIfNull(resolution);

        return new HostClassification(
            resolution.OrganizationId is null ? HostClass.Tenant : HostClass.Organization,
            host,
            resolution.TenantId,
            resolution.OrganizationId);
    }
}
