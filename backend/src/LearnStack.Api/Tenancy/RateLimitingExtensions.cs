using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;

namespace LearnStack.Api.Tenancy;

/// <summary>
/// The in-process rate limiter
/// <see href="../../../../docs/decisions/0036-tenant-resolution-trusted-inputs.md">ADR-0036
/// § Effective host and the trusted hop</see> makes a Packet 4 deliverable, at
/// the anonymous budget
/// <see href="../../../../docs/standards/04-api-design.md">Standards 04
/// § Request and Response Limits</see> fixes.
/// </summary>
/// <remarks>
/// <para>
/// <see href="../../../../docs/architecture/30-api-gateway.md">architecture/30</see>
/// has APISIX carry rate limiting, and has said since Phase 01 that until the
/// gateway lands "the same responsibilities are carried by ASP.NET middleware
/// inside the API process". Nothing delivered it. ADR-0035 puts the gateway in
/// Phase 11 against a trigger, so "the gateway will do it" is not a plan for
/// the packets in between.
/// </para>
/// <para>
/// It runs <b>before</b> host classification and before the resolver, because
/// what it exists to cap is the cost of an unauthenticated request that has not
/// been classified yet: from Packet 7 every novel <c>Host</c> value costs a
/// Postgres transaction and a cache entry, on a pre-auth surface.
/// </para>
/// <para>
/// ADR-0053 / P02d-5 G34 uses a canonical visitor-IP budget shared by direct and
/// authenticated-hop traffic, plus a physical-peer ceiling. Only a network-and-
/// secret authenticated hop may state the visitor; invalid trusted metadata spends
/// the peer fallback budget before masked refusal. Phase 02b owns token budgets.
/// </para>
/// </remarks>
public static class RateLimitingExtensions
{
    /// <summary>The anonymous budget from Standards 04: 60 requests a minute per IP.</summary>
    public const int AnonymousPermitPerWindow = 60;

    /// <summary>The separate physical-peer backstop accepted in ADR-0053.</summary>
    public const int PeerPermitPerWindow = 600;

    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    /// <summary>The partition an unidentifiable peer falls into.</summary>
    /// <remarks>
    /// A request with no socket peer is not normal — in-process test hosts
    /// produce it. They share one partition rather than bypassing the limiter,
    /// because "unidentifiable" must not be cheaper than "identified".
    /// </remarks>
    public const string UnknownPeerPartition = "unknown-peer";

    public static IServiceCollection AddLearnStackRateLimiting(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<AnonymousRequestIdentity>();
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            // Peer first: exhausted peers cannot mint additional visitor partitions.
            options.GlobalLimiter = PartitionedRateLimiter.CreateChained(
                Budget(identity => identity.PeerKey, PeerPermitPerWindow),
                Budget(identity => identity.VisitorKey, AnonymousPermitPerWindow));

            options.OnRejected = (context, cancellationToken) =>
            {
                // Retry-After is required on a 429 by Standards 04 § Status
                // Codes. The body is deliberately left empty: UseStatusCodePages
                // gives it the one Problem Details shape, so a 429 reads exactly
                // as every other client error does.
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter =
                        ((int)retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);
                }

                return ValueTask.CompletedTask;
            };
        });

        return services;
    }

    private static PartitionedRateLimiter<HttpContext> Budget(Func<AnonymousVisitor, string> key, int permits) =>
        PartitionedRateLimiter.Create<HttpContext, string>(context =>
            RateLimitPartition.GetFixedWindowLimiter(
                key(context.RequestServices.GetRequiredService<AnonymousRequestIdentity>().For(context)),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = permits,
                    Window = Window,
                    QueueLimit = 0,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                }));

    /// <summary>Runs after both budgets, before host classification or any database lookup.</summary>
    public static IApplicationBuilder UseLearnStackVisitorAdmission(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.Use(async (context, next) =>
        {
            if (context.RequestServices.GetRequiredService<AnonymousRequestIdentity>().For(context).Refuse)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            await next(context);
        });
    }
}
