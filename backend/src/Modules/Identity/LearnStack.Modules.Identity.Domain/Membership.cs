using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Domain;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;

namespace LearnStack.Modules.Identity.Domain;

public enum MembershipStatus { Active = 1, Suspended = 2, Revoked = 3 }

[TenantOwned]
[OrganizationScoped]
public sealed class Membership : AuditableEntity<MembershipId>, IAggregateRoot<MembershipId>, IOrganizationScoped
{
    public const int MaximumRoleAssignments = 8;

    private readonly List<MembershipRoleAssignment> _roleAssignments = [];
    private Membership() { Profile = null!; }

    public TenantId TenantId { get; private set; }
    public OrganizationId? OrganizationId { get; private set; }
    public UserId UserId { get; private set; }
    public MembershipStatus Status { get; private set; }
    public MembershipProfile Profile { get; private set; }
    public IReadOnlyCollection<MembershipRoleAssignment> RoleAssignments => _roleAssignments.AsReadOnly();

    public static Result<Membership> Create(MembershipId id, TenantId tenantId, OrganizationId? organizationId,
        UserId userId, Role role, DateTimeOffset at, UserId actor)
    {
        ArgumentNullException.ThrowIfNull(role);
        if (!id.IsInitialized() || id.Value == Guid.Empty || id.Value.Version != 7)
            throw new ArgumentException("Membership identifier is invalid.", nameof(id));
        TenantOwnership.EnsureRealTenant(tenantId, "Membership tenant is invalid.", nameof(tenantId));
        if (organizationId is { } organization && (!organization.IsInitialized() || organization.Value == Guid.Empty))
            throw new ArgumentException("Membership organization is invalid.", nameof(organizationId));
        if (!userId.IsInitialized() || userId.Value == Guid.Empty || userId.Value.Version != 7 || userId == UserId.SystemActor)
            return Result<Membership>.Fail(IdentityFailures.Refused());
        if (role.TenantId != tenantId || role.IsDeleted
            || role.PermissionScope != (organizationId is null ? PermissionScope.Tenant : PermissionScope.Organization))
            return Result<Membership>.Fail(IdentityFailures.Refused());
        var membership = new Membership
        {
            Id = id,
            TenantId = tenantId,
            OrganizationId = organizationId,
            UserId = userId,
            Status = MembershipStatus.Active,
        };
        membership.MarkCreated(at, actor);
        membership.Profile = new MembershipProfile(membership);
        membership._roleAssignments.Add(new MembershipRoleAssignment(membership, role.Id));
        return Result<Membership>.Ok(membership);
    }

    public Result<None> SetProfile(string? displayName, string? preferredLocale, DateTimeOffset at, UserId actor)
    {
        if (Status != MembershipStatus.Active || IsDeleted || !MembershipProfile.IsValid(displayName, preferredLocale))
            return IdentityFailures.BusinessRule();
        if (Profile.DisplayName == displayName && Profile.PreferredLocale == preferredLocale)
            return Result<None>.Ok(None.Value);
        MarkUpdated(at, actor);
        Profile.Set(displayName, preferredLocale);
        return Result<None>.Ok(None.Value);
    }

    public Result<None> SetCustomFields(IReadOnlyCollection<MembershipCustomFieldValue> values, DateTimeOffset at, UserId actor)
    {
        var json = MembershipCustomFieldValues.Serialize(values);
        if (Status != MembershipStatus.Active || IsDeleted || json is null) return IdentityFailures.BusinessRule();
        if (MembershipCustomFieldValues.Serialize(MembershipCustomFieldValues.Read(Profile.CustomFieldsJson).ToArray()) == json)
            return Result<None>.Ok(None.Value);
        MarkUpdated(at, actor);
        Profile.SetCustomFields(json);
        return Result<None>.Ok(None.Value);
    }

