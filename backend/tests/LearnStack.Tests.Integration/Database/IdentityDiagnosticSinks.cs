using System.Collections.Concurrent;
using System.Text;
using FluentAssertions;
using LearnStack.Api.Common;
using LearnStack.Infrastructure.ErrorTracking;
using LearnStack.Infrastructure.Observability.Serilog;
using LearnStack.SharedKernel.Hosting;
using LearnStack.SharedKernel.Observability;
using LearnStack.SharedKernel.Secrets;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.Tools.Seeder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sentry;
using Sentry.Extensibility;
using Sentry.Protocol.Envelopes;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;
using Serilog.Formatting.Json;

namespace LearnStack.Tests.Integration.Database;

/// <summary>Real SDK serialization; only delivery transport is replaced to prohibit external egress.</summary>
internal sealed class IdentityDiagnosticSinks : IDisposable
{
    private const string Marker = "identity-sink-positive-control";
    private const string Dsn = "https://public@identity-sinks.invalid/1";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "identity-sinks-" + Guid.NewGuid().ToString("N"));
    private readonly JsonLogSink _log = new();
    private readonly EnvelopeTransport _transport = new();
    private readonly ServiceProvider _local;
    private readonly ServiceProvider _sentry;
    private readonly IDisposable _sdk;
    private readonly Logger _logger;
    private readonly List<string> _responses = [];

    public IdentityDiagnosticSinks()
    {
        _logger = new LoggerConfiguration().MinimumLevel.Debug()
            .Enrich.With<RedactSensitiveFieldsEnricher>().WriteTo.Sink(_log).CreateLogger();
        Loggers = new SerilogLoggerFactory(_logger);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ErrorTracking:LocalFile:Directory"] = _directory,
        }).Build();
        _local = new ServiceCollection().AddSingleton(Loggers).AddLogging()
            .AddLearnStackErrorTracking(new FixedSecret(), config, DeploymentMode.SelfHostedAirGapped).BuildServiceProvider();
        _sentry = new ServiceCollection().AddSingleton(Loggers).AddLogging()
            .AddLearnStackErrorTracking(new FixedSecret(), config, DeploymentMode.SaaS).BuildServiceProvider();
        _sdk = SentrySdk.Init(options =>
        {
            options.Dsn = Dsn;
            options.Transport = _transport;
            options.IsGlobalModeEnabled = true;
            options.AutoSessionTracking = false;
            options.DisableAppDomainUnhandledExceptionCapture();
            options.DisableUnobservedTaskExceptionCapture();
            options.DisableAppDomainProcessExitFlush();
            options.DisableDiagnosticSourceIntegration();
        });
    }

    public ILoggerFactory Loggers { get; }
    public string LogText => string.Join('\n', _log.Payloads);

    public async Task CaptureThroughL1Async(Exception failure, int expectedStatus = 503)
    {
        foreach (var services in new[] { _local, _sentry })
        {
            var context = new DefaultHttpContext { RequestServices = services, TraceIdentifier = Marker };
            context.Request.Path = "/__test/identity-diagnostic-sink";
            context.Request.Method = "POST";
            await using var response = new MemoryStream();
            context.Response.Body = response;
            var handler = new LearnStackExceptionHandler(services.GetRequiredService<IErrorTrackingProvider>(),
                new StaticTenantContextAccessor(null), Loggers.CreateLogger<LearnStackExceptionHandler>());
            (await handler.TryHandleAsync(context, failure, CancellationToken.None)).Should().BeTrue();
            context.Response.StatusCode.Should().Be(expectedStatus);
            _responses.Add(Encoding.UTF8.GetString(response.ToArray()));
        }
        await SentrySdk.FlushAsync(TimeSpan.FromSeconds(10));
    }

    public async Task<CapturedSinks> ReadAsync()
    {
        var files = Directory.GetFiles(_directory, "*.json");
        files.Should().ContainSingle("the real air-gapped provider must persist its capture");
        _transport.Payloads.Should().ContainSingle("the real Sentry SDK must serialize and send its event");
        return new(string.Join('\n', _log.Payloads), await File.ReadAllTextAsync(files.Single()),
            _transport.Payloads.Single(), string.Join('\n', _responses));
    }

    public void Dispose()
    {
        _sdk.Dispose();
        _sentry.Dispose();
        _local.Dispose();
        Loggers.Dispose();
        _logger.Dispose();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    internal sealed record CapturedSinks(string Log, string Local, string Sentry, string Response)
    {
        public void AssertPositiveControls(string responseCode = "dependency_unavailable")
        {
            Log.Should().Contain("L1 exception handler captured");
            Log.Should().Contain("Microsoft.EntityFrameworkCore.Database.Command",
                "ordinary real EF command logging remains active");
            Local.Should().Contain(Marker).And.Contain("exception");
            Sentry.Should().Contain(Marker).And.Contain("\"type\":\"event\"").And.Contain("exception");
            Response.Should().Contain(Marker).And.Contain(responseCode);
        }

        public void AssertExcludes(IEnumerable<string> canaries)
        {
            foreach (var canary in canaries)
            {
                Log.Should().NotContain(canary, "no value reaches actual serialized Serilog output");
                Local.Should().NotContain(canary, "no value reaches the actual local archive");
                Sentry.Should().NotContain(canary, "no value reaches the real SDK's serialized Sentry envelope");
                Response.Should().NotContain(canary, "no value reaches the actual L1 response bytes");
            }
        }
    }

    private sealed class FixedSecret : ISecretProvider
    {
        public string? GetSecret(string key) => key == "ErrorTracking:Sentry:Dsn" ? Dsn : null;
    }

    private sealed class JsonLogSink : ILogEventSink
    {
        private readonly JsonFormatter _formatter = new(renderMessage: true);
        public ConcurrentQueue<string> Payloads { get; } = new();
        public void Emit(LogEvent logEvent)
        {
            using var writer = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
            _formatter.Format(logEvent, writer);
            Payloads.Enqueue(writer.ToString());
        }
    }

    private sealed class EnvelopeTransport : ITransport
    {
        public ConcurrentQueue<string> Payloads { get; } = new();
        public async Task SendEnvelopeAsync(Envelope envelope, CancellationToken cancellationToken = default)
        {
            await using var stream = new MemoryStream();
            await envelope.SerializeAsync(stream, null, cancellationToken);
            Payloads.Enqueue(Encoding.UTF8.GetString(stream.ToArray()));
        }
    }
}
