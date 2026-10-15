using LearnStack.Modules.Customization.Application.Abstractions;
using LearnStack.Modules.Customization.Application.Contracts.Customization;
using LearnStack.Modules.Customization.Domain;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using MediatR;

namespace LearnStack.Modules.Customization.Application.Customization;

/// <summary>Installs the built-in vocabulary without revising any existing history.</summary>
internal sealed class InitializeTenantLevelTaxonomyDefaultCommandHandler(
    ITenantLevelTaxonomyStore taxonomies,
    ICustomizationGenerationStore generations,
    ITenantContext tenantContext,
    IClock clock,
    IGuidFactory guids)
    : IRequestHandler<InitializeTenantLevelTaxonomyDefaultCommand, Result<None>>
{
    public async Task<Result<None>> Handle(
        InitializeTenantLevelTaxonomyDefaultCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!tenantContext.IsResolved) return CustomizationFailures.Unresolved<None>();

        var definition = BuiltInCustomizations.Plain;
        await taxonomies.LockConceptAsync(definition.Key, cancellationToken);
        if (await taxonomies.HasConceptHistoryAsync(definition.Key, cancellationToken))
            return Result.Ok(None.Value);

        var actor = tenantContext.UserId ?? UserId.SystemActor;
        var root = TenantLevelTaxonomy.Create(
            TenantLevelTaxonomyId.From(guids.NewUuidV7()), tenantContext.TenantId,
            definition.Key, BuiltInCustomizations.SchemaVersion, LocalizedText.From(definition.DisplayName),
            clock, actor);
        foreach (var band in definition.Bands)
            root.AddItem(band.Key, LocalizedText.From(band.DisplayName), band.Sort, null, clock, actor);
        root.Publish(clock, actor);

        await taxonomies.AddAsync(root, cancellationToken);
        await generations.BumpAsync(tenantContext.TenantId, cancellationToken);
        return Result.Ok(None.Value);
    }
}
