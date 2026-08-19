using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
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
/// Partitioned on the socket peer, and <b>only</b> the socket peer. There is no
/// authenticated partition yet because there is no authentication yet — Phase
/// 02b adds the token-keyed budgets Standards 04 also fixes, and adding a
/// partition key that is constant-null today would be a partition in name only.
/// </para>
/// </remarks>
public static class RateLimitingExtensions
{
    /// <summary>The anonymous budget from Standards 04: 60 requests a minute per IP.</summary>
    public const int AnonymousPermitPerWindow = 60;

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

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(
                context => RateLimitPartition.GetFixedWindowLimiter(
                    PartitionKeyFor(context),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = AnonymousPermitPerWindow,
                        Window = Window,

                        // No queue. Queuing a request that is over budget spends
                        // the server's memory to delay an answer the client is
                        // going to get anyway, and under a flood the queue is
                        // the thing that falls over.
                        QueueLimit = 0,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    }));

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

    /// <summary>
    /// Reads the socket peer, with the same caveat as the trusted hop:
    /// <see cref="IHttpConnectionFeature"/> is the storage
    /// <c>UseForwardedHeaders</c> mutates, so if that middleware ever runs ahead
    /// of this one, every request behind a proxy shares one partition — which
    /// turns the limiter into a global cap. <c>Forwarded_Headers_Are_Not_Wired</c>
    /// is the tripwire for both.
    /// </summary>
    private static string PartitionKeyFor(HttpContext context) =>
        context.Features.Get<IHttpConnectionFeature>()?.RemoteIpAddress?.ToString()
            ?? UnknownPeerPartition;
}
