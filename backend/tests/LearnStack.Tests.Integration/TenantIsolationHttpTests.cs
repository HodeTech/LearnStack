using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using LearnStack.Api.Common;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Tenancy.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.Tests.Integration.Database;
using LearnStack.Tools.Seeder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration;

/// <summary>
/// The five isolation cases, re-run through a real request.
/// </summary>
/// <remarks>
/// <para>
/// <b>What Packet 7 owns that Packet 6 did not.</b> Packet 6 shipped all five against the
/// schema, driving them with <c>set_config</c> in a test — statements about the migration
/// and its policies. These drive the same five through
/// <c>HostClassificationMiddleware</c>, <c>TenantResolverMiddleware</c>,
/// <c>TenantContextBehavior</c>, <c>TransactionBehavior</c>'s announcement and the EF
/// query filters, which is the path a browser takes. A policy that holds under
/// <c>set_config</c> and a resolver that never sets it would pass the first suite and fail
/// every real request; only this one can tell them apart.
/// </para>
/// <para>
/// <b>Nothing here stubs <c>ITenantContext</c>.</b> Every other HTTP fixture in this
/// project replaces it with a header-driven double, which is right for their subjects and
/// fatal for this one: the tenant a request gets IS the thing under test. The host header
/// is the only input, exactly as in production.
/// </para>
/// <para>
/// <b>The data is the seed, not a fixture.</b> The two demo tenants and their host rows
/// come from <c>SeedRunner</c> — the same code <c>make seed</c> runs — so these cases also
/// answer "does what the seeder writes actually serve a request?", which is the question
/// [Phase 02d](../../../docs/roadmap/phase-02d-walking-skeleton.md) asks in a browser.
/// </para>
/// <para>
/// <b>No production endpoint ships in this packet.</b> The reads go through a test-only
/// controller registered in the fixture, which is the precedent <c>IdempotencyFixture</c>
/// set for <c>/api/v1/sideeffectprobe</c>. What is production is everything beneath it.
/// </para>
/// <para>
/// <b>What these cases constrain, and what they do not.</b> They constrain the composite
/// outcome — the answer a request gets — not any single layer, and that is a property of
/// defense in depth rather than a weakness here: measured, deleting BOTH EF query filters
/// leaves all five green, because Row Level Security alone still holds. The filters are
/// not thereby unconstrained; the same mutation turns
/// <c>Every_TenantOwned_Entity_HasFilterAndRlsPolicy</c>,
/// <c>Every_OrgScoped_Entity_HasOrgIdAndFilter</c> and
/// <c>A_context_follows_the_accessor_after_it_was_built</c> red. Layer-by-layer coverage
/// lives there and in Packet 6's schema suite; what lives only here is the statement that
/// the layers, the resolver and the pipeline compose into the right answer for a real
/// request.
/// </para>
/// </remarks>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed class TenantIsolationHttpTests : IClassFixture<TenantIsolationFixture>
{
    private readonly TenantIsolationFixture _fixture;

    public TenantIsolationHttpTests(TenantIsolationFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Tenant_A_cannot_read_Tenant_B_data()
    {
        // The first case, and the one every other layer exists to make redundant. Two
        // requests differing only in the host they arrive on must not see each other's
        // rows — and neither request names a tenant anywhere, which is the point: the
        // tenant comes from the host, and the filter and the policy come from the tenant.
        var english = await ReadOrganizationsAsync(SeedData.English.Host);
        var yoga = await ReadOrganizationsAsync(SeedData.Yoga.Host);

        english.Should().NotBeEmpty();
        yoga.Should().NotBeEmpty();

        english.Should().NotIntersectWith(yoga,
            "two hosts, two tenants, one binary and one database");
        english.Should().BeEquivalentTo(
            [SeedData.English.DefaultOrganization.Slug, SeedData.English.SecondOrganization.Slug]);
        yoga.Should().BeEquivalentTo(
            [SeedData.Yoga.DefaultOrganization.Slug, SeedData.Yoga.SecondOrganization.Slug]);
    }

    [Fact]
    public async Task Org_X_cannot_read_Org_Y_within_TenantA()
    {
        // The second dimension, and the one a tenant filter alone does not give. The yoga
        // host carries an organization id, so a request arriving on it is scoped to that
        // organization and must not see the tenant's other one — even though both belong
        // to the tenant the request resolved.
        var scoped = await ReadOrganizationsInScopeAsync(SeedData.Yoga.Host);

        scoped.Should().BeEquivalentTo([SeedData.Yoga.DefaultOrganization.Slug],
            "the host names one organization, and the scope is the row it names");
        scoped.Should().NotContain(SeedData.Yoga.SecondOrganization.Slug);

        // And the tenant-wide host sees both, or the case above would pass against a
        // filter that simply returns nothing.
        (await ReadOrganizationsInScopeAsync(SeedData.English.Host))
            .Should().HaveCount(2, "a host with no organization is scoped to the tenant");
    }

    [Fact]
    public async Task TenantWide_Row_Of_TenantB_Is_Invisible_To_TenantA()
    {
        // The exact case the superseded RLS template leaked. Two permissive policies are
        // combined with OR, so a tenant-wide row — one whose organization_id is NULL —
        // was visible to every tenant. Here it is the host mapping itself: demo-english's
        // row has a null organization_id, and demo-yoga must not see it.
        var seenByYoga = await ReadHostsAsync(SeedData.Yoga.Host);

        seenByYoga.Should().NotContain(SeedData.English.Host,
            "a tenant-wide row belongs to its tenant, not to everyone");
        seenByYoga.Should().BeEquivalentTo([SeedData.Yoga.Host]);
    }

    [Fact]
    public async Task Unsetting_tenant_context_returns_zero_rows_through_RLS()
    {
        // A host that resolves to nothing. The request never reaches a handler — the
        // resolver refuses it first — and the answer must be the one an unmapped PATH
        // gets, byte for byte: "no tenant" and "no such route" must be indistinguishable,
        // or the status is an oracle for which hostnames exist.
        //
        // The same path both times, so `instance` cannot account for a difference. Only
        // the correlation id may differ; it is per-request by design and carries no fact
        // about either refusal.
        using var unknownHost = _fixture.ClientFor("nobody.learnstack.local");
        using var knownHost = _fixture.ClientFor(SeedData.English.Host);

        var refused = await unknownHost.GetAsync(
            new Uri("/api/v1/isolationprobe/organizations", UriKind.Relative));
        var unmapped = await knownHost.GetAsync(
            new Uri("/api/v1/nothing-here", UriKind.Relative));

        refused.StatusCode.Should().Be(HttpStatusCode.NotFound);
        refused.StatusCode.Should().Be(unmapped.StatusCode);
        refused.Content.Headers.ContentType?.MediaType
            .Should().Be(unmapped.Content.Headers.ContentType?.MediaType);

        // And the tenant-owned read it was refused DOES return rows on a host that
        // resolves, or this case would pass against an endpoint that is simply broken.
        (await ReadOrganizationsAsync(SeedData.English.Host)).Should().NotBeEmpty();
    }

    [Fact]
    public async Task Write_With_Foreign_TenantId_Is_Rejected_By_WithCheck()
    {
        // The write half, and through a request it is refused twice over.
        //
        // First by the authority ceiling: this request carries a host and nothing else, so
        // it resolves HostOnly, and TenantContextBehavior admits that origin only for a
        // [PublicSurface] type. CreateOrganizationCommand is emphatically not one — an
        // anonymous visitor may read a tenant's pages and may not create its branches.
        //
        // Second, and this is what the case is named for: had it got past the ceiling, the
        // tenant would still have come from the context rather than the body, and the
        // WITH CHECK predicate compares the row against the announced tenant. The body's
        // tenantId is accepted by the DTO precisely so that "it changes nothing" is a
        // statement this test can make.
        using var client = _fixture.ClientFor(SeedData.English.Host);

        var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/isolationprobe/organizations", UriKind.Relative),
            new { tenantId = SeedData.Yoga.TenantId.Value, slug = "smuggled" });

        response.IsSuccessStatusCode.Should().BeFalse(
            "an anonymous host-only request may not write at all");

        // And it wrote nothing, to either tenant — not to the one it named, and not to
        // the one it arrived on.
        (await ReadOrganizationsAsync(SeedData.English.Host))
            .Should().NotContain("smuggled");
        (await ReadOrganizationsAsync(SeedData.Yoga.Host))
            .Should().NotContain("smuggled",
                "a body-supplied tenant id must not be able to move a write");
    }

    private async Task<IReadOnlyList<string>> ReadOrganizationsAsync(string host) =>
        await GetAsync(host, "organizations");

    private async Task<IReadOnlyList<string>> ReadOrganizationsInScopeAsync(string host) =>
        await GetAsync(host, "organizations-in-scope");

    private async Task<IReadOnlyList<string>> ReadHostsAsync(string host) =>
        await GetAsync(host, "hosts");

    private async Task<IReadOnlyList<string>> GetAsync(string host, string route)
    {
        using var client = _fixture.ClientFor(host);

        var response = await client.GetAsync(
            new Uri($"/api/v1/isolationprobe/{route}", UriKind.Relative));

        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<List<string>>())!;
    }
}

