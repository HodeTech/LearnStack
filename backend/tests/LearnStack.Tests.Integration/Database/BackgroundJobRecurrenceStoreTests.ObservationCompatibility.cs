using FluentAssertions;
using LearnStack.Infrastructure.Jobs;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobRecurrenceStoreTests
{
    [Theory]
    [InlineData("compatible", 0)]
    [InlineData("older_binary", 4)]
    [InlineData("same_revision_different_hash", 4)]
    public async Task Observation_checks_current_revision_without_rejecting_supported_frozen_history(string deployment, int expectedCode)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = Source(database);
        var store = Store(source);
        var today = await TodayAsync(database);
        var original = Fixture.Manifest(today.AddDays(-10), today.AddDays(-10));
        var newer = Fixture.Manifest(today.AddDays(3), original.Facts.Anchor,
            revision: 2, target: "jobsrecurrencefixture.run.v2");
        await AssertRegistrationAsync(store, original, BackgroundJobRecurrenceRegistrationResult.Registered);
        await AssertRegistrationAsync(store, newer, BackgroundJobRecurrenceRegistrationResult.Registered);
        var local = deployment switch
        {
            "compatible" => newer,
            "older_binary" => original,
            "same_revision_different_hash" => Fixture.Manifest(newer.Facts.ActivatesAt, original.Facts.Anchor, revision: 2),
            _ => throw new ArgumentOutOfRangeException(nameof(deployment)),
        };
        var observation = await store.ReadDurableObservationAsync(
            BackgroundJobObservationQueries.Manifest(Fixture.Definitions(), Fixture.Recurrences(local)), []);
        observation.ThrowIfFailed();
        observation.CleanupConfirmed.Should().BeTrue();
        var sample = observation.Value!;
        sample.HasDeferredWork.Should().Be(expectedCode != 0);
        sample.HasQuarantinedControl.Should().BeFalse();
        var deferred = sample.Points.Where(point => point.Family == BackgroundJobObservationFamily.Deferred);
        if (expectedCode == 0) deferred.Should().BeEmpty();
        else deferred.Should().ContainSingle().Which.Should().Be(new BackgroundJobObservationPoint(
            BackgroundJobObservationFamily.Deferred, "recurrence", expectedCode, 1, 0, 0));
        sample.Points.Where(point => point.Family == BackgroundJobObservationFamily.FanoutPending)
            .Should().ContainSingle().Which.Should().Be(new BackgroundJobObservationPoint(
                BackgroundJobObservationFamily.FanoutPending, original.Facts.Key, 1, 2, 0, 0));
        await using var dispatcher = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        (await ReadAsync<bool>(dispatcher, """
            SELECT count(*)=2 AND bool_and(last_error IS NULL AND quarantined_at IS NULL AND ownership_token IS NULL)
                AND bool_and(CASE WHEN revision=1 THEN manifest_hash=@old ELSE manifest_hash=@new END)
            FROM background_job_recurrences
            """, ("old", original.ManifestHash), ("new", newer.ManifestHash))).Should().BeTrue();
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("activation")]
    [InlineData("anchor")]
    [InlineData("future_activation")]
    [InlineData("future_slot")]
    public async Task Observation_refuses_independently_invalid_frozen_facts_without_overflow_or_mutating_a_marker(string mode)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = Source(database);
        var store = Store(source);
        var today = await TodayAsync(database);
        var local = Fixture.Manifest(today, today, revision: 2, key: "jobsrecurrencefixture.poison");
        await using var dispatcher = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        if (mode == "future_slot")
        {
            await AssertRegistrationAsync(store, local, BackgroundJobRecurrenceRegistrationResult.Registered);
            await InvalidControlSqlAsync(dispatcher, """
                UPDATE background_job_recurrences SET next_scheduled_at=timestamptz '10000-01-01 00:00:00+00'
                """);
        }
        else
        {
            // The stored revision is lower, so comparing only the local manifest
            // cannot detect its independently forged hash or non-.NET timestamps.
            await InsertInvalidManifestAsync(dispatcher, mode);
        }
        var manifest = BackgroundJobObservationQueries.Manifest(Fixture.Definitions(), Fixture.Recurrences(local));
        var atoms = (await ReadObservationHashesAsync((NpgsqlConnection)dispatcher, manifest)).Should().ContainSingle().Subject;
        if (mode is "activation" or "anchor" or "future_activation")
        {
            AssertInvalidObservationDate(atoms);
            var dirty = (await ReadObservationHashesAsync((NpgsqlConnection)dispatcher, manifest,
                ObservationHashMutation.IgnoreDateBounds)).Should().ContainSingle().Subject;
            Action detector = () => AssertInvalidObservationDate(dirty);
            detector.Should().Throw<Xunit.Sdk.XunitException>();
        }
        else
        {
            atoms.AtomsValid.Should().BeTrue();
            if (mode == "hash") atoms.Calculated.Should().NotBe(atoms.Stored);
            else atoms.Calculated.Should().Be(local.ManifestHash);
        }
        var observed = await store.ReadDurableObservationAsync(manifest, []);
        observed.ThrowIfFailed();
        observed.CleanupConfirmed.Should().BeTrue();
        var sample = observed.Value!;
        sample.Points.Where(point => point.Family == BackgroundJobObservationFamily.Deferred)
            .Should().ContainSingle().Which.Should().Be(new BackgroundJobObservationPoint(
                BackgroundJobObservationFamily.Deferred, "recurrence", 5, 1, 0, 0));
        sample.HasQuarantinedControl.Should().BeFalse("observation reports invalid facts without executing a quarantine transition");
        (await ReadAsync<bool>(dispatcher, """
            SELECT count(*)=1 AND bool_and(last_error IS NULL AND quarantined_at IS NULL AND ownership_token IS NULL)
            FROM background_job_recurrences
            """)).Should().BeTrue();
    }

    private static void AssertInvalidObservationDate(ObservationHashRow atoms)
    {
        atoms.AtomsValid.Should().BeFalse("the date guard must fail independently of the also-invalid stored hash");
        atoms.Calculated.Should().BeNull("non-.NET dates must not enter the hash arithmetic");
    }
}
