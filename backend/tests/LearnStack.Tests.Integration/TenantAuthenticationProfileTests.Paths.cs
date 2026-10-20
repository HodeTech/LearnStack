using System.Net;
using FluentAssertions;
using LearnStack.Api.Authentication;
using LearnStack.SharedKernel.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LearnStack.Tests.Integration;

public sealed partial class TenantAuthenticationProfileTests
{
    [Fact]
    public async Task Metadata_paths_require_exact_realm_segments_before_any_outbound_request()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
        await using var server = builder.Build();
        var requests = 0;
        // Every path succeeds, including escaped paths. A downstream 404 cannot
        // accidentally satisfy the refusal proof or conceal a network request.
        server.Run(context =>
        {
            Interlocked.Increment(ref requests);
            return context.Response.WriteAsync("{\"keys\":[]}", context.RequestAborted);
        });
        await server.StartAsync();
        var origin = server.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var settings = TenantAuthenticationOptions.Create(
            origin + "/auth%20portal/realms/learnstack", DeploymentMode.Development);
        using var backchannel = new TenantAuthenticationBackchannel();
        var retriever = new TenantOidcDocumentRetriever(settings, backchannel);

        foreach (var suffix in new[] { "/keys", "/protocol/openid-connect/certs", "/key%20set", "/keys.v2.json" })
        {
            var address = settings.Issuer + suffix;
            (await retriever.GetDocumentAsync(address, CancellationToken.None))
                .Should().Be("{\"keys\":[]}");
        }
        requests.Should().Be(4, "private loopback transport and harmless escaped path data remain supported");

        foreach (var address in new[]
        {
            settings.Issuer,
            settings.Issuer + "-hub/keys",
            origin + "/other/realms/learnstack/keys",
            settings.Issuer + "/..%2flearnstack-hub/keys",
            settings.Issuer + "/..%2Flearnstack-hub/keys",
            settings.Issuer + "/%2e%2e%2flearnstack-hub/keys",
            settings.Issuer + "/..%5clearnstack-hub/keys",
            settings.Issuer + "/%2e%2e/keys",
            settings.Issuer + "/%252e%252e%252flearnstack-hub/keys",
            settings.Issuer + "/keys%252fother",
            settings.Issuer + "//keys",
            settings.Issuer + "/keys?private=query",
            settings.Issuer + "/keys#fragment",
        })
        {
            var read = () => retriever.GetDocumentAsync(address, CancellationToken.None);
            await read.Should().ThrowAsync<IOException>().WithMessage("Tenant authentication metadata refused.");
            requests.Should().Be(4, "an ambiguous or non-descendant address is refused before I/O: " + address);
        }
    }
}
