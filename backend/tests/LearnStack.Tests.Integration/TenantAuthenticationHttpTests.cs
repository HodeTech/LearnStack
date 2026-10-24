using System.Net;
using System.Text.Json;
using FluentAssertions;
using LearnStack.Api.Authentication;
using LearnStack.Api.Common;
using LearnStack.Api.Tenancy;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace LearnStack.Tests.Integration;

/// <summary>Actual production HTTP activation, without a fabricated principal or alternate pipeline.</summary>
[Collection(TenantAuthenticationDiagnosticsTestGroup.Name)]
public sealed class TenantAuthenticationHttpTests
{
    [Fact]
    public async Task Api_Registers_Only_The_Tenant_Realm_Authority()
    {
        await using var issuer = await TenantAuthenticationIssuer.StartAsync();
        await using var factory = new TenantAuthenticationApiFactory(issuer);
        var schemes = await factory.Services.GetRequiredService<IAuthenticationSchemeProvider>().GetAllSchemesAsync();
        schemes.Select(scheme => scheme.Name).Should().Equal(TenantAuthenticationRegistration.Scheme);
        schemes.Single().HandlerType.Should().Be<JwtBearerHandler>();
        var options = factory.Services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(TenantAuthenticationRegistration.Scheme);
        options.Authority.Should().Be(issuer.Issuer);
        options.MetadataAddress.Should().Be(issuer.MetadataAddress);
        issuer.MetadataRequests.Should().Be(0, "the production registration does not eagerly contact an issuer");

        using var client = factory.CreateClient();
        using var response = await TenantAuthenticationHttpRequest.Send(client,
            TenantAuthenticationReconciliationHost.TenantHost, token: issuer.Token(issuer.Payload(
                ("iss", issuer.Issuer.Replace("/realms/learnstack", "/realms/learnstack-hub", StringComparison.Ordinal)))));
        await Problem(response, HttpStatusCode.Unauthorized, "unauthorized");
        BareBearer(response);
        issuer.MetadataRequests.Should().BeGreaterThan(0, "the trusted signing key must be obtained before rejecting the foreign issuer");
        issuer.JwksRequests.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Backend_RequiresJwt_OnAllAuthenticatedRoutes()
    {
        await using var issuer = await TenantAuthenticationIssuer.StartAsync();
        await using var factory = new TenantAuthenticationApiFactory(issuer);
        using var client = factory.CreateClient();
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText == "api/v1/account/site").ToArray();
        endpoints.Should().ContainSingle("the non-public production route must actually exist");
        endpoints.Single().Metadata.GetOrderedMetadata<IAuthorizeData>().Should().NotBeEmpty();
        endpoints.Single().Metadata.GetMetadata<IAllowAnonymous>().Should().BeNull();
        endpoints.Single().Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Should().Equal("GET");

        using var unknown = await TenantAuthenticationHttpRequest.Send(client,
            TenantAuthenticationReconciliationHost.UnknownHost);
        await Problem(unknown, HttpStatusCode.NotFound, "not_found");
        var masked = await WithoutCorrelation(unknown);
        foreach (var host in new[] { TenantAuthenticationReconciliationHost.TenantHost,
                     TenantAuthenticationReconciliationHost.OrganizationHost,
                     TenantAuthenticationReconciliationHost.PlatformHost })
        {
            using var response = await TenantAuthenticationHttpRequest.Send(client, host);
            await Problem(response, HttpStatusCode.NotFound, "not_found");
            response.Headers.WwwAuthenticate.Should().BeEmpty();
            (await WithoutCorrelation(response)).Should().Be(masked,
                "every protected absent-credential refusal is indistinguishable from the unknown-host result");
        }
        issuer.MetadataRequests.Should().Be(0);
    }

