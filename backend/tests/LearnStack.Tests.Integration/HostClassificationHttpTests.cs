using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using LearnStack.Api.Common;
using LearnStack.Api.Tenancy;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace LearnStack.Tests.Integration;

/// <summary>
/// Host classification through the real pipeline: which requests are classified,
/// what an unknown host gets, and what a classified one carries forward.
/// </summary>
/// <remarks>
/// <para>
/// A host test — no Docker. The resolver is stubbed, because what is under test is
/// the middleware's decisions and not the query behind them;
/// <c>HostResolutionTests</c> covers that against a real database, connected as
/// <c>learnstack_app</c>.
/// </para>
/// <para>
/// The fixture runs in <c>Development</c>, where <c>Tenancy:PlatformHosts</c>
/// carries <c>localhost</c> — which is what every other host test in this
/// assembly depends on without knowing it. A request to <c>localhost</c> is
/// classified <c>Platform</c> and short-circuits before the resolver, so the
/// Docker-free suites keep working with no database at all.
/// </para>
/// </remarks>
public sealed class HostClassificationHttpTests(HostClassificationFixture fixture)
    : IClassFixture<HostClassificationFixture>
{
    private readonly HttpClient _client = fixture.CreateClient();

    [Fact]
    public async Task A_Platform_Host_Is_Served_Without_Reaching_The_Resolver()
    {
        fixture.Resolver.Calls.Clear();

        var response = await _client.GetAsync(new Uri("/api/v1/hostprobe", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var probe = await ReadProbeAsync(response);
        probe.Class.Should().Be("Platform");
        probe.Resolved.Should().BeFalse(
            "matrix row 13: a platform host resolves no tenant, and that is not a refusal");
        fixture.Resolver.Calls.Should().BeEmpty(
            "a platform host maps to no tenant by configuration, so it costs no lookup");
    }

    [Fact]
    public async Task An_Unknown_Host_Is_A_404_Before_Any_Handler()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, new Uri("/api/v1/hostprobe", UriKind.Relative));
        request.Headers.Host = "stranger.example.com";

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_Unknown_Host_Answers_Exactly_As_An_Unmapped_Path_Does()
    {
        // The bit an anonymous caller must not be able to tell apart. Saying
        // "unknown tenant" — by a different status, a different body, or a
        // different content type — confirms which hostnames exist.
        //
        // The SAME path both times, so `instance` cannot account for a difference:
        // one request is refused because its host resolves to nothing, the other
        // because the path routes to nothing, and the two answers must be the same
        // answer. Only the correlation id may differ, and it is normalized out —
        // it is per-request by design and carries no fact about either refusal.
        const string Path = "/api/v1/nothing-here";

        using var unknownHost = new HttpRequestMessage(HttpMethod.Get, new Uri(Path, UriKind.Relative));
        unknownHost.Headers.Host = "stranger.example.com";

        var rejected = await _client.SendAsync(unknownHost);
        var routed = await _client.GetAsync(new Uri(Path, UriKind.Relative));

        rejected.StatusCode.Should().Be(routed.StatusCode);
        rejected.Content.Headers.ContentType?.MediaType
            .Should().Be(routed.Content.Headers.ContentType?.MediaType);
        WithoutCorrelation(await rejected.Content.ReadAsStringAsync())
            .Should().Be(WithoutCorrelation(await routed.Content.ReadAsStringAsync()));
    }

    private static async Task<HostProbe> ReadProbeAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<HostProbe>())!;

    private static string WithoutCorrelation(string body) =>
        System.Text.RegularExpressions.Regex.Replace(
            body, "\"correlationId\":\"[^\"]*\"", "\"correlationId\":\"<per-request>\"");

    [Fact]
    public async Task A_Resolved_Host_Carries_Its_Classification_Forward()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, new Uri("/api/v1/hostprobe", UriKind.Relative));
        request.Headers.Host = HostClassificationFixture.OrganizationHost;

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var probe = await ReadProbeAsync(response);
        probe.Class.Should().Be("Organization",
            "a mapping row carrying an organization classifies as OrgHost");
        probe.Resolved.Should().BeTrue();
        probe.TenantId.Should().Be(HostClassificationFixture.Tenant);
        probe.OrganizationId.Should().Be(HostClassificationFixture.Organization,
            "matrix row 3: the anonymous organization scope IS the mapping row");
        probe.Origin.Should().Be(nameof(TenantContextOrigin.HostOnly));
    }

    [Fact]
    public async Task A_Tenant_Wide_Host_Classifies_As_Tenant_Rather_Than_Organization()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, new Uri("/api/v1/hostprobe", UriKind.Relative));
        request.Headers.Host = HostClassificationFixture.TenantHost;

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var probe = await ReadProbeAsync(response);
        probe.Class.Should().Be("Tenant");
        probe.Resolved.Should().BeTrue();
        probe.TenantId.Should().Be(HostClassificationFixture.Tenant);
        probe.OrganizationId.Should().BeNull(
            "matrix row 2: a tenant-wide host resolves no organization");
        probe.Origin.Should().Be(nameof(TenantContextOrigin.HostOnly),
            "the ceiling that makes a forged host harmless");
    }

    [Theory]
    [InlineData("1.2.3.4", "an IPv4 literal is not a name")]
    [InlineData("1.2.3.4.", "nor is one a trailing dot used to hide")]
    [InlineData("[::1]", "nor an IPv6 literal")]
    [InlineData("ex%41mple.com", "nor a percent-escape, which gives one host two spellings")]
    public async Task A_Host_That_Names_Nothing_Is_A_404_And_Not_An_Error(
        string host, string because)
    {
        // The branch that had no test, and the one the IPv4 blocker escaped
        // through: `1.2.3.4.` normalized to `1.2.3.4`, reached the resolver, and
        // threw in the cache-key factory — a 500 and an error-tracker capture per
        // request, from an unauthenticated caller. TryAddWithoutValidation because
        // HttpClient refuses to send several of these through Headers.Host, and
        // the point is what the server does with a header a client can still put
        // on the wire.
        using var request = new HttpRequestMessage(
            HttpMethod.Get, new Uri("/api/v1/hostprobe", UriKind.Relative));
        request.Headers.TryAddWithoutValidation("Host", host);

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound, because);
    }

    [Fact]
    public async Task A_Rejection_Is_Counted_And_A_Served_Host_Is_Not()
    {
        // The counter an operator watches for a hostname flood, which had no
        // reader in any of the four test assemblies. The level the rejected host
        // is logged at is the other half of this guarantee and is asserted in
        // HostClassificationLoggingTests — through the middleware directly,
        // because Serilog is wired without writeToProviders, so an ILoggerProvider
        // registered in DI here receives nothing at all.
        var counted = 0L;
        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Name == HostClassificationMiddleware.RejectedCounterName)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        meterListener.SetMeasurementEventCallback<long>(
            (_, measurement, _, _) => Interlocked.Add(ref counted, measurement));
        meterListener.Start();

        using var platform = new HttpRequestMessage(
            HttpMethod.Get, new Uri("/api/v1/hostprobe", UriKind.Relative));
        (await _client.SendAsync(platform)).StatusCode.Should().Be(HttpStatusCode.OK);

        Interlocked.Read(ref counted).Should().Be(0, "a served host is not a rejection");

        using var unknown = new HttpRequestMessage(
            HttpMethod.Get, new Uri("/api/v1/hostprobe", UriKind.Relative));
        unknown.Headers.Host = "counted.example.com";
        (await _client.SendAsync(unknown)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        meterListener.RecordObservableInstruments();
        Interlocked.Read(ref counted).Should().Be(1, "one refused host, one increment");
    }

    [Fact]
    public async Task A_Platform_Host_Wins_Over_A_Mapping_Row_That_Names_It()
    {
        // Pinning today's actual behaviour, which nothing stated. The platform
        // branch short-circuits before the resolver is ever called, so a row in
        // platform_host_to_tenant for a configured platform host is permanently
        // and silently inert — no log, no counter, no startup check.
        //
        // The precedence is the right way round: Tenancy:PlatformHosts is the
        // operator's own entry point, and a tenant that managed to claim that
        // hostname would otherwise take it over. What is worth knowing is that the
        // losing row is invisible, so this asserts it rather than leaving the next
        // reader to discover it from a support ticket. A real cross-check belongs
        // to whichever packet builds the host-mapping writer; a database
        // constraint cannot see application configuration.
        fixture.Resolver.Calls.Clear();

        using var request = new HttpRequestMessage(
            HttpMethod.Get, new Uri("/api/v1/hostprobe", UriKind.Relative));
        request.Headers.Host = HostClassificationFixture.PlatformHostWithAMappingRow;

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadProbeAsync(response)).Class.Should().Be("Platform");
        fixture.Resolver.Calls.Should().BeEmpty(
            "the row is never read, which is why it is inert rather than conflicting");
    }

    [Fact]
    public async Task An_Unclassified_Prefix_Is_Served_Whatever_Its_Host()
    {
        // /healthz has no tenant and must answer a probe from anywhere. The same
        // property is what keeps /api/internal/* reachable for the Hub, whose
        // tenant comes from the envelope rather than from a host.
        using var request = new HttpRequestMessage(
            HttpMethod.Get, new Uri("/healthz", UriKind.Relative));
        request.Headers.Host = "stranger.example.com";

        (await _client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.OK);
    }
}

