using LearnStack.SharedKernel.Permissions;

namespace LearnStack.SharedKernel.Platform;

/// <summary>One closed registry entry: what an operator act is, and what it needs.</summary>
/// <remarks>
/// The reason is this entry's slug and never caller text, so the audit <c>reason</c> column
/// stays a value a query can group by (Standard 18). Construction is internal: the registry
/// in <see cref="PlatformOperations"/> is the only place an operation exists.
/// </remarks>
public sealed record PlatformOperation
{
    internal PlatformOperation(
        string auditSlug, string reasonSlug, PermissionDefinition permission, bool needsPlatformRole,
        string? attemptAuditSlug = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(auditSlug);
        if (attemptAuditSlug is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(attemptAuditSlug);
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(reasonSlug);
        ArgumentNullException.ThrowIfNull(permission);
        if (permission.Scope != PermissionScope.Platform)
        {
            throw new ArgumentException("A platform operation needs a Platform permission.", nameof(permission));
        }

        AuditSlug = auditSlug;
        ReasonSlug = reasonSlug;
        Permission = permission;
        NeedsPlatformRole = needsPlatformRole;
        AttemptAuditSlug = attemptAuditSlug;
    }

    /// <summary>The audit operation slug of the result row.</summary>
    public string AuditSlug { get; }

    /// <summary>
    /// The audit slug of the intent row written before the act's own transaction, or null when the act writes
    /// no such row. Only an act that runs outside the request pipeline has one (ADR-0104 § 8).
    /// </summary>
    public string? AttemptAuditSlug { get; }

    /// <summary>The closed reason slug every row of this act carries.</summary>
    public string ReasonSlug { get; }

    /// <summary>The Platform permission an operator must hold, evaluated per act.</summary>
    public PermissionDefinition Permission { get; }

    /// <summary>Whether the act enters the platform scope (the <c>BYPASSRLS</c> role).</summary>
    public bool NeedsPlatformRole { get; }
}

/// <summary>The closed set of operator operations (ADR-0101 § 7).</summary>
public static class PlatformOperations
{
    /// <summary>Toggle a declared killswitch (ADR-0101 § 13).</summary>
    public static PlatformOperation KillswitchToggle { get; } = new(
        "tenancy.killswitch.toggle", "killswitch_toggle", PlatformPermissions.KillswitchWrite, true);

    /// <summary>Provision a Tenant and its default Organization (ADR-0102 § 2).</summary>
    public static PlatformOperation TenantProvision { get; } = new(
        "tenancy.tenant.create", "tenant_provision", PlatformPermissions.TenantWrite, false);

    /// <summary>Map a host to a Tenant (ADR-0102 § 2, § 4).</summary>
    public static PlatformOperation HostMap { get; } = new(
        "tenancy.hostmapping.write", "host_map", PlatformPermissions.TenantWrite, false);

    /// <summary>Create one finite Role template (ADR-0102 § 2).</summary>
    public static PlatformOperation RoleInitialize { get; } = new(
        "identity.role.initialize", "role_initialize", PlatformPermissions.TenantBootstrapWrite, false);

    /// <summary>Create the first tenant-wide administrator Membership (ADR-0102 § 7).</summary>
    public static PlatformOperation FirstAdminCreate { get; } = new(
        "identity.membership.create_first_admin", "first_admin_create", PlatformPermissions.TenantBootstrapWrite, false);

    /// <summary>Create a replacement administrator for a Tenant that has none (ADR-0102 § 8).</summary>
    public static PlatformOperation AdminRecover { get; } = new(
        "identity.membership.recover_admin", "admin_recover", PlatformPermissions.TenantBootstrapWrite, false);

    /// <summary>
    /// Rebind the identity authority's issuer from the offline tool, which neither enters the platform scope nor
    /// runs through the pipeline and writes an intent row and a result row of its own role (ADR-0104 § 8).
    /// </summary>
    public static PlatformOperation IdentityAuthorityRebind { get; } = new(
        "identity.authority.rebind", "issuer_rebind", PlatformPermissions.IdentityAuthorityAdmin, false,
        attemptAuditSlug: "identity.authority.rebind_attempt");

    /// <summary>Every registered operation.</summary>
    public static IReadOnlyList<PlatformOperation> All { get; } =
        [KillswitchToggle, TenantProvision, HostMap, RoleInitialize, FirstAdminCreate, AdminRecover, IdentityAuthorityRebind];

    /// <summary>Whether <paramref name="operation"/> is one of the registered instances.</summary>
    public static bool IsRegistered(PlatformOperation? operation) =>
        operation is not null && All.Any(registered => ReferenceEquals(registered, operation));
}
