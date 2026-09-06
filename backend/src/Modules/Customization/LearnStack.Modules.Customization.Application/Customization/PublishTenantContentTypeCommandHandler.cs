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
/// Makes a drafted content type live, retiring the revision it succeeds.
/// </summary>
/// <remarks>
/// <para>
/// <b>The retirement is this handler's work.</b> An aggregate cannot see its
/// siblings — the incumbent is a different row of the same type — so nothing in
/// the domain can deprecate it. The partial index
/// <c>UNIQUE (tenant_id, key) WHERE status = 'Active' AND deleted_at IS NULL</c>
/// is what catches the case where this did not happen, which is why it exists
/// rather than being implied by this code.
/// </para>
/// <para>
/// <b>Two rows, one aggregate root, one port.</b> ADR-0042's allow-list is about
/// writing two <i>different</i> aggregates on one transaction; the incumbent and
/// the successor are two instances of one, reached through one port. The rule that
/// enforces it counts the roots a handler's ports can write, and this handler's
/// count is one.
/// </para>
/// <para>
/// <b>Order matters and is deliberate.</b> The incumbent is deprecated first: the
/// index refuses two live rows for a key, so publishing before retiring is the one
/// ordering PostgreSQL rejects — and it would reject it after the successor's
/// <c>UPDATE</c> had already been sent, turning an ordinary succession into a
/// constraint violation the caller cannot act on.
/// </para>
/// </remarks>
internal sealed class PublishTenantContentTypeCommandHandler(
    ITenantContentTypeStore contentTypes,
    ICustomizationGenerationStore generations,
    ITenantContext tenantContext,
    IClock clock)
    : IRequestHandler<PublishTenantContentTypeCommand, Result<TenantContentTypeDto>>
{
    public async Task<Result<TenantContentTypeDto>> Handle(
        PublishTenantContentTypeCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!tenantContext.IsResolved)
        {
            return CustomizationFailures.Unresolved<TenantContentTypeDto>();
        }

        var successor = await contentTypes.FindAsync(TenantContentTypeId.From(request.ContentTypeId), cancellationToken);

        if (successor is null)
        {
            return CustomizationFailures.NotFound<TenantContentTypeDto>(
                nameof(PublishTenantContentTypeCommand.ContentTypeId));
        }

        // Checked here rather than caught from the aggregate. `Publish` guards the
        // same thing with an `InvalidOperationException`, which is a backstop
        // against a caller that did not look — reaching it is a programmer error and
        // becomes a 500, whereas "that revision is already live" is an ordinary
        // refusal a tenant admin acts on.
        if (successor.Status != CustomizationStatus.Draft)
        {
            return CustomizationFailures.Field<TenantContentTypeDto>(
                "lockey_business_rule_violation",
                nameof(PublishTenantContentTypeCommand.ContentTypeId),
                "lockey_customization_not_a_draft");
        }

        var actor = tenantContext.UserId ?? UserId.SystemActor;
        var incumbent = await contentTypes.FindActiveAsync(successor.Key, cancellationToken);

        // No identity comparison between the two: the guard above has already
        // established the successor is a Draft and this read returns an Active row,
        // so they cannot be the same row. A defensive `incumbent.Id != successor.Id`
        // here would be a branch no test can reach and therefore a comment.
        if (incumbent is not null)
        {
            incumbent.Deprecate(clock, actor);

            try
            {
                await contentTypes.UpdateAsync(incumbent, cancellationToken);
            }
            catch (AggregateConcurrencyException)
            {
                // The loser of two concurrent successions. Its UPDATE matched nothing
                // because the winner already retired this row — re-read and retry is
                // the answer, and it is the one the concurrency token exists to give.
                return CustomizationFailures.Stale<TenantContentTypeDto>();
            }
        }

        successor.Publish(clock, actor);

        try
        {
            await contentTypes.UpdateAsync(successor, cancellationToken);
        }
        catch (AggregateConflictException conflict)
        {
            var (field, reason) = CustomizationFailures.Conflict(conflict.ConstraintName);

            return CustomizationFailures.Field<TenantContentTypeDto>(
                "lockey_business_rule_violation", field, reason);
        }
        catch (AggregateConcurrencyException)
        {
            return CustomizationFailures.Stale<TenantContentTypeDto>();
        }

        await generations.BumpAsync(tenantContext.TenantId, cancellationToken);

        return Result.Ok(new TenantContentTypeDto(
            successor.Id.Value,
            successor.TenantId,
            successor.Key,
            successor.SchemaVersion,
            successor.Status.ToString()));
    }
}
