using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Scalar.AspNetCore;

namespace LearnStack.Api.Versioning;

/// <summary>
/// Composition-root wiring for the API versioning surface
/// <see href="../../../../docs/decisions/0024-api-versioning-policy.md">ADR-0024</see>
/// fixes: URL-based majors under <c>/api/v{N}/</c>, one OpenAPI document per
/// live major at <c>/openapi/v{N}.json</c>, and a reference UI over it.
/// </summary>
public static class ApiVersioningExtensions
{
    /// <summary>
    /// The majors that currently accept traffic. ADR-0024 allows exactly two
    /// adjacent majors to coexist; a third requires the oldest to have reached
    /// its sunset date first. Adding <c>2</c> here is what makes
    /// <c>/openapi/v2.json</c> exist.
    /// </summary>
    public static readonly IReadOnlyList<int> LiveMajors = [1];

    /// <summary>
    /// Registers controllers under the versioned route convention and one
    /// OpenAPI document per entry in <see cref="LiveMajors"/>.
    /// </summary>
    public static IServiceCollection AddLearnStackApiVersioning(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddControllers(options =>
            options.Conventions.Add(new VersionedRouteConvention()));

        foreach (var major in LiveMajors)
        {
            services.AddLearnStackOpenApiDocument(major);
        }

        return services;
    }

    /// <summary>
    /// Registers the OpenAPI document for one major. Public because it is the
    /// single definition of what a LearnStack API document looks like — a test
    /// that needs a second major calls this rather than re-deriving the
    /// transformer chain, so the two cannot drift.
    /// </summary>
    public static IServiceCollection AddLearnStackOpenApiDocument(
        this IServiceCollection services,
        int major)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentOutOfRangeException.ThrowIfLessThan(major, 1);

        var documentName = $"v{major}";
        var pathPrefix = $"/api/{documentName}/";

        // The document name IS the URL segment: MapOpenApi serves
        // /openapi/{documentName}.json, so naming the document "v1" produces
        // the /openapi/v1.json that Standards 04 § OpenAPI publishes.
        // Renaming it silently moves the contract's address.
        services.AddOpenApi(documentName, options =>
        {
            options.AddOperationTransformer(new VersionIntroducedOperationTransformer());

            // One document per major, holding only that major's operations.
            // Without the filter every document would carry every operation
            // and `oasdiff` — which Phase 02d wires against the prior main
            // spec — would read a v2 addition as a breaking change to v1.
            options.AddDocumentTransformer((document, _, _) =>
            {
                var foreign = document.Paths
                    .Where(path => !path.Key.StartsWith(pathPrefix, StringComparison.Ordinal))
                    .Select(path => path.Key)
                    .ToList();

                foreach (var path in foreign)
                {
                    document.Paths.Remove(path);
                }

                document.Info.Title = "LearnStack API";
                document.Info.Version = documentName;
                return Task.CompletedTask;
            });
        });

        return services;
    }

    /// <summary>
    /// Maps <c>/openapi/v{N}.json</c> for every registered major, plus the
    /// Scalar reference UI at <c>/openapi</c>.
    /// </summary>
    /// <remarks>
    /// Standards 04 § OpenAPI previously promised "Swagger UI at
    /// <c>/openapi/v1/</c>". Swashbuckle ships no document generator for
    /// .NET 10 and ADR-0024 § Implementation Notes fixes
    /// <c>Microsoft.AspNetCore.OpenApi</c> as the generator, which ships no UI
    /// at all — so the standard was corrected to name Scalar and this method is
    /// where that lands.
    /// </remarks>
    public static WebApplication MapLearnStackOpenApi(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapOpenApi();
        app.MapScalarApiReference("/openapi", options =>
            options.WithTitle("LearnStack API"));

        return app;
    }
}