/// <summary>A host whose resolver is a stub, so no database is involved.</summary>
public sealed class HostClassificationFixture : WebApplicationFactory<Program>
{
    public const string OrganizationHost = "branch.example.com";
    public const string TenantHost = "school.example.com";

    /// <summary>Configured as a platform host <b>and</b> answered by the resolver.</summary>
    public const string PlatformHostWithAMappingRow = "both.example.com";

    public static readonly Guid Tenant = Guid.Parse("018f4d40-0000-7000-8000-0000000000c1");
    public static readonly Guid Organization = Guid.Parse("018f4d40-0000-7000-8000-0000000000c2");

    public StubResolver Resolver { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEnvironment(Environments.Development);

        // UseSetting, not ConfigureAppConfiguration: the composition root reads
        // Tenancy:PlatformHosts while the builder is being assembled, which is
        // before a deferred ConfigureAppConfiguration runs — the same trap
        // DeploymentModeCompositionTests documents for Deployment:Mode, and
        // measured here too (the host classified Tenant, not Platform).
        // appsettings.Development.json already carries localhost at index 0; this
        // adds the one that ALSO has a mapping row.
        builder.UseSetting($"{PlatformHostOptions.SectionName}:1", PlatformHostWithAMappingRow);

        builder.ConfigureTestServices(services =>
        {
            services.AddControllers(options =>
                    options.Conventions.Insert(0, new TestControllerFilter(
                        typeof(HostProbeController))))
                .AddApplicationPart(typeof(HostProbeController).Assembly);

            services.RemoveAll<IHostToTenantResolver>();
            services.AddSingleton<IHostToTenantResolver>(Resolver);
        });
    }

