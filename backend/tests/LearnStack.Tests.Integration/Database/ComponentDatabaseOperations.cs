using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// Finite policies for isolated database-backed component transports. They grant
/// no production route or human authority; the actual canonical slot 5 remains active.
/// </summary>
internal static class ComponentDatabaseOperations
{
    internal static void AddIsolationOperations(IServiceCollection services)
    {
        services.AddSingleton<IOperationPolicySource, IsolationPolicies>();
        services.AddScoped<ITrustedOperationSource, UnresolvedReadSource>();
        services.AddSingleton(new ExplicitReadOnlyRequest(typeof(UnresolvedProbeQuery)));
        services.AddSingleton(new ExplicitReadOnlyRequest(typeof(UnresolvedCustomizationProbeQuery)));
    }

    internal static void AddProvisioningOperation(IServiceCollection services)
    {
        services.AddSingleton<IOperationPolicySource, ProvisioningPolicy>();
        services.AddScoped<ITrustedOperationSource, ProvisioningSource>();
    }

    private sealed class IsolationPolicies : IOperationPolicySource
    {
        private static readonly PermissionDefinition Read = new(PermissionScope.Tenant, "tests.isolation.read");
        public IEnumerable<PermissionRegistration> Definitions => [new(Read, false)];
        public IEnumerable<OperationPolicy> Policies =>
        [
            OperationPolicy.ForPublic<ProbeQuery>("tests.isolation.read", Read),
            // The deliberate INSERT probe is public solely to prove the physical RO
            // barrier refuses it. The identical writable app-role controls are separate.
            OperationPolicy.ForPublic<ForeignWriteCommand>("tests.isolation.foreign_write", Read),
            OperationPolicy.ForTrusted<UnresolvedProbeQuery>("tests.isolation.unresolved_settings", AuthorizationSourceKind.Seed),
            OperationPolicy.ForTrusted<UnresolvedCustomizationProbeQuery>("tests.isolation.unresolved_customization", AuthorizationSourceKind.Seed),
        ];
    }

    private sealed class UnresolvedReadSource : TrustedOperationDescriptor.MintingSource, ITrustedOperationSource
    {
        protected override bool IsEvidenceCurrent(object? evidence) =>
            ReferenceEquals(evidence, UnresolvedTenantContext.Instance);

        public TrustedOperationDescriptor? Capture(Type requestType, ITenantContext context) =>
            CanIssue && ReferenceEquals(context, UnresolvedTenantContext.Instance)
                && (requestType == typeof(UnresolvedProbeQuery) || requestType == typeof(UnresolvedCustomizationProbeQuery))
                ? ForOperation(AuthorizationSourceKind.Seed, requestType, context, context) : null;
    }

    private sealed class ProvisioningPolicy : IOperationPolicySource
    {
        public IEnumerable<PermissionRegistration> Definitions => [];
        public IEnumerable<OperationPolicy> Policies =>
            [OperationPolicy.ForTrusted<ProvisionTenantCommand>("tests.outbox.provision", AuthorizationSourceKind.Seed)];
    }

    private sealed class ProvisioningSource : TrustedOperationDescriptor.MintingSource, ITrustedOperationSource
    {
        protected override bool IsEvidenceCurrent(object? evidence) =>
            ReferenceEquals(evidence, UnresolvedTenantContext.Instance);

        public TrustedOperationDescriptor? Capture(Type requestType, ITenantContext context) =>
            CanIssue && requestType == typeof(ProvisionTenantCommand)
                && ReferenceEquals(context, UnresolvedTenantContext.Instance)
                ? ForOperation(AuthorizationSourceKind.Seed, requestType, context, context) : null;
    }
}
