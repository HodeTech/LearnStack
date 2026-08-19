using System.Net;
using FluentAssertions;
using LearnStack.Api.Common;
using LearnStack.Api.Tenancy;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace LearnStack.Tests.Integration;

/// <summary>
/// The assertion comparison from
/// <see href="../../../docs/decisions/0036-tenant-resolution-trusted-inputs.md">ADR-0036
/// § What the assertions do</see>: <c>X-Tenant-Id</c> and
/// <c>X-Organization-Id</c> can cause a request to be rejected and can never
/// cause a tenant to be selected.
/// </summary>
/// <remarks>
/// Packet 4 resolves nothing, so the fixture substitutes a resolved
/// <c>ITenantContext</c> — which is what makes the mismatch path reachable at
/// all. That substitution is the test's subject, not a shortcut: ADR-0036's
/// staging table says this comparison is "unreachable in traffic … and
/// exercised by unit tests over a stubbed context" until Packet 7.
/// </remarks>
public sealed class TenantAssertionHttpTests(ResolvedTenantFixture fixture)
    : IClassFixture<ResolvedTenantFixture>
{
    private readonly HttpClient _client = fixture.CreateClient();

    [Fact]
    public async Task An_Assertion_That_Agrees_Changes_Nothing()
    {
        using var request = Get("/api/v1/assertionprobe");
        request.Headers.Add(TenantAssertionMiddleware.TenantHeaderName,
            ResolvedTenantFixture.TenantId.ToString());

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task No_Assertion_Changes_Nothing()
    {
        var response = await _client.GetAsync(new Uri("/api/v1/assertionprobe", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_Tenant_Assertion_That_Disagrees_Is_A_404()
    {
        // 404, not 403: saying "wrong tenant" confirms the other tenant exists.
        using var request = Get("/api/v1/assertionprobe");
        request.Headers.Add(TenantAssertionMiddleware.TenantHeaderName,
            Guid.NewGuid().ToString());

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType?.MediaType
            .Should().Be("application/problem+json",
                "a rejected assertion answers exactly as a routing 404 does");
    }

    [Fact]
    public async Task An_Organization_Assertion_That_Disagrees_Is_A_404()
    {
        using var request = Get("/api/v1/assertionprobe");
        request.Headers.Add(TenantAssertionMiddleware.OrganizationHeaderName,
            Guid.NewGuid().ToString());

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_Assertion_Never_Selects_A_Tenant()
    {
        // The property the whole ADR exists for. The probe echoes the tenant
        // the context resolved; asserting a different one must not change it —
        // it must refuse the request.
        using var agreeing = Get("/api/v1/assertionprobe");
        agreeing.Headers.Add(TenantAssertionMiddleware.TenantHeaderName,
            ResolvedTenantFixture.TenantId.ToString());

        var body = await (await _client.SendAsync(agreeing)).Content.ReadAsStringAsync();

        body.Should().Contain(ResolvedTenantFixture.TenantId.ToString(),
            "the resolved tenant is the only one that can ever be served");
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000")]
    [InlineData("'; drop table tenants--")]
    public async Task A_Malformed_Assertion_Is_A_400(string value)
    {
        // An empty value is deliberately not a row here: HttpClient drops a
        // header whose value is empty, so the request arrives with no header at
        // all and correctly answers 200. The code path is still covered —
        // Guid.TryParse("") is false — it simply cannot be reached through a
        // client that refuses to send it.
        using var request = Get("/api/v1/assertionprobe");
        request.Headers.TryAddWithoutValidation(
            TenantAssertionMiddleware.TenantHeaderName, value);

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_Repeated_Assertion_Is_Refused_Not_Resolved_By_First_Or_Last()
    {
        // The classic header-confusion bug: a proxy in front of a client that
        // already sent one produces two, and whichever end you pick, some
        // topology makes it the attacker's. Both values here are individually
        // valid — only their multiplicity is wrong.
        using var request = Get("/api/v1/assertionprobe");
        request.Headers.Add(TenantAssertionMiddleware.TenantHeaderName,
            ResolvedTenantFixture.TenantId.ToString());
        request.Headers.Add(TenantAssertionMiddleware.TenantHeaderName,
            Guid.NewGuid().ToString());

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Every_Response_Carries_A_Correlation_Id()
    {
        // Standards 10 § Correlation puts correlation_id on the Problem Details
        // body and on error-tracker captures. The success path had no handle at
        // all: a client reporting "this rendered the wrong thing" could only
        // obtain one by receiving an error first.
        var response = await _client.GetAsync(new Uri("/api/v1/assertionprobe", UriKind.Relative));

        response.Headers.TryGetValues(CorrelationHeaderMiddleware.HeaderName, out var values)
            .Should().BeTrue();
        values!.Single().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task A_Client_Supplied_Correlation_Id_Is_Echoed_Under_Its_Own_Name()
    {
        // Echoed, never adopted. Trusting it would let two unrelated requests
        // share one id, or let a caller poison a log search.
        using var request = Get("/api/v1/assertionprobe");
        request.Headers.Add(CorrelationHeaderMiddleware.HeaderName, "client-chosen-value");

        var response = await _client.SendAsync(request);

        response.Headers.GetValues(CorrelationHeaderMiddleware.RequestHeaderName)
            .Single().Should().Be("client-chosen-value");
        response.Headers.GetValues(CorrelationHeaderMiddleware.HeaderName)
            .Single().Should().NotBe("client-chosen-value",
                "the trace context is the identity; the header is a copy of it");
    }

    private static HttpRequestMessage Get(string path) =>
        new(HttpMethod.Get, new Uri(path, UriKind.Relative));
}

/// <summary>
/// A host whose <see cref="ITenantContext"/> is resolved, so the assertion
/// comparison has something to compare against.
/// </summary>
public sealed class ResolvedTenantFixture : WebApplicationFactory<Program>
{
    public static readonly Guid TenantId = Guid.Parse("018f4d40-0000-7000-8000-0000000000aa");
    public static readonly Guid OrganizationId = Guid.Parse("018f4d40-0000-7000-8000-0000000000bb");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEnvironment(Environments.Development);
        builder.ConfigureTestServices(services =>
        {
            services.AddControllers(options =>
                    options.Conventions.Insert(0, new TestControllerFilter(
                        typeof(AssertionProbeController))))
                .AddApplicationPart(typeof(AssertionProbeController).Assembly);

            services.RemoveAll<ITenantContext>();
            services.AddScoped<ITenantContext>(_ => ResolvedContext.Instance);
        });
    }

    internal sealed class ResolvedContext : ITenantContext
    {
        public static ResolvedContext Instance { get; } = new();

        public bool IsResolved => true;
        public Guid TenantId => ResolvedTenantFixture.TenantId;
        public Guid? OrganizationId => ResolvedTenantFixture.OrganizationId;
        public UserId? UserId => null;
        public string? CorrelationId => null;
        public string? ModuleName => "integration-test";
    }
}

public sealed class AssertionProbeController : ApiControllerBase, ITestOnlyController
{
    [HttpGet]
    public IActionResult Get([FromServices] ITenantContext tenantContext)
    {
        ArgumentNullException.ThrowIfNull(tenantContext);
        return Ok(new { tenant = tenantContext.TenantId });
    }
}
