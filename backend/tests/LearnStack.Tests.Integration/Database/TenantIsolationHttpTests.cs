using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using LearnStack.Api.Common;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Tenancy.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Persistence;
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

namespace LearnStack.Tests.Integration.Database;

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
/// query filters, which is the path a browser takes. Not because nothing else exercises
/// the resolver — a resolver that never populates the accessor turns twelve tests red
/// across three assemblies — but because those test the resolver, and these test what a
/// tenant-owned SELECT returns at the end of it.
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
/// <b>What these cases constrain, measured in both directions.</b> They constrain the
/// composite outcome — the answer a request gets — and each read is protected by two
/// independent layers, so no single-layer mutation breaks one. Delete BOTH EF query
/// filters and all five stay green, because Row Level Security alone holds; disable RLS on
/// every tenancy table instead and the four reads stay green, because the filters alone
/// hold. Remove both and all five go red. That is defense in depth behaving as designed
/// rather than a gap, and the two halves are separately constrained elsewhere: the filters
/// by <c>Every_TenantOwned_Entity_HasFilterAndRlsPolicy</c>,
/// <c>Every_OrgScoped_Entity_HasOrgIdAndFilter</c> and
/// <c>A_context_follows_the_accessor_after_it_was_built</c>, the policies by Packet 6's
/// <c>TenancySchemaTests</c>.
/// </para>
/// <para>
/// <b>The write case is the exception, and deliberately so.</b> It issues raw SQL on the
/// ambient connection, so no filter is in front of it and only <c>WITH CHECK</c> can
/// refuse it — disabling RLS turns it red on its own. It is the one case here that
/// observes a policy directly.
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
        // Read from `tenant_settings`, the organization-scoped table class: `TenantSetting`
        // implements IOrganizationScoped and its policy carries an organization term.
        // `organizations` does not — it is tenant-owned and tenant-WIDE, so within a tenant
        // every organization is visible to every other, by design. The first version of
        // this case read that table and narrowed the rows with a `Where` the probe handler
        // wrote itself, which tested the test.
        //
        // The yoga host names an organization, so a request arriving on it is scoped to
        // that organization and must see its setting and not its sibling's. Nothing in the
        // probe narrows anything; the query filter and the policy do.
        var scoped = await ReadSettingsAsync(SeedData.Yoga.Host);

        scoped.Should().Contain(Setting("theme", SeedData.Yoga.DefaultOrganization.Slug));
        scoped.Should().NotContain(Setting("theme", SeedData.Yoga.SecondOrganization.Slug),
            "the sibling organization's row belongs to a scope this request is not in");
    }

    [Fact]
    public async Task TenantWide_Row_Of_TenantB_Is_Invisible_To_TenantA()
    {
        // The exact row shape the superseded RLS template leaked: a tenant-owned row with
        // `organization_id IS NULL`. That template wrote two PERMISSIVE policies, which
        // PostgreSQL combines with OR, so the tenant-wide arm matched for everyone and
        // every such row was visible across tenants.
        //
        // Both seed tenants carry one under the same key, so a leak shows up as a request
        // seeing the OTHER tenant's value — which makes this a statement about isolation
        // rather than about a row count.
        var english = await ReadSettingsAsync(SeedData.English.Host);
        var yoga = await ReadSettingsAsync(SeedData.Yoga.Host);

        english.Should().Contain(Setting("tz", SeedData.English.Slug));
        english.Should().NotContain(Setting("tz", SeedData.Yoga.Slug),
            "a tenant-wide row belongs to its tenant, not to everyone");

        yoga.Should().Contain(Setting("tz", SeedData.Yoga.Slug));
        yoga.Should().NotContain(Setting("tz", SeedData.English.Slug));
    }

    [Fact]
    public async Task Unsetting_tenant_context_returns_zero_rows_through_RLS()
    {
        // A request that reaches a handler with NO tenant, and reads. `localhost` is in
        // `Tenancy:PlatformHosts`, so it classifies PlatformHost — the operator's own entry
        // point — and the pipeline runs under UnresolvedTenantContext rather than refusing.
        // The announcement is then the empty string, NULLIF makes every policy predicate
        // NULL, and a tenant-owned read must come back empty.
        //
        // Not a 404 parity check. An unknown host never reaches a handler and never reads a
        // table, and HostClassificationHttpTests already pins that answer more strictly
        // than this file could. What only this case can say is what a SELECT returns when
        // the context is unresolved and the query actually runs.
        var unresolved = await ReadUnresolvedSettingsAsync("localhost");

        unresolved.Should().BeEmpty(
            "an unresolved context fails closed — the table returns nothing, not everything");

        // And the same read on a resolved host is not empty, or this would pass against a
        // probe that never queried anything.
        (await ReadSettingsAsync(SeedData.English.Host)).Should().NotBeEmpty();
    }

    [Fact]
    public async Task Write_With_Foreign_TenantId_Is_Rejected_By_WithCheck()
    {
        // An actual INSERT, on the ambient transaction, carrying a tenant_id that is not
        // the announced one — which is what the name says and what ADR-0003 Amendment 3
        // lists among the minimum cases. The first version asserted only that an anonymous
        // POST failed, and passed against a DELETED endpoint and against a database with
        // every policy dropped.
        //
        // The request arrives on demo-english's host, so the transaction is announced with
        // demo-english; the row names demo-yoga. WITH CHECK compares the two and raises
        // 42501. No layer above the database is involved — the statement is raw SQL on the
        // connection the unit of work owns, so a query filter cannot account for the
        // refusal.
        using var client = _fixture.ClientFor(SeedData.English.Host);

        var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/isolationprobe/foreign-write", UriKind.Relative),
            new { tenantId = SeedData.Yoga.TenantId.Value });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Trim('"').Should().Be("42501",
            "the policy's WITH CHECK rejects a row whose tenant is not the announced one");

        // And nothing landed, under either tenant.
        (await ReadSettingsAsync(SeedData.Yoga.Host)).Should().NotContain(
            value => value.StartsWith("smuggled", StringComparison.Ordinal));
        (await ReadSettingsAsync(SeedData.English.Host)).Should().NotContain(
            value => value.StartsWith("smuggled", StringComparison.Ordinal));
    }

    private async Task<IReadOnlyList<string>> ReadOrganizationsAsync(string host) =>
        await GetAsync(host, "organizations");

    /// <summary>One seeded setting, as the probe projects it: the stored jsonb scalar.</summary>
    private static string Setting(string key, string scope) => $"{key}=\"{scope}\"";

    private async Task<IReadOnlyList<string>> ReadSettingsAsync(string host) =>
        await GetAsync(host, "settings");

    /// <summary>The same read, on a request the pipeline runs with no tenant.</summary>
    private async Task<IReadOnlyList<string>> ReadUnresolvedSettingsAsync(string host) =>
        await GetAsync(host, "settings-unresolved");

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

        await SeedSettingsAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        // The host first, then the container: shutting the server down under a live host
        // leaves the data source disposing against a dead connection.
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    /// <summary>
    /// One tenant-wide setting per tenant, and one per organization.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>These rows are what three of the five cases are about.</b> `tenant_settings` is
    /// the organization-scoped table class — <c>TenantSetting</c> implements
    /// <c>IOrganizationScoped</c> and its policy carries an organization term — so it is
    /// the only seeded table where "organization X cannot read organization Y" is a
    /// question the platform answers. `organizations` is tenant-wide: within a tenant every
    /// organization sees every other, by design.
    /// </para>
    /// <para>
    /// <b>Written as <c>learnstack_app</c>, one organization at a time.</b> The
    /// organization-scoped <c>WITH CHECK</c> admits a row only under its own organization's
    /// context, so a single statement covering both would be refused — which is the guard
    /// working, and the reason the announcement moves between inserts.
    /// </para>
    /// <para>
    /// Not written by the seeder: settings are not a Packet 7 seed deliverable, and
    /// inventing one to serve a test would put fixture data in front of every future
    /// developer running <c>make seed</c>.
    /// </para>
    /// </remarks>
    private async Task SeedSettingsAsync()
    {
        foreach (var tenant in SeedData.All)
        {
            await using var connection = await PostgresFixture.OpenAsync(
                _postgres.AppConnectionString);
            await using var command = new NpgsqlCommand(
                $"""
                 BEGIN;
                 SELECT set_config('app.tenant_id', '{tenant.TenantId.Value}', true);
                 SELECT set_config('app.organization_id', '', true);
                 INSERT INTO tenant_settings
                     (id, tenant_id, organization_id, key, value,
                      created_at, created_by, row_version)
                 VALUES (uuidv7(), '{tenant.TenantId.Value}', NULL,
                         'tz', '"{tenant.Slug}"', now(), '{Actor}', 0);

                 SELECT set_config('app.organization_id',
                     '{tenant.DefaultOrganization.OrganizationId.Value}', true);
                 INSERT INTO tenant_settings
                     (id, tenant_id, organization_id, key, value,
                      created_at, created_by, row_version)
                 VALUES (uuidv7(), '{tenant.TenantId.Value}',
                         '{tenant.DefaultOrganization.OrganizationId.Value}',
                         'theme', '"{tenant.DefaultOrganization.Slug}"', now(), '{Actor}', 0);

                 SELECT set_config('app.organization_id',
                     '{tenant.SecondOrganization.OrganizationId.Value}', true);
                 INSERT INTO tenant_settings
                     (id, tenant_id, organization_id, key, value,
                      created_at, created_by, row_version)
                 VALUES (uuidv7(), '{tenant.TenantId.Value}',
                         '{tenant.SecondOrganization.OrganizationId.Value}',
                         'theme', '"{tenant.SecondOrganization.Slug}"', now(), '{Actor}', 0);
                 COMMIT;
                 """,
                (NpgsqlConnection)connection);

            await command.ExecuteNonQueryAsync();
        }
    }

    /// <summary>The registry-assigned actor; every seeded row is attributed to it.</summary>
    private static readonly Guid Actor = Guid.Parse("00000000-0000-7000-8000-000000000001");

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
            services.AddTransient<
                MediatR.IRequestHandler<UnresolvedProbeQuery,
                    SharedKernel.Results.Result<List<string>>>,
                ProbeQueryHandler>();
            services.AddTransient<
                MediatR.IRequestHandler<ForeignWriteCommand, SharedKernel.Results.Result<string>>,
                ForeignWriteHandler>();
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
    public async Task<IActionResult> Organizations(CancellationToken cancellationToken) =>
        (await sender.Send(new ProbeQuery(ProbeSubject.Organizations), cancellationToken))
            .ToActionResult();

    [HttpGet("settings")]
    public async Task<IActionResult> Settings(CancellationToken cancellationToken) =>
        (await sender.Send(new ProbeQuery(ProbeSubject.Settings), cancellationToken))
            .ToActionResult();

    [HttpGet("settings-unresolved")]
    public async Task<IActionResult> SettingsUnresolved(CancellationToken cancellationToken) =>
        (await sender.Send(new UnresolvedProbeQuery(), cancellationToken)).ToActionResult();

    [HttpPost("foreign-write")]
    public async Task<IActionResult> ForeignWrite(
        [FromBody] ForeignWriteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return (await sender.Send(
            new ForeignWriteCommand(request.TenantId), cancellationToken)).ToActionResult();
    }

    public sealed record ForeignWriteRequest(Guid TenantId);
}

