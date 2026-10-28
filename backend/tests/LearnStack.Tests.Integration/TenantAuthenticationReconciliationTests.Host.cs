using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using LearnStack.Api.Authentication;
using LearnStack.Api.Common;
using LearnStack.Api.Tenancy;
using LearnStack.Infrastructure.Observability;
using LearnStack.SharedKernel.Hosting;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LearnStack.Tests.Integration;

/// <summary>
/// Completed Step 3 components in their real order. These probes do not activate
/// the production scheme or pretend to exercise Step 4's production controller.
/// Only host lookup, membership and organization persistence are substituted.
/// </summary>
internal sealed class TenantAuthenticationReconciliationHost : IAsyncDisposable
{
    private static readonly string[] KnownBindingSubjects = ["opaque-keycloak-subject", "independent-subject-B"];
    internal const string PublicPath = "/api/v1/public/site";
    internal const string AccountPath = "/api/v1/account/site";
    internal const string TenantHost = "tenant.auth.test";
    internal const string OtherTenantHost = "other.auth.test";
    internal const string OrganizationHost = "organization.auth.test";
    internal const string PlatformHost = "platform.auth.test";
    internal const string UnknownHost = "unknown.auth.test";
    internal static readonly Guid OtherTenant = Guid.Parse("019b24b6-0cf0-7000-8000-000000000044");
    internal static readonly Guid OtherOrganization = Guid.Parse("019b24b6-0cf0-7000-8000-000000000055");
    private readonly WebApplication _app;

    private TenantAuthenticationReconciliationHost(WebApplication app, ReconciliationLogProvider logs)
    {
        _app = app;
        Logs = logs;
        Client = app.GetTestClient();
        Recorder = app.Services.GetRequiredService<ReconciliationRecorder>();
        Ports = app.Services.GetRequiredService<ReconciliationPorts>();
    }

    internal HttpClient Client { get; }
    internal IServiceProvider Services => _app.Services;
    internal ReconciliationRecorder Recorder { get; }
    internal ReconciliationPorts Ports { get; }
    internal ReconciliationLogProvider Logs { get; }
    internal ConcurrentQueue<ReconciliationObservation> ResolvedRequests { get; } = new();

