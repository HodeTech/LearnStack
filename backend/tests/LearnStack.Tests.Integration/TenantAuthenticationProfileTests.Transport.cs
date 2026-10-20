using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using LearnStack.Api.Authentication;
using LearnStack.SharedKernel.Hosting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.Configuration;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Xunit;

namespace LearnStack.Tests.Integration;

public sealed partial class TenantAuthenticationProfileTests
{
    [Fact]
    public async Task Profile_projects_the_token_validated_by_the_handler_even_if_the_request_header_changes()
    {
        await using var issuer = await TenantAuthenticationIssuer.StartAsync();
        await using var app = await CreateHost(issuer, options =>
        {
            var events = options.Events;
            options.Events = new JwtBearerEvents
            {
                OnMessageReceived = events.MessageReceived,
                OnAuthenticationFailed = events.AuthenticationFailed,
                OnTokenValidated = context =>
                {
                    context.Request.Headers.Authorization = "Bearer " + issuer.Token(
                        issuer.Payload(("sub", "header-substitution-never-validated")));
                    return events.TokenValidated(context);
                },
            };
        });
        using var client = app.GetTestClient();
        await AssertAccepted(client, issuer.Token(), issuer);
    }

    [Fact]
    public async Task Discovery_issuer_must_equal_the_configured_trust_root_even_with_a_matching_key()
    {
        await using var issuer = await TenantAuthenticationIssuer.StartAsync();
        issuer.DiscoveryResponse = JsonSerializer.Serialize(new
        {
            issuer = issuer.Issuer + "-hub",
            jwks_uri = issuer.Issuer + "/keys",
        });
        await using var app = await CreateHost(issuer);
        using var client = app.GetTestClient();
        await AssertRejected(client, issuer.Token(),
            "a valid configured-issuer payload cannot trust keys from mismatched discovery");
        issuer.MetadataRequests.Should().BeGreaterThan(0);
        issuer.JwksRequests.Should().BeGreaterThan(0,
            "the fixture supplies an otherwise valid key so issuer binding owns the refusal");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Discovery_cannot_move_key_retrieval_to_another_origin_or_realm(bool otherRealm)
    {
        await using var issuer = await TenantAuthenticationIssuer.StartAsync();
        await using var foreign = await TenantAuthenticationIssuer.StartAsync();
        issuer.DiscoveryResponse = JsonSerializer.Serialize(new
        {
            issuer = issuer.Issuer,
            jwks_uri = otherRealm ? issuer.Issuer + "-hub/keys" : foreign.Issuer + "/keys",
        });
        await using var app = await CreateHost(issuer);
        using var client = app.GetTestClient();
        await AssertRejected(client, issuer.Token(key: otherRealm ? issuer.CurrentKey : foreign.CurrentKey),
            "discovery cannot substitute a key origin outside the configured issuer realm");
        issuer.MetadataRequests.Should().BeGreaterThan(0);
        issuer.JwksRequests.Should().Be(0, "the other realm is refused before outbound I/O");
        foreign.JwksRequests.Should().Be(0, "another origin is refused before outbound I/O");
    }

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
    public async Task Warm_current_configuration_survives_an_observed_failed_manual_refresh_without_LKG_fallback()
    {
        await using var issuer = await TenantAuthenticationIssuer.StartAsync();
        var settings = TenantAuthenticationOptions.Create(issuer.Issuer, DeploymentMode.Development);
        var failedRetrieval = new TaskCompletionSource<IOException>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var app = await CreateHost(issuer, configureServices: services =>
            services.AddOptions<JwtBearerOptions>(TenantAuthenticationRegistration.Scheme)
                .Configure<TenantAuthenticationBackchannel>((options, backchannel) =>
                {
                    var original = (ConfigurationManager<OpenIdConnectConfiguration>)options.ConfigurationManager!;
                    // Observe and rethrow the exact production retriever's failure;
                    // the real stock manager retains control of its cache/refresh.
                    options.ConfigurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
                        settings.MetadataAddress, new OpenIdConnectConfigurationRetriever(),
                        new RefreshFailureDocumentRetriever(new TenantOidcDocumentRetriever(settings, backchannel), failedRetrieval),
                        new TenantOidcConfigurationValidator(settings))
                    {
                        AutomaticRefreshInterval = original.AutomaticRefreshInterval,
                        RefreshInterval = original.RefreshInterval,
                        UseLastKnownGoodConfiguration = original.UseLastKnownGoodConfiguration,
                    };
                }));
        using var client = app.GetTestClient();
        var token = issuer.Token();
        await AssertAccepted(client, token, issuer);
        var options = app.Services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(TenantAuthenticationRegistration.Scheme);
        var manager = (ConfigurationManager<OpenIdConnectConfiguration>)options.ConfigurationManager!;
        manager.UseLastKnownGoodConfiguration.Should().BeFalse();
        var current = await manager.GetConfigurationAsync();
        var requests = issuer.MetadataRequests;
        issuer.DiscoveryStatusCode = 503;

        // First manual refresh is immediately eligible under the stock manager.
        // This is no elapsed-time proof of the five-minute automatic schedule.
        manager.RequestRefresh();
        (await failedRetrieval.Task.WaitAsync(TimeSpan.FromSeconds(8))).Message
            .Should().Be("Tenant authentication metadata refused.");
        issuer.MetadataRequests.Should().BeGreaterThan(requests);
        (await manager.GetConfigurationAsync()).Should().BeSameAs(current);
        await AssertAccepted(client, token, issuer);
        await AssertRejected(client, issuer.Token(key: issuer.NewKey("unavailable-during-outage")),
            "a warmed current key set cannot validate an unavailable new signing key");
        await AssertAccepted(client, token, issuer);
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
        var refreshResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        issuer.JwksRelease = refreshResponse.Task;
        var rotation = new ConfigurationUpdateObservation(configuration =>
            configuration.SigningKeys.Any(key => key.KeyId == newKey.Kid));
        manager.ConfigurationEventHandler = rotation;
        // A cached stock manager refreshes in the background. Only this real
        // bearer request may request refresh: the observation never calls the
        // manager, provides configuration or changes its automatic schedule.
        try
        {
            using var response = await Send(client, "Bearer " + newToken);
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
                "the new key remains unavailable while the issuer holds the background JWKS response");
        }
        finally
        {
            refreshResponse.TrySetResult();
        }
        await rotation.Updated.Task.WaitAsync(TimeSpan.FromSeconds(8));
        issuer.JwksRelease = null;
        issuer.JwksRequests.Should().BeGreaterThan(initialJwksRequests,
            "the new-key token must drive the stock handler's discovery refresh");
        await AssertAccepted(client, newToken, issuer);
        await AssertAccepted(client, oldToken, issuer);

