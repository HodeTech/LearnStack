using System.Collections.Frozen;
using System.Collections.Immutable;
using LearnStack.SharedKernel.Permissions;

namespace LearnStack.Application.Authorization;

/// <summary>One frozen exact request/definition snapshot, independent of any caller's current Roles.</summary>
public sealed class OperationPolicyCatalog : IOperationPolicyCatalog
{
    private readonly FrozenDictionary<Type, OperationPolicy> _policies;
    private readonly FrozenDictionary<(PermissionScope Scope, string Key), PermissionRegistration> _definitions;
    private readonly ImmutableArray<OperationPolicy> _all;
    private readonly ImmutableArray<PermissionRegistration> _allDefinitions;

    public OperationPolicyCatalog(IEnumerable<IOperationPolicySource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var policies = new Dictionary<Type, OperationPolicy>();
        var definitions = new Dictionary<(PermissionScope, string), PermissionRegistration>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            ArgumentNullException.ThrowIfNull(source);
            foreach (var registration in source.Definitions)
            {
                if (registration?.Definition is not { } definition || definition.Scope == PermissionScope.Platform
                    || !definitions.TryAdd((definition.Scope, definition.Key), registration))
                    throw new InvalidOperationException("Operation permission definitions are missing, conflicting or unsupported.");
            }
            foreach (var policy in source.Policies)
            {
                if (policy is null || !policies.TryAdd(policy.RequestType, policy) || !ids.Add(policy.Id))
                    throw new InvalidOperationException("Operation policy declarations are missing or conflicting.");
            }
        }
        foreach (var policy in policies.Values)
        {
            IEnumerable<PermissionDefinition> requirements = policy.Kind == OperationPolicyKind.Protected
                ? policy.Alternatives.SelectMany(clause => clause.RequiredPermissions)
                : policy.RequiredPermission is { } permission ? [permission] : Enumerable.Empty<PermissionDefinition>();
            foreach (var required in requirements)
            {
                if (!definitions.TryGetValue((required.Scope, required.Key), out var registered)
                    || registered.Definition != required
                    || registered.Assignable != (policy.Kind == OperationPolicyKind.Protected)
                    || (policy.Kind == OperationPolicyKind.PublicRead
                        && (required.Scope != PermissionScope.Tenant || !required.Key.EndsWith(".read", StringComparison.Ordinal))))
                    throw new InvalidOperationException("Operation policy references an unregistered or incompatible permission.");
            }
        }
        if (policies.Values.Where(policy => policy.TrustedEventHandlerType is not null)
            .GroupBy(policy => policy.TrustedEventHandlerType).Any(group => group.Count() != 1))
            throw new InvalidOperationException("Event initializer policy declarations are ambiguous.");
        _policies = policies.ToFrozenDictionary();
        _definitions = definitions.ToFrozenDictionary();
        _all = [.. policies.Values.OrderBy(policy => policy.RequestType.FullName, StringComparer.Ordinal)];
        _allDefinitions = [.. definitions.Values.OrderBy(registration => registration.Definition.Scope)
            .ThenBy(registration => registration.Definition.Key, StringComparer.Ordinal)];
    }

    public IReadOnlyCollection<OperationPolicy> All => _all;
    public IReadOnlyCollection<PermissionRegistration> Definitions => _allDefinitions;

    public bool TryGet(Type requestType, out OperationPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(requestType);
        return _policies.TryGetValue(requestType, out policy!);
    }

    public bool TryGetDefinition(PermissionScope scope, string key, out PermissionRegistration definition) =>
        _definitions.TryGetValue((scope, key), out definition!);
}
