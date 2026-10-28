using LearnStack.Api.Common;
using LearnStack.Api.Composition;
using LearnStack.Api.Tenancy;
using LearnStack.Api.Versioning;
using LearnStack.SharedKernel.Hosting;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace LearnStack.Tests.Integration;

/// <summary>
/// Isolated host for legacy routing, MVC, error, body and tenancy component proofs.
/// This is not Program and makes no claim about the live HTTP authorization boundary.
/// Program's undeclared-operation refusal is covered by UndeclaredHttpOperationTests.
/// </summary>
/// <remarks>
/// The production component registrations and canonical MediatR pipeline remain intact.
/// Tests that dispatch application requests must supply exact test-owned policies and
/// provenance; this host supplies neither a human candidate nor a permissive authorizer.
/// No probe is disguised as infrastructure or as a declared production HTTP operation.
/// </remarks>
public class ComponentApiFactory : IDisposable, IAsyncDisposable
{
    private WebApplication? _app;
    private bool _disposed;

    public IServiceProvider Services => Application.Services;

    public HttpClient CreateClient() => CreateClient(new WebApplicationFactoryClientOptions());

    public HttpClient CreateClient(WebApplicationFactoryClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        // TestServer's client does not follow redirects; these component tests
        // observe redirects directly rather than exercising a browser transport.
        var client = Application.GetTestClient();
        client.BaseAddress = options.BaseAddress;
        return client;
    }

    public ComponentApiFactory WithWebHostBuilder(Action<IWebHostBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        return new ConfiguredFactory(ConfigureComponentSettings, ConfigureTestHost, configure);
    }

    protected virtual void ConfigureComponentSettings(WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
    }

    protected virtual void ConfigureTestHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
    }

    private WebApplication Application
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_app is not null) return _app;
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ApplicationName = typeof(Program).Assembly.FullName,
                EnvironmentName = Environments.Development,
                ContentRootPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/LearnStack.Api")),
            });
            builder.WebHost.UseTestServer();
            // These offline component proofs own no network telemetry collector.
            builder.Configuration["Telemetry:OtlpEndpoint"] = null;
            ConfigureComponentSettings(builder);
            builder.AddLearnStackCrossCuttingFoundation(DeploymentMode.Development,
                typeof(LearnStack.Modules.Tenancy.Application.AssemblyMarker).Assembly,
                typeof(LearnStack.Modules.Customization.Application.AssemblyMarker).Assembly,
                typeof(LearnStack.Modules.Education.Application.AssemblyMarker).Assembly,
                typeof(LearnStack.Modules.Identity.Application.AssemblyMarker).Assembly);
            builder.Services.AddLearnStackTenancyEdge(builder.Configuration);
            builder.Services.AddLearnStackPersistence(builder.Configuration);
            builder.Services.AddLearnStackRateLimiting();
            builder.Services.AddLearnStackApiVersioning();
            builder.Services.AddControllers(options =>
                options.Conventions.Insert(0, new ComponentControllerFilter()));
            builder.Services.AddSingleton<IProtectedOperationAuthorizer, NoHumanAuthorization>();
            builder.Services.AddSingleton<IClaimContextAssertionRecorder, NoClaimAssertionRecorder>();
            ConfigureTestHost(builder.WebHost);
            ConstructionOnlyApiFactory.RemoveDispatcher(builder.Services);
            _app = builder.Build();
            _app.UseExceptionHandler();
            _app.MapLearnStackClientErrors();
            _app.UseLearnStackCorrelationHeader();
            _app.UseRateLimiter();
            _app.UseLearnStackVisitorAdmission();
            _app.UseLearnStackRequestBodyLimit();
            _app.MapLearnStackOpenApi();
            _app.UseLearnStackHostClassification();
            _app.UseLearnStackTenantResolution();
            _app.UseLearnStackTenantAssertions();
            _app.MapGet("/healthz", () => Results.Ok(new { status = "healthy" }));
            _app.MapControllers();
            _app.StartAsync().GetAwaiter().GetResult();
            return _app;
        }
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;
        if (disposing && _app is not null) _app.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _disposed = true;
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeAsyncCore();
        Dispose(disposing: false);
        GC.SuppressFinalize(this);
    }

    protected virtual async ValueTask DisposeAsyncCore()
    {
        if (_disposed) return;
        if (_app is not null) await _app.DisposeAsync();
        _disposed = true;
    }

    private sealed class ComponentControllerFilter : IApplicationModelConvention
    {
        public void Apply(ApplicationModel application)
        {
            ArgumentNullException.ThrowIfNull(application);
            foreach (var controller in application.Controllers.Where(controller =>
                         !typeof(ITestOnlyController).IsAssignableFrom(controller.ControllerType)).ToArray())
                application.Controllers.Remove(controller);
        }
    }

    private sealed class ConfiguredFactory(
        Action<WebApplicationBuilder> settings,
        Action<IWebHostBuilder> original,
        Action<IWebHostBuilder> additional) : ComponentApiFactory
    {
        protected override void ConfigureComponentSettings(WebApplicationBuilder builder) => settings(builder);
        protected override void ConfigureTestHost(IWebHostBuilder builder)
        {
            original(builder);
            additional(builder);
        }
    }

    private sealed class NoHumanAuthorization : IProtectedOperationAuthorizer
    {
        public Task<ProtectedOperationDecision> AuthorizeAsync(OperationInvocation invocation, OperationPolicy policy,
            AuthorizationCandidate candidate, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The component host has no human authorization source.");
    }

    private sealed class NoClaimAssertionRecorder : IClaimContextAssertionRecorder
    {
        public Task RecordAsync(ITenantContext context, TenantAssertionDimension dimension) =>
            throw new InvalidOperationException("The component host cannot attest a confirmed human claim assertion.");
    }
}
