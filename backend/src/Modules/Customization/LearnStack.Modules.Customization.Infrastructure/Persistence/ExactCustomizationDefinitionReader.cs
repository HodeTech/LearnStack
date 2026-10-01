using System.Collections.Immutable;
using LearnStack.Modules.Customization.Application.Contracts.Definitions;
using LearnStack.Modules.Customization.Application.Customization;
using LearnStack.Modules.Customization.Domain;
using LearnStack.SharedKernel.Domain;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace LearnStack.Modules.Customization.Infrastructure.Persistence;

public sealed class ExactCustomizationDefinitionReader(CustomizationDbContext context, ITenantContext tenantContext)
    : IExactCustomizationDefinitionReader
{
    public async Task<Result<ContentTypeDefinitionDto>> ReadContentTypeAsync(
        string key, int schemaVersion, DefinitionReadPurpose purpose, CancellationToken cancellationToken)
    {
        if (!EligibleInput(key, schemaVersion, purpose))
        {
            return Refused<ContentTypeDefinitionDto>();
        }

        var definition = await context.TenantContentTypes.AsNoTracking().SingleOrDefaultAsync(
            row => row.Key == key && row.SchemaVersion == schemaVersion && row.DeletedAt == null,
            cancellationToken);
        if (definition is null || !EligibleStatus(definition.Status, purpose))
        {
            return Refused<ContentTypeDefinitionDto>();
        }

        var presentation = TextCardPresentation.Resolve(definition.JsonSchema, definition.RendererKey);
        if (presentation.IsFailure)
        {
            // A malformed stored definition must not leak its private schema details.
            return Refused<ContentTypeDefinitionDto>();
        }

        return Result.Ok(new ContentTypeDefinitionDto(definition.Id.Value, definition.Key,
            definition.SchemaVersion, Status(definition.Status), definition.JsonSchema,
            definition.RendererKey, presentation.Value));
    }

    public async Task<Result<TaxonomyDefinitionDto>> ReadTaxonomyAsync(
        string key, int schemaVersion, DefinitionReadPurpose purpose, CancellationToken cancellationToken)
    {
        if (!EligibleInput(key, schemaVersion, purpose))
        {
            return Refused<TaxonomyDefinitionDto>();
        }

        var definition = await context.TenantLevelTaxonomies.AsNoTracking().Include(row => row.Items)
            .SingleOrDefaultAsync(row => row.Key == key && row.SchemaVersion == schemaVersion && row.DeletedAt == null,
                cancellationToken);
        if (definition is null || !EligibleStatus(definition.Status, purpose))
        {
            return Refused<TaxonomyDefinitionDto>();
        }

        return Result.Ok(new TaxonomyDefinitionDto(definition.Id.Value, definition.Key,
            definition.SchemaVersion, Status(definition.Status), definition.Items.OrderBy(item => item.Sort)
                .Select(item => new TaxonomyBandDto(item.Key, item.DisplayName, item.Sort, item.Metadata))
                .ToImmutableArray()));
    }

    private bool EligibleInput(string key, int version, DefinitionReadPurpose purpose) =>
        tenantContext.IsResolved && !string.IsNullOrEmpty(key) && key.Length <= CustomizationKey.MaxLength
        && UrlSlug.IsUrlSafe(key) && version > 0 && Enum.IsDefined(purpose);

    private static bool EligibleStatus(CustomizationStatus status, DefinitionReadPurpose purpose) =>
        status == CustomizationStatus.Active
        || (purpose == DefinitionReadPurpose.ExistingPin && status == CustomizationStatus.Deprecated);

    private static DefinitionStatus Status(CustomizationStatus status) => status == CustomizationStatus.Active
        ? DefinitionStatus.Active : DefinitionStatus.Deprecated;

    private static Result<T> Refused<T>() => Result<T>.Fail(new Error(
        new LocalizedMessage("lockey_validation_failed"),
        new Dictionary<string, IReadOnlyList<LocalizedMessage>>(StringComparer.Ordinal)
        {
            ["Definition"] = [new LocalizedMessage("lockey_schema_extension_unresolved")],
        }));
}
