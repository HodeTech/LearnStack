using System.Net;
using System.Text.RegularExpressions;
using FluentAssertions;
using LearnStack.Api.Common;
using LearnStack.Api.Tenancy;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using MediatR;
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
/// The authority ceiling through the real pipeline, and the property that makes it
/// safe to have one: its refusal is indistinguishable from an unresolvable host's.
/// </summary>
/// <remarks>
/// <para>
/// No Docker. A step-4 refusal short-circuits before <c>TransactionBehavior</c> opens
/// anything at step 6, and the resolver is stubbed — what is under test is the
/// pipeline's decision and the bytes it produces, neither of which touches PostgreSQL.
/// </para>
/// <para>
/// <b>Why the parity matters more than the refusal.</b> A caller who can tell "this
/// tenant exists but you may not reach this" from "no such host" has an oracle over
/// which hostnames are live. The two refusals travel completely different routes — one
/// is a bodyless status filled in by <c>UseStatusCodePages</c>, the other an MVC
/// <c>ObjectResult</c> serialized by the framework — so their agreement is a property
/// to measure at the wire, not to derive from <c>HttpStatusMap</c>.
/// </para>
/// </remarks>
[Collection(HostClassificationMeter.Name)]
public sealed class AuthorityCeilingHttpTests(AuthorityCeilingFixture fixture)
    : IClassFixture<AuthorityCeilingFixture>
{
    private readonly HttpClient _client = fixture.CreateClient();

    [Fact]
    public async Task A_PublicSurface_Request_Is_Reachable_Anonymously()
    {
        // Matrix rows 2 and 3 doing their job: the host named the tenant, nothing else
        // spoke, and the marked request type is exactly what that reaches.
        var response = await SendAsync("/api/v1/ceilingprobe/public");

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        body.Should().Contain("reached");
    }

    [Fact]
    public async Task An_Unmarked_Request_Is_Refused_Under_A_Host_Only_Context()
    {
        var response = await SendAsync("/api/v1/ceilingprobe/guarded");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "a host-only context reaches only [PublicSurface] request types");
    }

    [Fact]
    public async Task The_Ceiling_Refusal_Is_Indistinguishable_From_An_Unknown_Host()
    {
        // The SAME path both times, so `instance` cannot account for a difference: one
        // request is refused because its host resolves to nothing, the other because
        // the tenant its host named may not reach this request type. Only the
        // correlation id may differ — it is per-request by design and is echoed in a
        // header the caller already holds, so it carries no fact about either refusal.
        const string Path = "/api/v1/ceilingprobe/guarded";

        var ceiling = await SendAsync(Path);
        var unknownHost = await SendAsync(Path, host: "stranger.example.com");

        ceiling.StatusCode.Should().Be(unknownHost.StatusCode);

        // The full media type, charset included. Two spellings of it — one with
        // `; charset=utf-8` and one without — once made a routing 404 tellable from an
        // MVC 404 without reading the body at all, and this pair crosses exactly that
        // boundary: one response is written by UseStatusCodePages, the other by MVC.
        ceiling.Content.Headers.ContentType?.ToString()
            .Should().Be(unknownHost.Content.Headers.ContentType?.ToString());

        WithoutCorrelation(await ceiling.Content.ReadAsStringAsync())
            .Should().Be(WithoutCorrelation(await unknownHost.Content.ReadAsStringAsync()),
                "the raw body, not a reparsed shape — property order and escaping are "
                + "as tellable as a field, and the two bodies are serialized by "
                + "different writers");
    }

    [Theory]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("OPTIONS")]
    public async Task A_Disallowed_Method_Discloses_Only_That_The_Host_Is_Publicly_Live(string method)
    {
        // MEASURED, and pinned deliberately rather than fixed. A verb no action accepts
        // is answered by routing, which runs ahead of every user middleware, so the
        // mapped host gets 405 and the unmapped host gets the resolver's 404 — the two
        // are tellable apart on the same path.
        //
        // Why that is acceptable, and why this test says so instead of a middleware
        // rewriting 405 to 404: the only hosts that reach routing at all are ones the
        // resolver admitted, and it admits a row only under `is_active AND
        // is_publicly_live`. ADR-0036 defines the second flag as meaning DNS points at
        // LearnStack and the tenant's public site is served — a host that is not
        // publicly live resolves to nothing and answers the unmapped 404 here too. So
        // the bit disclosed is `is_publicly_live`, which is by definition not a secret;
        // once Phase 02d ships the first [PublicSurface] page, a plain GET discloses it
        // more directly by returning 200.
        //
        // The invariant that DOES hold, and that the GET case above asserts: nothing
        // distinguishes a live tenant host from an unknown one on the paths the ceiling
        // controls, and nothing anywhere names WHICH tenant. Two review rounds reached
        // opposite conclusions about this, which is why the boundary is written down
        // here rather than left to be re-derived.
        var mapped = await SendAsync("/api/v1/ceilingprobe/guarded", method: new HttpMethod(method));
        var unmapped = await SendAsync(
            "/api/v1/ceilingprobe/guarded", host: "stranger.example.com", method: new HttpMethod(method));

        mapped.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed,
            "routing decides this before the pipeline, on a host that resolved");
        unmapped.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "an unresolvable host never reaches routing's method table");

        // An unrouted path is identical on both, which is the half that must not drift:
        // it is the shape an attacker probing for hostnames would actually use.
        var mappedMiss = await SendAsync("/api/v1/nothing-here", method: new HttpMethod(method));
        var unmappedMiss = await SendAsync(
            "/api/v1/nothing-here", host: "stranger.example.com", method: new HttpMethod(method));

        mappedMiss.StatusCode.Should().Be(unmappedMiss.StatusCode);
        WithoutCorrelation(await mappedMiss.Content.ReadAsStringAsync())
            .Should().Be(WithoutCorrelation(await unmappedMiss.Content.ReadAsStringAsync()));
    }

    private async Task<HttpResponseMessage> SendAsync(
        string path,
        string host = AuthorityCeilingFixture.TenantHost,
        HttpMethod? method = null)
    {
        using var request = new HttpRequestMessage(
            method ?? HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.Host = host;
        return await _client.SendAsync(request);
    }

    private static string WithoutCorrelation(string body) =>
        Regex.Replace(body, "\"correlationId\":\"[^\"]*\"", "\"correlationId\":\"<per-request>\"");
}

