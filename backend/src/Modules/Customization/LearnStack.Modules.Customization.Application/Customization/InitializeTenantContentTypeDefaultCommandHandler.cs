using LearnStack.Modules.Customization.Application.Seeding;
using LearnStack.Modules.Customization.Application.Abstractions;
using LearnStack.Modules.Customization.Application.Contracts.Customization;
using LearnStack.Modules.Customization.Domain;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using LearnStack.SharedKernel.Validation;
using MediatR;

namespace LearnStack.Modules.Customization.Application.Customization;

/// <summary>Installs the built-in only for a concept the tenant has never touched.</summary>
internal sealed class InitializeTenantContentTypeDefaultCommandHandler(
    ITenantContentTypeStore contentTypes,
    ICustomizationGenerationStore generations,
    ITenantLevelTaxonomyCatalog taxonomies,
    IJsonSchemaValidator schemas,
    ITenantContext tenantContext,
    IClock clock,
    IGuidFactory guids,
    ISeedCustomizationWriteGuard seedGuard)
    : IRequestHandler<InitializeTenantContentTypeDefaultCommand, Result<None>>
{
    public async Task<Result<None>> Handle(
        InitializeTenantContentTypeDefaultCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!tenantContext.IsResolved) return CustomizationFailures.Unresolved<None>();

        if (!await seedGuard.PrepareAsync(request, cancellationToken)) return Result<None>.Fail(seedGuard.Failure);

        var definition = BuiltInCustomizations.Card;
        if (await contentTypes.HasConceptHistoryAsync(definition.Key, cancellationToken))
            return Result.Ok(None.Value);

        var admitted = schemas.AdmitSchema(definition.JsonSchema);
        if (admitted.IsFailure) return CustomizationFailures.SchemaRefused<None>(admitted.Error!);

        var presentation = TextCardPresentation.Resolve(definition.JsonSchema, definition.RendererKey);
        if (presentation.IsFailure) return CustomizationFailures.SchemaRefused<None>(presentation.Error);

        var unresolved = await SchemaExtensionResolution.UnresolvedAsync(
            admitted.Value!, taxonomies, cancellationToken, textCardResolved: true);
        if (unresolved.Count > 0) return CustomizationFailures.ExtensionsUnresolved<None>(unresolved);

        var actor = tenantContext.UserId ?? UserId.SystemActor;
        if (!seedGuard.IsCurrent(request)) return CustomizationFailures.BusinessRule<None>("Seed", "lockey_business_rule_violation");
        var root = TenantContentType.Create(
            TenantContentTypeId.From(guids.NewUuidV7()), tenantContext.TenantId,
            definition.Key, BuiltInCustomizations.SchemaVersion, LocalizedText.From(definition.DisplayName),
            definition.JsonSchema, definition.RendererKey, clock, actor);
        root.Publish(clock, actor);

        // One save observes the complete Active root and produces one initialization audit.
        await contentTypes.AddAsync(root, cancellationToken);
        await generations.BumpAsync(tenantContext.TenantId, cancellationToken);
        return Result.Ok(None.Value);
    }
}
