using LearnStack.Modules.Customization.Application.Abstractions;
using LearnStack.Modules.Customization.Application.Contracts.Customization;
using LearnStack.Modules.Customization.Domain;
using LearnStack.SharedKernel.Audit;
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
    IUnitOfWork unitOfWork,
    ITenantContext tenantContext,
    IAuditSubject auditSubject,
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

        // The successor is this operation's subject, for the reason the content type's
        // handler gives: the incumbent is a second instance of the same aggregate, and the
        // composer refuses to guess between them (ADR-0044 Amendment 6 § 1).
        auditSubject.Designate(successor);

        if (successor.Status != CustomizationStatus.Draft)
        {
            return CustomizationFailures.BusinessRule<TenantLevelTaxonomyDto>(
                nameof(PublishTenantLevelTaxonomyCommand.TaxonomyId),
                "lockey_customization_not_a_draft");
        }

        // The aggregate's own EnsurePublishable states the same rule and throws;
        // reaching it is a programmer error, and an empty vocabulary is something a
        // tenant admin fixes by adding a band.
        if (successor.Items.Count == 0)
        {
            return CustomizationFailures.BusinessRule<TenantLevelTaxonomyDto>(
                nameof(PublishTenantLevelTaxonomyCommand.TaxonomyId),
                "lockey_taxonomy_items_required");
        }

        var actor = tenantContext.UserId ?? UserId.SystemActor;
        var incumbent = await taxonomies.FindActiveAsync(successor.Key, cancellationToken);

        // READ COMMITTED can observe a competitor's publication between these two
        // reads. EF returns the already-tracked Draft instance for that same id;
        // it is not an incumbent to retire, and this attempt is an ordinary stale write.
        if (incumbent?.Id == successor.Id)
            return CustomizationFailures.Stale<TenantLevelTaxonomyDto>();

        if (request.RequireNoIncumbent && incumbent is not null)
            return CustomizationFailures.BusinessRule<TenantLevelTaxonomyDto>("Key", "lockey_customization_key_already_live");

        if (incumbent is not null)
        {
            incumbent.Deprecate(clock, actor);

            try
            {
                await taxonomies.UpdateAsync(incumbent, cancellationToken);
            }
            catch (AggregateConcurrencyException)
            {
                // The loser of two concurrent successions. Its UPDATE matched nothing
                // because the winner already retired this row — re-read and retry is
                // the answer, and it is the one the concurrency token exists to give.
                return Undo(CustomizationFailures.Stale<TenantLevelTaxonomyDto>());
            }
        }

        successor.Publish(clock, actor);

        try
        {
            await taxonomies.UpdateAsync(successor, cancellationToken);
        }
        catch (AggregateConflictException conflict)
        {
            var (field, reason) = CustomizationFailures.Conflict(conflict.ConstraintName);

            return Undo(CustomizationFailures.BusinessRule<TenantLevelTaxonomyDto>(field, reason));
        }
        catch (AggregateConcurrencyException)
        {
            return Undo(CustomizationFailures.Stale<TenantLevelTaxonomyDto>());
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

    /// <summary>Refuses later commit after a publication mutation/save has failed.</summary>
    /// <remarks>
    /// Both the successor's dirty tracked state and an already-saved retirement
    /// belong to the ambient transaction. An outer handler may absorb Result.Fail,
    /// so ADR-0040 requires rollback-only even for a first publication with no incumbent.
    /// </remarks>
    private Result<TenantLevelTaxonomyDto> Undo(Result<TenantLevelTaxonomyDto> failure)
    {
        unitOfWork.MarkRollbackOnly();
        return failure;
    }
}
