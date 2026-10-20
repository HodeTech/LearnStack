using System.Diagnostics;
using System.Net;
using System.Net.Http.Metrics;
using System.Net.Sockets;
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
    [Theory]
    [InlineData("OTEL_METRICS_EXEMPLAR_FILTER", "always_on")]
    [InlineData("OTEL_METRICS_EXEMPLAR_FILTER", "trace_based")]
    [InlineData("OTEL_DOTNET_EXPERIMENTAL_METRICS_EXEMPLAR_FILTER_HISTOGRAMS", "always_on")]
    [InlineData("OTEL_DOTNET_EXPERIMENTAL_METRICS_EXEMPLAR_FILTER_HISTOGRAMS", "trace_based")]
    public async Task Metadata_native_metrics_preserve_durations_without_exporting_private_hostnames(
        string exemplarVariable, string exemplarFilter)
    {
        // The collection owns process-wide telemetry and proxy state. Bypass
        // ambient proxies so the owned backchannel really resolves these DNS
        // names, including on CI machines with an HTTP(S)_PROXY configured.
        var originalProxy = HttpClient.DefaultProxy;
        const string filterVariable = "OTEL_METRICS_EXEMPLAR_FILTER";
        const string histogramFilterVariable = "OTEL_DOTNET_EXPERIMENTAL_METRICS_EXEMPLAR_FILTER_HISTOGRAMS";
        var originalFilter = Environment.GetEnvironmentVariable(filterVariable);
        var originalHistogramFilter = Environment.GetEnvironmentVariable(histogramFilterVariable);
        HttpClient.DefaultProxy = new DiagnosticDirectProxy();
        Environment.SetEnvironmentVariable(filterVariable, null);
        Environment.SetEnvironmentVariable(histogramFilterVariable, null);
        Environment.SetEnvironmentVariable(exemplarVariable, exemplarFilter);
        try
        {
            // TraceBased must be exercised under a sampled W3C context; it must
            // not pass simply because the native measurements lack an activity.
            using var activity = new Activity("tenant-authentication-metric-proof")
                .SetIdFormat(ActivityIdFormat.W3C);
            activity.ActivityTraceFlags = ActivityTraceFlags.Recorded;
            activity.Start();
            await using var issuer = await TenantAuthenticationIssuer.StartAsync();
            var privateHost = "private-tenant-metadata-" + Guid.NewGuid().ToString("N") + ".invalid";
            var settings = TenantAuthenticationOptions.Create(
                "https://" + privateHost + "/realms/learnstack", DeploymentMode.Development);
            var localIssuer = new UriBuilder(issuer.Issuer) { Host = "localhost" }.Uri.AbsoluteUri;
            var localSettings = TenantAuthenticationOptions.Create(localIssuer, DeploymentMode.Development);
            var ordinary = OrdinaryMetricDestinations.Create(issuer);

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
                await RetrieveOrdinary(issuer, ordinary);
                meters.ForceFlush(5000).Should().BeTrue();
            }
            controlCollector.Metrics.Should().Contain("http.client.request.duration")
                .And.Contain("dns.lookup.duration").And.Contain("server.address")
                .And.Contain("dns.question.name").And.Contain("GET").And.Contain(privateHost,
                    "span suppression alone leaves native HTTP/DNS metric attributes observable");
            var controlPoints = controlCollector.MetricPoints;
            controlPoints.Should().Contain(point => point.Name == "http.client.request.duration"
                && point.Attributes.ContainsKey("server.port")
                && point.Attributes["server.port"].Equals((long)ordinary.Port),
                "the actual unprojected OTLP payload must contain the numeric destination port");
            controlPoints.Should().Contain(point => point.Name == "dns.lookup.duration"
                && point.Attributes.ContainsKey("dns.question.name")
                && point.Attributes["dns.question.name"].Equals(ordinary.Host));
            foreach (var canary in ordinary.Canaries)
                controlCollector.Metrics.Should().Contain(canary,
                    "the real ordinary request supplies destination and enriched URL dimensions before projection");

            await using var exemplarCollector = await TenantAuthenticationDiagnosticCollector.StartAsync();
            // Keep the production point-tag projection, but deliberately omit
            // AlwaysOff. Real environment configuration must enable exemplars
            // carrying precisely the attributes that the point view removed.
            using (var meters = Sdk.CreateMeterProviderBuilder()
                .AddView(instrument => instrument.Meter.Name switch
                {
                    "System.Net.Http" => new MetricStreamConfiguration
                    {
                        TagKeys = ["http.request.method", "http.response.status_code", "network.protocol.version",
                            "url.scheme", "error.type"],
                    },
                    "System.Net.NameResolution" => new MetricStreamConfiguration { TagKeys = ["error.type"] },
                    _ => null,
                })
                .AddHttpClientInstrumentation()
                .AddOtlpExporter(options =>
                {
                    options.Endpoint = new Uri(exemplarCollector.Endpoint);
                    options.Protocol = OtlpExportProtocol.Grpc;
                    options.TimeoutMilliseconds = 5000;
                }).Build())
            {
                await using var control = await CreateHost(issuer, settings: settings);
                await RetrieveMetadata(control.Services, settings, localSettings);
                meters.ForceFlush(5000).Should().BeTrue();
                await RetrieveOrdinary(issuer, ordinary, () => meters.ForceFlush(5000).Should().BeTrue());
            }
            var projectedPoints = exemplarCollector.MetricPoints;
            projectedPoints.SelectMany(point => point.Attributes.Keys).Should()
                .NotContain(["server.address", "server.port", "dns.question.name", "url.full", "url.path", "url.query"]);
            var exemplars = projectedPoints.SelectMany(point => point.Exemplars).ToArray();
            exemplars.Should().Contain(exemplar => exemplar.Value > 0
                && exemplar.TraceId.Length == 32 && exemplar.SpanId.Length == 16,
                "real sampled native measurements must reach the decoded OTLP exemplar messages");
            var exemplarAttributes = exemplars.SelectMany(exemplar => exemplar.FilteredAttributes).ToArray();
            exemplarAttributes.Should().Contain(attribute => attribute.Key == "server.address"
                && attribute.Value.Equals(privateHost));
            exemplarAttributes.Should().Contain(attribute => attribute.Key == "dns.question.name"
                && attribute.Value.Equals(privateHost));
            exemplarAttributes.Should().Contain(attribute => attribute.Key == "server.port"
                && attribute.Value.Equals((long)ordinary.Port),
                "numeric destination ports must be decoded inside exemplars, separately from point attributes");
            foreach (var canary in ordinary.Canaries)
                exemplarAttributes.Should().Contain(attribute =>
                    attribute.Value is string && ((string)attribute.Value).Contains(canary, StringComparison.Ordinal));
            exemplarAttributes.Select(attribute => attribute.Key).Should().Contain(
                ["server.address", "server.port", "dns.question.name", "url.full", "url.path", "url.query"]);

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
                await RetrieveOrdinary(issuer, ordinary);
                app.Services.GetRequiredService<MeterProvider>().ForceFlush(5000).Should().BeTrue();
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
            collector.Traces.Should().Contain(ordinary.Host).And.Contain(ordinary.Address)
                .And.Contain(ordinary.Path).And.Contain(ordinary.QueryKey,
                    "ordinary dependency traces remain available despite native metric projection");
            collector.Traces.Should().NotContain(localSettings.MetadataAddress);
            foreach (var canary in ordinary.Canaries)
                collector.Metrics.Should().NotContain(canary,
                    "ordinary destinations and URL data are private in metrics, separately from preserved traces");
            var points = collector.MetricPoints;
            points.SelectMany(point => point.Exemplars).Should().BeEmpty(
                "the production AlwaysOff setting must override the real " + exemplarVariable + "=" + exemplarFilter);
            points.SelectMany(point => point.Attributes.Values).Should().NotContain((long)ordinary.Port);
            var http = points.Where(point => point.ScopeName == "System.Net.Http").ToArray();
            http.Should().Contain(point => point.Name == "http.client.request.duration" && point.Value > 0);
            string[] httpDimensions = ["http.request.method", "http.response.status_code", "network.protocol.version", "url.scheme", "error.type"];
            http.Should().OnlyContain(point => point.Attributes.Keys.All(key => httpDimensions.Contains(key)));
            var dns = points.Where(point => point.ScopeName == "System.Net.NameResolution").ToArray();
            dns.Should().Contain(point => point.Name == "dns.lookup.duration" && point.Value > 0);
            dns.Should().OnlyContain(point => point.Attributes.Keys.All(key => key == "error.type"));
            collector.AllPayloads.Contains(privateHost, StringComparison.Ordinal).Should().BeFalse(
                "configured metadata addresses are private in logs, traces and native HTTP/DNS metrics");
        }
        finally
        {
            HttpClient.DefaultProxy = originalProxy;
            Environment.SetEnvironmentVariable(filterVariable, originalFilter);
            Environment.SetEnvironmentVariable(histogramFilterVariable, originalHistogramFilter);
        }
    }

    private static async Task RetrieveOrdinary(TenantAuthenticationIssuer issuer,
        OrdinaryMetricDestinations destinations, Action? afterRequest = null)
    {
        var actual = new Uri(issuer.Issuer);
        using var client = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            ConnectCallback = async (context, cancel) =>
            {
                if (context.DnsEndPoint.Host == destinations.Host)
                {
                    try
                    {
                        await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancel);
                        throw new InvalidOperationException("The reserved ordinary .invalid hostname must not resolve.");
                    }
                    catch (SocketException)
                    {
                        // Record a real ordinary DNS lookup, then route the
                        // physical connection only to the owned loopback server.
                    }
                }
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, actual.Port), cancel);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        })
        { Timeout = TimeSpan.FromSeconds(5) };
        foreach (var address in destinations.Addresses)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, address);
            // Enrich the real native request-duration measurement, so the
            // unprojected control proves URL/path/query canaries existed rather
            // than passing because current default metrics never emitted them.
            HttpMetricsEnrichmentContext.AddCallback(request, static context =>
            {
                var target = context.Request.RequestUri
                    ?? throw new InvalidOperationException("The controlled request needs its destination.");
                context.AddCustomTag("url.full", target.AbsoluteUri);
                context.AddCustomTag("url.path", target.AbsolutePath);
                context.AddCustomTag("url.query", target.Query);
            });
            using var response = await client.SendAsync(request);
            response.StatusCode.Should().Be(HttpStatusCode.NotFound,
                "the real controlled issuer has no ordinary diagnostic route");
            afterRequest?.Invoke();
        }
    }

    private sealed record OrdinaryMetricDestinations(
        string Host, string Address, int Port, string Path, string QueryKey, string QueryValue)
    {
        internal string[] Canaries => [Host, Address, Path, QueryKey, QueryValue];

        internal Uri[] Addresses => new[] { Host, Address }.Select(host =>
            new UriBuilder(Uri.UriSchemeHttp, host, Port, Path) { Query = QueryKey + "=" + QueryValue }.Uri).ToArray();

        internal static OrdinaryMetricDestinations Create(TenantAuthenticationIssuer issuer)
        {
            var suffix = Guid.NewGuid().ToString("N");
            return new("ordinary-dependency-" + suffix + ".invalid", "127.91.92.93",
                new Uri(issuer.Issuer).Port == 65007 ? 65008 : 65007,
                "/ordinary-path-" + suffix, "ordinary_query_" + suffix, "ordinary-value-" + suffix);
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
