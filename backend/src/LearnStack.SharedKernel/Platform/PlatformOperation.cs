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
        string auditSlug, string reasonSlug, PermissionDefinition permission, bool needsPlatformRole)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(auditSlug);
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
    }

    /// <summary>The audit operation slug of the result row.</summary>
    public string AuditSlug { get; }

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

    /// <summary>Every registered operation.</summary>
    public static IReadOnlyList<PlatformOperation> All { get; } = [KillswitchToggle];

    /// <summary>Whether <paramref name="operation"/> is one of the registered instances.</summary>
    public static bool IsRegistered(PlatformOperation? operation) =>
        operation is not null && All.Any(registered => ReferenceEquals(registered, operation));
}
