using LearnStack.SharedKernel.Permissions;

namespace LearnStack.SharedKernel.Platform;

/// <summary>Where an operator act runs: the one place its effect is made, and so which owner may take its admission.</summary>
public enum PlatformOperationExecutor
{
    /// <summary>A pipeline act on the application role, run by the operator act executor (ADR-0102).</summary>
    Pipeline = 1,

    /// <summary>A read or write in the platform scope, on the <c>BYPASSRLS</c> platform role (ADR-0101).</summary>
    PlatformScope,

    /// <summary>The offline tool, on its own confined role (ADR-0104).</summary>
    OfflineTool,

    /// <summary>The confined recovery owner, on the dispatcher's outbox role (ADR-0103).</summary>
    RecoveryOwner,
}

/// <summary>One closed registry entry: what an operator act is, and what it needs.</summary>
/// <remarks>
/// The reason is this entry's slug and never caller text, so the audit <c>reason</c> column
/// stays a value a query can group by (Standard 18). Construction is internal: the registry
/// in <see cref="PlatformOperations"/> is the only place an operation exists.
/// </remarks>
public sealed record PlatformOperation
{
    internal PlatformOperation(
        string auditSlug, string reasonSlug, PermissionDefinition permission, PlatformOperationExecutor executor,
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
        Executor = executor;
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

    /// <summary>Where the act runs, which decides the one owner that may consume its admission.</summary>
    public PlatformOperationExecutor Executor { get; }

    /// <summary>Whether the act enters the platform scope (the <c>BYPASSRLS</c> role).</summary>
    public bool NeedsPlatformRole => Executor == PlatformOperationExecutor.PlatformScope;

    /// <summary>Whether the operator act executor runs the act through the pipeline on the application role.</summary>
    public bool RunsInPipeline => Executor == PlatformOperationExecutor.Pipeline;
}

/// <summary>The closed set of operator operations (ADR-0101 § 7).</summary>
public static class PlatformOperations
{
    /// <summary>Toggle a declared killswitch (ADR-0101 § 13).</summary>
    public static PlatformOperation KillswitchToggle { get; } = new(
        "tenancy.killswitch.toggle", "killswitch_toggle", PlatformPermissions.KillswitchWrite,
        PlatformOperationExecutor.PlatformScope);

    /// <summary>Provision a Tenant and its default Organization (ADR-0102 § 2).</summary>
    public static PlatformOperation TenantProvision { get; } = new(
        "tenancy.tenant.create", "tenant_provision", PlatformPermissions.TenantWrite, PlatformOperationExecutor.Pipeline);

    /// <summary>Map a host to a Tenant (ADR-0102 § 2, § 4).</summary>
    public static PlatformOperation HostMap { get; } = new(
        "tenancy.hostmapping.write", "host_map", PlatformPermissions.TenantWrite, PlatformOperationExecutor.Pipeline);

    /// <summary>Create one finite Role template (ADR-0102 § 2).</summary>
    public static PlatformOperation RoleInitialize { get; } = new(
        "identity.role.initialize", "role_initialize", PlatformPermissions.TenantBootstrapWrite,
        PlatformOperationExecutor.Pipeline);

    /// <summary>Create the first tenant-wide administrator Membership (ADR-0102 § 7).</summary>
    public static PlatformOperation FirstAdminCreate { get; } = new(
        "identity.membership.create_first_admin", "first_admin_create", PlatformPermissions.TenantBootstrapWrite,
        PlatformOperationExecutor.Pipeline);

    /// <summary>Create a replacement administrator for a Tenant that has none (ADR-0102 § 8).</summary>
    public static PlatformOperation AdminRecover { get; } = new(
        "identity.membership.recover_admin", "admin_recover", PlatformPermissions.TenantBootstrapWrite,
        PlatformOperationExecutor.Pipeline);

    /// <summary>
    /// Rebind the identity authority's issuer from the offline tool, which neither enters the platform scope nor
    /// runs through the pipeline and writes an intent row and a result row of its own role (ADR-0104 § 8).
    /// </summary>
    public static PlatformOperation IdentityAuthorityRebind { get; } = new(
        "identity.authority.rebind", "issuer_rebind", PlatformPermissions.IdentityAuthorityAdmin,
        PlatformOperationExecutor.OfflineTool, attemptAuditSlug: "identity.authority.rebind_attempt");

    /// <summary>
    /// Inspect dead-lettered and pending-terminal deliveries and quarantined jobs: metadata only, in the platform
    /// scope on column grants, audited by the scope's entry row (ADR-0103 § 1).
    /// </summary>
    public static PlatformOperation DeliveryInspect { get; } = new(
        "platform.delivery.inspect", "delivery_inspect", PlatformPermissions.DeliveryRead,
        PlatformOperationExecutor.PlatformScope);

    /// <summary>Reset one dead-lettered subscriber delivery under the fence read at inspection (ADR-0103 § 2).</summary>
    public static PlatformOperation DeliveryReplay { get; } = new(
        "platform.delivery.replay", "delivery_replay", PlatformPermissions.DeliveryWrite,
        PlatformOperationExecutor.RecoveryOwner);

    /// <summary>Reset one dead-lettered producer row whose reason is retry exhaustion (ADR-0103 § 3).</summary>
    public static PlatformOperation DeliveryReset { get; } = new(
        "platform.delivery.reset", "delivery_reset", PlatformPermissions.DeliveryWrite,
        PlatformOperationExecutor.RecoveryOwner);

    /// <summary>Every registered operation.</summary>
    public static IReadOnlyList<PlatformOperation> All { get; } =
    [
        KillswitchToggle, TenantProvision, HostMap, RoleInitialize, FirstAdminCreate, AdminRecover,
        IdentityAuthorityRebind, DeliveryInspect, DeliveryReplay, DeliveryReset,
    ];

    /// <summary>Whether <paramref name="operation"/> is one of the registered instances.</summary>
    public static bool IsRegistered(PlatformOperation? operation) =>
        operation is not null && All.Any(registered => ReferenceEquals(registered, operation));
}
