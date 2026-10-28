using LearnStack.Application.Authorization;
using LearnStack.Application.Messaging;
using LearnStack.Application.Persistence;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace LearnStack.Tests.Integration;

/// <summary>Explicit, bounded authority for isolated component obligations; canonical authorization remains active.</summary>
internal static partial class ComponentOperationRegistration
{
    internal static void AddValidationOperation(IServiceCollection services)
    {
        services.AddSingleton<IOperationPolicySource, ValidationPolicy>();
        services.AddScoped<ITrustedOperationSource, ValidationSource>();
        UseNoDatabaseCompletion(services, typeof(TestValidationCommand));
    }

    internal static void AddCeilingOperations(IServiceCollection services, CeilingPolicyCatalog policies)
    {
        services.Replace(ServiceDescriptor.Singleton<IOperationPolicyCatalog>(policies));
        UseNoDatabaseCompletion(services, typeof(CeilingPublicQuery), typeof(CeilingGuardedQuery));
    }

    private static void UseNoDatabaseCompletion(IServiceCollection services, params Type[] requests)
    {
        // Keep all seven actual behaviors and the actual slot-5 coordinator/evidence
        // owner. These particular test operations are explicitly Off and own no DB;
        // omit only the optional physical-completion evidence participant, rather
        // than inventing a DbTransaction or a SQL announcement for this unit double.
        services.Replace(ServiceDescriptor.Scoped(provider =>
        {
            var catalog = provider.GetRequiredService<IAuditCatalog>();
            if (requests.Any(type => !catalog.TryGet(type, out var registration)
                    || !registration.WritesNoRow || registration.Entries.Count != 0)
                || provider.GetRequiredService<IUnitOfWork>() is not NoDatabaseUnitOfWork)
                throw new InvalidOperationException("Component completion requires explicit Off operations and the no-database unit.");
            return new TransactionExecutionCoordinator(
                provider.GetRequiredService<IUnitOfWork>(), provider.GetRequiredService<ITenantContext>(),
                provider.GetRequiredService<IAuditStore>(), provider.GetRequiredService<IAuditStateCapture>(),
                provider.GetRequiredService<ProducerExecution>(), provider.GetRequiredService<DomainEventCollector>(),
                provider.GetRequiredService<ILogger<TransactionExecutionCoordinator>>(),
                provider.GetRequiredService<OperationAuthorizationCoordinator>());
        }));
    }

    private sealed class ValidationPolicy : IOperationPolicySource
    {
        public IEnumerable<PermissionRegistration> Definitions => [];
        public IEnumerable<OperationPolicy> Policies =>
            [OperationPolicy.ForTrusted<TestValidationCommand>("tests.validation.execute", AuthorizationSourceKind.Seed)];
    }

    private sealed class ValidationSource : TrustedOperationDescriptor.MintingSource, ITrustedOperationSource
    {
        protected override bool IsEvidenceCurrent(object? evidence) =>
            ReferenceEquals(evidence, TestResolvedTenantContext.Instance);

        public TrustedOperationDescriptor? Capture(Type requestType, ITenantContext context) =>
            requestType == typeof(TestValidationCommand) && ReferenceEquals(context, TestResolvedTenantContext.Instance)
                ? ForOperation(AuthorizationSourceKind.Seed, requestType, context, context) : null;
    }
}

/// <summary>Counts actual catalogue consultation so the step-4 refusal cannot pass merely at step 5.</summary>
internal sealed class CeilingPolicyCatalog : IOperationPolicyCatalog
{
    private readonly OperationPolicyCatalog _inner = new([new CeilingPolicies()]);
    private int _publicLookups;
    private int _guardedLookups;
    public int PublicLookups => Volatile.Read(ref _publicLookups);
    public int GuardedLookups => Volatile.Read(ref _guardedLookups);
    public IReadOnlyCollection<OperationPolicy> All => _inner.All;
    public IReadOnlyCollection<PermissionRegistration> Definitions => _inner.Definitions;
    public bool TryGet(Type requestType, out OperationPolicy policy)
    {
        if (requestType == typeof(CeilingPublicQuery)) Interlocked.Increment(ref _publicLookups);
        if (requestType == typeof(CeilingGuardedQuery)) Interlocked.Increment(ref _guardedLookups);
        return _inner.TryGet(requestType, out policy);
    }
    public bool TryGetDefinition(PermissionScope scope, string key, out PermissionRegistration definition) =>
        _inner.TryGetDefinition(scope, key, out definition);

    private sealed class CeilingPolicies : IOperationPolicySource
    {
        private static readonly PermissionDefinition Read = new(PermissionScope.Tenant, "tests.ceiling.read");
        private static readonly PermissionDefinition Admin = new(PermissionScope.Tenant, "tests.ceiling.admin");
        public IEnumerable<PermissionRegistration> Definitions => [new(Read, false), new(Admin, true)];
        public IEnumerable<OperationPolicy> Policies =>
        [
            OperationPolicy.ForPublic<CeilingPublicQuery>("tests.ceiling.public", Read),
            OperationPolicy.ForProtected<CeilingGuardedQuery>("tests.ceiling.guarded", Admin),
        ];
    }
}
