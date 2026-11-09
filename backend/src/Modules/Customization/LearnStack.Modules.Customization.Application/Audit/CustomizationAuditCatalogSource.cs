using LearnStack.Modules.Customization.Application.Contracts.Seeding;
using LearnStack.Modules.Customization.Application.Contracts.CustomFields;
using LearnStack.Modules.Customization.Application.Contracts.Customization;
using LearnStack.Modules.Customization.Application.IntegrationEvents;
using LearnStack.Modules.Customization.Domain;
using LearnStack.SharedKernel.Audit;

namespace LearnStack.Modules.Customization.Application.Audit;

/// <summary>
/// What Customization audits, in code, beside
/// <see href="../../../../../docs/modules/customization/audit.md">the matrix</see>.
/// </summary>
/// <remarks>
/// Each write command declares its own MUST operation. Registration and publication are separate operations
/// because they have different blast radii — a wrong draft constrains nothing, while a
/// wrong publication retires the incumbent and changes what every subsequent content write
/// is validated against — and the matrix carries one row for each.
/// </remarks>
public sealed class CustomizationAuditCatalogSource : IAuditCatalogSource
{
    /// <inheritdoc />
    public string ModuleName => "customization";

    /// <inheritdoc />
    public void Describe(IAuditCatalogBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Off<GetCustomFieldDefinitionQuery>();
        builder.Off<ListCustomFieldDefinitionsQuery>();
        builder.Off<ListCustomFieldDefinitionRevisionsQuery>();
        builder
            .MustAudit<RegisterCustomFieldDefinitionCommand>("customization.custom_field.register", OperationType.Create, typeof(TenantCustomFieldDef))
            .MustAudit<ReviseCustomFieldDefinitionDraftCommand>("customization.custom_field.revise", OperationType.Update, typeof(TenantCustomFieldDef))
            .MustAudit<PublishCustomFieldDefinitionCommand>("customization.custom_field.publish", OperationType.Update, typeof(TenantCustomFieldDef))
            .MustAudit<RetireCustomFieldDefinitionCommand>("customization.custom_field.retire", OperationType.Update, typeof(TenantCustomFieldDef))
            .MustAudit<DiscardCustomFieldDefinitionDraftCommand>("customization.custom_field.discard", OperationType.Delete, typeof(TenantCustomFieldDef));

        builder.Off<GetContentTypeSeedStateQuery>();
        builder.Off<GetTaxonomySeedStateQuery>();
        builder.Off<GetActiveContentTypeSeedRevisionQuery>();
        builder.Off<GetActiveTaxonomySeedRevisionQuery>();
        builder.Off<InitializeContentTypeDefaultHandler>();
        builder.Off<InitializeTaxonomyDefaultHandler>();

        builder
            .MustAudit<InitializeTenantContentTypeDefaultCommand>(
                "customization.content_type.initialize",
                OperationType.Create,
                typeof(TenantContentType))
            .MustAudit<InitializeTenantLevelTaxonomyDefaultCommand>(
                "customization.level_taxonomy.initialize",
                OperationType.Create,
                typeof(TenantLevelTaxonomy))
            .MustAudit<RegisterTenantContentTypeCommand>(
                "customization.content_type.register",
                OperationType.Create,
                typeof(TenantContentType))
            .MustAudit<PublishTenantContentTypeCommand>(
                "customization.content_type.publish",
                OperationType.Update,
                typeof(TenantContentType))
            .MustAudit<RegisterTenantLevelTaxonomyCommand>(
                "customization.level_taxonomy.register",
                OperationType.Create,
                typeof(TenantLevelTaxonomy))
            .MustAudit<PublishTenantLevelTaxonomyCommand>(
                "customization.level_taxonomy.publish",
                OperationType.Update,
                typeof(TenantLevelTaxonomy));
    }
}
