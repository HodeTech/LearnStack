using LearnStack.SharedKernel.Domain;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;

namespace LearnStack.Modules.Identity.Domain;

[TenantOwned]
public sealed class Role : AuditableEntity<RoleId>, IAggregateRoot<RoleId>, ITenantOwned
{
    private readonly List<RolePermissionGrant> _grants = [];
    private Role() { }

    public TenantId TenantId { get; private set; }
    public BuiltInRoleKey BuiltInKey { get; private set; }
    public PermissionScope PermissionScope { get; private set; }
    public IReadOnlyCollection<RolePermissionGrant> Grants => _grants.AsReadOnly();

    public static Result<Role> Create(RoleId id, TenantId tenantId, BuiltInRoleKey builtInKey,
        PermissionScope permissionScope, DateTimeOffset at, UserId actor)
    {
        if (!id.IsInitialized() || id.Value == Guid.Empty || id.Value.Version != 7)
            throw new ArgumentException("Role identifier is invalid.", nameof(id));
        TenantOwnership.EnsureRealTenant(tenantId, "Role tenant is invalid.", nameof(tenantId));
        var template = IdentityFoundationManifest.Version1.FindTemplate(builtInKey, permissionScope);
        if (template is null) return Result<Role>.Fail(IdentityFailures.Refused());
        var role = new Role { Id = id, TenantId = tenantId, BuiltInKey = builtInKey, PermissionScope = permissionScope };
        role.MarkCreated(at, actor);
        foreach (var definition in template.Grants) role._grants.Add(new RolePermissionGrant(role, definition));
        return Result<Role>.Ok(role);
    }

    public bool MatchesInitialTemplate()
    {
        var template = IdentityFoundationManifest.Version1.FindTemplate(BuiltInKey, PermissionScope);
        return !IsDeleted && template is not null && _grants.Count == template.Grants.Length
            && template.Grants.All(definition => _grants.Any(grant => grant.PermissionScope == definition.Scope
                && grant.PermissionKey == definition.Key));
    }

    public bool IsTenantAdministrator => !IsDeleted && BuiltInKey == BuiltInRoleKey.TenantAdmin
        && PermissionScope == PermissionScope.Tenant
        && IdentityFoundationManifest.Version1.TenantAdminKernel.All(definition =>
            _grants.Any(grant => grant.PermissionScope == definition.Scope && grant.PermissionKey == definition.Key));

    public bool CanReplaceGrants(IReadOnlyCollection<PermissionDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        var manifest = IdentityFoundationManifest.Version1;
        return !IsDeleted && manifest.FindTemplate(BuiltInKey, PermissionScope) is not null
            && definitions.Count <= manifest.Definitions.Length
            && definitions.All(definition => definition is { IsActive: true }
                && definition.Scope == PermissionScope && manifest.IsActive(definition.Scope, definition.Key)
                && (definition.Key != IdentityFoundationManifest.RoleAdmin || BuiltInKey == BuiltInRoleKey.TenantAdmin))
            && definitions.Distinct().Count() == definitions.Count
            && (BuiltInKey != BuiltInRoleKey.TenantAdmin || IsTenantAdministrator
                && manifest.TenantAdminKernel.All(definition => definitions.Contains(definition)));
    }

    /// <summary>Retains unchanged grant instances so EF tracks only the actual contained additions/removals.</summary>
    public Result<None> ReplaceGrants(IReadOnlyCollection<PermissionDefinition> definitions, DateTimeOffset at, UserId actor)
    {
        if (!CanReplaceGrants(definitions)) return IdentityFailures.BusinessRule();
        if (_grants.Count == definitions.Count && definitions.All(definition => _grants.Any(grant =>
            grant.PermissionScope == definition.Scope && grant.PermissionKey == definition.Key)))
            return Result<None>.Ok(None.Value);
        MarkUpdated(at, actor);
        _grants.RemoveAll(grant => !definitions.Any(definition => definition.Scope == grant.PermissionScope && definition.Key == grant.PermissionKey));
        foreach (var definition in definitions)
            if (!_grants.Any(grant => grant.PermissionScope == definition.Scope && grant.PermissionKey == definition.Key))
                _grants.Add(new RolePermissionGrant(this, definition));
        return Result<None>.Ok(None.Value);
    }
}

[TenantOwned]
public sealed class RolePermissionGrant : ITenantOwned
{
    private RolePermissionGrant() { PermissionKey = null!; Role = null!; }
    internal RolePermissionGrant(Role role, PermissionDefinition definition)
    {
        Role = role;
        TenantId = role.TenantId;
        RoleId = role.Id;
        PermissionScope = definition.Scope;
        PermissionKey = definition.Key;
    }
    public TenantId TenantId { get; private set; }
    public RoleId RoleId { get; private set; }
    public PermissionScope PermissionScope { get; private set; }
    public string PermissionKey { get; private set; }
    public Role Role { get; private set; }
}
