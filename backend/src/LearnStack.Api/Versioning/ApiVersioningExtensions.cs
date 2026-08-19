using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
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
    /// <c>/openapi/v2.json</c> exist — and what lets a controller declare
    /// <c>[ApiVersion(2)]</c> at all: <see cref="VersionedRouteConvention"/>
    /// refuses to start against a major this list does not carry, so a route
    /// can never be served under a major nothing publishes.
    /// </summary>
    public static readonly IReadOnlyList<int> LiveMajors = [1];

    /// <summary>
    /// Where the reference console is served. Deliberately not under
    /// <c>/openapi</c>: see <see cref="MapLearnStackOpenApi"/>.
    /// </summary>
    public const string ScalarUiPath = "/docs";

    /// <summary>
    /// Registers controllers under the versioned route convention and one
    /// OpenAPI document per entry in <see cref="LiveMajors"/>.
    /// </summary>
    public static IServiceCollection AddLearnStackApiVersioning(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Standards 04 § Style — "Resources are plural nouns (/courses,
        // /users)". The [controller] token expands to the C# class name, so
        // CoursesController would otherwise publish /api/v1/Courses. URLs are
        // part of the contract and renaming one later is a breaking change
        // under ADR-0024, so the casing is fixed here rather than left to
        // whoever writes the first controller.
        services.Configure<RouteOptions>(options => options.LowercaseUrls = true);

        services.AddControllers(options =>
        {
            options.Conventions.Add(new VersionedRouteConvention());

            // Last-resort normaliser. The IClientErrorFactory below only sees
            // IClientErrorActionResult, which the *ObjectResult family does not
            // implement — NotFound() produced the LearnStack shape while
            // NotFound(body), Problem() and ValidationProblem() did not.
            options.Filters.Add<Common.ProblemDetailsNormalizationFilter>();
        });

        // [ApiController]'s automatic 400 runs before MediatR, so a binding
        // failure never reaches ValidationBehavior. Left at its default it
        // emits ASP.NET's own Problem Details body — a second error shape,
        // which Standards 09 § API Surface does not admit.
        services.Configure<Microsoft.AspNetCore.Mvc.ApiBehaviorOptions>(options =>
            options.InvalidModelStateResponseFactory = Common.ModelBindingProblemDetails.For);

        // MVC converts a bodyless StatusCodeResult from an [ApiController]
        // action into ASP.NET's own ProblemDetails — right idea, wrong shape:
        // no code, no messageKey, no correlationId. Replacing the factory is
        // the sanctioned hook, and it keeps one shape rather than two.
        services.AddSingleton<Microsoft.AspNetCore.Mvc.Infrastructure.IClientErrorFactory,
            Common.LearnStackClientErrorFactory>();

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
        var routePrefix = $"api/{documentName}/";

        // Record the name so MapLearnStackOpenApi can constrain its route to
        // the documents that actually exist. Reading LiveMajors there instead
        // would be wrong the moment anything registers a document outside it —
        // which a test exercising two adjacent majors does by construction.
        RegistryFor(services).Add(documentName);

        // The document name IS the URL segment: MapOpenApi serves
        // /openapi/{documentName}.json, so naming the document "v1" produces
        // the /openapi/v1.json that Standards 04 § OpenAPI publishes.
        // Renaming it silently moves the contract's address.
        services.AddOpenApi(documentName, options =>
        {
            // Filter at the ApiDescription level, BEFORE schemas and tags are
            // generated. Removing paths from a finished document leaves the
            // other majors' `components.schemas` and document-level `tags`
            // behind as orphans — including the /api/internal/* Hub surface's,
            // which would publish the shape of an mTLS-only contract in the
            // tenant-facing document.
            options.ShouldInclude = api =>
                api.RelativePath?.StartsWith(routePrefix, StringComparison.OrdinalIgnoreCase) == true;

            options.AddOperationTransformer(new VersionIntroducedOperationTransformer());
            options.AddOperationTransformer(
                new Idempotency.IdempotencyOperationTransformer());

            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info.Title = "LearnStack API";
                document.Info.Version = documentName;

                // `servers` defaults to the request's scheme + Host, which is
                // client-controlled (AllowedHosts is "*"). Two reasons to drop
                // it: ADR-0036 fixes that no client input decides anything the
                // API asserts, and the openapi-diff gate Phase 02d wires needs
                // a byte-stable artefact — a document whose first bytes change
                // with the caller's Host header diffs against itself.
                document.Servers?.Clear();

                return Task.CompletedTask;
            });
        });

        return services;
    }

    private static OpenApiDocumentRegistry RegistryFor(IServiceCollection services)
    {
        if (services.FirstOrDefault(d => d.ServiceType == typeof(OpenApiDocumentRegistry))
                ?.ImplementationInstance is OpenApiDocumentRegistry existing)
        {
            return existing;
        }

        var registry = new OpenApiDocumentRegistry();
        services.AddSingleton(registry);
        return registry;
    }

    /// <summary>
    /// Maps <c>/openapi/v{N}.json</c> for every registered major, plus the
    /// Scalar reference console at <see cref="ScalarUiPath"/>.
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

        // The document route is constrained to the majors that actually exist.
        // Unconstrained, `/openapi/v9.json` answered 404 in text/plain with
        // English framework prose — a fourth error shape, on a published route.
        // Constrained, it matches no route and becomes an ordinary routing 404
        // in the one Problem Details shape.
        var documentNames = string.Join(
            "|", app.Services.GetRequiredService<OpenApiDocumentRegistry>().Names);
        app.MapOpenApi($"/openapi/{{documentName:regex(^({documentNames})$)}}.json");

        // The console lives at /docs, not under /openapi. Scalar maps its own
        // `{documentName?}` inside the prefix it is given, so a prefix under
        // /openapi both collides with the parameter name and shadows the whole
        // document namespace with a catch-all — `/openapi/garbage` answered
        // 200 text/html, making an unknown document look like a success.
        // Separating the console from the contract removes the shadowing
        // instead of fighting it.
        app.MapScalarApiReference(ScalarUiPath, options =>
        {
            options.WithTitle("LearnStack API");

            // No proxy. Scalar's "Test Request" button otherwise routes the
            // call through a Scalar-operated service, so a developer trying an
            // endpoint would send a LearnStack bearer token to a third party.
            // The same setting is what makes the console usable in a
            // SelfHostedAirGapped deployment, where nothing outside the
            // network is reachable by design ([ADR-0020]).
            options.WithProxy(string.Empty);
        });

        return app;
    }
}

/// <summary>
/// The OpenAPI document names this host registered. Exists so the document
/// route can be constrained to them: unconstrained,
/// <c>/openapi/v9.json</c> answered 404 in <c>text/plain</c> with English
/// framework prose — a fourth error shape on a published route.
/// </summary>
public sealed class OpenApiDocumentRegistry
{
    private readonly SortedSet<string> _names = new(StringComparer.Ordinal);

    public IReadOnlyCollection<string> Names => _names;

    public void Add(string documentName) => _names.Add(documentName);
}
