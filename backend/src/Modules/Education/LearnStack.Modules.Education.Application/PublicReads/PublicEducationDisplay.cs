using System.Collections.Immutable;
using System.Text.Json;
using LearnStack.Modules.Customization.Application.Contracts.Definitions;
using LearnStack.Modules.Education.Application.Abstractions;
using LearnStack.Modules.Education.Application.Contracts.PublicReads;
using LearnStack.Modules.Education.Domain;
using LearnStack.Modules.Tenancy.Application.Contracts.PublicReads;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Results;
using Microsoft.Extensions.Logging;

namespace LearnStack.Modules.Education.Application.PublicReads;

/// <summary>Allowlisted presentation only; no schema evaluation or raw payload in diagnostics.</summary>
public sealed partial class PublicEducationDisplay(ICustomizationDefinitionProjectionReader definitions, ILogger<PublicEducationDisplay> logger)
{
    public async Task<IReadOnlyList<PublicCourseSummary>> SummariesAsync(
        IReadOnlyList<PublicCourseReadRow> rows, PublicLocaleContext locale, CancellationToken cancellationToken)
    {
        var pins = rows.Where(row => row.TaxonomyKey is not null && row.TaxonomyVersion is not null)
            .Select(row => new DefinitionRevision(row.TaxonomyKey!, row.TaxonomyVersion!.Value)).Distinct().ToImmutableArray();
        DefinitionProjection? projection = null;
        if (pins.Length != 0)
        {
            var result = await definitions.ReadAsync(new DefinitionProjectionRequest([], pins, locale.Locale, locale.DefaultLocale), cancellationToken);
            if (result.IsSuccess) projection = result.Value;
        }
        return rows.Select(row => new PublicCourseSummary(row.Slug, row.Title, row.Summary,
            row.ContentAccess == CourseContentAccess.Public ? PublicCourseAccess.Public : PublicCourseAccess.EnrollmentRequired,
            Level(row, projection))).ToArray();
    }

    private PublicLevel? Level(PublicCourseReadRow row, DefinitionProjection? projection)
    {
        if (row.TaxonomyKey is null && row.TaxonomyVersion is null && row.BandKey is null) return null;
        if (row.TaxonomyKey is not null && row.TaxonomyVersion is { } version && row.BandKey is not null
            && projection is not null && projection.Taxonomies.TryGetValue(new DefinitionRevision(row.TaxonomyKey, version), out var taxonomy))
        {
            var band = taxonomy.Bands.SingleOrDefault(item => item.Key == row.BandKey);
            if (band is not null) return new PublicLevel(PublicDisplayState.Ready, Label(band.DisplayName));
        }
        UnavailableLevel(logger);
        return new PublicLevel(PublicDisplayState.Unavailable, null);
    }

    public async Task<PublicLessonContent> ContentAsync(PublicLessonReadRow row, PublicLocaleContext locale, CancellationToken cancellationToken)
    {
        var pin = new DefinitionRevision(row.ContentTypeKey, row.ContentTypeVersion);
        var result = await definitions.ReadAsync(new DefinitionProjectionRequest([pin], [], locale.Locale, locale.DefaultLocale), cancellationToken);
        if (result.IsFailure || !result.Value.ContentTypes.TryGetValue(pin, out var type)
            || type.RendererKey != "default-card" || type.Fields.Length == 0) return Unavailable();
        try
        {
            using var document = JsonDocument.Parse(row.Body);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return Unavailable();
            var fields = new List<PublicTextField>();
            foreach (var descriptor in type.Fields)
            {
                if (!document.RootElement.TryGetProperty(descriptor.Name, out var value))
                {
                    if (descriptor.IsRequired) return Unavailable();
                    continue;
                }
                if (value.ValueKind != JsonValueKind.String) return Unavailable();
                fields.Add(new PublicTextField(descriptor.Name, Label(descriptor.Label), value.GetString()!));
            }
            return new PublicReadyContent(type.RendererKey, Label(type.DisplayName), fields);
        }
        catch (JsonException) { return Unavailable(); }
    }

    private PublicUnavailableContent Unavailable()
    {
        UnavailableContent(logger);
        return new PublicUnavailableContent();
    }
    private static PublicLabel Label(ResolvedLocalizedText value) => new(value.Value, value.Locale);
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Public course level is unavailable; an exact definition or band requires remediation.")]
    private static partial void UnavailableLevel(ILogger logger);
    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Public lesson presentation is unavailable; its exact definition or selected content requires remediation.")]
    private static partial void UnavailableContent(ILogger logger);
}

public sealed record PublicLocaleContext(string Locale, string DefaultLocale, string[] EnabledLocales);

public static class PublicEducationFailures
{
    public static Result<T> Hidden<T>() => Result<T>.Fail(new Error(new LocalizedMessage("lockey_not_found")));
    public static Result<T> Invalid<T>(string field) => Result<T>.Fail(new Error(new LocalizedMessage("lockey_validation_failed"),
        new Dictionary<string, IReadOnlyList<LocalizedMessage>>(StringComparer.Ordinal) { [field] = [new LocalizedMessage("lockey_invalid_value")] }));

    public static async Task<Result<PublicLocaleContext>> ConfigurationAsync(
        PublicReadInput input, IPublicTenantConfigurationReader configuration, CancellationToken cancellationToken)
    {
        if (!PublicReadValidation.TryLocale(input.Locale, out var locale)) return Invalid<PublicLocaleContext>("locale");
        var result = await configuration.ReadAsync(cancellationToken);
        if (result.IsFailure) return Result<PublicLocaleContext>.Fail(result.Error!);
        if (!result.Value.EnabledLocales.Contains(locale, StringComparer.Ordinal))
            return Result<PublicLocaleContext>.Fail(new Error(new LocalizedMessage("lockey_unsupported_locale")));
        if (result.Value.DefaultLocale is not { } defaultLocale)
            return Result<PublicLocaleContext>.Fail(new Error(new LocalizedMessage("lockey_dependency_unavailable")));
        return Result.Ok(new PublicLocaleContext(locale, defaultLocale, result.Value.EnabledLocales.ToArray()));
    }
}
