using System.Net;
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
        (await response.Content.ReadAsStringAsync()).Should().Contain("Platform");
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
        (await response.Content.ReadAsStringAsync()).Should().Contain("Organization",
            "a mapping row carrying an organization classifies as OrgHost");
    }

    [Fact]
    public async Task A_Tenant_Wide_Host_Classifies_As_Tenant_Rather_Than_Organization()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, new Uri("/api/v1/hostprobe", UriKind.Relative));
        request.Headers.Host = HostClassificationFixture.TenantHost;

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Tenant");
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

    public static readonly Guid Tenant = Guid.Parse("018f4d40-0000-7000-8000-0000000000c1");
    public static readonly Guid Organization = Guid.Parse("018f4d40-0000-7000-8000-0000000000c2");

    public StubResolver Resolver { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEnvironment(Environments.Development);
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
                _ => null,
            });
        }
    }
}

/// <summary>Echoes the classification the middleware attached.</summary>
[ApiExplorerSettings(IgnoreApi = true)]
public sealed class HostProbeController : ApiControllerBase, ITestOnlyController
{
    [HttpGet]
    public IActionResult Get() =>
        Ok(new
        {
            @class = HttpContext.Features.Get<HostClassification>()?.Class.ToString() ?? "none",
        });
}
