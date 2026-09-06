using LearnStack.Modules.Customization.Application.Abstractions;
using LearnStack.Modules.Customization.Application.Contracts.Customization;
using LearnStack.Modules.Customization.Domain;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using MediatR;

namespace LearnStack.Modules.Customization.Application.Customization;

/// <summary>
/// Writes a drafted level taxonomy and its bands.
/// </summary>
/// <remarks>
/// <para>
/// <b>The bands go on before the write, not after it.</b> <c>AddItem</c> refuses a
/// duplicate key and a duplicate sort with a message a tenant admin can read; the
/// database refuses the same two with a constraint name. Building the whole
/// aggregate first means the readable refusal is the one that happens for a
/// submission this handler can see all of — the database's is left holding the
/// case only two concurrent transactions produce.
/// </para>
/// <para>
/// <b>One aggregate, one write port</b>, so ADR-0042's allow-list is untouched.
/// </para>
/// </remarks>
internal sealed class RegisterTenantLevelTaxonomyCommandHandler(
    ITenantLevelTaxonomyStore taxonomies,
    ICustomizationGenerationStore generations,
    ITenantContext tenantContext,
    IClock clock)
    : IRequestHandler<RegisterTenantLevelTaxonomyCommand, Result<TenantLevelTaxonomyDto>>
{
    public async Task<Result<TenantLevelTaxonomyDto>> Handle(
        RegisterTenantLevelTaxonomyCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!tenantContext.IsResolved)
        {
            return CustomizationFailures.Unresolved<TenantLevelTaxonomyDto>();
        }

        var actor = tenantContext.UserId ?? UserId.SystemActor;

        // Checked here rather than caught: the aggregate guards both with an
        // `InvalidOperationException`, which is the backstop for a caller that did
        // not look, and a duplicate band is an ordinary refusal the author fixes.
        var duplicate = FirstDuplicate(request.Items);

        if (duplicate is not null)
        {
            return CustomizationFailures.Field<TenantLevelTaxonomyDto>(
                "lockey_validation_failed",
                nameof(RegisterTenantLevelTaxonomyCommand.Items),
                duplicate);
        }

        var taxonomy = TenantLevelTaxonomy.Create(
            TenantLevelTaxonomyId.From(request.TaxonomyId),
            tenantContext.TenantId,
            request.Key,
            request.SchemaVersion,
            LocalizedText.From(request.DisplayName),
            clock,
            actor);

        foreach (var item in request.Items)
        {
            taxonomy.AddItem(
                item.Key,
                LocalizedText.From(item.DisplayName),
                item.Sort,
                item.Metadata,
                clock,
                actor);
        }

        try
        {
            await taxonomies.AddAsync(taxonomy, cancellationToken);
        }
        catch (AggregateConflictException conflict)
        {
            var (field, reason) = CustomizationFailures.Conflict(conflict.ConstraintName);

            return CustomizationFailures.BusinessRule<TenantLevelTaxonomyDto>(field, reason);
        }

        await generations.BumpAsync(tenantContext.TenantId, cancellationToken);

        return Result.Ok(new TenantLevelTaxonomyDto(
            taxonomy.Id.Value,
            taxonomy.TenantId,
            taxonomy.Key,
            taxonomy.SchemaVersion,
            taxonomy.Status.ToString(),
            taxonomy.Items.Count));
    }

    /// <summary>
    /// The first collision in the submitted bands, as a message key, or nothing.
    /// </summary>
    /// <remarks>
    /// Both are collisions <i>within one submission</i>, which is why a per-item
    /// validator cannot see them: FluentValidation's <c>RuleForEach</c> looks at one
    /// element at a time. A duplicate key is a band declared twice; a duplicate sort
    /// is the harder bug — it is not a constraint violation in memory at all, it is
    /// a render whose order changes between two requests.
    /// </remarks>
    private static string? FirstDuplicate(IReadOnlyList<TaxonomyItemInput> items)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var sorts = new HashSet<short>();

        foreach (var item in items)
        {
            if (!keys.Add(item.Key))
            {
                return "lockey_taxonomy_item_key_duplicated";
            }

            if (!sorts.Add(item.Sort))
            {
                return "lockey_taxonomy_item_sort_duplicated";
            }
        }

        return null;
    }
}
