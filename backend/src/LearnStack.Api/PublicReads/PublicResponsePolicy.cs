using LearnStack.Api.Versioning;

namespace LearnStack.Api.PublicReads;

/// <summary>Wraps all public responses, including refusals before MVC or the tenant pipeline.</summary>
public static class PublicResponsePolicy
{
    public static IApplicationBuilder UseLearnStackPublicResponsePolicy(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.Use(async (context, next) =>
        {
            if (!ApiVersioningExtensions.LiveMajors.Any(major =>
                    context.Request.Path.StartsWithSegments($"/api/v{major}/public", StringComparison.OrdinalIgnoreCase)))
            {
                await next(context);
                return;
            }

            Apply(context.Response);
            context.Response.OnStarting(() => { Apply(context.Response); return Task.CompletedTask; });
            // TestServer does not implement Kestrel's HEAD body suppression. Keep the
            // shared error writers and success mapping identical, but discard all bytes.
            var body = context.Response.Body;
            if (HttpMethods.IsHead(context.Request.Method)) context.Response.Body = Stream.Null;
            try { await next(context); }
            finally { context.Response.Body = body; }
        });
    }

    private static void Apply(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store";
        response.Headers.Remove("ETag");
        response.Headers.Remove("Last-Modified");
    }
}
