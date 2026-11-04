using LearnStack.Modules.Customization.Application.Abstractions;
using LearnStack.Modules.Customization.Application.Contracts.Customization;
using LearnStack.Modules.Customization.Application.Seeding;
using LearnStack.Modules.Education.Application.Seeding;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Results;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LearnStack.Tests.Integration;

internal static partial class ComponentOperationRegistration
{
    // Legacy authoring component proofs deliberately use tests.component policies
    // and arbitrary revisions. They establish domain/audit/concept coordination,
    // never fixed-v1 admission. Unlisted production policies retain the real guard.
    private static void AddContentComponentGuards(IServiceCollection services, ComponentAuthority authority)
    {
        services.TryAddScoped<SeedCustomizationWriteGuard>();
        services.TryAddScoped<SeedEducationWriteGuard>();
        services.Replace(ServiceDescriptor.Scoped<ISeedCustomizationWriteGuard>(provider => new ComponentCustomizationGuard(
            authority, provider.GetRequiredService<IOperationInvocationContext>(), provider.GetRequiredService<SeedCustomizationWriteGuard>(),
            provider.GetRequiredService<ITenantContentTypeStore>(), provider.GetRequiredService<ITenantLevelTaxonomyStore>())));
        services.Replace(ServiceDescriptor.Scoped<ISeedEducationWriteGuard>(provider => new ComponentEducationGuard(
            authority, provider.GetRequiredService<IOperationInvocationContext>(), provider.GetRequiredService<SeedEducationWriteGuard>())));
    }

    private static bool HasContentComponentInvocation(ComponentAuthority authority, OperationInvocation? invocation, object request) =>
        invocation is { IsActive: true, EntryKind: OperationEntryKind.Application }
        && ReferenceEquals(invocation.Operation, request) && authority.Policies.TryGetValue(request.GetType(), out var policy)
        && ReferenceEquals(invocation.Policy, policy) && policy.Id.StartsWith("tests.component.", StringComparison.Ordinal)
        && invocation.TrustedSource is { SourceKind: AuthorizationSourceKind.Seed } source
        && source.Matches(request.GetType(), invocation.Context);

    private sealed class ComponentCustomizationGuard(ComponentAuthority authority, IOperationInvocationContext execution,
        SeedCustomizationWriteGuard actual, ITenantContentTypeStore content, ITenantLevelTaxonomyStore taxonomies)
        : ISeedCustomizationWriteGuard
    {
        private OperationInvocation? _component;
        public Error Failure => _component is null ? actual.Failure : new(new LocalizedMessage("lockey_business_rule_violation"));

        public async Task<bool> PrepareAsync(object request, CancellationToken cancellationToken)
        {
            if (!HasContentComponentInvocation(authority, execution.CurrentInvocation, request))
            {
                _component = null;
                return await actual.PrepareAsync(request, cancellationToken);
            }
            _component = execution.CurrentInvocation;
            switch (request)
            {
                case RegisterTenantContentTypeCommand value:
                    if (value.Key != BuiltInCustomizations.Card.Key) await content.LockRegistrationsAsync(cancellationToken);
                    await content.LockConceptAsync(value.Key, cancellationToken);
                    break;
                case RegisterTenantLevelTaxonomyCommand value:
                    await taxonomies.LockConceptAsync(value.Key, cancellationToken);
                    break;
                case InitializeTenantContentTypeDefaultCommand:
                    await content.LockConceptAsync(BuiltInCustomizations.Card.Key, cancellationToken);
                    break;
                case InitializeTenantLevelTaxonomyDefaultCommand:
                    await taxonomies.LockConceptAsync(BuiltInCustomizations.Plain.Key, cancellationToken);
                    break;
                case PublishTenantContentTypeCommand or PublishTenantLevelTaxonomyCommand:
                    break;
                default:
                    return false;
            }
            return IsCurrent(request);
        }

        public bool IsCurrent(object request) => _component is null ? actual.IsCurrent(request)
            : ReferenceEquals(_component, execution.CurrentInvocation) && HasContentComponentInvocation(authority, _component, request);
    }

    private sealed class ComponentEducationGuard(ComponentAuthority authority, IOperationInvocationContext execution,
        SeedEducationWriteGuard actual) : ISeedEducationWriteGuard
    {
        private OperationInvocation? _component;
        public Error Failure => _component is null ? actual.Failure : new(new LocalizedMessage("lockey_business_rule_violation"));

        public Task<bool> PrepareAsync(object request, CancellationToken cancellationToken)
        {
            if (!HasContentComponentInvocation(authority, execution.CurrentInvocation, request))
            {
                _component = null;
                return actual.PrepareAsync(request, cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            _component = execution.CurrentInvocation;
            return Task.FromResult(IsCurrent(request));
        }

        public bool IsCurrent(object request) => _component is null ? actual.IsCurrent(request)
            : ReferenceEquals(_component, execution.CurrentInvocation) && HasContentComponentInvocation(authority, _component, request);
    }
}