    internal static async Task<TenantAuthenticationReconciliationHost> StartAsync(
        TenantAuthenticationIssuer issuer,
        int? subjectPermitLimit = null,
        bool forgeRawPrincipal = false,
        Func<IServiceProvider, ITenantAssertionRecorder>? recorder = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var logs = new ReconciliationLogProvider();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
        builder.Logging.AddFilter(typeof(TenantResolverMiddleware).FullName, LogLevel.Debug);
        builder.Logging.AddProvider(logs);
        builder.Services.AddMetrics();
        builder.Services.AddLearnStackObservabilityServices();
        builder.Services.AddTransient<ITenantContext>(services =>
            services.GetRequiredService<ITenantContextAccessor>().Current ?? UnresolvedTenantContext.Instance);
        builder.Services.AddSingleton<IOptions<TrustedHopOptions>>(Options.Create(new TrustedHopOptions()));
        builder.Services.AddSingleton<EffectiveHostAccessor>();
        builder.Services.AddSingleton(new PlatformHostOptions { Hosts = [PlatformHost] });
        builder.Services.AddSingleton<ReconciliationPorts>();
        builder.Services.AddSingleton<IHostToTenantResolver>(services =>
            services.GetRequiredService<ReconciliationPorts>());
        builder.Services.AddSingleton<ITenantMembershipReader>(services =>
            services.GetRequiredService<ReconciliationPorts>());
        builder.Services.AddSingleton<IOrganizationScopeValidator>(services =>
            services.GetRequiredService<ReconciliationPorts>());
        builder.Services.AddSingleton<LoggingTenantAssertionRecorder>();
        builder.Services.AddSingleton(new TenantAssertionAuditBudget(TimeProvider.System));
        builder.Services.AddSingleton(services => new ReconciliationRecorder(
            recorder?.Invoke(services) ?? services.GetRequiredService<LoggingTenantAssertionRecorder>()));
        builder.Services.AddSingleton<ITenantAssertionRecorder>(services =>
            services.GetRequiredService<ReconciliationRecorder>());
        builder.Services.AddSingleton<IClaimContextAssertionRecorder, ResolutionOnlyClaimAssertionRecorder>();
        builder.Services.AddLearnStackRateLimiting();
        builder.Services.AddLearnStackTenantAuthentication(
            TenantAuthenticationOptions.Create(issuer.Issuer, DeploymentMode.Development));
        builder.Services.AddLearnStackTenantSubjectAdmission();
        builder.Services.AddSingleton<ITenantIdentityBindingResolver>(new FixtureTenantIdentityBindingResolver(
            issuer.Issuer, KnownBindingSubjects.Concat(
                Enumerable.Range(0, RateLimitingExtensions.AnonymousPermitPerWindow)
                    .Select(index => "subject-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture)))));
        if (subjectPermitLimit is { } permitLimit)
        {
            builder.Services.RemoveAll<TenantSubjectRateLimiter>();
            builder.Services.AddSingleton(_ => new TenantSubjectRateLimiter(
                () => TenantSubjectRateLimiter.CreateBudget(permitLimit: permitLimit),
                TenantSubjectRateLimiter.Window, TimeProvider.System));
        }
        builder.Services.AddAuthorization();
        var app = builder.Build();
        TenantAuthenticationReconciliationHost? host = null;
        app.Use((context, next) =>
        {
            // TestServer has no socket. Both real admission budgets read the
            // same physical-peer feature; no visitor address is manufactured.
            context.Connection.RemoteIpAddress = IPAddress.Loopback;
            return next(context);
        });
        app.MapLearnStackClientErrors();
        app.UseLearnStackCorrelationHeader();
        app.UseRateLimiter();
        app.UseLearnStackVisitorAdmission();
        app.UseLearnStackRequestBodyLimit();
        app.UseLearnStackHostClassification();
        app.UseAuthentication();
        if (forgeRawPrincipal)
        {
            app.Use((context, next) =>
            {
                context.User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim("sub", "forged-unvalidated-subject"),
                    new Claim("user_id", TenantAuthenticationIssuer.Actor.ToString("D")),
                    new Claim("tenant_id", TenantAuthenticationIssuer.Tenant.ToString("D")),
                    new Claim("organization_id", TenantAuthenticationIssuer.Organization.ToString("D")),
                ], "test-forged-principal"));
                return next(context);
            });
        }
        app.UseLearnStackTenantCredentialAdmission();
        app.UseLearnStackTenantSubjectAdmission();
        app.UseLearnStackTenantIdentityBindingAdmission();
        app.UseLearnStackTenantResolution();
        app.Use((context, next) =>
        {
            host!.ResolvedRequests.Enqueue(Observe(context));
            return next(context);
        });
        app.UseLearnStackTenantAssertions();
        app.UseAuthorization();
        app.UseLearnStackTenantSurfaceAdmission();
        app.MapGet(PublicPath, (HttpContext context) => Results.Ok(Observe(context))).AllowAnonymous();
        app.MapGet(AccountPath, (HttpContext context) => Results.Ok(Observe(context))).AllowAnonymous();
        await app.StartAsync();
        host = new TenantAuthenticationReconciliationHost(app, logs);
        return host;
    }

    internal Task<HttpResponseMessage> SendAsync(string? token = null, string host = TenantHost,
        string path = AccountPath, Guid? tenantAssertion = null, Guid? organizationAssertion = null,
        string? authorization = null) => SendAsync(Client, token, host, path,
            tenantAssertion, organizationAssertion, authorization);

    internal static async Task<HttpResponseMessage> SendAsync(HttpClient client, string? token,
        string host, string path, Guid? tenantAssertion = null, Guid? organizationAssertion = null,
        string? authorization = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Host = host;
        if (authorization is not null || token is not null)
            request.Headers.TryAddWithoutValidation("Authorization", authorization ?? "Bearer " + token);
        if (tenantAssertion is { } tenant)
            request.Headers.Add(TenantAssertionMiddleware.TenantHeaderName, tenant.ToString("D"));
        if (organizationAssertion is { } organization)
            request.Headers.Add(TenantAssertionMiddleware.OrganizationHeaderName, organization.ToString("D"));
        return await client.SendAsync(request);
    }

    private static ReconciliationObservation Observe(HttpContext context)
    {
        var tenant = context.RequestServices.GetRequiredService<ITenantContext>();
        return new ReconciliationObservation(tenant.IsResolved,
            tenant.IsResolved ? tenant.TenantId.Value : null, tenant.OrganizationId?.Value,
            tenant.UserId?.Value, tenant.Origin, tenant.HostScope?.TenantId.Value,
            tenant.HostScope?.OrganizationId?.Value, tenant.CorrelationId,
            context.Features.Get<TenantCredentialFeature>()?.Identity is not null);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.DisposeAsync();
    }

    // CoversAsync deliberately refuses every claim-only scope in this host.
    // An admitted-context evidence path would exceed its resolution-only proof.
    private sealed class ResolutionOnlyClaimAssertionRecorder : IClaimContextAssertionRecorder
    {
        public Task RecordAsync(ITenantContext context, TenantAssertionDimension dimension) =>
            throw new InvalidOperationException("This resolution-only host does not admit claim-context assertions.");
    }
}

internal sealed record ReconciliationObservation(bool Resolved, Guid? Tenant, Guid? Organization,
    Guid? Actor, TenantContextOrigin? Origin, Guid? HostTenant, Guid? HostOrganization,
    string? Correlation, bool ValidatedProfile);

internal sealed class ReconciliationPorts : IHostToTenantResolver, ITenantMembershipReader, IOrganizationScopeValidator
{
    internal ConcurrentQueue<string> HostLookups { get; } = new();
    internal ConcurrentQueue<(UserId Actor, TenantId Tenant, OrganizationId? Organization)> Memberships { get; } = new();
    internal ConcurrentQueue<(TenantId Tenant, OrganizationId Organization)> OrganizationChecks { get; } = new();

