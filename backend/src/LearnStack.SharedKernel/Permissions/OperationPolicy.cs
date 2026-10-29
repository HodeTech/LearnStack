using System.Collections.Immutable;
using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.SharedKernel.Permissions;

public enum OperationPolicyKind { PublicRead = 1, AccountRead = 2, Protected = 3, FiniteTrusted = 4, NativeSessionAdmission = 5 }
public enum OperationReadScope { Scoped = 0, Tenant = 1 }

/// <summary>Definition composition is separate from stored grants and operation admission.</summary>
public sealed record PermissionRegistration(PermissionDefinition Definition, bool Assignable);

/// <summary>One immutable conjunction of exact scoped pairs and its own read authority ceiling.</summary>
public sealed class OperationPolicyClause
{
    public OperationPolicyClause(IEnumerable<PermissionDefinition> requiredPermissions,
        OperationReadScope readScope = OperationReadScope.Scoped)
    {
        ArgumentNullException.ThrowIfNull(requiredPermissions);
        var permissions = requiredPermissions.Take(9).ToImmutableArray();
        if (permissions.IsDefaultOrEmpty || permissions.Length > 8 || !Enum.IsDefined(readScope)
            || permissions.Any(permission => permission is null || permission.Scope == PermissionScope.Platform)
            || permissions.Select(permission => (permission.Scope, permission.Key)).Distinct().Count() != permissions.Length
            || readScope == OperationReadScope.Tenant && permissions.Any(permission => permission.Scope != PermissionScope.Tenant))
            throw new ArgumentException("Operation policy clause metadata is invalid.");
        RequiredPermissions = permissions;
        ReadScope = readScope;
    }

    public ImmutableArray<PermissionDefinition> RequiredPermissions { get; }
    public OperationReadScope ReadScope { get; }
    public override string ToString() => nameof(OperationPolicyClause);
}

