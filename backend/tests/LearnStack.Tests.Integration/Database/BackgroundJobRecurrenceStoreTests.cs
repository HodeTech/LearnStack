using System.Data.Common;
using FluentAssertions;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>Real confined-role manifest activation, coalescing and atomic progress under PostgreSQL locks.</summary>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed partial class BackgroundJobRecurrenceStoreTests(SchemaFixture schema)
{
    private static BackgroundJobRecurrenceFixture Fixture => BackgroundJobRecurrenceFixture.Shared;

    [Fact]
    public async Task Registration_converges_across_sources_and_rejects_obsolete_or_conflicting_revisions()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        var today = await TodayAsync(database);
        var original = Fixture.Manifest(today.AddDays(-10), today.AddDays(-10));
        await using var first = Source(database);
        await using var second = Source(database);
        var registrations = await Task.WhenAll(Store(first).RegisterRecurrenceAsync(original), Store(second).RegisterRecurrenceAsync(original));
        foreach (var result in registrations) { result.ThrowIfFailed(); result.CleanupConfirmed.Should().BeTrue(); }
        registrations.Select(result => result.Value).Should().BeEquivalentTo(
            [BackgroundJobRecurrenceRegistrationResult.Registered, BackgroundJobRecurrenceRegistrationResult.Existing]);

        var newer = Fixture.Manifest(today.AddDays(-2), original.Facts.Anchor, revision: 2, target: "jobsrecurrencefixture.run.v2");
        await AssertRegistrationAsync(Store(first), newer, BackgroundJobRecurrenceRegistrationResult.Registered);
        await AssertRegistrationAsync(Store(second), original, BackgroundJobRecurrenceRegistrationResult.Obsolete);
        await AssertRegistrationAsync(Store(first), Fixture.Manifest(newer.Facts.ActivatesAt, newer.Facts.Anchor, revision: 2),
            BackgroundJobRecurrenceRegistrationResult.Conflict);
        await AssertRegistrationAsync(Store(first), Fixture.Manifest(today.AddDays(-3), original.Facts.Anchor, revision: 3),
            BackgroundJobRecurrenceRegistrationResult.Conflict);
        await using var connection = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        (await ReadAsync<long>(connection, "SELECT count(*) FROM background_job_recurrences"))
            .Should().Be(2);
        (await ReadAsync<DateTimeOffset>(connection,
            "SELECT superseded_at FROM background_job_recurrences WHERE revision=1"))
            .Should().Be(newer.Facts.ActivatesAt);
        (await ReadAsync<string>(connection,
            "SELECT manifest_hash FROM background_job_recurrences WHERE revision=2"))
            .Should().Be(newer.ManifestHash);
    }

    [Fact]
    public async Task BackgroundJob_Recurrence_Is_Durable_And_Deduplicated()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        var today = await TodayAsync(database);
        // Mid-slot activation rounds upward; the next ten missed daily slots
        // coalesce to today's slot, rather than replaying the entire backlog.
        var manifest = Fixture.Manifest(today.AddDays(-11).AddHours(3), today.AddDays(-12), jitter: TimeSpan.FromMinutes(17));
        await using var first = Source(database);
        await using var second = Source(database);
        await AssertRegistrationAsync(Store(first), manifest, BackgroundJobRecurrenceRegistrationResult.Registered);
        var results = await Task.WhenAll(Store(first).MaterializeRecurrenceAsync(Fixture.Recurrences(manifest)),
            Store(second).MaterializeRecurrenceAsync(Fixture.Recurrences(manifest)));
        foreach (var result in results) { result.ThrowIfFailed(); result.CleanupConfirmed.Should().BeTrue(); }
        var occurrence = results.Select(result => result.Value).OfType<BackgroundJobMaterializedOccurrence>().Should().ContainSingle().Subject;
        occurrence.ScheduledAt.Should().Be(today);
        occurrence.SkippedSlots.Should().Be(10);
        occurrence.Id.Value.Version.Should().Be(7);
        await using var connection = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        (await ReadAsync<long>(connection, "SELECT count(*) FROM background_job_occurrences"))
            .Should().Be(1);
        (await ReadAsync<DateTimeOffset>(connection, "SELECT next_scheduled_at FROM background_job_recurrences"))
            .Should().Be(today.AddDays(1));
        (await ReadAsync<long>(connection, "SELECT jitter_microseconds FROM background_job_occurrences"))
            .Should().Be(17L * 60 * 1_000_000);
    }

    [Fact]
    public async Task New_binary_materializes_old_frozen_target_and_policy_only_before_its_activation_boundary()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        var today = await TodayAsync(database);
        var original = Fixture.Manifest(today.AddDays(-10), today.AddDays(-10));
        var newer = Fixture.Manifest(today.AddDays(-2).AddHours(12), original.Facts.Anchor, revision: 2,
            target: "jobsrecurrencefixture.run.v2", policy: BackgroundJobRecipientPolicy.ProvisionedNonArchivedTenants,
            jitter: TimeSpan.FromMinutes(1));
        await using var source = Source(database);
        var store = Store(source);
        await AssertRegistrationAsync(store, original, BackgroundJobRecurrenceRegistrationResult.Registered);
        await AssertRegistrationAsync(store, newer, BackgroundJobRecurrenceRegistrationResult.Registered);
        // Only the new manifest remains in this binary; both code target
        // definitions are intentionally still supported.
        var registry = Fixture.Recurrences(newer);
        var oldSlot = await store.MaterializeRecurrenceAsync(registry);
        oldSlot.ThrowIfFailed();
        oldSlot.Value!.Revision.Should().Be(1);
        oldSlot.Value.ScheduledAt.Should().Be(today.AddDays(-2));
        var newSlot = await store.MaterializeRecurrenceAsync(registry);
        newSlot.ThrowIfFailed();
        newSlot.Value!.Revision.Should().Be(2);
        newSlot.Value.ScheduledAt.Should().Be(today);
        await using var connection = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        (await ReadAsync<string>(connection,
            "SELECT target_definition_key||':'||recipient_policy FROM background_job_occurrences WHERE revision=1"))
            .Should().Be("jobsrecurrencefixture.run.v1:serving_tenants");
        (await ReadAsync<string>(connection,
            "SELECT target_definition_key||':'||recipient_policy FROM background_job_occurrences WHERE revision=2"))
            .Should().Be("jobsrecurrencefixture.run.v2:provisioned_non_archived_tenants");
        (await ReadAsync<string>(connection,
            "SELECT manifest_hash FROM background_job_occurrences WHERE revision=1")).Should().Be(original.ManifestHash);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Pending_or_unknown_target_defers_without_advancing_its_slot_and_yields_to_healthy_work(bool pending)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        var today = await TodayAsync(database);
        var held = Fixture.Manifest(today.AddDays(-1), today.AddDays(-1));
        var healthy = Fixture.Manifest(today.AddDays(-1), today.AddDays(-1), key: "jobsrecurrencefixture.other",
            target: "jobsrecurrencefixture.run.v2");
        await using var source = Source(database);
        var store = Store(source, Fixture.Definitions(pendingV1: pending, omitV1: !pending));
        await AssertRegistrationAsync(store, held, BackgroundJobRecurrenceRegistrationResult.Registered);
        await AssertRegistrationAsync(store, healthy, BackgroundJobRecurrenceRegistrationResult.Registered);
        var registry = Fixture.Recurrences(held, healthy);
        var deferred = await store.MaterializeRecurrenceAsync(registry);
        deferred.ThrowIfFailed();
        deferred.Value.Should().BeNull();
        var completed = await store.MaterializeRecurrenceAsync(registry);
        completed.ThrowIfFailed();
        completed.Value!.Key.Should().Be(healthy.Facts.Key);
        await using var connection = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        (await ReadAsync<DateTimeOffset>(connection,
            "SELECT next_scheduled_at FROM background_job_recurrences WHERE recurrence_key=@key", ("key", held.Facts.Key)))
            .Should().Be(held.Facts.ActivatesAt);
        (await ReadAsync<bool>(connection,
            "SELECT last_error='deployment_mismatch' AND quarantined_at IS NULL AND available_after>clock_timestamp() "
            + "FROM background_job_recurrences WHERE recurrence_key=@key", ("key", held.Facts.Key)))
            .Should().BeTrue();
    }

    [Fact]
    public async Task Future_activation_is_index_deferred_until_its_first_aligned_slot()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        var today = await TodayAsync(database);
        var manifest = Fixture.Manifest(today.AddDays(6).AddHours(3), today);
        await using var source = Source(database);
        var store = Store(source);
        await AssertRegistrationAsync(store, manifest, BackgroundJobRecurrenceRegistrationResult.Registered);
        var result = await store.MaterializeRecurrenceAsync(Fixture.Recurrences(manifest));
        result.ThrowIfFailed();
        result.Value.Should().BeNull();
        await using var connection = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        (await ReadAsync<DateTimeOffset>(connection, "SELECT next_scheduled_at FROM background_job_recurrences"))
            .Should().Be(today.AddDays(7));
        (await ReadAsync<bool>(connection, "SELECT available_after=next_scheduled_at FROM background_job_recurrences"))
            .Should().BeTrue("future manifests must not pollute the due index range");
        (await ReadAsync<long>(connection, "SELECT count(*) FROM background_job_occurrences")).Should().Be(0);
    }

    [Fact]
    public async Task Older_binary_cannot_materialize_a_newer_manifest_or_advance_its_checkpoint()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        var today = await TodayAsync(database);
        var older = Fixture.Manifest(today.AddDays(-10), today.AddDays(-10));
        var newer = Fixture.Manifest(today.AddDays(-2), older.Facts.Anchor, revision: 2, target: "jobsrecurrencefixture.run.v2");
        await using var source = Source(database);
        var store = Store(source);
        await AssertRegistrationAsync(store, older, BackgroundJobRecurrenceRegistrationResult.Registered);
        await AssertRegistrationAsync(store, newer, BackgroundJobRecurrenceRegistrationResult.Registered);
        var oldRegistry = Fixture.Recurrences(older);
        var historical = await store.MaterializeRecurrenceAsync(oldRegistry);
        historical.ThrowIfFailed();
        historical.Value!.ScheduledAt.Should().Be(today.AddDays(-3));
        var incompatible = await store.MaterializeRecurrenceAsync(oldRegistry);
        incompatible.ThrowIfFailed();
        incompatible.Value.Should().BeNull();
        await using var connection = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        (await ReadAsync<DateTimeOffset>(connection, "SELECT next_scheduled_at FROM background_job_recurrences WHERE revision=2"))
            .Should().Be(newer.Facts.ActivatesAt);
        (await ReadAsync<bool>(connection, """
            SELECT last_error='deployment_mismatch' AND quarantined_at IS NULL AND available_after>clock_timestamp()
            FROM background_job_recurrences WHERE revision=2
            """)).Should().BeTrue();
        (await ReadAsync<long>(connection, "SELECT count(*) FROM background_job_occurrences WHERE revision=2")).Should().Be(0);
    }

    [Fact]
    public async Task Occurrence_insert_failure_rolls_back_manifest_progress_and_a_clean_retry_converges()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        var today = await TodayAsync(database);
        var manifest = Fixture.Manifest(today.AddDays(-10), today.AddDays(-10));
        await using var source = Source(database);
        var store = Store(source);
        await AssertRegistrationAsync(store, manifest, BackgroundJobRecurrenceRegistrationResult.Registered);
        await using var migration = await PostgresFixture.OpenAsync(database.MigrationConnectionString);
        await SchemaQueries.ExecuteAsync(migration, null, """
            REVOKE INSERT (id,recurrence_key,revision,manifest_hash,target_definition_key,recipient_policy,
                scheduled_at,jitter_microseconds,skipped_slots) ON background_job_occurrences FROM learnstack_outbox_admin
            """);
        try
        {
            var refused = await store.MaterializeRecurrenceAsync(Fixture.Recurrences(manifest));
            var failure = () => refused.ThrowIfFailed();
            failure.Should().Throw<PostgresException>().Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
            refused.KnownRollback.Should().BeTrue();
            refused.CommitAcknowledged.Should().BeFalse();
            await using var connection = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
            (await ReadAsync<DateTimeOffset>(connection, "SELECT next_scheduled_at FROM background_job_recurrences"))
                .Should().Be(manifest.Facts.ActivatesAt);
            (await ReadAsync<long>(connection, "SELECT count(*) FROM background_job_occurrences")).Should().Be(0);
        }
        finally
        {
            await SchemaQueries.ExecuteAsync(migration, null, """
                GRANT INSERT (id,recurrence_key,revision,manifest_hash,target_definition_key,recipient_policy,
                    scheduled_at,jitter_microseconds,skipped_slots) ON background_job_occurrences TO learnstack_outbox_admin
                """);
        }
        var accepted = await store.MaterializeRecurrenceAsync(Fixture.Recurrences(manifest));
        accepted.ThrowIfFailed();
        accepted.Value!.ScheduledAt.Should().Be(today);
        accepted.CommitAcknowledged.Should().BeTrue();
    }

    private static PrivilegedDataSource Source(DisposableSchemaDatabase database) =>
        new(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));

    private static PostgresBackgroundJobControlStore Store(PrivilegedDataSource source, BackgroundJobDefinitionRegistry? definitions = null) =>
        new(source, definitions ?? Fixture.Definitions(), new(), new SystemGuidFactory());

    private static async Task AssertRegistrationAsync(PostgresBackgroundJobControlStore store,
        BackgroundJobRecurrenceDescriptor descriptor, BackgroundJobRecurrenceRegistrationResult expected)
    {
        var result = await store.RegisterRecurrenceAsync(descriptor);
        result.ThrowIfFailed();
        result.Value.Should().Be(expected);
        result.CommitAcknowledged.Should().BeTrue();
        result.CleanupConfirmed.Should().BeTrue();
    }

    private static async Task<T> ReadAsync<T>(DbConnection connection, string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, (NpgsqlConnection)connection) { CommandTimeout = 5 };
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var reader = await command.ExecuteReaderAsync(deadline.Token);
        (await reader.ReadAsync(deadline.Token)).Should().BeTrue();
        return reader.GetFieldValue<T>(0);
    }

    private static async Task<DateTimeOffset> TodayAsync(DisposableSchemaDatabase database)
    {
        await using var connection = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        return await ReadAsync<DateTimeOffset>(connection,
            "SELECT date_bin(interval '1 day',clock_timestamp(),timestamptz '2000-01-01 00:00:00+00')");
    }
}
