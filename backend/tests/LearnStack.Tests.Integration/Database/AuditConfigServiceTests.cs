using System.Diagnostics.Metrics;
using FluentAssertions;
using LearnStack.Infrastructure.Audit;
using LearnStack.Infrastructure.Caching;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Time;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// The tenant override, against the real table and the real policy.
/// </summary>
/// <remarks>
/// <para>
/// <b>The branch this covers had none.</b> The unit suite classifies through a double,
/// both shipped catalogue sources declare only MUST — which the classifier short-circuits
/// before reading anything — and the module spec nevertheless said "the override branch is
/// exercised by a test that seeds the row as the migration role". It was not. This is that
/// test.
/// </para>
/// <para>
/// The rows are the ones <c>SchemaFixture</c> seeds: tenant A silences
/// <c>tenancy.organization.create</c>, tenant B silences that slug <b>and</b>
/// <c>tenancy.hostmapping.write</c>, which is tenant B's alone and is what makes the
/// cross-tenant case falsifiable. All seeded as the OWNER, because Packet 9 grants both
/// runtime roles <c>SELECT</c> on <c>audit_config</c> and nothing more — the editor that
/// authors a row lands with the Studio in Phase 06.
/// </para>
/// <para>
/// The service reads as <c>learnstack_app</c>, which is the half that matters: the loader
/// opens its own short transaction and announces the tenant itself, and a read that
/// forgot to would return zero rows <b>silently</b> — indistinguishable from "this tenant
/// has no overrides".
/// </para>
/// </remarks>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class AuditConfigServiceTests
{
    private readonly SchemaFixture _schema;

    public AuditConfigServiceTests(SchemaFixture schema) => _schema = schema;

    private const string OverriddenSlug = "tenancy.organization.create";

    [Fact]
    public async Task An_override_silences_a_SHOULD_operation()
    {
        // The only thing an override does. is_enabled = false narrows; there is no lever
        // that elevates (ADR-0033 Amendment 4 § 1).
        await using var dataSource = NpgsqlDataSource.Create(_schema.Postgres.AppConnectionString);

        var classification = await Service(dataSource).ClassifyAsync(
            TenantId.From(SchemaFixture.TenantA),
            Entry(OverriddenSlug, OperationClass.Should));

        classification.Should().Be(AuditClassification.Off,
            "the seeded row disables it, and a SHOULD is the tenant's to silence");
    }

    [Fact]
    public async Task An_override_cannot_remove_a_MUST()
    {
        // The floor, and the reason it is checked before anything is read: an attacker who
        // compromises one tenant admin must not be able to switch off the detector that
        // would catch the next cross-tenant probe. The row says false for this very slug.
        await using var dataSource = NpgsqlDataSource.Create(_schema.Postgres.AppConnectionString);

        var classification = await Service(dataSource).ClassifyAsync(
            TenantId.From(SchemaFixture.TenantA),
            Entry(OverriddenSlug, OperationClass.Must));

        classification.Should().Be(AuditClassification.Must);
    }

    [Fact]
    public async Task An_operation_with_no_row_keeps_its_declared_tier()
    {
        // An absent override reads as "no overrides", which is the safe answer — and the
        // same answer an is_enabled = true row gives, because true is the baseline.
        await using var dataSource = NpgsqlDataSource.Create(_schema.Postgres.AppConnectionString);

        var classification = await Service(dataSource).ClassifyAsync(
            TenantId.From(SchemaFixture.TenantA),
            Entry("tenancy.tenant.create", OperationClass.Should));

        classification.Should().Be(AuditClassification.Should);
    }

    [Fact]
    public async Task A_tenant_never_sees_another_tenants_overrides()
    {
        // The slug tenant B silences and tenant A has never heard of. Asking B first is
        // deliberate and both halves are load-bearing on ONE service instance, so the
        // two calls share a cache:
        //
        //   - B answering Off proves the loader announced B — a read with no tenant
        //     announced returns zero rows and would answer May here;
        //   - A then answering May proves B's warm entry did not serve A — a cache key
        //     that omitted the tenant, or a loader that announced the wrong one, answers
        //     Off and fails.
        //
        // Both tenants used to carry the same slug, under which a fully leaking read
        // produced the correct answer for both and this case could not fail at all.
        await using var dataSource = NpgsqlDataSource.Create(_schema.Postgres.AppConnectionString);
        var service = Service(dataSource);
        var betaOnly = Entry("tenancy.hostmapping.write", OperationClass.May);

        var forB = await service.ClassifyAsync(TenantId.From(SchemaFixture.TenantB), betaOnly);
        var forA = await service.ClassifyAsync(TenantId.From(SchemaFixture.TenantA), betaOnly);

        forB.Should().Be(AuditClassification.Off, "tenant B's own row silences it");
        forA.Should().Be(AuditClassification.May, "tenant A has no row about this slug");
    }

    [Fact]
    public async Task A_read_failure_falls_back_to_the_declared_tier()
    {
        // Rejecting every request platform-wide because a cache or a connection is
        // unavailable is a worse compliance outcome than losing one tenant's narrowing —
        // and the in-process catalogue carries the same MUST floor, so nothing proceeds
        // unaudited either way.
        // Port 1 with a nobody/nobody credential: nothing listens there, which is the
        // point — the connection fails before any statement, which is what a real outage
        // looks like from here.
        await using var broken = NpgsqlDataSource.Create(
            "Host=127.0.0.1;Port=1;Database=nope;Username=nobody;Password=nobody;Timeout=1"); // leakwatch:ignore

        var classification = await Service(broken).ClassifyAsync(
            TenantId.From(SchemaFixture.TenantA),
            Entry(OverriddenSlug, OperationClass.Should));

        classification.Should().Be(AuditClassification.Should,
            "the declared tier stands when the override cannot be read");
    }

    [Fact]
    public async Task A_request_with_no_resolved_tenant_has_no_overrides_to_read()
    {
        // A provisioning command's tenant does not exist yet, so there is nothing to
        // override — and reaching for the table would be a read on a connection that has
        // announced no tenant.
        await using var dataSource = NpgsqlDataSource.Create(_schema.Postgres.AppConnectionString);

        var classification = await Service(dataSource).ClassifyAsync(
            tenantId: null, Entry(OverriddenSlug, OperationClass.Should));

        classification.Should().Be(AuditClassification.Should);
    }

    [Fact]
    public void The_cache_family_is_the_one_Standards_20_registers()
    {
        // No Docker needed, but it belongs beside the reads it constrains. Standards 20
        // § ICacheService lists `{tenant_id}:audit:config`, and that table is also the
        // allowlist for the low-cardinality cache.name metric label — so a drifted
        // segment here does not merely miss the cache, it reports as `other` on a
        // dashboard while every read silently reloads.
        AuditConfigService.CacheKeyFor(TenantId.From(SchemaFixture.TenantA))
            .Should().Be($"{SchemaFixture.TenantA}:audit:config");
    }

    [Fact]
    public async Task The_projection_outlives_the_cache_implementations_own_default()
    {
        // The constant below is only worth something if the call site passes it, and the
        // one that pins it cannot tell: `null` options compile, read the same, and expire
        // this family at InMemoryCacheService's 60 seconds. So this case reads the entry
        // back through the cache at 61 seconds and again past the stated bound — which is
        // the only place the difference between the two is observable at all.
        await using var dataSource = NpgsqlDataSource.Create(_schema.Postgres.AppConnectionString);

        var meterFactory = new ServiceCollection().AddMetrics()
            .BuildServiceProvider().GetRequiredService<IMeterFactory>();
        var clock = new MovableClock(DateTimeOffset.UnixEpoch);
        var cache = new InMemoryCacheService(clock, meterFactory);

        var service = new AuditConfigService(
            cache,
            new Lazy<NpgsqlDataSource>(() => dataSource),
            NullLogger<AuditConfigService>.Instance);

        await service.ClassifyAsync(
            TenantId.From(SchemaFixture.TenantA), Entry(OverriddenSlug, OperationClass.Should));

        var key = AuditConfigService.CacheKeyFor(TenantId.From(SchemaFixture.TenantA));

        clock.Advance(TimeSpan.FromSeconds(61));

        (await cache.GetAsync<IReadOnlyDictionary<string, bool>>(key)).Should().NotBeNull(
            "61 seconds is past the implementation default and inside this family's own bound");

        clock.Advance(AuditConfigService.OverrideTtl);

        (await cache.GetAsync<IReadOnlyDictionary<string, bool>>(key)).Should().BeNull(
            "the stated bound is the whole of the staleness a tenant sees, so it has to end");
    }

    /// <summary>A clock a case moves, so a TTL is observable without waiting for one.</summary>
    private sealed class MovableClock(DateTimeOffset start) : IClock
    {
        private DateTimeOffset _now = start;

        public DateTimeOffset UtcNow => _now;

        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }

    [Fact]
    public void The_override_staleness_bound_is_the_one_Standards_20_states()
    {
        // The family has no eager invalidation until Phase 06 ships the writer, so this
        // TTL is the whole of what a tenant is promised. Inheriting InMemoryCacheService's
        // 60-second default would move a documented tenant-visible bound from an unrelated
        // file.
        AuditConfigService.OverrideTtl.Should().Be(TimeSpan.FromMinutes(5));
    }

    private static AuditCatalogEntry Entry(string operation, OperationClass operationClass) =>
        new("tenancy", operation, OperationType.Create, operationClass, typeof(object));

    /// <summary>The real service, with the cache implementation the composition roots pick.</summary>
    private static AuditConfigService Service(NpgsqlDataSource dataSource)
    {
        var meterFactory = new ServiceCollection().AddMetrics()
            .BuildServiceProvider().GetRequiredService<IMeterFactory>();

        // A cache instance per call, so one case's answer cannot satisfy the next one's
        // read — which would let a broken loader pass on a warm key.
        return new AuditConfigService(
            new InMemoryCacheService(new SystemClock(), meterFactory),
            new Lazy<NpgsqlDataSource>(() => dataSource),
            NullLogger<AuditConfigService>.Instance);
    }
}