/// <summary>Immutable metadata for one exact request type; it grants no authority by itself.</summary>
public sealed class OperationPolicy
{
    private OperationPolicy(Type requestType, string id, OperationPolicyKind kind,
        PermissionDefinition? permission, OperationReadScope readScope,
        ImmutableArray<AuthorizationSourceKind> allowedSources, bool requiresOrglessWrite, Type? eventHandler = null,
        ImmutableArray<OperationPolicyClause> alternatives = default, bool requiresOrganization = false)
    {
        ArgumentNullException.ThrowIfNull(requestType);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        alternatives = alternatives.IsDefault ? [] : alternatives;
        if (requestType.IsAbstract || requestType.ContainsGenericParameters || id.Length > 128
            || id.Any(character => !(character is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-'))
            || !Enum.IsDefined(kind) || !Enum.IsDefined(readScope)
            || (kind == OperationPolicyKind.PublicRead) != (permission is not null)
            || (kind == OperationPolicyKind.Protected) != !alternatives.IsDefaultOrEmpty
            || alternatives.Length > 8 || alternatives.Any(clause => clause is null)
            || alternatives.Any(clause => alternatives.Count(other => SameRequirements(clause, other)) != 1)
            || permission is { Scope: PermissionScope.Platform }
            || (readScope == OperationReadScope.Tenant && (kind != OperationPolicyKind.Protected
                || alternatives.Length != 1 || alternatives[0].ReadScope != OperationReadScope.Tenant || requiresOrglessWrite))
            || (requiresOrglessWrite && alternatives.Any(clause => clause.ReadScope != OperationReadScope.Scoped))
            || (kind == OperationPolicyKind.FiniteTrusted) != !allowedSources.IsDefaultOrEmpty
            || allowedSources.Any(source => source is not (AuthorizationSourceKind.Seed
                or AuthorizationSourceKind.IntegrationEvent or AuthorizationSourceKind.BackgroundJob))
            || allowedSources.Distinct().Count() != allowedSources.Length
            || allowedSources.Contains(AuthorizationSourceKind.IntegrationEvent) != (eventHandler is not null)
            || eventHandler is { IsAbstract: true } or { ContainsGenericParameters: true }
            || (requiresOrglessWrite && kind != OperationPolicyKind.Protected)
            || (requiresOrganization && (kind != OperationPolicyKind.Protected || requiresOrglessWrite)))
            throw new ArgumentException("Operation policy metadata is invalid.");
        RequestType = requestType;
        Id = id;
        Kind = kind;
        Alternatives = alternatives;
        RequiredPermission = permission ?? (Alternatives.Length == 1 && Alternatives[0].RequiredPermissions.Length == 1
            ? Alternatives[0].RequiredPermissions[0] : null);
        ReadScope = readScope;
        AllowedSources = allowedSources;
        RequiresOrglessWrite = requiresOrglessWrite;
        RequiresOrganization = requiresOrganization;
        TrustedEventHandlerType = eventHandler;
    }

    public Type RequestType { get; }
    public string Id { get; }
    public OperationPolicyKind Kind { get; }
    /// <summary>Compatibility metadata for a public or single-pair protected policy.</summary>
    public PermissionDefinition? RequiredPermission { get; }
    /// <summary>Compatibility metadata for a single clause; combinations consume the matched clause instead.</summary>
    public OperationReadScope ReadScope { get; }
    public ImmutableArray<OperationPolicyClause> Alternatives { get; }
    public ImmutableArray<AuthorizationSourceKind> AllowedSources { get; }
    public bool RequiresOrglessWrite { get; }
    public bool RequiresOrganization { get; }
    public Type? TrustedEventHandlerType { get; }

    public static OperationPolicy ForPublic<TRequest>(string id, PermissionDefinition permission) =>
        new(typeof(TRequest), id, OperationPolicyKind.PublicRead, permission, OperationReadScope.Scoped, [], false);

    public static OperationPolicy ForAccount<TRequest>(string id) =>
        new(typeof(TRequest), id, OperationPolicyKind.AccountRead, null, OperationReadScope.Scoped, [], false);

    public static OperationPolicy ForNativeSessionAdmission<TRequest>(string id) =>
        new(typeof(TRequest), id, OperationPolicyKind.NativeSessionAdmission, null, OperationReadScope.Scoped, [], false);

    public static OperationPolicy ForProtected<TRequest>(string id, PermissionDefinition permission,
        OperationReadScope readScope = OperationReadScope.Scoped, bool requiresOrglessWrite = false, bool requiresOrganization = false) =>
        ForProtected<TRequest>(id, [new OperationPolicyClause([permission], readScope)], requiresOrglessWrite, requiresOrganization);

    public static OperationPolicy ForProtected<TRequest>(string id, IEnumerable<OperationPolicyClause> alternatives,
        bool requiresOrglessWrite = false, bool requiresOrganization = false)
    {
        ArgumentNullException.ThrowIfNull(alternatives);
        var clauses = alternatives.Take(9).ToImmutableArray();
        var scope = clauses.Length == 1 && clauses[0] is not null ? clauses[0].ReadScope : OperationReadScope.Scoped;
        return new(typeof(TRequest), id, OperationPolicyKind.Protected, null, scope, [], requiresOrglessWrite,
            alternatives: clauses, requiresOrganization: requiresOrganization);
    }

    public static OperationPolicy ForTrusted<TRequest>(string id, params AuthorizationSourceKind[] allowedSources) =>
        new(typeof(TRequest), id, OperationPolicyKind.FiniteTrusted, null, OperationReadScope.Scoped,
            [.. allowedSources ?? throw new ArgumentNullException(nameof(allowedSources))], false);

    public static OperationPolicy ForEventInitializer<TRequest, THandler>(string id) =>
        new(typeof(TRequest), id, OperationPolicyKind.FiniteTrusted, null, OperationReadScope.Scoped,
            [AuthorizationSourceKind.Seed, AuthorizationSourceKind.IntegrationEvent], false, typeof(THandler));

    private static bool SameRequirements(OperationPolicyClause left, OperationPolicyClause right) =>
        left.RequiredPermissions.Length == right.RequiredPermissions.Length
        && left.RequiredPermissions.All(permission => right.RequiredPermissions.Any(other =>
            other.Scope == permission.Scope && other.Key == permission.Key));

    public override string ToString() => nameof(OperationPolicy);
}

/// <summary>Each source describes only its module's exact operations and scoped definitions.</summary>
public interface IOperationPolicySource
{
    IEnumerable<PermissionRegistration> Definitions { get; }
    IEnumerable<OperationPolicy> Policies { get; }
}

public interface IOperationPolicyCatalog
{
    IReadOnlyCollection<OperationPolicy> All { get; }
    IReadOnlyCollection<PermissionRegistration> Definitions { get; }
    bool TryGet(Type requestType, out OperationPolicy policy);
    bool TryGetDefinition(PermissionScope scope, string key, out PermissionRegistration definition);
}

/// <summary>Only the verified credential owner supplies a live human candidate.</summary>
public interface IAuthorizationCandidateSource
{
    AuthorizationCandidate? ForValidatedScope(TenantId tenant, OrganizationId? organization);
}

/// <summary>Identity owns fresh scoped evaluation; common Application consumes only its decision.</summary>
public interface IProtectedOperationAuthorizer
{
    Task<ProtectedOperationDecision> AuthorizeAsync(OperationInvocation invocation, OperationPolicy policy,
        AuthorizationCandidate candidate, CancellationToken cancellationToken = default);
}