    /// <summary>Answers from a fixed table, and records what it was asked.</summary>
    public sealed class StubResolver : IHostToTenantResolver
    {
        private readonly List<string> _calls = [];

        public IList<string> Calls
        {
            get { lock (_calls) { return _calls; } }
        }

        public Task<HostResolution?> ResolveAsync(
            string host, CancellationToken cancellationToken = default)
        {
            lock (_calls)
            {
                _calls.Add(host);
            }

            return Task.FromResult(host switch
            {
                OrganizationHost => new HostResolution(
                    TenantId.From(Tenant), OrganizationId.From(Organization)),
                TenantHost => new HostResolution(TenantId.From(Tenant), null),

                // Deliberately answerable. If the platform branch ever stopped
                // short-circuiting, this host would classify Tenant and the
                // precedence case would fail rather than silently pass.
                PlatformHostWithAMappingRow => new HostResolution(TenantId.From(Tenant), null),
                _ => null,
            });
        }
    }
}

/// <summary>
/// Echoes what the tenancy edge decided — the host classification, and the tenant
/// context the resolver put on the accessor.
/// </summary>
/// <remarks>
/// Test-only, and registered by the fixture rather than by the application: ADR-0036
/// and the Packet 7 plan both hold that no production <c>/api/v1</c> endpoint ships
/// in this packet, and the first real read endpoints are Phase 02d's. It takes
/// <c>ITenantContext</c> by injection precisely as a handler would, so what it
/// reports is what a handler would see rather than what the middleware believes it
/// wrote.
/// </remarks>
[ApiExplorerSettings(IgnoreApi = true)]
public sealed class HostProbeController(ITenantContext tenantContext)
    : ApiControllerBase, ITestOnlyController
{
    [HttpGet]
    public IActionResult Get() =>
        Ok(new HostProbe(
            HttpContext.Features.Get<HostClassification>()?.Class.ToString() ?? "none",
            tenantContext.IsResolved,
            tenantContext.IsResolved ? tenantContext.TenantId.Value : null,
            tenantContext.OrganizationId?.Value,
            tenantContext.Origin?.ToString()));
}

/// <summary>What <see cref="HostProbeController"/> reports, typed.</summary>
/// <remarks>
/// A record rather than an anonymous object so the assertions deserialize instead of
/// substring-matching a body — <c>Contain("Tenant")</c> is satisfied by the word
/// appearing anywhere, including inside "TenantHost", and a test that passes on a
/// coincidence of spelling is the failure this packet keeps finding.
/// </remarks>
public sealed record HostProbe(
    string Class, bool Resolved, Guid? TenantId, Guid? OrganizationId, string? Origin);
