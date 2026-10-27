using System.Collections.Immutable;
using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.SharedKernel.Permissions;

public enum OperationPolicyKind { PublicRead = 1, AccountRead = 2, Protected = 3, FiniteTrusted = 4 }
public enum OperationReadScope { Scoped = 0, Tenant = 1 }

/// <summary>Definition composition is separate from stored grants and operation admission.</summary>
public sealed record PermissionRegistration(PermissionDefinition Definition, bool Assignable);

/// <summary>Immutable metadata for one exact request type; it grants no authority by itself.</summary>
public sealed class OperationPolicy
{
    private OperationPolicy(Type requestType, string id, OperationPolicyKind kind,
        PermissionDefinition? permission, OperationReadScope readScope,
        ImmutableArray<AuthorizationSourceKind> allowedSources, bool requiresOrglessWrite, Type? eventHandler = null)
    {
        ArgumentNullException.ThrowIfNull(requestType);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (requestType.IsAbstract || requestType.ContainsGenericParameters || id.Length > 128
            || id.Any(character => !(character is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-'))
            || !Enum.IsDefined(kind) || !Enum.IsDefined(readScope)
            || (kind is OperationPolicyKind.PublicRead or OperationPolicyKind.Protected) != (permission is not null)
            || permission is { Scope: PermissionScope.Platform }
            || (readScope == OperationReadScope.Tenant && (kind != OperationPolicyKind.Protected
                || permission?.Scope != PermissionScope.Tenant || requiresOrglessWrite))
            || (kind == OperationPolicyKind.FiniteTrusted) != !allowedSources.IsDefaultOrEmpty
            || allowedSources.Any(source => source is not (AuthorizationSourceKind.Seed
                or AuthorizationSourceKind.IntegrationEvent or AuthorizationSourceKind.BackgroundJob))
            || allowedSources.Distinct().Count() != allowedSources.Length
            || allowedSources.Contains(AuthorizationSourceKind.IntegrationEvent) != (eventHandler is not null)
            || eventHandler is { IsAbstract: true } or { ContainsGenericParameters: true }
            || (requiresOrglessWrite && kind != OperationPolicyKind.Protected))
            throw new ArgumentException("Operation policy metadata is invalid.");
        RequestType = requestType;
        Id = id;
        Kind = kind;
        RequiredPermission = permission;
        ReadScope = readScope;
        AllowedSources = allowedSources;
        RequiresOrglessWrite = requiresOrglessWrite;
        TrustedEventHandlerType = eventHandler;
    }

    public Type RequestType { get; }
    public string Id { get; }
    public OperationPolicyKind Kind { get; }
    public PermissionDefinition? RequiredPermission { get; }
    public OperationReadScope ReadScope { get; }
    public ImmutableArray<AuthorizationSourceKind> AllowedSources { get; }
    public bool RequiresOrglessWrite { get; }
    public Type? TrustedEventHandlerType { get; }

    public static OperationPolicy ForPublic<TRequest>(string id, PermissionDefinition permission) =>
        new(typeof(TRequest), id, OperationPolicyKind.PublicRead, permission, OperationReadScope.Scoped, [], false);

    public static OperationPolicy ForAccount<TRequest>(string id) =>
        new(typeof(TRequest), id, OperationPolicyKind.AccountRead, null, OperationReadScope.Scoped, [], false);

    public static OperationPolicy ForProtected<TRequest>(string id, PermissionDefinition permission,
        OperationReadScope readScope = OperationReadScope.Scoped, bool requiresOrglessWrite = false) =>
        new(typeof(TRequest), id, OperationPolicyKind.Protected, permission, readScope, [], requiresOrglessWrite);

    public static OperationPolicy ForTrusted<TRequest>(string id, params AuthorizationSourceKind[] allowedSources) =>
        new(typeof(TRequest), id, OperationPolicyKind.FiniteTrusted, null, OperationReadScope.Scoped,
            [.. allowedSources ?? throw new ArgumentNullException(nameof(allowedSources))], false);

    public static OperationPolicy ForEventInitializer<TRequest, THandler>(string id) =>
        new(typeof(TRequest), id, OperationPolicyKind.FiniteTrusted, null, OperationReadScope.Scoped,
            [AuthorizationSourceKind.Seed, AuthorizationSourceKind.IntegrationEvent], false, typeof(THandler));

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
