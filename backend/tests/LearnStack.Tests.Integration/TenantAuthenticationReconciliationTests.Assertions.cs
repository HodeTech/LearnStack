using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using LearnStack.Api.Tenancy;
using Xunit;

namespace LearnStack.Tests.Integration;

public sealed partial class TenantAuthenticationReconciliationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Platform_host_headers_never_resolve_a_tenant_and_malformed_assertions_still_refuse(bool organization, bool malformed)
    {
        await using var issuer = await TenantAuthenticationIssuer.StartAsync();
        await using var host = await TenantAuthenticationReconciliationHost.StartAsync(issuer);
        using var request = new HttpRequestMessage(HttpMethod.Get, TenantAuthenticationReconciliationHost.PublicPath);
        request.Headers.Host = TenantAuthenticationReconciliationHost.PlatformHost;
        request.Headers.TryAddWithoutValidation(organization ? TenantAssertionMiddleware.OrganizationHeaderName
            : TenantAssertionMiddleware.TenantHeaderName,
            malformed ? "malformed" : (organization ? TenantAuthenticationIssuer.Organization : TenantAuthenticationIssuer.Tenant).ToString("D"));

        using var response = await host.Client.SendAsync(request);

        if (malformed)
            await AssertProblem(response, HttpStatusCode.BadRequest, "validation_failed");
        else
        {
            response.StatusCode.Should().Be(HttpStatusCode.OK, "the public observation probe can run without tenant authority");
            (await response.Content.ReadFromJsonAsync<ReconciliationObservation>())!.Resolved.Should().BeFalse();
        }
        host.ResolvedRequests.Should().ContainSingle().Which.Resolved.Should().BeFalse();
        host.Recorder.Unresolved.Should().Equal(organization ? TenantAssertionDimension.Organization : TenantAssertionDimension.Tenant);
        host.Recorder.Rejections.Should().BeEmpty();
        host.Recorder.NoTenantRefusals.Should().BeEmpty();
        host.Ports.Memberships.Should().BeEmpty();
        host.Ports.OrganizationChecks.Should().BeEmpty();
        issuer.MetadataRequests.Should().Be(0, "an assertion header is neither a credential nor resolution authority");
    }
}
