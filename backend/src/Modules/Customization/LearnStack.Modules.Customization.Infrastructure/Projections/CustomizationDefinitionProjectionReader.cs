using System.Collections.Immutable;
using LearnStack.Modules.Customization.Application.Contracts.Definitions;
using LearnStack.Modules.Customization.Domain;
using LearnStack.SharedKernel.Domain;
using LearnStack.SharedKernel.Errors;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;

namespace LearnStack.Modules.Customization.Infrastructure.Projections;

public sealed class CustomizationDefinitionProjectionReader(
    DefinitionSnapshotStore store, ITenantContext context, IUnitOfWork unit)
    : ICustomizationDefinitionProjectionReader
{
    public async Task<Result<DefinitionProjection>> ReadAsync(
        DefinitionProjectionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!context.IsResolved || context.TenantId == TenantId.PlatformSentinel
            || !unit.HasActiveTransaction || !unit.IsTenantContextIssuedOn(unit.Transaction)
            || !store.IsEnlistedOn(unit.Transaction))
        {
            throw new TenantContextMissingException("Definition reads require a resolved, announced and enlisted ambient transaction.");
        }

        if (!Valid(request))
        {
            return Refused();
        }

        // Step 2 deliberately probes and then always loads; Step 3 adds clean-scope caching.
        await store.ProbeAsync(context.TenantId, cancellationToken);
        var snapshot = await store.LoadAsync(context.TenantId, cancellationToken);
        if ((snapshot.Generation is null && snapshot.HasDefinitionRows) || snapshot.Generation is <= 0)
        {
            return Refused();
        }

        return Result.Ok(Resolve(snapshot, request));
    }

    private static DefinitionProjection Resolve(DefinitionSnapshot snapshot, DefinitionProjectionRequest request)
    {
        string[] fallback = [request.TenantDefaultLocale, "en"];
        ResolvedLocalizedText Label(LocalizedText value) => value.ResolveWithLocale(request.RequestedLocale, fallback);
        var types = request.ContentTypes.Distinct().Where(snapshot.ContentTypes.Definitions.ContainsKey)
            .ToImmutableDictionary(pin => pin, pin =>
            {
                var definition = snapshot.ContentTypes.Definitions[pin];
                return new ContentTypeDisplayDefinition(definition.Id, pin, definition.Status, Label(definition.DisplayName),
                    definition.RendererKey, definition.Fields.Select(field => new TextCardDisplayField(field.Name, Label(field.Label)))
                        .ToImmutableArray());
            });
        var taxonomies = request.Taxonomies.Distinct().Where(snapshot.Taxonomies.Definitions.ContainsKey)
            .ToImmutableDictionary(pin => pin, pin =>
            {
                var definition = snapshot.Taxonomies.Definitions[pin];
                return new TaxonomyDisplayDefinition(definition.Id, pin, definition.Status, Label(definition.DisplayName),
                    definition.Bands.Select(band => new TaxonomyDisplayBand(band.Key, Label(band.DisplayName), band.Sort, band.Metadata))
                        .ToImmutableArray());
            });
        return new DefinitionProjection(snapshot.Generation, types, taxonomies,
            request.ContentTypes.Except(types.Keys).ToImmutableHashSet(), request.Taxonomies.Except(taxonomies.Keys).ToImmutableHashSet());
    }

    private static bool Valid(DefinitionProjectionRequest request)
    {
        if (request.ContentTypes.IsDefault || request.Taxonomies.IsDefault
            || request.ContentTypes.Concat(request.Taxonomies).Any(pin =>
                string.IsNullOrEmpty(pin.Key) || pin.Key.Length > CustomizationKey.MaxLength
                || !UrlSlug.IsUrlSafe(pin.Key) || pin.SchemaVersion <= 0)
            || string.IsNullOrEmpty(request.RequestedLocale) || string.IsNullOrEmpty(request.TenantDefaultLocale)
            || request.RequestedLocale.Length > LocaleTag.MaxLength || request.TenantDefaultLocale.Length > LocaleTag.MaxLength)
        {
            return false;
        }

        try
        {
            LocaleTag.EnsureWellFormed(request.RequestedLocale, nameof(request.RequestedLocale));
            LocaleTag.EnsureWellFormed(request.TenantDefaultLocale, nameof(request.TenantDefaultLocale));
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static Result<DefinitionProjection> Refused() => Result<DefinitionProjection>.Fail(new Error(
        new LocalizedMessage("lockey_validation_failed"),
        new Dictionary<string, IReadOnlyList<LocalizedMessage>>(StringComparer.Ordinal)
        {
            ["Definition"] = [new LocalizedMessage("lockey_schema_extension_unresolved")],
        }));
}