    /// <summary>Retains unchanged assignment instances so tracked writes capture only the actual difference.</summary>
    public Result<None> ReplaceAssignments(IReadOnlyCollection<Role> desired, DateTimeOffset at, UserId actor)
    {
        ArgumentNullException.ThrowIfNull(desired);
        var scope = OrganizationId is null ? PermissionScope.Tenant : PermissionScope.Organization;
        if (Status is not (MembershipStatus.Active or MembershipStatus.Suspended) || IsDeleted
            || desired.Count > MaximumRoleAssignments
            || desired.Any(role => role is null || !role.Id.IsInitialized() || role.Id.Value == Guid.Empty
                || role.Id.Value.Version != 7 || role.TenantId != TenantId || role.IsDeleted || role.PermissionScope != scope)
            || desired.Select(role => role.Id).Distinct().Count() != desired.Count)
            return IdentityFailures.BusinessRule();

        if (_roleAssignments.Count == desired.Count
            && desired.All(role => _roleAssignments.Any(assignment => assignment.RoleId == role.Id)))
            return Result<None>.Ok(None.Value);

        MarkUpdated(at, actor);
        _roleAssignments.RemoveAll(assignment => !desired.Any(role => role.Id == assignment.RoleId));
        foreach (var role in desired)
            if (!_roleAssignments.Any(assignment => assignment.RoleId == role.Id))
                _roleAssignments.Add(new MembershipRoleAssignment(this, role.Id));
        return Result<None>.Ok(None.Value);
    }

    public Result<None> Suspend(DateTimeOffset at, UserId actor)
    {
        if (Status != MembershipStatus.Active || IsDeleted) return IdentityFailures.BusinessRule();
        MarkUpdated(at, actor);
        Status = MembershipStatus.Suspended;
        return Result<None>.Ok(None.Value);
    }

    public Result<None> Reactivate(DateTimeOffset at, UserId actor)
    {
        if (Status != MembershipStatus.Suspended || IsDeleted) return IdentityFailures.BusinessRule();
        MarkUpdated(at, actor);
        Status = MembershipStatus.Active;
        return Result<None>.Ok(None.Value);
    }

    public Result<None> Revoke(DateTimeOffset at, UserId actor)
    {
        if (Status == MembershipStatus.Revoked || IsDeleted) return IdentityFailures.BusinessRule();
        SoftDelete(at, actor);
        Status = MembershipStatus.Revoked;
        return Result<None>.Ok(None.Value);
    }

    /// <summary>
    /// Whether nothing is left for an erasure to change: the Membership is the Revoked tombstone, holds no role
    /// assignment and its profile is cleared (ADR-0107 § 5, § 6).
    /// </summary>
    public bool IsErased => Status == MembershipStatus.Revoked && _roleAssignments.Count == 0 && Profile.IsCleared;

    /// <summary>
    /// ADR-0107 § 5: deletes every role assignment, of a Membership of any status, under the erasure witness. Idempotent:
    /// a Membership with no assignment changes nothing.
    /// </summary>
    public Result<None> RemoveAssignmentsForErasure(DataErasureWitness witness, DateTimeOffset at, UserId actor)
    {
        if (!Erasable(witness)) return IdentityFailures.BusinessRule();
        if (_roleAssignments.Count == 0) return Result<None>.Ok(None.Value);
        StampErasure(at, actor);
        _roleAssignments.Clear();
        return Result<None>.Ok(None.Value);
    }

    /// <summary>
    /// ADR-0107 § 5: clears the display name and locale to <see langword="null"/> and the custom values to <c>[]</c>, of a
    /// Membership of any status, under the erasure witness. Idempotent: a cleared profile changes nothing.
    /// </summary>
    public Result<None> ClearProfileForErasure(DataErasureWitness witness, DateTimeOffset at, UserId actor)
    {
        if (!Erasable(witness)) return IdentityFailures.BusinessRule();
        if (Profile.IsCleared) return Result<None>.Ok(None.Value);
        StampErasure(at, actor);
        Profile.Clear();
        return Result<None>.Ok(None.Value);
    }

