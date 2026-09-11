using System.Diagnostics.Metrics;
using FluentAssertions;
using LearnStack.Infrastructure.Caching;
using LearnStack.Modules.Tenancy.Infrastructure;
using LearnStack.SharedKernel.Caching;
using LearnStack.SharedKernel.Entitlements;
using LearnStack.SharedKernel.Time;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// The killswitch read path, against the real table and the real policy.
/// </summary>
/// <remarks>
/// It reads as <c>learnstack_app</c>, which is the half that matters: that role holds
/// <c>SELECT</c> and nothing else, and the only policy on the table is
/// <c>USING (true)</c> — so a read that returned nothing would fail OPEN and look exactly
/// like a deployment with no switch flipped.
/// </remarks>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class KillswitchOverlayTests
{
    private readonly SchemaFixture _schema;

    public KillswitchOverlayTests(SchemaFixture schema) => _schema = schema;

    [Fact]
    public async Task An_unflipped_switch_reads_as_enabled()
    {
        // The fixture seeds killswitch.classroom.recording enabled, which is the state
        // every switch is in until an incident.
        (await Overlay(Source()).IsEnabledAsync(KillswitchKeys.RecordingEnabled))
            .Should().BeTrue();
    }

    [Fact]
    public async Task A_switch_with_no_row_reads_as_enabled()
    {
        // An absent row and `true` mean the same thing: nobody has flipped it. The fixture
        // seeds one of the three, so the other two are the absent case.
        (await Overlay(Source()).IsEnabledAsync(KillswitchKeys.EmailDispatchEnabled))
            .Should().BeTrue();
    }

    [Fact]
    public async Task A_flipped_switch_reads_as_disabled_through_the_policy()
    {
        // The half that proves the read is real rather than a default. Written as
        // learnstack_platform — the role every toggle will use, and the only one that can
        // write here — and read back as learnstack_app through USING (true).
        await using (var platform = await PostgresFixture.OpenAsync(
            _schema.Postgres.PlatformConnectionString))
        {
            await using var flip = new NpgsqlCommand(
                """
                INSERT INTO platform_killswitches (key, is_enabled, reason, toggled_at, toggled_by)
                VALUES ('killswitch.analytics.ingest', false, 'probe', now(), NULL)
                """,
                (NpgsqlConnection)platform);
            await flip.ExecuteNonQueryAsync();
        }

        try
        {
            (await Overlay(Source()).IsEnabledAsync(KillswitchKeys.AnalyticsIngestEnabled))
            .Should().BeFalse("the row says so, and the row is the whole point");
        }
        finally
        {
            await using var platform = await PostgresFixture.OpenAsync(
                _schema.Postgres.PlatformConnectionString);
            await using var clean = new NpgsqlCommand(
                "DELETE FROM platform_killswitches WHERE key = 'killswitch.analytics.ingest'",
                (NpgsqlConnection)platform);
            await clean.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task The_whole_set_is_one_cache_entry()
    {
        // One entry, not one per key: a toggle has to invalidate a single key, because
        // ICacheService deliberately has no RemoveByPrefixAsync to sweep a per-key family
        // with. Asserted by reading the cache directly under the key the factory composes.
        var cache = Cache();

        await Overlay(Source(), cache).IsEnabledAsync(KillswitchKeys.RecordingEnabled);
        await Overlay(Source(), cache).IsEnabledAsync(KillswitchKeys.EmailDispatchEnabled);

        var entry = await cache.GetAsync<IReadOnlyDictionary<string, bool>>(
            CacheKey.ForKillswitchOverlay());

        entry.Should().NotBeNull();
        entry!.Should().ContainKey("killswitch.classroom.recording");
    }

    [Fact]
    public async Task A_read_failure_resolves_to_enabled_rather_than_disabling_everything()
    {
        // A cache or database outage must not disable every gated path platform-wide. The
        // context points at a port nothing listens on, so the load throws before any
        // statement — which is what a real outage looks like from here.
        var broken = new Lazy<NpgsqlDataSource>(() => NpgsqlDataSource.Create(
            "Host=127.0.0.1;Port=1;Database=nope;Username=nobody;Password=nobody;Timeout=1")); // leakwatch:ignore

        (await Overlay(broken).IsEnabledAsync(KillswitchKeys.RecordingEnabled))
            .Should().BeTrue("a killswitch nobody can read is one that fails open, and that "
                + "is the correct direction because flipping one is the exceptional act");
    }

    [Fact]
    public async Task A_cancelled_read_is_not_swallowed_as_an_outage()
    {
        // The filter on the catch. Without it a cancellation is reported as "the overlay
        // could not be read" and answered `true` — so an aborted request continues into
        // whatever the killswitch was gating, and a shutdown-time cancellation hides
        // behind an Error line that reads like a database outage.
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var act = async () =>
            await Overlay(Source()).IsEnabledAsync(KillswitchKeys.RecordingEnabled, cancelled.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task A_switch_the_registry_does_not_declare_resolves_to_enabled()
    {
        // Nothing can flip it, so nothing should be gating on it. Answering enabled is the
        // honest result; the Error line is what says the caller is asking about a switch
        // that does not exist.
        (await Overlay(Source()).IsEnabledAsync(new KillswitchKey("killswitch.nobody.declared")))
            .Should().BeTrue();
    }

    /// <summary>
    /// A context with NO resolved tenant, deliberately.
    /// </summary>
    /// <remarks>
    /// The killswitch table carries no tenant column and its policy is
    /// <c>USING (true)</c>, so the overlay must answer for a caller that has no tenant at
    /// all — a platform-host request, or a job. Announcing one here would hide a
    /// dependency the shipped code must not have.
    /// </remarks>
    /// <summary>The application data source, as the composition root hands it over.</summary>
    private Lazy<NpgsqlDataSource> Source() =>
        new(() => NpgsqlDataSource.Create(_schema.Postgres.AppConnectionString));

    private static InMemoryCacheService Cache() =>
        new(new SystemClock(),
            new ServiceCollection().AddMetrics().BuildServiceProvider()
                .GetRequiredService<IMeterFactory>());

    private static KillswitchOverlay Overlay(
        Lazy<NpgsqlDataSource> source, ICacheService? cache = null) =>
        new(source, cache ?? Cache(), NullLogger<KillswitchOverlay>.Instance);
}