    [Fact]
    public async Task Authorization_result_handler_masks_only_the_closed_classified_surface_and_preserves_default_challenges()
    {
        await using var issuer = await TenantAuthenticationIssuer.StartAsync();
        await using var factory = new TenantAuthenticationApiFactory(issuer);
        using var client = factory.CreateClient();
        var account = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Single(endpoint => endpoint.RoutePattern.RawText == "api/v1/account/site");
        await using var scope = factory.Services.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<IAuthorizationMiddlewareResultHandler>();
        handler.Should().BeOfType<TenantAuthorizationResultHandler>();
        var policy = new AuthorizationPolicyBuilder(TenantAuthenticationRegistration.Scheme).RequireAuthenticatedUser().Build();
        var protectedContext = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        protectedContext.Request.Path = TenantAuthenticationHttpRequest.AccountPath;
        protectedContext.SetEndpoint(account);
        protectedContext.Features.Set(HostClassification.Platform(TenantAuthenticationReconciliationHost.PlatformHost));
        await handler.HandleAsync(_ => throw new InvalidOperationException("A challenge cannot dispatch the endpoint."),
            protectedContext, policy, PolicyAuthorizationResult.Challenge());
        protectedContext.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        protectedContext.Response.Headers.WWWAuthenticate.Should().BeEmpty();

        await using var forbiddenScope = factory.Services.CreateAsyncScope();
        var forbidden = new DefaultHttpContext { RequestServices = forbiddenScope.ServiceProvider };
        forbidden.Request.Path = TenantAuthenticationHttpRequest.AccountPath;
        forbidden.SetEndpoint(account);
        forbidden.Features.Set(HostClassification.Platform(TenantAuthenticationReconciliationHost.PlatformHost));
        await handler.HandleAsync(_ => throw new InvalidOperationException("A forbidden request cannot dispatch."),
            forbidden, policy, PolicyAuthorizationResult.Forbid());
        forbidden.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden,
            "only a missing-credential challenge is masked; an authorization refusal retains 403");
        forbidden.Response.Headers.WWWAuthenticate.Should().BeEmpty();