    /// <summary>
    /// ADR-0107 § 5: an Active or Suspended Membership becomes the Revoked tombstone that keeps its <see cref="UserId"/>,
    /// under the erasure witness. Idempotent: a Revoked one changes nothing, so its row, which admits no UPDATE, is never
    /// written.
    /// </summary>
    public Result<None> RevokeForErasure(DataErasureWitness witness, DateTimeOffset at, UserId actor)
    {
        if (!Erasable(witness)) return IdentityFailures.BusinessRule();
        if (Status == MembershipStatus.Revoked) return Result<None>.Ok(None.Value);
        SoftDelete(at, actor);
        Status = MembershipStatus.Revoked;
        return Result<None>.Ok(None.Value);
    }

    /// <summary>
    /// The witness covers this Membership's tenant, group and person, and the stored lifecycle is coherent: deleted
    /// exactly when Revoked, as <c>ck_memberships_lifecycle</c> requires.
    /// </summary>
    private bool Erasable(DataErasureWitness? witness) => witness is not null && witness.Covers(TenantId, OrganizationId, UserId)
        && IsDeleted == (Status == MembershipStatus.Revoked);

    /// <summary>
    /// The root is stamped as every ordinary change stamps it, except a Revoked tombstone's: the contextual guard admits no
    /// UPDATE of a Revoked row, so its contained rows change alone and the change is recorded in the erasure's audit row.
    /// </summary>
    private void StampErasure(DateTimeOffset at, UserId actor)
    {
        if (Status != MembershipStatus.Revoked) MarkUpdated(at, actor);
    }
}

[TenantOwned]
[OrganizationScoped]
public sealed class MembershipProfile : IOrganizationScoped
{
    private MembershipProfile() { Membership = null!; }
    internal MembershipProfile(Membership membership)
    {
        Membership = membership;
        TenantId = membership.TenantId;
        MembershipId = membership.Id;
        OrganizationId = membership.OrganizationId;
    }

    public const int MaximumDisplayNameLength = 128;
    public const int MaximumLocaleLength = 35;
    public TenantId TenantId { get; private set; }
    public MembershipId MembershipId { get; private set; }
    public OrganizationId? OrganizationId { get; private set; }
    [PiiSensitive]
    public string? DisplayName { get; private set; }
    [PiiSensitive]
    public string? PreferredLocale { get; private set; }
    [PiiSensitive]
    public string CustomFieldsJson { get; private set; } = "[]";
    public Membership Membership { get; private set; }

    public static bool IsValid(string? displayName, string? preferredLocale) =>
        (displayName is null || displayName.Length is > 0 and <= MaximumDisplayNameLength
            && !displayName.Any(char.IsControl) && !string.IsNullOrWhiteSpace(displayName) && JsonValue.IsStorableText(displayName))
        && (preferredLocale is null || preferredLocale.Length <= MaximumLocaleLength && LocaleTag.IsWellFormed(preferredLocale));

    internal void Set(string? displayName, string? preferredLocale)
    {
        DisplayName = displayName;
        PreferredLocale = preferredLocale;
    }

    internal void SetCustomFields(string json) => CustomFieldsJson = json;

    /// <summary>The three personal columns hold nothing: the state ADR-0107 § 5's clearing leaves.</summary>
    public bool IsCleared => DisplayName is null && PreferredLocale is null && CustomFieldsJson == EmptyCustomFields;

    /// <summary>The clearing the contextual guard admits on a Membership of any status under the erasure witness.</summary>
    internal void Clear()
    {
        DisplayName = null;
        PreferredLocale = null;
        CustomFieldsJson = EmptyCustomFields;
    }

    private const string EmptyCustomFields = "[]";
}

[TenantOwned]
[OrganizationScoped]
public sealed class MembershipRoleAssignment : IOrganizationScoped
{
    private MembershipRoleAssignment() { Membership = null!; }
    internal MembershipRoleAssignment(Membership membership, RoleId roleId)
    {
        Membership = membership;
        TenantId = membership.TenantId;
        MembershipId = membership.Id;
        OrganizationId = membership.OrganizationId;
        RoleId = roleId;
    }
    public TenantId TenantId { get; private set; }
    public MembershipId MembershipId { get; private set; }
    public RoleId RoleId { get; private set; }
    public OrganizationId? OrganizationId { get; private set; }
    public Membership Membership { get; private set; }
}
