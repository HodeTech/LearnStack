using LearnStack.Modules.Customization.Application.Contracts.Customization;
using LearnStack.Modules.Customization.Application.Contracts.Seeding;
using LearnStack.Modules.Customization.Application.IntegrationEvents;
using LearnStack.SharedKernel.Permissions;

namespace LearnStack.Modules.Customization.Application.Authorization;

public sealed class CustomizationOperationPolicySource : IOperationPolicySource
{
    public IEnumerable<PermissionRegistration> Definitions => [];
    public IEnumerable<OperationPolicy> Policies =>
    [
        OperationPolicy.ForTrusted<GetContentTypeSeedStateQuery>("customization.seed.read_content_type", AuthorizationSourceKind.Seed),
        OperationPolicy.ForTrusted<GetTaxonomySeedStateQuery>("customization.seed.read_taxonomy", AuthorizationSourceKind.Seed),
        OperationPolicy.ForTrusted<GetActiveContentTypeSeedRevisionQuery>("customization.seed.read_content_revision", AuthorizationSourceKind.Seed),
        OperationPolicy.ForTrusted<GetActiveTaxonomySeedRevisionQuery>("customization.seed.read_taxonomy_revision", AuthorizationSourceKind.Seed),
        OperationPolicy.ForTrusted<RegisterTenantContentTypeCommand>("customization.seed.register_content_type", AuthorizationSourceKind.Seed),
        OperationPolicy.ForTrusted<PublishTenantContentTypeCommand>("customization.seed.publish_content_type", AuthorizationSourceKind.Seed),
        OperationPolicy.ForTrusted<RegisterTenantLevelTaxonomyCommand>("customization.seed.register_taxonomy", AuthorizationSourceKind.Seed),
        OperationPolicy.ForTrusted<PublishTenantLevelTaxonomyCommand>("customization.seed.publish_taxonomy", AuthorizationSourceKind.Seed),
        OperationPolicy.ForEventInitializer<InitializeTenantContentTypeDefaultCommand, InitializeContentTypeDefaultHandler>("customization.default.initialize_content_type"),
        OperationPolicy.ForEventInitializer<InitializeTenantLevelTaxonomyDefaultCommand, InitializeTaxonomyDefaultHandler>("customization.default.initialize_taxonomy"),
    ];
}