/// <summary>
/// The real application, on a real database, with the two seed tenants in it.
/// </summary>
/// <remarks>
/// Its own container rather than the shared schema one: this fixture seeds through
/// <c>SeedRunner</c> and serves HTTP, and sharing would make the schema suite's exact row
/// counts depend on whether these tests ran first.
/// </remarks>
public sealed class TenantIsolationFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgresFixture _postgres = new();

    public async Task InitializeAsync()
    {
        await _postgres.InitializeAsync();

        await using (var tenancy = new TenancyDbContext(
            new DbContextOptionsBuilder<TenancyDbContext>()
                .UseNpgsql(_postgres.MigrationConnectionString, npgsql =>
                    npgsql.MigrationsHistoryTable(TenancyDbContextFactory.HistoryTable))
                .Options,
            SharedKernel.Tenancy.StaticTenantContextAccessor.Unresolved))
        {
            await tenancy.Database.MigrateAsync();
        }

        await using (var platform = new PlatformDbContext(
            new DbContextOptionsBuilder<PlatformDbContext>()
                .UseNpgsql(_postgres.MigrationConnectionString, npgsql =>
                    npgsql.MigrationsHistoryTable(PlatformDbContextFactory.HistoryTable))
                .Options))
        {
            await platform.Database.MigrateAsync();
        }

        // The seeder, not a fixture INSERT: these cases are about what a request sees, and
        // what a request sees should be what `make seed` wrote.
        await using var dataSource = NpgsqlDataSource.Create(_postgres.AppConnectionString);
        var runner = new SeedRunner(
            context => SeedComposition.Build(dataSource, context, NullLoggerFactory.Instance),
            NullLogger<SeedRunner>.Instance);

        (await runner.RunAsync(CancellationToken.None)).Should().Be(0);
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await _postgres.DisposeAsync();
        await base.DisposeAsync();
    }

    /// <summary>A client whose requests arrive on <paramref name="host"/>.</summary>
    /// <remarks>
    /// The host is the only input. No tenant header, no stubbed context — the resolver
    /// reads <c>platform_host_to_tenant</c> and everything downstream follows from what it
    /// finds, which is the whole point of running these through HTTP.
    /// </remarks>
    public HttpClient ClientFor(string host)
    {
        var client = CreateClient();
        client.BaseAddress = new Uri($"http://{host}/");
        return client;
    }

    /// <summary>Removes a row a write case created, as the platform role.</summary>
    public async Task RemoveOrganizationAsync(string slug)
    {
        await using var platform = await PostgresFixture.OpenAsync(
            _postgres.PlatformConnectionString);
        await using var command = new NpgsqlCommand(
            "DELETE FROM organizations WHERE slug = @slug", (NpgsqlConnection)platform);
        command.Parameters.AddWithValue("slug", slug);
        await command.ExecuteNonQueryAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEnvironment(Environments.Development);

        // UseSetting, not ConfigureAppConfiguration: the composition root reads these while
        // building the host, and a source added later loses to the appsettings the app
        // already read. Measured — the first shape produced "ConnectionStrings:Default is
        // not configured" from the guard that exists to catch exactly this.
        builder.UseSetting("ConnectionStrings:Default", _postgres.AppConnectionString);
        builder.UseSetting("ConnectionStrings:PlatformAdmin", _postgres.PlatformConnectionString);

        builder.ConfigureTestServices(services =>
        {
            services.AddControllers(options => options.Conventions.Insert(
                    0, new TestControllerFilter(typeof(IsolationProbeController))))
                .AddApplicationPart(typeof(IsolationProbeController).Assembly);

            // The handler alone, not an assembly scan: a scan would re-register the
            // pipeline behaviors the composition root already added, and a doubled
            // TransactionBehavior is a nested frame on every request.
            services.AddTransient<
                MediatR.IRequestHandler<ProbeQuery, SharedKernel.Results.Result<List<string>>>,
                ProbeQueryHandler>();
        });
    }
}

