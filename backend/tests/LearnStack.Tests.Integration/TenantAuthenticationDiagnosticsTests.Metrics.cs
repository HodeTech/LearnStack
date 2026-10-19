using System.Net;
using FluentAssertions;
using LearnStack.Api.Authentication;
using LearnStack.SharedKernel.Hosting;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using Serilog;
using Xunit;

namespace LearnStack.Tests.Integration;

public sealed partial class TenantAuthenticationDiagnosticsTests
{
    [Fact]
    public async Task Metadata_native_metrics_preserve_durations_without_exporting_private_hostnames()
    {
        // The collection owns process-wide telemetry and proxy state. Bypass
        // ambient proxies so the owned backchannel really resolves these DNS
        // names, including on CI machines with an HTTP(S)_PROXY configured.
        var originalProxy = HttpClient.DefaultProxy;
        HttpClient.DefaultProxy = new DiagnosticDirectProxy();
        try
        {
            await using var issuer = await TenantAuthenticationIssuer.StartAsync();
            var privateHost = "private-tenant-metadata-" + Guid.NewGuid().ToString("N") + ".invalid";
            var settings = TenantAuthenticationOptions.Create(
                "https://" + privateHost + "/realms/learnstack", DeploymentMode.Development);
            var localIssuer = new UriBuilder(issuer.Issuer) { Host = "localhost" }.Uri.AbsoluteUri;
            var localSettings = TenantAuthenticationOptions.Create(localIssuer, DeploymentMode.Development);

            await using var controlCollector = await TenantAuthenticationDiagnosticCollector.StartAsync();
            // Load-bearing control: a real SDK, real native meters, real OTLP,
            // and the exact production retriever, without the root's views.
            using (var meters = Sdk.CreateMeterProviderBuilder()
                .AddHttpClientInstrumentation()
                .AddOtlpExporter(options =>
                {
                    options.Endpoint = new Uri(controlCollector.Endpoint);
                    options.Protocol = OtlpExportProtocol.Grpc;
                    options.TimeoutMilliseconds = 5000;
                }).Build())
            {
                await using var control = await CreateHost(issuer, settings: settings);
                await RetrieveMetadata(control.Services, settings, localSettings);
                meters.ForceFlush(5000).Should().BeTrue();
            }
            controlCollector.Metrics.Should().Contain("http.client.request.duration")
                .And.Contain("dns.lookup.duration").And.Contain("server.address")
                .And.Contain("dns.question.name").And.Contain("GET").And.Contain(privateHost,
                    "span suppression alone leaves native HTTP/DNS metric attributes observable");

            await using var collector = await TenantAuthenticationDiagnosticCollector.StartAsync();
            var observation = new DiagnosticObservation();
            await using (var app = await CreateHost(issuer, observed: observation,
                endpoint: collector.Endpoint, settings: settings))
            {
                await RetrieveMetadata(app.Services, settings, localSettings);
                app.Services.GetRequiredService<MeterProvider>().ForceFlush(5000).Should().BeTrue();
                // Check before the independent HTTP trace control: the only
                // GETs so far are the real retriever's metadata requests. The
                // collector uses a numeric IP and POST, and cannot supply a
                // DNS duration or GET duration to make these positives pass.
                collector.Metrics.Should().Contain("http.client.request.duration").And.Contain("GET")
                    .And.Contain("dns.lookup.duration");
                app.Services.GetRequiredService<Serilog.ILogger>()
                    .ForContext("SourceContext", "LearnStack.Tests.NativeMetrics")
                    .Information("{AllowedDiagnostic}", AllowedLog);
                using var outbound = new HttpClient();
                using var response = await outbound.GetAsync(issuer.CanaryAddress);
                response.StatusCode.Should().Be(HttpStatusCode.OK);
                observation.Captures.Should().BeEmpty();
            }

            collector.Metrics.Should().Contain("http.client.request.duration").And.Contain("dns.lookup.duration")
                .And.Contain("http.request.method").And.Contain("http.response.status_code")
                .And.Contain("network.protocol.version").And.Contain("url.scheme").And.Contain("error.type");
            collector.Logs.Should().Contain(AllowedLog);
            collector.Traces.Should().Contain(issuer.CanaryAddress);
            collector.Traces.Should().NotContain(localSettings.MetadataAddress);
            collector.AllPayloads.Contains(privateHost, StringComparison.Ordinal).Should().BeFalse(
                "configured metadata addresses are private in logs, traces and native HTTP/DNS metrics");
        }
        finally
        {
            HttpClient.DefaultProxy = originalProxy;
        }
    }

    private static async Task RetrieveMetadata(IServiceProvider services,
        TenantAuthenticationOptions privateSettings, TenantAuthenticationOptions localSettings)
    {
        var backchannel = services.GetRequiredService<TenantAuthenticationBackchannel>();
        var local = new TenantOidcDocumentRetriever(localSettings, backchannel);
        var document = await local.GetDocumentAsync(localSettings.MetadataAddress, CancellationToken.None);
        document.Should().Contain("jwks_uri", "successful localhost discovery records real HTTP and DNS durations");

        var metadata = new TenantOidcDocumentRetriever(privateSettings, backchannel);
        var failure = await Record.ExceptionAsync(() =>
            metadata.GetDocumentAsync(privateSettings.MetadataAddress, CancellationToken.None));
        failure.Should().NotBeNull("the reserved .invalid metadata hostname must not resolve to an issuer");
        (failure is HttpRequestException or OperationCanceledException).Should().BeTrue(
            "the actual bounded transport must fail DNS/connection establishment or its five-second deadline");
    }

    private sealed class DiagnosticDirectProxy : IWebProxy
    {
        public ICredentials? Credentials { get; set; }
        public Uri GetProxy(Uri destination) => destination;
        public bool IsBypassed(Uri host) => true;
    }
}