/// <summary>What the probe reads.</summary>
public enum ProbeSubject
{
    Organizations,
    Settings,
}

/// <remarks>
/// <c>[PublicSurface]</c>, and the marker is load-bearing rather than decoration. These
/// requests arrive on a host and nothing else, so <c>TenantResolverMiddleware</c> resolves
/// them <c>HostOnly</c>, and <c>TenantContextBehavior</c>'s second gate admits that origin
/// only for a marked type. Measured: without it every read came back with an empty body —
/// the ceiling refusing, exactly as designed. It is also the honest shape:
/// [Phase 02d](../../../../docs/roadmap/phase-02d-walking-skeleton.md) renders both seed
/// tenants to anonymous visitors, so an anonymous host-only read is what production does.
/// </remarks>
[SharedKernel.Tenancy.PublicSurface]
public sealed record ProbeQuery(ProbeSubject Subject)
    : MediatR.IRequest<SharedKernel.Results.Result<List<string>>>;

/// <summary>A read on a request the pipeline runs with no tenant at all.</summary>
/// <remarks>
/// <c>[AllowsUnresolvedTenantContext]</c> because a <c>PlatformHost</c> request — one
/// arriving on an entry in <c>Tenancy:PlatformHosts</c> — resolves to no tenant by design
/// and must still be able to run. It is the only way to put a tenant-owned SELECT in front
/// of an unresolved context through a real request, which is what
/// <c>Unsetting_tenant_context_returns_zero_rows_through_RLS</c> is named for. The marker
/// on a test-assembly type is invisible to
/// <c>AllowsUnresolvedTenantContext_Only_On_Provisioning_Commands</c>, whose sweep
/// enumerates <c>backend/src</c>.
/// </remarks>
[SharedKernel.Tenancy.AllowsUnresolvedTenantContext]
public sealed record UnresolvedProbeQuery
    : MediatR.IRequest<SharedKernel.Results.Result<List<string>>>;

