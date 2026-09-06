using LearnStack.Modules.Customization.Application.Abstractions;
using LearnStack.Modules.Customization.Application.Contracts.Customization;
using LearnStack.Modules.Customization.Domain;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using MediatR;

namespace LearnStack.Modules.Customization.Application.Customization;

/// <summary>
/// Makes a drafted taxonomy live, retiring the revision it succeeds.
/// </summary>
/// <remarks>
/// The same shape and the same three reasons as
/// <see cref="PublishTenantContentTypeCommandHandler"/> — the retirement is the
/// handler's work because an aggregate cannot see its siblings, the incumbent goes
/// first because the index refuses two live rows, and both rows are one aggregate
/// root reached through one port. It carries one guard that has no counterpart
/// there: a taxonomy with no bands resolves every reference to nothing, so it is
/// refused before publication rather than after somebody renders it.
/// </remarks>
internal sealed class PublishTenantLevelTaxonomyCommandHandler(
    ITenantLevelTaxonomyStore taxonomies,
    ICustomizationGenerationStore generations,
    ITenantContext tenantContext,
    IClock clock)
    : IRequestHandler<PublishTenantLevelTaxonomyCommand, Result<TenantLevelTaxonomyDto>>
{
    public async Task<Result<TenantLevelTaxonomyDto>> Handle(
        PublishTenantLevelTaxonomyCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!tenantContext.IsResolved)
        {
            return CustomizationFailures.Unresolved<TenantLevelTaxonomyDto>();
        }

        var successor = await taxonomies.FindAsync(TenantLevelTaxonomyId.From(request.TaxonomyId), cancellationToken);

        if (successor is null)
        {
            return CustomizationFailures.NotFound<TenantLevelTaxonomyDto>(
                nameof(PublishTenantLevelTaxonomyCommand.TaxonomyId));
        }

        if (successor.Status != CustomizationStatus.Draft)
        {
            return CustomizationFailures.Field<TenantLevelTaxonomyDto>(
                "lockey_business_rule_violation",
                nameof(PublishTenantLevelTaxonomyCommand.TaxonomyId),
                "lockey_customization_not_a_draft");
        }

        // The aggregate's own EnsurePublishable states the same rule and throws;
        // reaching it is a programmer error, and an empty vocabulary is something a
        // tenant admin fixes by adding a band.
        if (successor.Items.Count == 0)
        {
            return CustomizationFailures.Field<TenantLevelTaxonomyDto>(
                "lockey_business_rule_violation",
                nameof(PublishTenantLevelTaxonomyCommand.TaxonomyId),
                "lockey_taxonomy_items_required");
        }

        var actor = tenantContext.UserId ?? UserId.SystemActor;
        var incumbent = await taxonomies.FindActiveAsync(successor.Key, cancellationToken);

        if (incumbent is not null)
        {
            incumbent.Deprecate(clock, actor);
            await taxonomies.UpdateAsync(incumbent, cancellationToken);
        }

        successor.Publish(clock, actor);

        try
        {
            await taxonomies.UpdateAsync(successor, cancellationToken);
        }
        catch (AggregateConflictException conflict)
        {
            var (field, reason) = CustomizationFailures.Conflict(conflict.ConstraintName);

            return CustomizationFailures.Field<TenantLevelTaxonomyDto>(
                "lockey_business_rule_violation", field, reason);
        }

        await generations.BumpAsync(tenantContext.TenantId, cancellationToken);

        return Result.Ok(new TenantLevelTaxonomyDto(
            successor.Id.Value,
            successor.TenantId,
            successor.Key,
            successor.SchemaVersion,
            successor.Status.ToString(),
            successor.Items.Count));
    }
}
