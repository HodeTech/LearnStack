using System.Globalization;
using System.Net;
using FluentAssertions;
using Xunit;

namespace LearnStack.Tests.Integration;

public sealed partial class TenantAuthenticationReconciliationTests
{
    [Fact]
    public async Task Subject_exhaustion_stops_recording_survives_token_and_tenant_rotation_and_leaves_B_independent()
    {
        await using var issuer = await TenantAuthenticationIssuer.StartAsync();
        await using var host = await TenantAuthenticationReconciliationHost.StartAsync(issuer, subjectPermitLimit: 3);
        for (var index = 0; index < 3; index++)
        {
            var payload = TenantAuthenticationIssuer.AddProperty(
                issuer.Without("tenant_id", "organization_id"), "tenant_id",
                TenantAuthenticationReconciliationHost.OtherTenant.ToString("D"));
            payload = TenantAuthenticationIssuer.AddProperty(payload, "jti", "rotating-jti-" + index.ToString(CultureInfo.InvariantCulture));
            using var admitted = await host.SendAsync(issuer.Token(payload));
            await AssertProblem(admitted, HttpStatusCode.NotFound, "tenant_mismatch");
        }
        host.Recorder.Rejections.Should().HaveCount(3,
            "each admitted authenticated mismatch reaches the recorder separately");

        // Same signed issuer/sub, but new jti and a now-matching tenant selector.
        // Rotation must reach the exhausted partition before resolution/audit.
        var rotated = TenantAuthenticationIssuer.AddProperty(issuer.Without("organization_id"), "jti", "fresh-jti");
        using var rejected = await host.SendAsync(issuer.Token(rotated));
        await AssertProblem(rejected, HttpStatusCode.TooManyRequests, "rate_limited");
        (rejected.Headers.RetryAfter?.Delta).Should().BeGreaterThan(TimeSpan.Zero);
        host.Recorder.Rejections.Should().HaveCount(3, "quota refusal does no downstream durable recording work");
        host.ResolvedRequests.Should().BeEmpty();

        var subjectB = TenantAuthenticationIssuer.AddProperty(issuer.Without("organization_id", "sub"), "sub", "independent-subject-B");
        using var independent = await host.SendAsync(issuer.Token(subjectB));
        independent.StatusCode.Should().Be(HttpStatusCode.OK,
            "B shares the same physical peer and visitor; seven total requests are below both ingress floors");
        using var anonymous = await host.SendAsync(path: TenantAuthenticationReconciliationHost.PublicPath);
        anonymous.StatusCode.Should().Be(HttpStatusCode.OK, "an exhausted subject grants no authority over anonymous admission");

        using var stillExhausted = await host.SendAsync(issuer.Token());
        await AssertProblem(stillExhausted, HttpStatusCode.TooManyRequests, "rate_limited");
        host.Recorder.Rejections.Should().HaveCount(3);
        host.Ports.Memberships.Should().BeEmpty("the rotated organization selector is stopped before membership work");
    }

    [Fact]
    public async Task A_claimless_token_rejected_by_subject_admission_emits_no_claimless_diagnostic()
    {
        await using var issuer = await TenantAuthenticationIssuer.StartAsync();
        await using var host = await TenantAuthenticationReconciliationHost.StartAsync(issuer, subjectPermitLimit: 3);
        for (var index = 0; index < 3; index++)
        {
            using var admitted = await host.SendAsync(issuer.Token(issuer.Without("organization_id")));
            admitted.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        using var refused = await host.SendAsync(issuer.Token(issuer.Without("tenant_id", "organization_id")),
            path: TenantAuthenticationReconciliationHost.PublicPath);

        await AssertProblem(refused, HttpStatusCode.TooManyRequests, "rate_limited");
        host.ResolvedRequests.Should().HaveCount(3);
        AssertClaimlessEvents(host, 0);
        host.Recorder.Rejections.Should().BeEmpty();
        host.Recorder.NoTenantRefusals.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Real_visitor_floor_still_applies_to_anonymous_and_validated_requests(bool authenticated)
    {
        await using var issuer = await TenantAuthenticationIssuer.StartAsync();
        await using var host = await TenantAuthenticationReconciliationHost.StartAsync(issuer);
        for (var index = 0; index < LearnStack.Api.Tenancy.RateLimitingExtensions.AnonymousPermitPerWindow; index++)
        {
            // Subject changes cannot bypass the existing physical visitor floor.
            // The physical peer remains loopback for every request.
            var payload = TenantAuthenticationIssuer.AddProperty(issuer.Without("organization_id", "sub"),
                "sub", "subject-" + index.ToString(CultureInfo.InvariantCulture));
            using var response = await host.SendAsync(authenticated ? issuer.Token(payload) : null,
                path: TenantAuthenticationReconciliationHost.PublicPath);
            response.StatusCode.Should().Be(HttpStatusCode.OK, "request {0} is within the actual visitor budget", index + 1);
        }

        using var refused = await host.SendAsync(authenticated ? issuer.Token(issuer.Without("organization_id")) : null,
            path: TenantAuthenticationReconciliationHost.PublicPath);
        await AssertProblem(refused, HttpStatusCode.TooManyRequests, "rate_limited");
        (refused.Headers.RetryAfter?.Delta).Should().BeGreaterThan(TimeSpan.Zero);
        host.Ports.HostLookups.Should().HaveCount(LearnStack.Api.Tenancy.RateLimitingExtensions.AnonymousPermitPerWindow,
            "the ingress refusal still precedes classification and JWT work");
        host.ResolvedRequests.Should().HaveCount(LearnStack.Api.Tenancy.RateLimitingExtensions.AnonymousPermitPerWindow);
        host.Recorder.Rejections.Should().BeEmpty();
    }
}
