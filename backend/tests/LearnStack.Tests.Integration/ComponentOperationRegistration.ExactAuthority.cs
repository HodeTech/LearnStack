using System.Collections.Frozen;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LearnStack.Tests.Integration;

internal static partial class ComponentOperationRegistration
{
    /// <summary>
    /// Explicit authority for named component obligations, never a production permission
    /// or HTTP admission proof. The canonical behavior and every unlisted policy remain intact.
    /// </summary>
    internal static void AddExactComponentAuthority(IServiceCollection services, params OperationPolicy[] exactPolicies)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(exactPolicies);
        var policies = exactPolicies.ToArray();
        if (policies.Length == 0 || policies.Any(policy => policy is null
                || policy.Kind != OperationPolicyKind.FiniteTrusted || policy.ReadScope != OperationReadScope.Scoped
                || policy.TrustedEventHandlerType is not null || policy.RequiresOrglessWrite
                || policy.AllowedSources.Length != 1 || policy.AllowedSources[0] != AuthorizationSourceKind.Seed
                || !policy.Id.StartsWith("tests.component.", StringComparison.Ordinal))
            || policies.Select(policy => policy.RequestType).Distinct().Count() != policies.Length
            || policies.Select(policy => policy.Id).Distinct(StringComparer.Ordinal).Count() != policies.Length
            || services.Any(descriptor => descriptor.ServiceType == typeof(ComponentAuthority)))
            throw new InvalidOperationException("Component authority requires one explicit finite request set.");

        var catalog = services.Single(descriptor => descriptor.ServiceType == typeof(IOperationPolicyCatalog));
        if (catalog.IsKeyedService || catalog.Lifetime != ServiceLifetime.Singleton
            || (catalog.ImplementationInstance is not IOperationPolicyCatalog && catalog.ImplementationFactory is null))
            throw new InvalidOperationException("Component authority requires the existing frozen policy catalogue.");

        var authority = new ComponentAuthority(policies);
        services.AddSingleton(authority);
        services.Replace(ServiceDescriptor.Singleton<IOperationPolicyCatalog>(provider =>
            new ComponentCatalog(catalog.ImplementationInstance as IOperationPolicyCatalog
                ?? (IOperationPolicyCatalog)catalog.ImplementationFactory!(provider), authority)));
        // No ITenantContext dependency here: binding admission can resolve the graph
        // before it installs the actual context. Bind only at the first exact capture.
        services.AddScoped<ITrustedOperationSource>(_ => new ComponentSource(authority));
        AddContentComponentGuards(services, authority);
    }

    private sealed class ComponentAuthority(OperationPolicy[] policies)
    {
        internal FrozenDictionary<Type, OperationPolicy> Policies { get; } = policies.ToFrozenDictionary(policy => policy.RequestType);
    }

    private sealed class ComponentCatalog : IOperationPolicyCatalog
    {
        private readonly IOperationPolicyCatalog _actual;
        private readonly ComponentAuthority _authority;

        internal ComponentCatalog(IOperationPolicyCatalog actual, ComponentAuthority authority)
        {
            _actual = actual;
            _authority = authority;
            All = actual.All.Where(policy => !authority.Policies.ContainsKey(policy.RequestType))
                .Concat(authority.Policies.Values).ToArray();
            if (All.Select(policy => policy.Id).Distinct(StringComparer.Ordinal).Count() != All.Count)
                throw new InvalidOperationException("Component policy identities must remain unambiguous.");
        }

        public IReadOnlyCollection<OperationPolicy> All { get; }
        public IReadOnlyCollection<PermissionRegistration> Definitions => _actual.Definitions;
        public bool TryGet(Type requestType, out OperationPolicy policy) =>
            _authority.Policies.TryGetValue(requestType, out policy!) || _actual.TryGet(requestType, out policy!);
        public bool TryGetDefinition(PermissionScope scope, string key, out PermissionRegistration definition) =>
            _actual.TryGetDefinition(scope, key, out definition!);
    }

    private sealed class ComponentSource(ComponentAuthority authority)
        : TrustedOperationDescriptor.MintingSource, ITrustedOperationSource
    {
        private readonly object _gate = new();
        private ContextSnapshot? _snapshot;
        private bool _invalidated;

        public TrustedOperationDescriptor? Capture(Type requestType, ITenantContext context)
        {
            ArgumentNullException.ThrowIfNull(requestType);
            ArgumentNullException.ThrowIfNull(context);
            lock (_gate)
            {
                if (!CanIssue || _invalidated || !authority.Policies.ContainsKey(requestType)) return null;
                _snapshot ??= new ContextSnapshot(context);
                if (!_snapshot.Matches(context))
                {
                    if (ReferenceEquals(_snapshot.Context, context)) _invalidated = true;
                    return null;
                }
                return ForOperation(AuthorizationSourceKind.Seed, requestType, context, _snapshot);
            }
        }

        protected override bool IsEvidenceCurrent(object? evidence)
        {
            lock (_gate)
            {
                if (_invalidated || !ReferenceEquals(evidence, _snapshot) || _snapshot is not { } snapshot) return false;
                if (snapshot.Matches(snapshot.Context)) return true;
                _invalidated = true;
                return false;
            }
        }
    }

    private sealed class ContextSnapshot(ITenantContext context)
    {
        private readonly bool _resolved = context.IsResolved;
        private readonly TenantId? _tenant = context.IsResolved ? context.TenantId : null;
        private readonly OrganizationId? _organization = context.OrganizationId;
        private readonly UserId? _actor = context.UserId;
        private readonly UserId? _causalActor = context.CausalActorUserId;
        private readonly TenantContextOrigin? _origin = context.Origin;
        private readonly HostScope? _host = context.HostScope;
        internal ITenantContext Context { get; } = context;

        internal bool Matches(ITenantContext current) => ReferenceEquals(Context, current)
            && current.IsResolved == _resolved && (!_resolved || current.TenantId == _tenant)
            && current.OrganizationId == _organization && current.UserId == _actor
            && current.CausalActorUserId == _causalActor && current.Origin == _origin
            && ReferenceEquals(current.HostScope, _host);
    }
}