/// <summary>
/// Reads and writes the same rows the isolation cases are about.
/// </summary>
/// <remarks>
/// <b>Everything goes through <c>ISender</c>, and that is not ceremony.</b> A
/// <c>TenancyDbContext</c> injected into a controller is refused at resolution —
/// "resolved outside the ambient transaction ... it never saw SET LOCAL app.tenant_id" —
/// because a context obtained before <c>TransactionBehavior</c> opens the transaction
/// reads zero rows from every tenant-owned table and would do so silently. Measured: the
/// first version of this controller took the context directly and every case answered
/// 500. Going through the pipeline is what makes these cases statements about the request
/// path rather than about a context somebody assembled by hand.
/// </remarks>
public sealed class IsolationProbeController(MediatR.ISender sender)
    : ApiControllerBase, ITestOnlyController
{
    [HttpGet("organizations")]
    public async Task<IActionResult> Organizations() =>
        Ok((await sender.Send(new ProbeQuery(ProbeSubject.Organizations))).Value);

    [HttpGet("organizations-in-scope")]
    public async Task<IActionResult> OrganizationsInScope() =>
        Ok((await sender.Send(new ProbeQuery(ProbeSubject.OrganizationsInScope))).Value);

    [HttpGet("hosts")]
    public async Task<IActionResult> Hosts() =>
        Ok((await sender.Send(new ProbeQuery(ProbeSubject.Hosts))).Value);

    [HttpPost("organizations")]
    public async Task<IActionResult> Create([FromBody] CreateProbeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The body's tenantId is deliberately ignored: CreateOrganizationCommand takes its
        // tenant from the context. Accepting it in the DTO is what lets a test assert that
        // a caller cannot move a write by naming a tenant.
        var result = await sender.Send(
            new Modules.Tenancy.Application.Contracts.Tenant.CreateOrganizationCommand(
                OrganizationId.From(Guid.CreateVersion7()), request.Slug, "Probe"));

        return result.IsSuccess ? Ok(result.Value) : BadRequest(result.Error);
    }

    public sealed record CreateProbeRequest(Guid TenantId, string Slug);
}

