using System.Collections.Concurrent;
using LearnStack.Api.Authentication;
using LearnStack.Api.Composition;
using LearnStack.Api.Tenancy;
using LearnStack.SharedKernel.Hosting;
using LearnStack.SharedKernel.Observability;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace LearnStack.Tests.Integration;

// Real production Serilog and OTel instrumentation have process-wide ownership.
// Other collections' API hosts must not replace/dispose those globals while
// this collection qualifies the actual sinks and network exporters.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TenantAuthenticationDiagnosticsTestGroup
{
    public const string Name = "Tenant authentication diagnostics";
}

public sealed partial class TenantAuthenticationDiagnosticsTests
{
    private static async Task<WebApplication> CreateHost(TenantAuthenticationIssuer issuer,
        DiagnosticObservation? observed = null, string? endpoint = null, DiagnosticVendorProvider? control = null,
        bool configurationAdapter = false, TenantAuthenticationOptions? settings = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Serilog:MinimumLevel:Default"] = "Verbose",
            ["Telemetry:OtlpEndpoint"] = endpoint,
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        if (observed is not null)
        {
            builder.Services.AddSingleton<ILogEventSink>(observed);
            builder.AddLearnStackCrossCuttingFoundation(DeploymentMode.Development);
            builder.Services.RemoveAll<IErrorTrackingProvider>();
            builder.Services.AddSingleton<IErrorTrackingProvider>(observed);
        }
        builder.Services.AddLearnStackTenantAuthentication(
            settings ?? TenantAuthenticationOptions.Create(issuer.Issuer, DeploymentMode.Development));
        if (configurationAdapter)
        {
            // The production BaseConfigurationManager returns initial discovery
            // faults inside TokenValidationResult (event 1). Its interface-only
            // adapter lets the stock handler await that SAME real manager before
            // token validation, qualifying its distinct event 3 catch boundary.
            builder.Services.PostConfigure<JwtBearerOptions>(TenantAuthenticationRegistration.Scheme,
                options => options.ConfigurationManager = new DiagnosticConfigurationAdapter(options.ConfigurationManager!));
        }
        if (control is not null)
        {
            builder.Logging.AddProvider(control);
            builder.Services.PostConfigure<LoggerFilterOptions>(options => options.Rules.Add(
                new LoggerFilterRule(null, TenantAuthenticationDiagnostics.VendorCategory, LogLevel.Trace, null)));
        }
        var app = builder.Build();
        if (observed is not null) app.UseExceptionHandler();
        app.Use((context, next) =>
        {
            context.Features.Set(HostClassification.Platform("known.test"));
            return next(context);
        });
        app.UseAuthentication();
        app.Run(context =>
        {
            if (context.Request.Path == "/diagnostic/allowed-error")
            {
                throw new InvalidOperationException(AllowedError);
            }
            context.Response.StatusCode = context.User.Identity?.IsAuthenticated == true
                ? StatusCodes.Status200OK : context.Request.Headers.ContainsKey("Authorization")
                    ? StatusCodes.Status401Unauthorized : StatusCodes.Status204NoContent;
            return Task.CompletedTask;
        });
        await app.StartAsync();
        return app;
    }

    private static async Task<HttpResponseMessage> Send(HttpClient client, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth-diagnostic");
        request.Headers.Host = "known.test";
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        return await client.SendAsync(request);
    }

    private sealed class DiagnosticObservation : ILogEventSink, IErrorTrackingProvider
    {
        private readonly ConcurrentQueue<LogEvent> _events = new();
        private readonly ConcurrentQueue<(Exception Exception, CapturedContext Context)> _captures = new();
        internal LogEvent[] Events => _events.ToArray();
        internal (Exception Exception, CapturedContext Context)[] Captures => _captures.ToArray();
        public void Emit(LogEvent logEvent) => _events.Enqueue(logEvent);
        public ValueTask CaptureAsync(Exception exception, CapturedContext context,
            CancellationToken cancellationToken = default)
        {
            _captures.Enqueue((exception, context));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class DiagnosticVendorProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<DiagnosticVendorEvent> _entries = new();
        internal DiagnosticVendorEvent[] Entries => _entries.ToArray();
        public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new DiagnosticVendorLogger(categoryName, _entries);
        public void Dispose() { }
    }

    private sealed class DiagnosticVendorLogger(string category, ConcurrentQueue<DiagnosticVendorEvent> entries)
        : Microsoft.Extensions.Logging.ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            entries.Enqueue(new(category, eventId, formatter(state, exception), exception));
    }

    private sealed record DiagnosticVendorEvent(string Category, EventId Event, string Message, Exception? Exception);

    private sealed class DiagnosticConfigurationAdapter(IConfigurationManager<OpenIdConnectConfiguration> manager)
        : IConfigurationManager<OpenIdConnectConfiguration>
    {
        public Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel) =>
            manager.GetConfigurationAsync(cancel);
        public void RequestRefresh() => manager.RequestRefresh();
    }
}
