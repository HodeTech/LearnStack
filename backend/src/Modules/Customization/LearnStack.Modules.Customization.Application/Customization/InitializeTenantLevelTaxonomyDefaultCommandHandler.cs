using LearnStack.Modules.Customization.Application.Seeding;
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
    IGuidFactory guids,
    ISeedCustomizationWriteGuard seedGuard)
    : IRequestHandler<InitializeTenantLevelTaxonomyDefaultCommand, Result<None>>
{
    public async Task<Result<None>> Handle(
        InitializeTenantLevelTaxonomyDefaultCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!tenantContext.IsResolved) return CustomizationFailures.Unresolved<None>();

        if (!await seedGuard.PrepareAsync(request, cancellationToken)) return Result<None>.Fail(seedGuard.Failure);

        var definition = BuiltInCustomizations.Plain;
        if (await taxonomies.HasConceptHistoryAsync(definition.Key, cancellationToken))
            return Result.Ok(None.Value);

        var actor = tenantContext.UserId ?? UserId.SystemActor;
        if (!seedGuard.IsCurrent(request)) return CustomizationFailures.BusinessRule<None>("Seed", "lockey_business_rule_violation");
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
