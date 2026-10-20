using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using LearnStack.Api.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace LearnStack.Tests.Integration;

[Collection(TenantAuthenticationDiagnosticsTestGroup.Name)]
public sealed class TenantAuthenticationStartupTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Production_Program_refuses_missing_or_empty_issuer_before_starting_dependencies(bool emptyIssuer)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            await using var factory = new MissingIssuerFactory(port, emptyIssuer);
            Action start = () => { using var client = factory.CreateClient(); };

            var failure = start.Should().Throw<InvalidOperationException>().Which;
            failure.Message.Should().StartWith(TenantAuthenticationOptions.IssuerConfigurationKey);
            failure.InnerException.Should().BeNull();
            failure.ToString().Should().NotContain("startup-password-canary");
            listener.Pending().Should().BeFalse(
                "Program must refuse issuer configuration before its hosted workers can connect to either database");
        }
        finally
        {
            listener.Stop();
        }
    }

    private sealed class MissingIssuerFactory(int port, bool emptyIssuer) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(Environments.Production);
            // Minimal hosting reads these before ConfigureAppConfiguration runs.
            // UseSetting supplies the actual Program's bootstrap configuration;
            // a null value masks an ambient issuer just as an absent key does.
            builder.UseSetting("Deployment:Mode", "SelfHostedOnline");
            builder.UseSetting("Tenancy:PlatformHosts:0", "localhost");
            builder.UseSetting("ConnectionStrings:Default", Connection("learnstack_app"));
            builder.UseSetting("ConnectionStrings:OutboxDispatcher", Connection("learnstack_outbox_admin"));
            builder.UseSetting(TenantAuthenticationOptions.IssuerConfigurationKey, emptyIssuer ? "" : null);
        }

        // Synthetic redaction canary, never a usable database credential.
        private string Connection(string role) =>
            $"Host=127.0.0.1;Port={port};Database=learnstack;Username={role};Password=startup-password-canary;Timeout=1"; // leakwatch:ignore
    }
}
