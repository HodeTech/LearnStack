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
        var retired = false;

        if (incumbent is not null)
        {
            retired = true;
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
                return CustomizationFailures.Stale<TenantLevelTaxonomyDto>();
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

            return Undo(retired, CustomizationFailures.BusinessRule<TenantLevelTaxonomyDto>(field, reason));
        }
        catch (AggregateConcurrencyException)
        {
            return Undo(retired, CustomizationFailures.Stale<TenantLevelTaxonomyDto>());
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

    /// <summary>
    /// Escalates a failure that arrives <b>after</b> the incumbent was retired.
    /// </summary>
    /// <remarks>
    /// The deprecation is already saved by the time the successor's write can
    /// fail, and
    /// <see href="../../../../../../docs/decisions/0040-ambient-unit-of-work.md">ADR-0040
    /// § Nesting</see> is explicit that an inner <c>Result.Fail</c> an outer
    /// handler absorbs does not roll the unit back — "only an exception, or an
    /// explicit <c>MarkRollbackOnly</c>, does". Without it, an outer handler that
    /// absorbs this and commits leaves the tenant with the incumbent deprecated,
    /// the successor still a draft, and NO live revision for the key — measured
    /// against a real database through the real pipeline.
    /// </remarks>
    private Result<TenantLevelTaxonomyDto> Undo(bool retired, Result<TenantLevelTaxonomyDto> failure)
    {
        if (retired)
        {
            unitOfWork.MarkRollbackOnly();
        }

        return failure;
    }
}