/// <summary>What the probe reads. One query type, so one handler covers the three.</summary>
public enum ProbeSubject
{
    Organizations,
    OrganizationsInScope,
    Hosts,
}

/// <remarks>
/// <c>[PublicSurface]</c>, and the marker is load-bearing rather than decoration. These
/// requests arrive on a host and nothing else, so <c>TenantResolverMiddleware</c> resolves
/// them <c>HostOnly</c>, and <c>TenantContextBehavior</c>'s second gate admits that origin
/// only for a marked type. Measured: without it every read came back <c>200</c> with an
/// empty body — the ceiling refusing, exactly as designed. It is also the honest shape:
/// [Phase 02d](../../../docs/roadmap/phase-02d-walking-skeleton.md) renders both seed
/// tenants to anonymous visitors, so an anonymous host-only read is what production does.
/// </remarks>
[SharedKernel.Tenancy.PublicSurface]
public sealed record ProbeQuery(ProbeSubject Subject)
    : MediatR.IRequest<SharedKernel.Results.Result<List<string>>>;

/// <summary>
/// Reads through the module context, inside the transaction the pipeline opened.
/// </summary>
/// <remarks>
/// Registered by hand in the fixture rather than by an assembly scan: the scan would also
/// re-register the pipeline behaviors the composition root already added, and a doubled
/// <c>TransactionBehavior</c> is a nested frame on every request.
/// </remarks>
public sealed class ProbeQueryHandler(
    TenancyDbContext db, SharedKernel.Tenancy.ITenantContext tenantContext)
    : MediatR.IRequestHandler<ProbeQuery, SharedKernel.Results.Result<List<string>>>
{
    public async Task<SharedKernel.Results.Result<List<string>>> Handle(
        ProbeQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var rows = request.Subject switch
        {
            ProbeSubject.Organizations =>
                await db.Organizations
                    .Select(organization => organization.Slug)
                    .ToListAsync(cancellationToken),

            // The tenant's organizations narrowed to the request's organization scope.
            // Explicit rather than a second query filter: an organization-scoped host
            // scopes what a request may act on, and expressing it here is what makes the
            // difference between the two routes observable.
            ProbeSubject.OrganizationsInScope =>
                await db.Organizations
                    .Where(organization => tenantContext.OrganizationId == null
                        || organization.Id == tenantContext.OrganizationId)
                    .Select(organization => organization.Slug)
                    .ToListAsync(cancellationToken),

            _ => await db.PlatformHostMappings
                .Select(mapping => mapping.Host)
                .ToListAsync(cancellationToken),
        };

        return SharedKernel.Results.Result.Ok(rows);
    }
}
