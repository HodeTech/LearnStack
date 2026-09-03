using LearnStack.Api.Versioning;
using System.Diagnostics.Metrics;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace LearnStack.Api.Tenancy;

/// <summary>
/// Decides which host a <c>/api/v1/*</c> request is for, and answers <c>404</c>
/// when it is for none.
/// </summary>
/// <remarks>
/// <para>
/// <b>Before authentication, and that is deliberate.</b> An unknown host is
/// rejected cheaply, before any token is validated or any handler runs
/// (<see href="../../../../docs/decisions/0036-tenant-resolution-trusted-inputs.md">ADR-0036</see>
/// § Rules). Context construction runs <i>after</i> authentication, so
/// <c>TenantContextFactory</c> sees both signals at once; this middleware
/// contributes only the first of them.
/// </para>
/// <para>
/// <b>404, never 403, and never a body that distinguishes.</b> Saying "unknown
/// tenant" confirms which hostnames exist. The rejection is counted on an
/// unlabelled counter and never recorded durably: the host is attacker-authored
/// on every anonymous request, so writing it anywhere retained hands a stranger a
/// pen.
/// </para>
/// </remarks>
public sealed class HostClassificationMiddleware
{
    /// <summary>The prefixes classification applies to — one per live API major.</summary>
    /// <remarks>
    /// <b>Derived from <see cref="ApiVersioningExtensions.LiveMajors"/>, not written
    /// here.</b> A hardcoded <c>/api/v1</c> silently stopped classifying the moment a
    /// second major went live: every request to <c>/api/v2</c> would skip host
    /// classification, so an unknown host would reach a handler instead of the bodyless
    /// 404, and a tenant-facing route would run with no <c>HostClassification</c> feature
    /// at all. The versioning list is the one place a major is declared live, and this
    /// follows it.
    /// </remarks>
    public static readonly IReadOnlyList<string> ClassifiedPrefixes =
        [.. ApiVersioningExtensions.LiveMajors.Select(major => $"/api/v{major}")];

    /// <summary>
    /// Prefixes classification does not apply to.
    /// </summary>
    /// <remarks>
    /// <b>A prefix list, not endpoint literals.</b> A closed allow-list of literals
    /// would 404 the entire Hub contract surface the first time it grew a route —
    /// <c>/api/internal/*</c> is a whole surface with its own resolver
    /// (<c>HubCorrelationMiddleware</c>, Phase 02c) and its tenant comes from the
    /// envelope's path segment, not from a host.
    /// <c>Host_Classification_Applies_To_Tenant_Facing_Routes_Only</c> asserts the
    /// shape as prefixes for that reason.
    /// </remarks>
    public static readonly IReadOnlyList<string> UnclassifiedPrefixes =
    [
        "/healthz",
        "/readyz",
        "/openapi",
        "/admin/hangfire",
        "/api/internal",
    ];

    /// <summary>Unknown hosts, unlabelled — the host itself is never a dimension.</summary>
    public const string RejectedCounterName = "learnstack_host_classification_rejected_total";

    private readonly RequestDelegate _next;
    private readonly EffectiveHostAccessor _hosts;
    private readonly IHostToTenantResolver _resolver;
    private readonly HashSet<string> _platformHosts;
    private readonly ILogger<HostClassificationMiddleware> _logger;
    private readonly Counter<long> _rejected;

    public HostClassificationMiddleware(
        RequestDelegate next,
        EffectiveHostAccessor hosts,
        IHostToTenantResolver resolver,
        PlatformHostOptions platformHosts,
        ILogger<HostClassificationMiddleware> logger,
        IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(platformHosts);
        ArgumentNullException.ThrowIfNull(meterFactory);

        _next = next;
        _hosts = hosts;
        _resolver = resolver;
        _logger = logger;

        // Validated here rather than at first request: a malformed entry is a
        // deployment mistake, and the only useful moment to say so is boot.
        _platformHosts = platformHosts.Validate();

        _rejected = meterFactory
            .Create(LoggingTenantAssertionRecorder.MeterName)
            .CreateCounter<long>(RejectedCounterName);
    }

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!ClassifiesPath(context.Request.Path))
        {
            await _next(context);
            return;
        }

        var host = _hosts.For(context);

        if (host is null)
        {
            // The host could not name one at all — over-long, an IP literal, a
            // percent-escape, an unparseable IDN. Indistinguishable on the wire
            // from a host that named nothing, deliberately.
            await RejectAsync(context, "unnamed");
            return;
        }

        // The platform branch first, and before any database work. A platform host
        // maps to no tenant by configuration, so resolving it would be one wasted
        // transaction per request on the operator's own entry point — and, in the
        // Docker-free host suites, a transaction against a database that is not
        // there.
        if (_platformHosts.Contains(host))
        {
            context.Features.Set(HostClassification.Platform(host));
            await _next(context);
            return;
        }

        var resolution = await _resolver.ResolveAsync(host, context.RequestAborted);

        if (resolution is null)
        {
            await RejectAsync(context, host);
            return;
        }

        context.Features.Set(HostClassification.ForResolution(host, resolution));
        await _next(context);
    }

    /// <summary>
    /// Whether classification applies to <paramref name="path"/>.
    /// </summary>
    /// <remarks>
    /// Public because it <i>is</i> the rule
    /// <c>Host_Classification_Applies_To_Tenant_Facing_Routes_Only</c> asserts, and
    /// a test that drove it through the middleware would need a resolver and a
    /// database to observe a predicate that touches neither.
    /// </remarks>
    public static bool ClassifiesPath(PathString path)
    {
        foreach (var prefix in UnclassifiedPrefixes)
        {
            if (path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return ClassifiedPrefixes.Any(
            prefix => path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private async Task RejectAsync(HttpContext context, string host)
    {
        _rejected.Add(1);

        // The host at Debug and nowhere else. It is attacker-authored on every
        // anonymous request, so it does not belong at a level anything ships to a
        // retained sink — ADR-0036 keeps attacker-authored strings out of
        // audit_log, and the same argument applies to an Information log an
        // operator forwards.
        LogRejected(_logger, host, null);

        // Bodyless: UseStatusCodePages, registered above this middleware, renders
        // the one Problem Details shape. Writing a body here would be a second
        // writer and — worse — a different one from the routing 404 an unmapped
        // path produces, which is exactly the bit an anonymous caller must not be
        // able to tell apart.
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        await Task.CompletedTask;
    }

    private static readonly Action<ILogger, string, Exception?> LogRejected =
        LoggerMessage.Define<string>(
            LogLevel.Debug,
            new EventId(1, nameof(LogRejected)),
            "Host classification rejected {Host}: no active, publicly-live mapping.");
}

/// <summary>Registration for <see cref="HostClassificationMiddleware"/>.</summary>
public static class HostClassificationMiddlewareExtensions
{
    public static IApplicationBuilder UseLearnStackHostClassification(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<HostClassificationMiddleware>();
    }
}