/// <summary>An INSERT naming a tenant other than the announced one.</summary>
[SharedKernel.Tenancy.PublicSurface]
public sealed record ForeignWriteCommand(Guid TenantId)
    : MediatR.IRequest<SharedKernel.Results.Result<string>>;

/// <summary>
/// Reads through the module context, inside the transaction the pipeline opened.
/// </summary>
/// <remarks>
/// Registered by hand in the fixture rather than by an assembly scan: the scan would also
/// re-register the pipeline behaviors the composition root already added, and a doubled
/// <c>TransactionBehavior</c> is a nested frame on every request.
/// </remarks>
public sealed class ProbeQueryHandler(TenancyDbContext db)
    : MediatR.IRequestHandler<ProbeQuery, SharedKernel.Results.Result<List<string>>>,
      MediatR.IRequestHandler<UnresolvedProbeQuery, SharedKernel.Results.Result<List<string>>>
{
    public async Task<SharedKernel.Results.Result<List<string>>> Handle(
        ProbeQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return request.Subject switch
        {
            ProbeSubject.Organizations => SharedKernel.Results.Result.Ok(
                await db.Organizations
                    .Select(organization => organization.Slug)
                    .ToListAsync(cancellationToken)),

            ProbeSubject.Settings => SharedKernel.Results.Result.Ok(
                await ReadSettingsAsync(db, cancellationToken)),

            // Exhaustive by construction, fail-closed on a member added without deciding
            // what it reads — the house style, and the opposite of falling through to
            // whichever subject happens to be last.
            _ => throw new ArgumentOutOfRangeException(
                nameof(request), request.Subject, "No probe reads that subject."),
        };
    }

    public async Task<SharedKernel.Results.Result<List<string>>> Handle(
        UnresolvedProbeQuery request, CancellationToken cancellationToken) =>
        SharedKernel.Results.Result.Ok(await ReadSettingsAsync(db, cancellationToken));

    /// <summary>
    /// Every setting the request can see, as <c>key=scope</c>.
    /// </summary>
    /// <remarks>
    /// The value is projected to the owning organization's slug — or the tenant's, for a
    /// tenant-wide row — so an assertion names what it expects to see rather than a raw
    /// setting value. No <c>Where</c>: what a request sees is the filters' and the
    /// policies' answer, and narrowing it here would be the test testing itself.
    /// </remarks>
    private static async Task<List<string>> ReadSettingsAsync(
        TenancyDbContext db, CancellationToken cancellationToken) =>
        await db.TenantSettings
            .OrderBy(setting => setting.Key)
            .Select(setting => setting.Key + "=" + setting.Value)
            .ToListAsync(cancellationToken);
}