/// <summary>
/// Serializes the suites that touch <c>learnstack_host_classification_rejected_total</c>.
/// </summary>
/// <remarks>
/// The counter is process-wide and a <c>MeterListener</c> sees every increment from
/// every instrument of that name, whichever host produced it. So a suite asserting an
/// exact count cannot run beside one that refuses a host — and this one does, in the
/// parity case, by design. Measured: the two classes are green alone and green paired,
/// and red in a full parallel run, which is the shape of a race rather than a defect in
/// either. Serializing is the honest fix; loosening the count to "at least one" would
/// keep the suite green by asserting less.
/// </remarks>
[CollectionDefinition(Name)]
public sealed class HostClassificationMeter : ICollectionFixture<HostClassificationMeter>
{
    public const string Name = "host-classification-meter";
}

/// <summary>A host whose resolver maps one tenant host and nothing else.</summary>
public sealed class AuthorityCeilingFixture : WebApplicationFactory<Program>
{
    public const string TenantHost = "school.example.com";

    public static readonly Guid Tenant = Guid.Parse("018f4d40-0000-7000-8000-0000000000d1");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEnvironment(Environments.Development);
        builder.ConfigureTestServices(services =>
        {
            services.AddControllers(options =>
                    options.Conventions.Insert(0, new TestControllerFilter(
                        typeof(CeilingProbeController))))
                .AddApplicationPart(typeof(CeilingProbeController).Assembly);

            // Registered by hand rather than by re-running AddMediatR, which would
            // double-register every behavior in the eight-step pipeline — the
            // precedent CrossCuttingFoundationHttpTests set for the same reason.
            services.AddTransient<
                IRequestHandler<CeilingGuardedQuery, Result<string>>, CeilingGuardedHandler>();
            services.AddTransient<
                IRequestHandler<CeilingPublicQuery, Result<string>>, CeilingPublicHandler>();

            services.RemoveAll<IHostToTenantResolver>();
            services.AddSingleton<IHostToTenantResolver>(new OneHostResolver());

            // TransactionBehavior opens a real transaction on every request that
            // reaches step 6, and this host has no database — the ceiling refusal
            // never gets that far, but the [PublicSurface] request does, and it is
            // the one that proves the marker admits rather than merely not-refuses.
            // Same seam CrossCuttingFoundationHttpTests uses, for the same reason.
            services.RemoveAll<IUnitOfWork>();
            services.AddScoped<IUnitOfWork, NoDatabaseUnitOfWork>();
        });
    }

    private sealed class OneHostResolver : IHostToTenantResolver
    {
        public Task<HostResolution?> ResolveAsync(
            string host, CancellationToken cancellationToken = default) =>
            Task.FromResult(host == TenantHost
                ? new HostResolution(TenantId.From(Tenant), null)
                : null);
    }
}

/// <summary>Two request types that differ only in whether they are marked.</summary>
public sealed record CeilingGuardedQuery : IRequest<Result<string>>;

/// <summary>The same query, reachable from a host-only context.</summary>
[PublicSurface]
public sealed record CeilingPublicQuery : IRequest<Result<string>>;

internal sealed class CeilingGuardedHandler : IRequestHandler<CeilingGuardedQuery, Result<string>>
{
    public Task<Result<string>> Handle(CeilingGuardedQuery request, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Ok("reached"));
}

internal sealed class CeilingPublicHandler : IRequestHandler<CeilingPublicQuery, Result<string>>
{
    public Task<Result<string>> Handle(CeilingPublicQuery request, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Ok("reached"));
}

/// <summary>Drives the two request types through the real pipeline.</summary>
/// <remarks>
/// Test-only and registered by the fixture: no production <c>/api/v1</c> endpoint
/// ships in this packet, and the first real read endpoints are Phase 02d's.
/// </remarks>
[Route("ceilingprobe")]
[ApiExplorerSettings(IgnoreApi = true)]
public sealed class CeilingProbeController(IMediator mediator) : ApiControllerBase, ITestOnlyController
{
    [HttpGet("guarded")]
    public async Task<IActionResult> Guarded(CancellationToken cancellationToken) =>
        (await mediator.Send(new CeilingGuardedQuery(), cancellationToken)).ToActionResult();

    [HttpGet("public")]
    public async Task<IActionResult> Public(CancellationToken cancellationToken) =>
        (await mediator.Send(new CeilingPublicQuery(), cancellationToken)).ToActionResult();
}
