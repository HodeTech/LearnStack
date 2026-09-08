using LearnStack.Modules.Customization.Application.Contracts.Customization;
using LearnStack.Modules.Customization.Domain;
using LearnStack.SharedKernel.Audit;

namespace LearnStack.Modules.Customization.Application.Audit;

/// <summary>
/// What Customization audits, in code, beside
/// <see href="../../../../../docs/modules/customization/audit.md">the matrix</see>.
/// </summary>
/// <remarks>
/// Four commands, four MUST rows. Registration and publication are separate operations
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

        builder
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