/// <summary>
/// Issues the foreign-tenant INSERT on the connection the unit of work owns.
/// </summary>
/// <remarks>
/// Raw SQL deliberately: a write through EF would carry the query filter's tenant and
/// could never name a foreign one, so the case would prove nothing about
/// <c>WITH CHECK</c>. This is the one place in the suite that reaches past every layer
/// above the database on purpose.
/// </remarks>
public sealed class ForeignWriteHandler(IUnitOfWork unitOfWork)
    : MediatR.IRequestHandler<ForeignWriteCommand, SharedKernel.Results.Result<string>>
{
    public async Task<SharedKernel.Results.Result<string>> Handle(
        ForeignWriteCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        await using var command = (NpgsqlCommand)unitOfWork.Connection.CreateCommand();
        command.Transaction = (NpgsqlTransaction?)unitOfWork.Transaction;
        command.CommandText =
            """
            INSERT INTO tenant_settings
                (id, tenant_id, organization_id, key, value, created_at, created_by, row_version)
            VALUES (uuidv7(), @tenant, NULL, 'smuggled', '"x"', now(), @actor, 0)
            """;
        command.Parameters.AddWithValue("tenant", request.TenantId);
        command.Parameters.AddWithValue(
            "actor", Guid.Parse("00000000-0000-7000-8000-000000000001"));

        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (PostgresException refused)
        {
            // Returned rather than rethrown: the SQLSTATE is the assertion, and an
            // exception would reach the L1 handler as a 500 with the code buried.
            return SharedKernel.Results.Result.Ok(refused.SqlState);
        }

        return SharedKernel.Results.Result.Ok("committed");
    }
}