    public Task<HostResolution?> ResolveAsync(string host, CancellationToken cancellationToken = default)
    {
        HostLookups.Enqueue(host);
        return Task.FromResult(host switch
        {
            TenantAuthenticationReconciliationHost.TenantHost => new HostResolution(
                TenantId.From(TenantAuthenticationIssuer.Tenant), null),
            TenantAuthenticationReconciliationHost.OrganizationHost => new HostResolution(
                TenantId.From(TenantAuthenticationIssuer.Tenant), OrganizationId.From(TenantAuthenticationIssuer.Organization)),
            TenantAuthenticationReconciliationHost.OtherTenantHost => new HostResolution(
                TenantId.From(TenantAuthenticationReconciliationHost.OtherTenant), null),
            _ => null,
        });
    }

    public Task<bool> CoversAsync(UserId userId, TenantId tenantId, OrganizationId? organizationId,
        CancellationToken cancellationToken = default)
    {
        Memberships.Enqueue((userId, tenantId, organizationId));
        return Task.FromResult(false);
    }

    public Task<bool> BelongsToTenantAsync(TenantId tenantId, OrganizationId organizationId,
        CancellationToken cancellationToken = default)
    {
        OrganizationChecks.Enqueue((tenantId, organizationId));
        return Task.FromResult(true);
    }
}

internal sealed class ReconciliationRecorder(ITenantAssertionRecorder inner) : ITenantAssertionRecorder
{
    internal ConcurrentQueue<TenantAssertionRejection> Rejections { get; } = new();
    internal ConcurrentQueue<TenantNoTenantRefusal> NoTenantRefusals { get; } = new();
    internal ConcurrentQueue<TenantAssertionDimension> Unresolved { get; } = new();

    public Task RecordRejectionAsync(TenantAssertionRejection rejection)
    {
        Rejections.Enqueue(rejection);
        return inner.RecordRejectionAsync(rejection);
    }

    public void RecordNoTenantRefusal(TenantNoTenantRefusal refusal)
    {
        NoTenantRefusals.Enqueue(refusal);
        inner.RecordNoTenantRefusal(refusal);
    }

    public void RecordUnresolved(TenantAssertionDimension dimension)
    {
        Unresolved.Enqueue(dimension);
        inner.RecordUnresolved(dimension);
    }
}

internal sealed class ReconciliationLogProvider : ILoggerProvider
{
    internal ConcurrentQueue<ReconciliationLog> Entries { get; } = new();
    public ILogger CreateLogger(string categoryName) => new ReconciliationLogger(categoryName, Entries);
    public void Dispose() { }

    private sealed class ReconciliationLogger(string category, ConcurrentQueue<ReconciliationLog> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => entries.Enqueue(new ReconciliationLog(
                category, logLevel, eventId,
                state is IEnumerable<KeyValuePair<string, object?>> properties
                    ? properties.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
                    : new Dictionary<string, object?>(StringComparer.Ordinal), exception));
    }
}

internal sealed record ReconciliationLog(string Category, LogLevel Level, EventId Event,
    IReadOnlyDictionary<string, object?> Properties, Exception? Exception);