        var internalContext = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        internalContext.Request.Path = "/api/internal/authorization-control";
        await handler.HandleAsync(_ => throw new InvalidOperationException("A challenge cannot dispatch the endpoint."),
            internalContext, policy, PolicyAuthorizationResult.Challenge());
        internalContext.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized,
            "outside the classified AccountSite surface the stock authorization challenge remains active");
        internalContext.Response.Headers.WWWAuthenticate.ToString().Should().Be("Bearer");
        issuer.MetadataRequests.Should().Be(0);
    }

    [Fact]
    public async Task Authentication_Middleware_Order_Is_Enforced()
    {
        TenantAuthenticationPipelineProof.AssertProductionOrderAndPlantedReorders();
        await using var issuer = await TenantAuthenticationIssuer.StartAsync();
        await using var factory = new TenantAuthenticationApiFactory(issuer);
        using var client = factory.CreateClient();

        using var oversized = new HttpRequestMessage(HttpMethod.Get, TenantAuthenticationHttpRequest.AccountPath);
        oversized.Headers.Host = TenantAuthenticationReconciliationHost.UnknownHost;
        oversized.Headers.TryAddWithoutValidation("Authorization", "Bearer " + issuer.Token());
        oversized.Content = new ByteArrayContent(new byte[checked((int)RequestBodyLimit.MaxBytes + 1)]);
        using var bodyRefusal = await client.SendAsync(oversized);
        await Problem(bodyRefusal, HttpStatusCode.RequestEntityTooLarge, "payload_too_large");
        factory.Ports.HostLookups.Should().BeEmpty("the body bound precedes host lookup and authentication");
        issuer.MetadataRequests.Should().Be(0);

        using var unknown = await TenantAuthenticationHttpRequest.Send(client,
            TenantAuthenticationReconciliationHost.UnknownHost, token: issuer.Token());
        await Problem(unknown, HttpStatusCode.NotFound, "not_found");
        issuer.MetadataRequests.Should().Be(0, "host classification precedes authentication");

        using var invalid = await TenantAuthenticationHttpRequest.Send(client,
            TenantAuthenticationReconciliationHost.OrganizationHost, token: issuer.Token(issuer.Payload(("typ", "ID"))),
            tenantAssertion: "not-a-guid");
        await Problem(invalid, HttpStatusCode.Unauthorized, "unauthorized");
        factory.Ports.Memberships.Should().BeEmpty("credential admission precedes tenant resolution");
        factory.Recorder!.Rejections.Should().BeEmpty("invalid credentials cannot reach assertion recording");

        using var mismatch = await TenantAuthenticationHttpRequest.Send(client,
            TenantAuthenticationReconciliationHost.OrganizationHost, token: issuer.Token(issuer.Payload(
                ("tenant_id", TenantAuthenticationReconciliationHost.OtherTenant.ToString("D")))),
            tenantAssertion: "not-a-guid");
        await Problem(mismatch, HttpStatusCode.NotFound, "tenant_mismatch");
        factory.Recorder!.Rejections.Should().ContainSingle().Which.Source.Should().Be(TenantAssertionSource.JwtClaim,
            "validated claims reach the resolver before malformed header assertions");
        factory.Ports.Memberships.Should().BeEmpty("claim disagreement precedes membership I/O");
        issuer.MetadataRequests.Should().Be(1, "real authentication is activated in the production pipeline");
    }

    [Theory]
    [InlineData("not-a-token")]
    [InlineData("Bearer")]
    [InlineData("Basic dXNlcjpwYXNz")]
    [InlineData("Bearer a.b.c, Bearer a.b.c")]
    public async Task Invalid_present_credentials_on_actual_public_routes_never_become_anonymous(string authorization)
    {
        await using var issuer = await TenantAuthenticationIssuer.StartAsync();
        await using var factory = new TenantAuthenticationApiFactory(issuer);
        using var client = factory.CreateClient();
        using var response = await TenantAuthenticationHttpRequest.Send(client,
            TenantAuthenticationReconciliationHost.TenantHost, TenantAuthenticationHttpRequest.PublicPath,
            authorization: authorization);

        await Problem(response, HttpStatusCode.Unauthorized, "unauthorized");
        BareBearer(response);
        factory.Recorder!.Rejections.Should().BeEmpty();
        issuer.MetadataRequests.Should().Be(0, "malformed credentials need no discovery");
    }

    [Fact]
    public async Task Invalid_credential_on_a_classified_unmapped_path_keeps_no_store_and_never_resolves()
    {
        await using var issuer = await TenantAuthenticationIssuer.StartAsync();
        await using var factory = new TenantAuthenticationApiFactory(issuer);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/unmapped-credential-control");
        request.Headers.Host = TenantAuthenticationReconciliationHost.TenantHost;
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer malformed");
        using var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        NoStore(response);
        BareBearer(response);
        await Problem(response, HttpStatusCode.Unauthorized, "unauthorized");
        factory.Ports.Memberships.Should().BeEmpty();
        factory.Recorder!.Rejections.Should().BeEmpty();
        issuer.MetadataRequests.Should().Be(0);
    }

    [Fact]
    public async Task A_trusted_signature_with_an_invalid_profile_is_401_on_an_actual_public_route()
    {
        await using var issuer = await TenantAuthenticationIssuer.StartAsync();
        await using var factory = new TenantAuthenticationApiFactory(issuer);
        using var client = factory.CreateClient();
        using var response = await TenantAuthenticationHttpRequest.Send(client,
            TenantAuthenticationReconciliationHost.TenantHost, TenantAuthenticationHttpRequest.PublicPath,
            token: issuer.Token(issuer.Payload(("typ", "ID"))));

        await Problem(response, HttpStatusCode.Unauthorized, "unauthorized");
        BareBearer(response);
        factory.Recorder.Rejections.Should().BeEmpty();
        factory.Ports.Memberships.Should().BeEmpty();
        issuer.MetadataRequests.Should().BeGreaterThan(0, "the real handler validates the trusted signing key");
        issuer.JwksRequests.Should().BeGreaterThan(0);
    }

    [Theory]
    [InlineData(TenantAuthenticationHttpRequest.PublicPath)]
    [InlineData(TenantAuthenticationHttpRequest.AccountPath)]
    public async Task Discovery_failure_on_actual_public_and_account_routes_is_401_and_never_anonymous(string path)
    {
        await using var issuer = await TenantAuthenticationIssuer.StartAsync();
        issuer.DiscoveryStatusCode = 503;
        await using var factory = new TenantAuthenticationApiFactory(issuer);
        using var client = factory.CreateClient();
        using var response = await TenantAuthenticationHttpRequest.Send(client,
            TenantAuthenticationReconciliationHost.TenantHost, path,
            token: issuer.Token());

        await Problem(response, HttpStatusCode.Unauthorized, "unauthorized");
        BareBearer(response);
        issuer.MetadataRequests.Should().BeGreaterThan(0);
        factory.Recorder!.Rejections.Should().BeEmpty();
        factory.Ports.Memberships.Should().BeEmpty("discovery refusal precedes membership and controller dispatch");
    }

    [Theory]
    [InlineData("/healthz", HttpStatusCode.OK)]
    [InlineData("/openapi/v1.json", HttpStatusCode.OK)]
    [InlineData("/api/internal/unmapped-auth-control", HttpStatusCode.NotFound)]
    public async Task Infrastructure_and_internal_paths_ignore_bad_credentials_without_discovery(string path,
        HttpStatusCode status)
    {
        await using var issuer = await TenantAuthenticationIssuer.StartAsync();
        issuer.DiscoveryStatusCode = 503;
        await using var factory = new TenantAuthenticationApiFactory(issuer);
        using var client = factory.CreateClient();
        using var response = await TenantAuthenticationHttpRequest.Send(client,
            TenantAuthenticationReconciliationHost.UnknownHost, path, authorization: "Bearer definitely-not-a-token",
            tenantAssertion: "not-a-guid", organizationAssertion: "also-not-a-guid");

        response.StatusCode.Should().Be(status);
        response.Headers.WwwAuthenticate.Should().BeEmpty();
        issuer.MetadataRequests.Should().Be(0);
        issuer.JwksRequests.Should().Be(0);
        factory.Ports.HostLookups.Should().BeEmpty();
        factory.Recorder!.Rejections.Should().BeEmpty();

        // A malformed token and unknown host independently avoid discovery.
        // Remove both escape hatches before qualifying the route exclusion.
        var token = issuer.Token();
        using var wellFormed = await TenantAuthenticationHttpRequest.Send(client,
            TenantAuthenticationReconciliationHost.TenantHost, path, token: token,
            tenantAssertion: "not-a-guid", organizationAssertion: "also-not-a-guid");
        wellFormed.StatusCode.Should().Be(status);
        wellFormed.Headers.WwwAuthenticate.Should().BeEmpty();
        issuer.MetadataRequests.Should().Be(0);
        issuer.JwksRequests.Should().Be(0);
        factory.Ports.HostLookups.Should().BeEmpty();
        factory.Recorder!.Rejections.Should().BeEmpty();

        // The same signed credential does reach the actual discovery transport
        // on an admitted public route, rather than passing through an inert spy.
        using var positive = await TenantAuthenticationHttpRequest.Send(client,
            TenantAuthenticationReconciliationHost.TenantHost,
            TenantAuthenticationHttpRequest.PublicPath, token: token);
        await Problem(positive, HttpStatusCode.Unauthorized, "unauthorized");
        issuer.MetadataRequests.Should().BeGreaterThan(0);
    }

    internal static async Task Problem(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        await TenantAuthenticationReconciliationTests.AssertProblem(response, status, code);
        NoStore(response);
    }

    internal static void NoStore(HttpResponseMessage response)
    {
        (response.Headers.CacheControl?.NoStore).Should().BeTrue();
        response.Headers.ETag.Should().BeNull();
        response.Content.Headers.LastModified.Should().BeNull();
    }

    private static void BareBearer(HttpResponseMessage response) =>
        response.Headers.WwwAuthenticate.Should().ContainSingle().Which.ToString().Should().Be("Bearer");

    private static async Task<string> WithoutCorrelation(HttpResponseMessage response)
    {
        var problem = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(await response.Content.ReadAsStringAsync())!;
        problem.Remove("correlationId");
        return JsonSerializer.Serialize(problem);
    }
}
