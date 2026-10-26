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
