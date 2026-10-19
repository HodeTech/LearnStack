using System.Net;
using FluentAssertions;
using LearnStack.Api.Authentication;
using LearnStack.SharedKernel.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Logging;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using Serilog.Events;
using Xunit;

namespace LearnStack.Tests.Integration;

/// <summary>Stock bearer failures through the production logging, OTLP and L1 capture paths.</summary>
[Collection(TenantAuthenticationDiagnosticsTestGroup.Name)]
public sealed partial class TenantAuthenticationDiagnosticsTests
{
    private const string ClaimCanary = "private-opaque-subject-18f1bddae593";
    private const string IssuerCanary = "https://private-issuer-18f1bddae593.invalid/realms/learnstack";
    private const string AudienceCanary = "private-audience-18f1bddae593";
    private const string ProviderCanary = "private-provider-body-18f1bddae593";
    private const string VendorSinkCanary = "private-vendor-sink-18f1bddae593";
    private const string AllowedLog = "allowed-application-diagnostic-18f1bddae593";
    private const string AllowedError = "allowed-application-error-18f1bddae593";
    private static readonly Action<Microsoft.Extensions.Logging.ILogger, string, Exception?> AllowedLoggerMessage =
        LoggerMessage.Define<string>(LogLevel.Information, new EventId(918, "AllowedDiagnostic"), "{AllowedDiagnostic}");
    private static readonly Action<Microsoft.Extensions.Logging.ILogger, string, Exception?> AllowedWarningMessage =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(920, "AllowedWarning"), "{AllowedDiagnostic}");

    [Theory]
    [InlineData(LogLevel.Trace, false)]
    [InlineData(LogLevel.Warning, false)]
    [InlineData(LogLevel.Trace, true)]
    public async Task The_ILogger_filter_suppresses_only_the_exact_vendor_category_and_preserves_other_thresholds(
        LogLevel minimumLevel, bool categoryWarning)
    {
        using var observed = new DiagnosticVendorProvider();
        var services = new ServiceCollection();
        services.AddLogging(logging =>
        {
            logging.AddProvider(observed).SetMinimumLevel(minimumLevel);
            if (categoryWarning) logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        });
        services.AddLearnStackTenantAuthentication(TenantAuthenticationOptions.Create(
            "https://issuer.invalid/realms/learnstack", DeploymentMode.SaaS));
        await using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<ILoggerFactory>();

        var vendor = factory.CreateLogger(TenantAuthenticationDiagnostics.VendorCategory);
        foreach (var level in Enum.GetValues<LogLevel>().Where(level => level != LogLevel.None))
        {
            LoggerMessage.Define<string>(level, new EventId(919, "VendorDiagnostic"), "{PrivateDiagnostic}")
                (vendor, VendorSinkCanary, null);
        }
        AllowedLoggerMessage(factory.CreateLogger(TenantAuthenticationDiagnostics.VendorCategory + ".Unrelated"), AllowedLog, null);
        AllowedWarningMessage(factory.CreateLogger(TenantAuthenticationDiagnostics.VendorCategory + ".Unrelated"), AllowedLog + "-warning", null);

        observed.Entries.Should().OnlyContain(entry =>
            entry.Category == TenantAuthenticationDiagnostics.VendorCategory + ".Unrelated");
        observed.Entries.Should().ContainSingle(entry => entry.Message == AllowedLog + "-warning");
        observed.Entries.Count(entry => entry.Message == AllowedLog).Should()
            .Be(minimumLevel <= LogLevel.Information && !categoryWarning ? 1 : 0);
    }

    [Theory]
    [InlineData("issuer", 1, "validation")]
    [InlineData("audience", 1, "validation")]
    [InlineData("metadata", 1, "validation")]
    [InlineData("metadata-processing", 3, "validation")]
    [InlineData("profile", 7, "profile")]
    [InlineData("credential", 7, "credential")]
    public async Task Vendor_failures_are_closed_before_production_sinks_and_error_capture(
        string failure, int vendorEvent, string stage)
    {
        await using var issuer = await TenantAuthenticationIssuer.StartAsync();
        if (failure.StartsWith("metadata", StringComparison.Ordinal))
        {
            issuer.DiscoveryStatusCode = 503;
            issuer.DiscoveryResponse = ProviderCanary;
        }
        var payload = failure switch
        {
            "issuer" => issuer.Payload(("sub", ClaimCanary), ("iss", IssuerCanary)),
            "audience" => issuer.Payload(("sub", ClaimCanary), ("aud", AudienceCanary)),
            "profile" => issuer.Payload(("sub", ClaimCanary), ("user_id", ClaimCanary)),
            _ => issuer.Payload(("sub", ClaimCanary)),
        };
        var token = failure == "credential" ? "malformed." + ClaimCanary : issuer.Token(payload);

        // This separate host lifts only the exact-category ILogger rule. The
        // stock handler must actually emit the event being qualified below.
        using var control = new DiagnosticVendorProvider();
        await using (var app = await CreateHost(issuer, control: control,
            configurationAdapter: failure == "metadata-processing"))
        {
            using var client = app.GetTestClient();
            if (!failure.StartsWith("metadata", StringComparison.Ordinal))
            {
                using var valid = await Send(client, issuer.Token());
                valid.StatusCode.Should().Be(HttpStatusCode.OK,
                    "the control must admit a valid token from the same signing key");
            }
            using var response = await Send(client, token);
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }
        var actualEvent = control.Entries.Should().ContainSingle(entry =>
            entry.Category == TenantAuthenticationDiagnostics.VendorCategory
            && entry.Event.Id == vendorEvent).Subject;
        if (failure == "issuer")
        {
            actualEvent.Exception.Should().BeOfType<SecurityTokenInvalidIssuerException>()
                .Which.InvalidIssuer.Should().Be(IssuerCanary);
        }
        else if (failure == "audience")
        {
            actualEvent.Exception.Should().BeOfType<SecurityTokenInvalidAudienceException>()
                .Which.Message.Should().Contain("Audience validation failed");
        }
        else if (failure.StartsWith("metadata", StringComparison.Ordinal))
        {
            actualEvent.Exception.Should().NotBeNull();
            if (vendorEvent == 3)
            {
                actualEvent.Exception!.ToString().Should().Contain(issuer.MetadataAddress);
            }
            else
            {
                // BaseConfigurationManager reports no keys after its initial
                // retrieval failure; this is the actual production path.
                actualEvent.Exception.Should().BeOfType<SecurityTokenSignatureKeyNotFoundException>();
            }
            issuer.MetadataRequests.Should().BeGreaterThan(0);
        }
        else
        {
            actualEvent.Message.Should().Contain("Tenant credential refused.");
        }
        control.Entries.Should().Contain(entry =>
            entry.Category == typeof(TenantAuthenticationEvents).FullName
            && entry.Message.Contains("stage " + stage + " outcome rejected", StringComparison.Ordinal));

        await using var collector = await TenantAuthenticationDiagnosticCollector.StartAsync();
        var observed = new DiagnosticObservation();
        var metadataRequestsBeforeProduction = issuer.MetadataRequests;
        await using (var app = await CreateHost(issuer, observed: observed, endpoint: collector.Endpoint,
            configurationAdapter: failure == "metadata-processing"))
        {
            using var client = app.GetTestClient();
            using var response = await Send(client, token);
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            observed.Captures.Should().BeEmpty("credential failures are handled by JwtBearer, never L1");
            if (failure.StartsWith("metadata", StringComparison.Ordinal))
            {
                issuer.MetadataRequests.Should().BeGreaterThan(metadataRequestsBeforeProduction,
                    "the production composition must itself enter the failing metadata transport");
            }

            // Challenge Serilog itself as well as ILogger's earlier rule. This
            // reaches the actual root filter even if ILogger already removed
            // every real vendor event above, and checks every vendor level.
            var logger = app.Services.GetRequiredService<Serilog.ILogger>();
            logger.IsEnabled(LogEventLevel.Verbose).Should().BeTrue(
                "a minimum-level threshold must not make the all-level sink challenge vacuous");
            var exceptionCanary = new InvalidOperationException(ProviderCanary,
                new IOException(issuer.MetadataAddress));
            exceptionCanary.Data["private-token"] = token;
            exceptionCanary.Data["private-subject"] = ClaimCanary;
            foreach (var level in Enum.GetValues<LogEventLevel>())
            {
                logger.ForContext("SourceContext", TenantAuthenticationDiagnostics.VendorCategory)
                    .Write(level, exceptionCanary, "Vendor sink challenge {PrivateValue}", VendorSinkCanary);
            }
            logger.ForContext("SourceContext", TenantAuthenticationDiagnostics.VendorCategory + ".Unrelated")
                .Information("{AllowedDiagnostic}", AllowedLog);
            AllowedLoggerMessage(app.Services.GetRequiredService<ILoggerFactory>()
                .CreateLogger(TenantAuthenticationDiagnostics.VendorCategory + ".Unrelated"), AllowedLog + "-ILogger", null);
            using var allowedError = await client.GetAsync("/diagnostic/allowed-error");
            allowedError.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            observed.Captures.Should().ContainSingle().Which.Exception.Message.Should().Be(AllowedError);

            // A real exported outbound span is the positive control for the
            // suppressed discovery/JWKS HTTP diagnostics, using another URL.
            using var outbound = new HttpClient();
            using var canaryResponse = await outbound.GetAsync(issuer.CanaryAddress);
            canaryResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        IdentityModelEventSource.ShowPII.Should().BeFalse();
        IdentityModelEventSource.LogCompleteSecurityArtifact.Should().BeFalse();
        observed.Events.Should().NotBeEmpty();
        observed.Events.Should().NotContain(entry => TenantAuthenticationDiagnostics.IsVendorEvent(entry));
        var outcome = observed.Events.Should().ContainSingle(entry =>
            entry.Properties.ContainsKey("SourceContext")
            && entry.Properties["SourceContext"].ToString() == "\"" + typeof(TenantAuthenticationEvents).FullName + "\"")
            .Subject;
        outcome.Exception.Should().BeNull();
        outcome.MessageTemplate.Text.Should().Be("Tenant authentication stage {Stage} outcome {Reason}.");
        outcome.Properties["Stage"].Should().BeEquivalentTo(new ScalarValue(stage));
        outcome.Properties["Reason"].Should().BeEquivalentTo(new ScalarValue("rejected"));

        var logs = collector.Logs;
        logs.Should().Contain("Tenant authentication stage").And.Contain(stage).And.Contain("rejected")
            .And.Contain(AllowedLog).And.Contain(AllowedError);
        collector.Traces.Should().Contain(issuer.CanaryAddress,
            "the production OTLP trace exporter must actually deliver HTTP spans");
        var privateValues = new[] { token, ClaimCanary, IssuerCanary, AudienceCanary,
            ProviderCanary, VendorSinkCanary, issuer.MetadataAddress, issuer.Issuer + "/keys" };
        var structured = string.Join('\n', observed.Events.Select(entry => entry.ToString()
            + " " + string.Join(' ', entry.Properties.Select(property => property.ToString()))
            + " " + entry.Exception));
        var captures = string.Join('\n', observed.Captures.Select(capture =>
            capture.Exception.ToString() + " " + capture.Context));
        foreach (var value in privateValues)
        {
            structured.Should().NotContain(value);
            collector.AllPayloads.Should().NotContain(value);
            captures.Should().NotContain(value);
        }
        structured.Should().Contain(AllowedLog + "-ILogger").And.Contain(AllowedError);
    }
}
