using LearnStack.Modules.Customization.Application.Contracts.Customization;
using LearnStack.Modules.Customization.Application.Contracts.CustomFields;
using LearnStack.Modules.Customization.Application.Contracts.Seeding;
using LearnStack.Modules.Customization.Application.IntegrationEvents;
using LearnStack.SharedKernel.Permissions;

namespace LearnStack.Modules.Customization.Application.Authorization;

public sealed class CustomizationOperationPolicySource : IOperationPolicySource
{
    public IEnumerable<PermissionRegistration> Definitions => [new(BoundedCustomFieldPermissions.Definition, true)];
    public IEnumerable<OperationPolicy> Policies =>
    [
        OperationPolicy.ForProtected<RegisterCustomFieldDefinitionCommand>("customization.custom_field.register", BoundedCustomFieldPermissions.Definition, requiresOrglessWrite: true),
        OperationPolicy.ForProtected<ReviseCustomFieldDefinitionDraftCommand>("customization.custom_field.revise", BoundedCustomFieldPermissions.Definition, requiresOrglessWrite: true),
        OperationPolicy.ForProtected<PublishCustomFieldDefinitionCommand>("customization.custom_field.publish", BoundedCustomFieldPermissions.Definition, requiresOrglessWrite: true),
        OperationPolicy.ForProtected<RetireCustomFieldDefinitionCommand>("customization.custom_field.retire", BoundedCustomFieldPermissions.Definition, requiresOrglessWrite: true),
        OperationPolicy.ForProtected<DiscardCustomFieldDefinitionDraftCommand>("customization.custom_field.discard", BoundedCustomFieldPermissions.Definition, requiresOrglessWrite: true),
        OperationPolicy.ForProtected<ReadCustomFieldDefinitionReceiptQuery>("customization.custom_field.read_receipt", BoundedCustomFieldPermissions.Definition),
        OperationPolicy.ForProtected<GetCustomFieldDefinitionQuery>("customization.custom_field.read", BoundedCustomFieldPermissions.Definition),
        OperationPolicy.ForProtected<ListCustomFieldDefinitionsQuery>("customization.custom_field.list", BoundedCustomFieldPermissions.Definition),
        OperationPolicy.ForProtected<ListCustomFieldDefinitionRevisionsQuery>("customization.custom_field.list_revisions", BoundedCustomFieldPermissions.Definition),
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