        issuer.Publish(newKey);
        // Respect the last on-demand refresh's public floor before requesting
        // the withdrawal snapshot. This delay is bounded and owns no fake time.
        await Task.Delay(TimeSpan.FromMilliseconds(1100));
        var withdrawal = new ConfigurationUpdateObservation(configuration =>
            configuration.SigningKeys.Count == 1 && configuration.SigningKeys.Single().KeyId == newKey.Kid);
        manager.ConfigurationEventHandler = withdrawal;
        manager.RequestRefresh();
        await withdrawal.Updated.Task.WaitAsync(TimeSpan.FromSeconds(8));
        await AssertAccepted(client, newToken, issuer);
        await AssertRejected(client, oldToken, "the old token remains unexpired but its key has been withdrawn");
        await AssertRejected(client, issuer.Token(key: newKey,
            header: TenantAuthenticationIssuer.Header(issuer.CurrentKey.Kid)),
            "a currently trusted signature cannot revive a withdrawn kid selector");
        await AssertAccepted(client, newToken, issuer);
        await AssertRejected(client, oldToken, "last-known-good fallback must not revive the observed withdrawal");
    }

    private sealed class RefreshFailureDocumentRetriever(
        IDocumentRetriever inner, TaskCompletionSource<IOException> failed) : IDocumentRetriever
    {
        public async Task<string> GetDocumentAsync(string address, CancellationToken cancel)
        {
            try { return await inner.GetDocumentAsync(address, cancel); }
            catch (IOException exception)
            {
                failed.TrySetResult(exception);
                throw;
            }
        }
    }

    private sealed class ConfigurationUpdateObservation(Func<OpenIdConnectConfiguration, bool> observed)
        : IConfigurationEventHandler<OpenIdConnectConfiguration>
    {
        internal TaskCompletionSource Updated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ConfigurationEventHandlerResult<OpenIdConnectConfiguration>> BeforeRetrieveAsync(
            string metadataAddress, CancellationToken cancellationToken = default) =>
            Task.FromResult(ConfigurationEventHandlerResult<OpenIdConnectConfiguration>.NoResult);

        public Task AfterUpdateAsync(string metadataAddress, OpenIdConnectConfiguration configuration,
            CancellationToken cancellationToken = default)
        {
            if (observed(configuration)) Updated.TrySetResult();
            return Task.CompletedTask;
        }
    }
}
