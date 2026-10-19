using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using LearnStack.Api.Tenancy;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LearnStack.Tests.Integration;

/// <summary>Signed tokens, stock JwtBearer and the completed reconciliation components.</summary>
public sealed partial class TenantAuthenticationReconciliationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Matching_tenant_and_organization_tokens_reach_rows_6_and_9(bool organizationHost)
    {
        await using var issuer = await TenantAuthenticationIssuer.StartAsync();
        await using var host = await TenantAuthenticationReconciliationHost.StartAsync(issuer);
        var token = issuer.Token(organizationHost ? issuer.Payload() : issuer.Without("organization_id"));
        using var response = await host.SendAsync(token, organizationHost
            ? TenantAuthenticationReconciliationHost.OrganizationHost
            : TenantAuthenticationReconciliationHost.TenantHost);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var observed = (await response.Content.ReadFromJsonAsync<ReconciliationObservation>())!;
        observed.Should().BeEquivalentTo(new ReconciliationObservation(true,
            TenantAuthenticationIssuer.Tenant, organizationHost ? TenantAuthenticationIssuer.Organization : null,
            TenantAuthenticationIssuer.Actor, TenantContextOrigin.HostAndClaim,
            TenantAuthenticationIssuer.Tenant, organizationHost ? TenantAuthenticationIssuer.Organization : null,
            Correlation(response), true));
        ActivityContext.TryParse(observed.Correlation, null, out _).Should().BeTrue();
        host.Ports.Memberships.Should().BeEmpty("neither row requires extra membership authority");
        host.Ports.OrganizationChecks.Should().BeEmpty();
        host.Recorder.Rejections.Should().BeEmpty();
        issuer.MetadataRequests.Should().Be(1, "the token passed through real discovery and validation");
        issuer.JwksRequests.Should().Be(1);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(11)]
    [InlineData(12)]
    public async Task JWT_disagreements_defend_the_host_tenant_and_record_only_the_first_refusal(int row)
    {
        await using var issuer = await TenantAuthenticationIssuer.StartAsync();
        await using var host = await TenantAuthenticationReconciliationHost.StartAsync(issuer);
        var payload = row == 8
            ? TenantAuthenticationIssuer.AddProperty(issuer.Without("tenant_id", "organization_id"),
                "tenant_id", TenantAuthenticationReconciliationHost.OtherTenant.ToString("D"))
            : issuer.Payload(
                ("tenant_id", (row == 12 ? TenantAuthenticationReconciliationHost.OtherTenant
                    : TenantAuthenticationIssuer.Tenant).ToString("D")),
                ("organization_id", TenantAuthenticationReconciliationHost.OtherOrganization.ToString("D")));
        using var response = await host.SendAsync(issuer.Token(payload), row == 8
                ? TenantAuthenticationReconciliationHost.TenantHost
                : TenantAuthenticationReconciliationHost.OrganizationHost,
            tenantAssertion: TenantAuthenticationReconciliationHost.OtherTenant,
            organizationAssertion: TenantAuthenticationReconciliationHost.OtherOrganization);

        await AssertProblem(response, HttpStatusCode.NotFound, "tenant_mismatch");
        var rejection = host.Recorder.Rejections.Should().ContainSingle().Subject;
        rejection.Should().Be(new TenantAssertionRejection(TenantAuthenticationIssuer.Tenant,
            row == 11 ? TenantAssertionDimension.Organization : TenantAssertionDimension.Tenant,
            row == 11 ? TenantAuthenticationReconciliationHost.OtherOrganization
                : TenantAuthenticationReconciliationHost.OtherTenant,
            true, TenantAssertionSource.JwtClaim, UserId.From(TenantAuthenticationIssuer.Actor),
            Correlation(response), row == 8 ? null : OrganizationId.From(TenantAuthenticationIssuer.Organization)));
        host.Recorder.NoTenantRefusals.Should().BeEmpty();
        host.Recorder.Unresolved.Should().BeEmpty("downstream headers cannot add a second refusal");
        host.ResolvedRequests.Should().BeEmpty("a rejected attempt is never installed as ambient context");
        host.Ports.Memberships.Should().BeEmpty("disagreement precedes membership I/O");
        host.Ports.OrganizationChecks.Should().BeEmpty();
    }

    [Theory]
    [InlineData(7)]
    [InlineData(10)]
    [InlineData(14)]
    public async Task Membership_denials_keep_the_matrix_audit_dispositions_without_organization_reads(int row)
    {
        await using var issuer = await TenantAuthenticationIssuer.StartAsync();
        await using var host = await TenantAuthenticationReconciliationHost.StartAsync(issuer);
        using var response = await host.SendAsync(issuer.Token(row == 10
            ? issuer.Without("organization_id") : issuer.Payload()), row switch
            {
                7 => TenantAuthenticationReconciliationHost.TenantHost,
                10 => TenantAuthenticationReconciliationHost.OrganizationHost,
                _ => TenantAuthenticationReconciliationHost.PlatformHost,
            }, tenantAssertion: TenantAuthenticationReconciliationHost.OtherTenant);

        await AssertProblem(response, HttpStatusCode.NotFound, "not_found");
        host.Ports.Memberships.Should().ContainSingle().Which.Should().Be((
            UserId.From(TenantAuthenticationIssuer.Actor), TenantId.From(TenantAuthenticationIssuer.Tenant),
            (OrganizationId?)OrganizationId.From(TenantAuthenticationIssuer.Organization)));
        host.Ports.OrganizationChecks.Should().BeEmpty("denial must precede any tenant-announcing organization read");
        host.ResolvedRequests.Should().BeEmpty();
        host.Recorder.Unresolved.Should().BeEmpty("the later header assertion must never execute");
        if (row == 7)
        {
            host.Recorder.Rejections.Should().ContainSingle().Which.Should().Be(new TenantAssertionRejection(
                TenantAuthenticationIssuer.Tenant, TenantAssertionDimension.Organization,
                TenantAuthenticationIssuer.Organization, true, TenantAssertionSource.JwtClaim,
                UserId.From(TenantAuthenticationIssuer.Actor), Correlation(response), null));
            host.Recorder.NoTenantRefusals.Should().BeEmpty();
        }
        else
        {
            host.Recorder.Rejections.Should().BeEmpty();
            if (row == 14)
                host.Recorder.NoTenantRefusals.Should().ContainSingle().Which.Should().Be(new TenantNoTenantRefusal(
                    TenantAssertionSource.JwtClaim, TenantAssertionDimension.Tenant,
                    UserId.From(TenantAuthenticationIssuer.Actor), Correlation(response)));
            else host.Recorder.NoTenantRefusals.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task Row_14_counts_exactly_one_unresolved_JWT_refusal_with_only_closed_labels()
    {
        await using var issuer = await TenantAuthenticationIssuer.StartAsync();
        await using var host = await TenantAuthenticationReconciliationHost.StartAsync(issuer);
        var meter = host.Services.GetRequiredService<IMeterFactory>().Create(LoggingTenantAssertionRecorder.MeterName);
        var measurements = new List<(long Count, Dictionary<string, object?> Tags)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, owner) =>
        {
            if (ReferenceEquals(instrument.Meter, meter)
                && instrument.Name == LoggingTenantAssertionRecorder.UnresolvedCounterName)
                owner.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            measurements.Add((value, tags.ToArray().ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal))));
        listener.Start();

        using var response = await host.SendAsync(issuer.Token(), TenantAuthenticationReconciliationHost.PlatformHost,
            tenantAssertion: TenantAuthenticationReconciliationHost.OtherTenant);

        await AssertProblem(response, HttpStatusCode.NotFound, "not_found");
        var measurement = measurements.Should().ContainSingle().Subject;
        measurement.Count.Should().Be(1);
        measurement.Tags.Should().BeEquivalentTo(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["source"] = "jwt-claim",
            ["dimension"] = "Tenant",
        });
    }

    [Theory]
    [InlineData(TenantAuthenticationReconciliationHost.TenantHost, false)]
    [InlineData(TenantAuthenticationReconciliationHost.OrganizationHost, true)]
    public async Task Claimless_profiles_keep_the_actor_and_HostOnly_ceiling_with_one_fixed_Debug_event_per_request(
        string requestHost, bool organizationHost)
    {
        await using var issuer = await TenantAuthenticationIssuer.StartAsync();
        await using var host = await TenantAuthenticationReconciliationHost.StartAsync(issuer);
        var token = issuer.Token(issuer.Without("tenant_id", "organization_id"));
        using var publicResponse = await host.SendAsync(token, requestHost, TenantAuthenticationReconciliationHost.PublicPath);

        publicResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var expected = new ReconciliationObservation(true, TenantAuthenticationIssuer.Tenant,
            organizationHost ? TenantAuthenticationIssuer.Organization : null,
            TenantAuthenticationIssuer.Actor, TenantContextOrigin.HostOnly, TenantAuthenticationIssuer.Tenant,
            organizationHost ? TenantAuthenticationIssuer.Organization : null, Correlation(publicResponse), true);
        (await publicResponse.Content.ReadFromJsonAsync<ReconciliationObservation>()).Should().Be(expected);
        AssertClaimlessEvents(host, 1);

        using var protectedResponse = await host.SendAsync(token, requestHost);
        await AssertProblem(protectedResponse, HttpStatusCode.NotFound, "not_found");
        host.ResolvedRequests.Should().HaveCount(2);
        host.ResolvedRequests.Last().Should().Be(expected with { Correlation = Correlation(protectedResponse) });
        AssertClaimlessEvents(host, 2);
        host.Ports.Memberships.Should().BeEmpty();
        host.Recorder.Rejections.Should().BeEmpty();
    }

    [Fact]
    public async Task Claimless_authenticated_header_rejections_retain_the_actor_and_resolved_host_organization()
    {
        await using var issuer = await TenantAuthenticationIssuer.StartAsync();
        await using var host = await TenantAuthenticationReconciliationHost.StartAsync(issuer);
        using var response = await host.SendAsync(issuer.Token(issuer.Without("tenant_id", "organization_id")),
            TenantAuthenticationReconciliationHost.OrganizationHost, TenantAuthenticationReconciliationHost.PublicPath,
            organizationAssertion: TenantAuthenticationReconciliationHost.OtherOrganization);

        await AssertProblem(response, HttpStatusCode.NotFound, "tenant_mismatch");
        host.Recorder.Rejections.Should().ContainSingle().Which.Should().Be(new TenantAssertionRejection(
            TenantAuthenticationIssuer.Tenant, TenantAssertionDimension.Organization,
            TenantAuthenticationReconciliationHost.OtherOrganization, true, TenantAssertionSource.Header,
            UserId.From(TenantAuthenticationIssuer.Actor), Correlation(response),
            OrganizationId.From(TenantAuthenticationIssuer.Organization)));
        host.ResolvedRequests.Should().ContainSingle().Which.Origin.Should().Be(TenantContextOrigin.HostOnly);
    }

    [Fact]
    public async Task Invalid_credentials_on_public_known_hosts_are_401_while_unknown_hosts_precede_JWT_work()
    {
        await using var issuer = await TenantAuthenticationIssuer.StartAsync();
        await using var host = await TenantAuthenticationReconciliationHost.StartAsync(issuer);
        using var unknown = await host.SendAsync(issuer.Token(), TenantAuthenticationReconciliationHost.UnknownHost,
            TenantAuthenticationReconciliationHost.PublicPath);
        await AssertProblem(unknown, HttpStatusCode.NotFound, "not_found");
        issuer.MetadataRequests.Should().Be(0);

        using var invalid = await host.SendAsync(issuer.Token(issuer.Payload(("typ", "ID"))),
            path: TenantAuthenticationReconciliationHost.PublicPath);
        await AssertProblem(invalid, HttpStatusCode.Unauthorized, "unauthorized");
        invalid.Headers.WwwAuthenticate.Should().ContainSingle().Which.ToString().Should().Be("Bearer");
        host.ResolvedRequests.Should().BeEmpty();
        host.Recorder.Rejections.Should().BeEmpty();

        using var absent = await host.SendAsync(path: TenantAuthenticationReconciliationHost.PublicPath);
        absent.StatusCode.Should().Be(HttpStatusCode.OK);
        (await absent.Content.ReadFromJsonAsync<ReconciliationObservation>())!.ValidatedProfile.Should().BeFalse();
    }

    [Fact]
    public async Task Absent_credentials_on_every_known_host_class_match_the_unknown_host_masked_404()
    {
        await using var issuer = await TenantAuthenticationIssuer.StartAsync();
        await using var host = await TenantAuthenticationReconciliationHost.StartAsync(issuer);
        using var unknown = await host.SendAsync(host: TenantAuthenticationReconciliationHost.UnknownHost);
        var expected = await WithoutCorrelation(unknown);
        foreach (var requestHost in new[] { TenantAuthenticationReconciliationHost.TenantHost,
            TenantAuthenticationReconciliationHost.OrganizationHost, TenantAuthenticationReconciliationHost.PlatformHost })
        {
            using var response = await host.SendAsync(host: requestHost);
            await AssertProblem(response, HttpStatusCode.NotFound, "not_found");
            (await WithoutCorrelation(response)).Should().Be(expected);
            response.Headers.WwwAuthenticate.Should().BeEmpty();
        }
        issuer.MetadataRequests.Should().Be(0, "credential-free requests have no issuer dependency");
    }

    [Fact]
    public async Task A_forged_raw_authenticated_principal_confers_no_profile_actor_or_tenant_authority()
    {
        await using var issuer = await TenantAuthenticationIssuer.StartAsync();
        await using var host = await TenantAuthenticationReconciliationHost.StartAsync(issuer, forgeRawPrincipal: true);
        using var publicResponse = await host.SendAsync(path: TenantAuthenticationReconciliationHost.PublicPath);
        publicResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var observed = (await publicResponse.Content.ReadFromJsonAsync<ReconciliationObservation>())!;
        observed.ValidatedProfile.Should().BeFalse();
        observed.Origin.Should().Be(TenantContextOrigin.HostOnly);
        observed.Actor.Should().BeNull();
        observed.Organization.Should().BeNull("raw organization claims cannot select context");

        using var protectedResponse = await host.SendAsync();
        await AssertProblem(protectedResponse, HttpStatusCode.NotFound, "not_found");
        using var mismatch = await host.SendAsync(path: TenantAuthenticationReconciliationHost.PublicPath,
            tenantAssertion: TenantAuthenticationReconciliationHost.OtherTenant);
        await AssertProblem(mismatch, HttpStatusCode.NotFound, "not_found");
        host.Recorder.Rejections.Should().ContainSingle().Which.IsAuthenticated.Should().BeFalse();
        host.Recorder.Rejections.Single().ActorUserId.Should().BeNull();
        host.Ports.Memberships.Should().BeEmpty();
        issuer.MetadataRequests.Should().Be(0);
    }

    private static void AssertClaimlessEvents(TenantAuthenticationReconciliationHost host, int count)
    {
        var events = host.Logs.Entries.Where(entry => entry.Category == typeof(TenantResolverMiddleware).FullName
            && entry.Event.Id == 2 && entry.Event.Name == "TenantClaimAbsent").ToArray();
        events.Should().HaveCount(count);
        foreach (var entry in events)
        {
            entry.Level.Should().Be(LogLevel.Debug);
            entry.Exception.Should().BeNull();
            entry.Properties.Should().ContainSingle().Which.Should().Be(new KeyValuePair<string, object?>(
                "{OriginalFormat}", "Tenant authentication outcome tenant_claim_absent."));
        }
    }

    internal static string Correlation(HttpResponseMessage response) =>
        response.Headers.GetValues("X-Correlation-Id").Single();

    internal static async Task AssertProblem(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        response.StatusCode.Should().Be(status);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var problem = document.RootElement;
        problem.GetProperty("status").GetInt32().Should().Be((int)status);
        problem.GetProperty("code").GetString().Should().Be(code);
        problem.GetProperty("correlationId").GetString().Should().Be(Correlation(response));
    }

    private static async Task<string> WithoutCorrelation(HttpResponseMessage response)
    {
        var problem = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            await response.Content.ReadAsStringAsync());
        problem!.Remove("correlationId");
        return JsonSerializer.Serialize(problem);
    }
}
