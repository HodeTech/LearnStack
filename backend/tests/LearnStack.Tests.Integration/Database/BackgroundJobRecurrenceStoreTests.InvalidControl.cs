using System.Data.Common;
using FluentAssertions;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Modules.Tenancy.Infrastructure.Jobs;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobRecurrenceStoreTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Invalid_stored_timestamp_is_quarantined_before_the_next_healthy_recurrence_quantum(bool activation)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        var today = await TodayAsync(database);
        var healthy = Fixture.Manifest(today.AddDays(-1), today.AddDays(-1));
        await using var source = Source(database);
        var store = Store(source);
        await AssertRegistrationAsync(store, healthy, BackgroundJobRecurrenceRegistrationResult.Registered);
        await using var connection = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        await InsertInvalidManifestAsync(connection, activation ? "activation" : "anchor");
        var registry = Fixture.Recurrences(healthy);
        var first = await store.MaterializeRecurrenceAsync(registry);
        var second = await store.MaterializeRecurrenceAsync(registry);
        var quarantined = await ReadAsync<bool>(connection, """
            SELECT quarantined_at IS NOT NULL AND last_error='invalid_control'
            FROM background_job_recurrences WHERE recurrence_key='jobsrecurrencefixture.poison'
            """);
        (first.Failed, second.Failed, quarantined, second.Value?.Key).Should()
            .Be((false, false, true, healthy.Facts.Key), "malformed stored facts cannot poison every due quantum");
        first.CleanupConfirmed.Should().BeTrue();
        first.CommitAcknowledged.Should().BeTrue();
        first.Value.Should().BeNull("invalid facts grant no occurrence authority");
        second.CleanupConfirmed.Should().BeTrue();
        second.CommitAcknowledged.Should().BeTrue();
        (await ReadAsync<long>(connection, "SELECT count(*) FROM background_job_occurrences")).Should().Be(1);
    }


    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Invalid_occurrence_facts_quarantine_only_the_locked_row_before_a_healthy_page(bool activation)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using (var app = await PostgresFixture.OpenAsync(database.AppConnectionString))
            await SeedLifecycleTenantAsync(app, SchemaFixture.TenantA, SchemaFixture.OrgA1, "Active", false, true);
        var today = await TodayAsync(database);
        var healthy = Fixture.Manifest(today.AddDays(-1), today.AddDays(-1));
        await using var source = Source(database);
        var store = Store(source);
        await AssertRegistrationAsync(store, healthy, BackgroundJobRecurrenceRegistrationResult.Registered);
        var registry = Fixture.Recurrences(healthy);
        var materialized = await store.MaterializeRecurrenceAsync(registry);
        materialized.ThrowIfFailed();
        materialized.Value.Should().NotBeNull();
        await using var connection = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        await InsertInvalidManifestAsync(connection, activation ? "activation" : "anchor");
        var invalid = await InsertInvalidOccurrenceAsync(connection, activation);
        var projection = BackgroundJobLifecycleProjectionDeclaration.Create();
        var first = await store.MaterializeRecipientPageAsync(registry, projection);
        first.ThrowIfFailed();
        first.Value.Should().BeNull();
        first.CommitAcknowledged.Should().BeTrue();
        first.CleanupConfirmed.Should().BeTrue();
        (await ReadAsync<bool>(connection, """
            SELECT quarantined_at IS NOT NULL AND last_error='invalid_control' AND bounds_frozen_at IS NULL
                AND upper_tenant_id IS NULL AND after_tenant_id IS NULL AND ownership_token IS NULL
                AND completed_at IS NULL FROM background_job_occurrences WHERE id=@id
            """, ("id", invalid))).Should().BeTrue();
        (await ReadAsync<long>(connection, "SELECT count(*) FROM background_job_recipients")).Should().Be(0);
        var second = await store.MaterializeRecipientPageAsync(registry, projection);
        second.ThrowIfFailed();
        second.CommitAcknowledged.Should().BeTrue();
        second.CleanupConfirmed.Should().BeTrue();
        second.Value.Should().BeEquivalentTo(new { OccurrenceId = materialized.Value!.Id.Value, InsertedRecipients = 1, Completed = true });
        (await ReadAsync<long>(connection, "SELECT count(*) FROM background_job_occurrences WHERE quarantined_at IS NOT NULL")).Should().Be(1);
        (await ReadAsync<long>(connection, "SELECT count(*) FROM background_job_recurrences WHERE quarantined_at IS NOT NULL")).Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Invalid_recipient_facts_quarantine_only_the_locked_route_without_minting_child_authority(bool activation)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = Source(database);
        var store = Store(source);
        var healthy = await PrepareClaimRouteAsync(database, store, organization: false);
        await using var connection = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        await InsertInvalidManifestAsync(connection, activation ? "activation" : "anchor");
        var occurrence = await InsertInvalidOccurrenceAsync(connection, activation);
        var invalid = Guid.CreateVersion7();
        await InvalidControlSqlAsync(connection, """
            UPDATE background_job_occurrences SET ownership_token=@token,ownership_until=clock_timestamp()+interval '1 minute'
            WHERE id=@occurrence;
            UPDATE background_job_occurrences SET bounds_frozen_at=clock_timestamp(),upper_tenant_id=@tenant
            WHERE id=@occurrence;
            INSERT INTO background_job_recipients
                (id,occurrence_id,tenant_id,recurrence_key,revision,manifest_hash,target_definition_key,
                 recipient_policy,scheduled_at,due_at)
            SELECT @id,o.id,@tenant,o.recurrence_key,o.revision,o.manifest_hash,o.target_definition_key,
                   o.recipient_policy,o.scheduled_at,o.scheduled_at
            FROM background_job_occurrences o WHERE o.id=@occurrence
            """, ("token", Guid.NewGuid()), ("occurrence", occurrence), ("tenant", SchemaFixture.TenantA), ("id", invalid));
        var registry = Fixture.Recurrences(healthy);
        var first = await store.TryClaimRecipientAsync(registry);
        first.ThrowIfFailed();
        first.Value.Should().BeNull("an invalid route grants neither a claim nor a business child");
        first.CommitAcknowledged.Should().BeTrue();
        first.CleanupConfirmed.Should().BeTrue();
        (await ReadAsync<bool>(connection, """
            SELECT quarantined_at IS NOT NULL AND last_error='invalid_control' AND ownership_token IS NULL
                AND accepted_job_id IS NULL AND skipped_at IS NULL FROM background_job_recipients WHERE id=@id
            """, ("id", invalid))).Should().BeTrue();
        var second = await store.TryClaimRecipientAsync(registry);
        second.ThrowIfFailed();
        second.CommitAcknowledged.Should().BeTrue();
        second.CleanupConfirmed.Should().BeTrue();
        second.Value.Should().NotBeNull();
        second.Value!.RecurrenceKey.Should().Be(healthy.Facts.Key);
        second.Value.HasLiveLease.Should().BeTrue();
        (await ReadAsync<bool>(connection, """
            SELECT ownership_token=@token AND ownership_until>clock_timestamp() AND quarantined_at IS NULL
            FROM background_job_recipients WHERE id=@id
            """, ("id", second.Value.RecipientId.Value), ("token", second.Value.Token))).Should().BeTrue();
        (await ReadAsync<long>(connection, "SELECT count(*) FROM background_jobs")).Should().Be(0);
        (await ReadAsync<long>(connection, "SELECT count(*) FROM background_job_recipients WHERE quarantined_at IS NOT NULL")).Should().Be(1);
        (await ReadAsync<long>(connection, "SELECT count(*) FROM background_job_occurrences WHERE quarantined_at IS NOT NULL")).Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Higher_revision_compares_a_nonrepresentable_incumbent_activation_in_postgres(bool future)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = Source(database);
        await using var connection = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        await InsertInvalidManifestAsync(connection, future ? "future_activation" : "activation");
        var today = await TodayAsync(database);
        var newer = Fixture.Manifest(today, today, revision: 2, key: "jobsrecurrencefixture.poison");
        var result = await Store(source).RegisterRecurrenceAsync(newer);
        result.ThrowIfFailed();
        result.Value.Should().Be(future ? BackgroundJobRecurrenceRegistrationResult.Conflict : BackgroundJobRecurrenceRegistrationResult.Registered);
        result.CommitAcknowledged.Should().BeTrue();
        result.CleanupConfirmed.Should().BeTrue();
        (await ReadAsync<long>(connection, "SELECT count(*) FROM background_job_recurrences")).Should().Be(future ? 1 : 2);
        (await ReadAsync<bool>(connection, """
            SELECT quarantined_at IS NULL AND
                CASE WHEN @future THEN superseded_at IS NULL ELSE superseded_at=@activation END
            FROM background_job_recurrences WHERE revision=1
            """, ("future", future), ("activation", newer.Facts.ActivatesAt))).Should().BeTrue();
        if (!future)
            (await ReadAsync<string>(connection, "SELECT manifest_hash FROM background_job_recurrences WHERE revision=2"))
                .Should().Be(newer.ManifestHash);
    }

    // All fixtures below use the original confined-role grants and live guards.
    // BC own dates require BC activation; these cases do not independently
    // qualify own-date checks hidden by the absent-manifest short circuit.
    private static Task InsertInvalidManifestAsync(DbConnection connection, string mode) =>
        InvalidControlSqlAsync(connection, """
            INSERT INTO background_job_recurrences
                (recurrence_key,revision,manifest_hash,target_definition_key,schedule,activates_at,
                 anchor_at,period_microseconds,jitter_microseconds,recipient_policy,next_scheduled_at)
            VALUES ('jobsrecurrencefixture.poison',1,repeat('0',64),'jobsrecurrencefixture.run.v1','daily',
                CASE WHEN @mode='activation' THEN timestamptz '0001-01-01 00:00:00 BC'
                     WHEN @mode='future_activation' THEN timestamptz '10000-01-01 00:00:00+00'
                     ELSE timestamptz '2000-01-01 00:00:00+00' END,
                CASE WHEN @mode='anchor' THEN timestamptz '10000-01-01 00:00:00+00'
                     ELSE timestamptz '2000-01-01 00:00:00+00' END,
                86400000000,0,'serving_tenants',
                CASE WHEN @mode='future_activation' THEN timestamptz '10000-01-01 00:00:00+00'
                     ELSE timestamptz '2000-01-01 00:00:00+00' END);
            UPDATE background_job_recurrences SET available_after='2000-01-01 00:00:00+00'
            WHERE recurrence_key='jobsrecurrencefixture.poison'
            """, ("mode", mode));

    private static async Task<Guid> InsertInvalidOccurrenceAsync(DbConnection connection, bool activation)
    {
        var id = Guid.CreateVersion7();
        await InvalidControlSqlAsync(connection, """
            INSERT INTO background_job_occurrences
                (id,recurrence_key,revision,manifest_hash,target_definition_key,recipient_policy,scheduled_at,jitter_microseconds,skipped_slots)
            VALUES (@id,'jobsrecurrencefixture.poison',1,repeat('0',64),'jobsrecurrencefixture.run.v1','serving_tenants',
                CASE WHEN @activation THEN timestamptz '0001-01-01 00:00:00 BC'
                     ELSE timestamptz '2000-01-01 00:00:00+00' END,0,0);
            UPDATE background_job_occurrences SET available_after='2000-01-01 00:00:00+00' WHERE id=@id
            """, ("id", id), ("activation", activation));
        return id;
    }

    private static async Task InvalidControlSqlAsync(DbConnection connection, string sql, params (string Name, object Value)[] values)
    {
        await using var command = new NpgsqlCommand(sql, (NpgsqlConnection)connection) { CommandTimeout = 5 };
        foreach (var value in values) command.Parameters.AddWithValue(value.Name, value.Value);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await command.ExecuteNonQueryAsync(deadline.Token);
    }
}
