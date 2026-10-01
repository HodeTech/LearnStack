using System.Collections.Immutable;
using LearnStack.Modules.Customization.Application.Abstractions;
using LearnStack.Modules.Customization.Application.Contracts.Seeding;
using LearnStack.Modules.Customization.Domain;
using Microsoft.EntityFrameworkCore;

namespace LearnStack.Modules.Customization.Infrastructure.Persistence;

public sealed class CustomizationSeedStateReader(CustomizationDbContext context) : ISeedStateReader
{
    public async Task<ContentTypeSeedDto?> ReadContentTypeAsync(Guid contentTypeId, CancellationToken cancellationToken)
    {
        var id = TenantContentTypeId.From(contentTypeId);
        var row = await context.TenantContentTypes.AsNoTracking().SingleOrDefaultAsync(
            definition => definition.Id == id && definition.DeletedAt == null, cancellationToken);
        return row is null ? null : new ContentTypeSeedDto(row.Id.Value, row.TenantId, row.Key, row.SchemaVersion,
            row.Status.ToString(), row.DisplayName.ToJson(), row.JsonSchema, row.RendererKey);
    }

    public async Task<TaxonomySeedDto?> ReadTaxonomyAsync(Guid taxonomyId, CancellationToken cancellationToken)
    {
        var id = TenantLevelTaxonomyId.From(taxonomyId);
        var row = await context.TenantLevelTaxonomies.AsNoTracking().Include(definition => definition.Items)
            .SingleOrDefaultAsync(definition => definition.Id == id && definition.DeletedAt == null, cancellationToken);
        return row is null ? null : new TaxonomySeedDto(row.Id.Value, row.TenantId, row.Key, row.SchemaVersion,
            row.Status.ToString(), row.DisplayName.ToJson(), row.Items.OrderBy(item => item.Sort)
                .Select(item => new TaxonomySeedItemDto(item.Key, item.DisplayName.ToJson(), item.Sort, item.Metadata))
                .ToImmutableArray());
    }
}
