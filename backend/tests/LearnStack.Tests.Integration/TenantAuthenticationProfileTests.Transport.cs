using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using LearnStack.Api.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Xunit;

namespace LearnStack.Tests.Integration;

public sealed partial class TenantAuthenticationProfileTests
{
    [Fact]
    public async Task Credential_free_unknown_and_infrastructure_requests_never_fetch_metadata()
    {
        await using var issuer = await TenantAuthenticationIssuer.StartAsync();
        issuer.DiscoveryStatusCode = 503;
        await using var app = await CreateHost(issuer);
        using var client = app.GetTestClient();
        issuer.MetadataRequests.Should().Be(0, "startup validates options without network I/O");
        var token = issuer.Token();

        using (var response = await Send(client, null))
            response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        using (var response = await Send(client, null, "/api/v1/auth-profile?access_token=" + token))
            response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        using (var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth-profile"))
        {
            request.Headers.Host = "known.test";
            request.Headers.TryAddWithoutValidation("Cookie", "access_token=" + token);
            request.Content = new StringContent("{\"access_token\":\"" + token + "\"}", Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(request);
            response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        }

        foreach (var path in new[] { "/health", "/health/ready", "/openapi/v1.json", "/api/internal/tenants", "/" })
        {
            using var response = await Send(client, "Bearer " + token, path);
            response.StatusCode.Should().Be(HttpStatusCode.NoContent, path);
        }
        using (var response = await Send(client, "Bearer " + token, host: "unknown.test"))
            response.StatusCode.Should().Be(HttpStatusCode.NoContent,
                "the isolated handler requires the independently classified host feature");
        issuer.MetadataRequests.Should().Be(0);
        issuer.JwksRequests.Should().Be(0);

        await AssertRejected(client, token, "present credentials cannot become absence when the issuer is unavailable");
        issuer.MetadataRequests.Should().BeGreaterThan(0);
        using (var response = await Send(client, null))
            response.StatusCode.Should().Be(HttpStatusCode.NoContent,
                "a previous failed credential does not make a later anonymous request depend on discovery");
    }

    [Fact]
    public async Task Credentials_require_one_bearer_value_and_enforce_sixteen_KiB_token_bound()
    {
        await using var issuer = await TenantAuthenticationIssuer.StartAsync();
        await using var app = await CreateHost(issuer);
        using var client = app.GetTestClient();
        var token = issuer.Token();
        // HttpClient drops empty values and normalizes authentication whitespace.
        // Supply the actual server header bytes to exercise this grammar rather
        // than accidentally testing HttpClient's permissive normalization.
        foreach (var value in new[] { "", "Bearer", "Bearer ", "Basic " + token, "Bearer  " + token,
            "Bearer\t" + token, "Bearer " + token + " ", "Bearer " + token + ",Bearer " + token,
            "Bearer " + token + ", " + token, "Bearer one.two", "Bearer one.two.three.four" })
        {
            var response = await app.GetTestServer().SendAsync(context =>
            {
                context.Request.Path = "/api/v1/auth-profile";
                context.Request.Headers.Host = "known.test";
                context.Request.Headers.Authorization = value;
            });
            response.Response.StatusCode.Should().Be(401, "malformed credential " + value[..Math.Min(18, value.Length)]);
        }
        var repeated = await app.GetTestServer().SendAsync(context =>
        {
            context.Request.Path = "/api/v1/auth-profile";
            context.Request.Headers.Host = "known.test";
            context.Request.Headers.Authorization = new StringValues(["Bearer " + token, "Bearer " + token]);
        });
        repeated.Response.StatusCode.Should().Be(401, "repeated equal credentials are still repeated");
        issuer.MetadataRequests.Should().Be(0, "malformed compact credentials fail before discovery");

        using (var response = await Send(client, "bEaReR " + token))
            response.StatusCode.Should().Be(HttpStatusCode.OK, "HTTP authentication scheme names are case insensitive");

        // A few legal JSON whitespace bytes in the protected header permit an
        // exact compact-token byte boundary without weakening any header field.
        string? atLimit = null;
        string? overLimit = null;
        for (var whitespace = 0; whitespace < 3 && atLimit is null; whitespace++)
        {
            var header = TenantAuthenticationIssuer.Header(issuer.CurrentKey.Kid);
            header = header[..^1] + new string(' ', whitespace) + "}";
            for (var padding = 11000; padding < 12000; padding++)
            {
                var candidate = issuer.Token(issuer.Payload(("padding", new string('p', padding))), header);
                if (candidate.Length == TenantAuthenticationOptions.MaxTokenBytes)
                {
                    atLimit = candidate;
                    overLimit = issuer.Token(issuer.Payload(("padding", new string('p', padding + 1))), header);
                    break;
                }
                if (candidate.Length > TenantAuthenticationOptions.MaxTokenBytes) break;
            }
        }
        atLimit.Should().NotBeNull("the fixture must exercise the actual exact maximum, not a smaller planted constant");
        Encoding.UTF8.GetByteCount(atLimit!).Should().Be(16 * 1024);
        await AssertAccepted(client, atLimit!, issuer);
        overLimit!.Length.Should().BeGreaterThan(TenantAuthenticationOptions.MaxTokenBytes);
        await AssertRejected(client, overLimit, "an otherwise valid signed token beyond the configured maximum");
        await AssertAccepted(client, token, issuer);
    }

    [Fact]
    public async Task Discovery_and_JWKS_use_finite_nonredirecting_byte_bounded_production_transport()
    {
        await using var issuer = await TenantAuthenticationIssuer.StartAsync();
        // A document at the actual byte cap remains valid JSON and authenticates.
        var document = issuer.DiscoveryDocument;
        issuer.DiscoveryResponse = document + new string(' ', TenantAuthenticationBackchannel.MaximumDocumentBytes
            - Encoding.UTF8.GetByteCount(document));
        await using (var app = await CreateHost(issuer))
        using (var client = app.GetTestClient())
            await AssertAccepted(client, issuer.Token(), issuer);

        issuer.DiscoveryResponse += " ";
        await using (var app = await CreateHost(issuer))
        using (var client = app.GetTestClient())
            await AssertRejected(client, issuer.Token(), "metadata one byte beyond its actual cap");

        issuer.DiscoveryResponse = null;
        // Multi-byte characters also count against the byte cap when the server
        // streams the response without a Content-Length header.
        var validKeys = JsonSerializer.Serialize(new { keys = new[] { issuer.CurrentKey.PublicJwk } });
        issuer.JwksResponse = validKeys[..^1] + ",\"padding\":\"" + new string('é', 140000) + "\"}";
        await using (var app = await CreateHost(issuer))
        using (var client = app.GetTestClient())
            await AssertRejected(client, issuer.Token(), "oversized streamed JWKS");

        issuer.JwksResponse = null;
        issuer.DiscoveryStatusCode = 302;
        issuer.DiscoveryRedirect = issuer.CanaryAddress;
        await using (var app = await CreateHost(issuer))
        using (var client = app.GetTestClient())
            await AssertRejected(client, issuer.Token(), "discovery redirects are refused");
        issuer.CanaryRequests.Should().Be(0);

        issuer.DiscoveryStatusCode = 200;
        issuer.DiscoveryRedirect = null;
        issuer.JwksStatusCode = 302;
        issuer.JwksRedirect = issuer.CanaryAddress;
        await using (var app = await CreateHost(issuer))
        using (var client = app.GetTestClient())
            await AssertRejected(client, issuer.Token(), "JWKS redirects are refused");
        issuer.CanaryRequests.Should().Be(0);

        issuer.JwksStatusCode = 200;
        issuer.JwksRedirect = null;
        issuer.DiscoveryDelay = TimeSpan.FromSeconds(10);
        var elapsed = Stopwatch.StartNew();
        await using (var app = await CreateHost(issuer))
        using (var client = app.GetTestClient())
            await AssertRejected(client, issuer.Token(), "five-second production metadata deadline");
        elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(8));
        issuer.DiscoveryDelay = TimeSpan.Zero;
        await using (var app = await CreateHost(issuer))
        using (var client = app.GetTestClient())
            await AssertAccepted(client, issuer.Token(), issuer);
    }

    [Fact]
    public async Task Real_configuration_manager_refreshes_new_key_and_never_reaccepts_withdrawn_unexpired_key()
    {
        await using var issuer = await TenantAuthenticationIssuer.StartAsync();
        await using var app = await CreateHost(issuer, options =>
        {
            // The stock manager's public minimum is one second. Its automatic
            // refresh minimum remains five minutes; no fake manager or private
            // field mutation participates in either key transition.
            var manager = (ConfigurationManager<OpenIdConnectConfiguration>)options.ConfigurationManager!;
            manager.RefreshInterval = TimeSpan.FromSeconds(1);
        });
        using var client = app.GetTestClient();
        var oldToken = issuer.Token();
        await AssertAccepted(client, oldToken, issuer);
        var options = app.Services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(TenantAuthenticationRegistration.Scheme);
        var manager = options.ConfigurationManager.Should().BeOfType<ConfigurationManager<OpenIdConnectConfiguration>>().Subject;
        manager.UseLastKnownGoodConfiguration.Should().BeFalse();
        manager.AutomaticRefreshInterval.Should().Be(TimeSpan.FromMinutes(5));

        var newKey = issuer.NewKey("rotated-key");
        issuer.Publish(issuer.CurrentKey, newKey);
        var newToken = issuer.Token(key: newKey);
        var initialJwksRequests = issuer.JwksRequests;
        using (var response = await Send(client, "Bearer " + newToken))
        {
            response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.Unauthorized);
        }
        await WaitForConfiguration(manager, configuration =>
            configuration.SigningKeys.Any(key => key.KeyId == newKey.Kid));
        issuer.JwksRequests.Should().BeGreaterThan(initialJwksRequests,
            "the new-key token must drive the stock handler's discovery refresh");
        await AssertAccepted(client, newToken, issuer);
        await AssertAccepted(client, oldToken, issuer);

        issuer.Publish(newKey);
        // Respect the last on-demand refresh's public floor before requesting
        // the withdrawal snapshot. This delay is bounded and owns no fake time.
        await Task.Delay(TimeSpan.FromMilliseconds(1100));
        manager.RequestRefresh();
        await WaitForConfiguration(manager, configuration =>
            configuration.SigningKeys.Count == 1 && configuration.SigningKeys.Single().KeyId == newKey.Kid);
        await AssertAccepted(client, newToken, issuer);
        await AssertRejected(client, oldToken, "the old token remains unexpired but its key has been withdrawn");
        await AssertAccepted(client, newToken, issuer);
        await AssertRejected(client, oldToken, "last-known-good fallback must not revive the observed withdrawal");
    }

    private static async Task WaitForConfiguration(ConfigurationManager<OpenIdConnectConfiguration> manager,
        Func<OpenIdConnectConfiguration, bool> observed)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (true)
        {
            var configuration = await manager.GetConfigurationAsync(timeout.Token);
            if (observed(configuration)) return;
            await Task.Delay(TimeSpan.FromMilliseconds(25), timeout.Token);
        }
    }
}
