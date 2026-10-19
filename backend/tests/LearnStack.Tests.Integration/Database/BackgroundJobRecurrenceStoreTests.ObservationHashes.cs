using FluentAssertions;
using LearnStack.Infrastructure.Jobs;
using LearnStack.SharedKernel.Jobs;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobRecurrenceStoreTests
{
    [Fact]
    public async Task Actual_observation_hash_matches_frozen_dotnet_manifests_across_schedules_policies_precision_and_session_formats()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = Source(database);
        var store = Store(source);
        var today = await TodayAsync(database);
        var historical = Fixture.Manifest(today.AddDays(-2), today.AddDays(-2), key: "jobsrecurrencefixture.hash_history");
        await AssertRegistrationAsync(store, historical, BackgroundJobRecurrenceRegistrationResult.Registered);
        var occurrence = await store.MaterializeRecurrenceAsync(Fixture.Recurrences(historical));
        occurrence.ThrowIfFailed();
        occurrence.Value.Should().NotBeNull();
        var successor = Fixture.Manifest(today, historical.Facts.Anchor, revision: 2,
            key: historical.Facts.Key, target: "jobsrecurrencefixture.run.v2");
        await AssertRegistrationAsync(store, successor, BackgroundJobRecurrenceRegistrationResult.Registered);
        var dates = new[]
        {
            DateTimeOffset.MinValue.AddTicks(10),
            new DateTimeOffset(1960, 2, 29, 12, 34, 56, TimeSpan.Zero).AddTicks(1_234_560),
            DateTimeOffset.UnixEpoch.AddTicks(-10),
            DateTimeOffset.UnixEpoch.AddTicks(10),
            new DateTimeOffset(2024, 2, 29, 3, 4, 5, TimeSpan.Zero).AddTicks(1_234_560),
            DateTimeOffset.MaxValue.AddTicks(-9),
        };
        var manifests = new List<BackgroundJobRecurrenceDescriptor>();
        var declaration = Fixture.Manifest(dates[1], dates[1]).Registration.DeclaringType;
        foreach (var schedule in Enum.GetValues<BackgroundJobRecurrenceSchedule>())
            foreach (var policy in Enum.GetValues<BackgroundJobRecipientPolicy>())
                for (var i = 0; i < dates.Length; i++)
                {
                    var period = schedule == BackgroundJobRecurrenceSchedule.Daily ? TimeSpan.FromDays(1) : i switch
                    {
                        0 or 2 => TimeSpan.FromMinutes(1),
                        1 => TimeSpan.FromMinutes(1).Add(TimeSpan.FromTicks(10)),
                        3 => TimeSpan.FromDays(1).Add(TimeSpan.FromTicks(10)),
                        4 => TimeSpan.FromHours(2),
                        _ => TimeSpan.FromDays(366),
                    };
                    var jitter = i switch
                    {
                        0 => TimeSpan.Zero,
                        1 => TimeSpan.FromTicks(10),
                        2 => TimeSpan.FromMinutes(1),
                        _ => TimeSpan.FromHours(2),
                    };
                    var key = FormattableString.Invariant($"jobsrecurrencefixture.hash_s{(int)schedule}_p{(int)policy}_v{i}");
                    var target = policy == BackgroundJobRecipientPolicy.ServingOrganizations
                        ? "jobsrecurrencefixture.organization.v1" : "jobsrecurrencefixture.run.v1";
                    // Unequal modern atoms expose a swapped/repeated hash field;
                    // equal extreme atoms keep initial slot rounding representable.
                    var anchor = i is 1 or 4 ? dates[i].AddHours(-6).AddTicks(-10) : dates[i];
                    var registration = new BackgroundJobRecurrenceRegistration(key, declaration, i == 5 ? long.MaxValue : i + 1, dates[i],
                        new(target), schedule, anchor, period, jitter, policy);
                    var registry = BackgroundJobRecurrenceRegistry.Create([registration], Fixture.Definitions(), [Fixture.Module]);
                    registry.TryGet(key, out var manifest).Should().BeTrue();
                    await AssertRegistrationAsync(store, manifest!, BackgroundJobRecurrenceRegistrationResult.Registered);
                    manifests.Add(manifest!);
                }
        manifests.Should().HaveCount(36);
        var expected = manifests.Concat([historical, successor])
            .ToDictionary(item => (item.Facts.Key, item.Facts.Revision), item => item.ManifestHash);
        var bound = BackgroundJobObservationQueries.Manifest(Fixture.Definitions(), Fixture.Recurrences([.. manifests, successor]));
        await using var dispatcher = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        (await ReadAsync<bool>(dispatcher, """
            SELECT next_scheduled_at>=superseded_at FROM background_job_recurrences
            WHERE recurrence_key=@key AND revision=1
            """, ("key", historical.Facts.Key))).Should().BeTrue(
                "only the pending occurrence can make this fully superseded historical manifest enter the hash population");
        foreach (var settings in new[]
                 {
                     "SET TIME ZONE 'UTC'; SET DateStyle TO 'ISO, MDY'",
                     "SET TIME ZONE 'Pacific/Chatham'; SET DateStyle TO 'SQL, DMY'",
                 })
        {
            await InvalidControlSqlAsync(dispatcher, settings);
            AssertObservationHashes(await ReadObservationHashesAsync((NpgsqlConnection)dispatcher, bound), expected);
            // The same independent .NET oracle rejects actual canonical atom
            // mutations, rather than agreeing with copied test SQL.
            foreach (var mutation in new[] { ObservationHashMutation.EpochOffset,
                         ObservationHashMutation.RepeatActivationForAnchor, ObservationHashMutation.OmitOccurrenceHistory })
            {
                var dirty = await ReadObservationHashesAsync((NpgsqlConnection)dispatcher, bound, mutation);
                Action detector = () => AssertObservationHashes(dirty, expected);
                detector.Should().Throw<Xunit.Sdk.XunitException>();
            }
            var complete = await store.ReadDurableObservationAsync(bound, []);
            complete.ThrowIfFailed();
            complete.CleanupConfirmed.Should().BeTrue();
            complete.Value!.HasDeferredWork.Should().BeFalse("the unmodified full statement must accept the same valid population");
        }
    }

    private static void AssertObservationHashes(IReadOnlyList<ObservationHashRow> actual,
        IReadOnlyDictionary<(string Key, long Revision), string> expected)
    {
        actual.Select(row => (row.Key, row.Revision)).Should().BeEquivalentTo(expected.Keys);
        foreach (var row in actual)
        {
            row.AtomsValid.Should().BeTrue();
            row.Stored.Should().Be(expected[(row.Key, row.Revision)]);
            row.Calculated.Should().Be(expected[(row.Key, row.Revision)],
                "the real SQL canonical byte representation must equal the registry's independent .NET fingerprint");
            row.Disposition.Should().Be(0);
        }
    }

    private sealed record ObservationHashRow(string Key, long Revision, bool AtomsValid,
        string? Calculated, string Stored, int Disposition);
    private enum ObservationHashMutation { None, EpochOffset, RepeatActivationForAnchor, OmitOccurrenceHistory, IgnoreDateBounds }

    private static async Task<IReadOnlyList<ObservationHashRow>> ReadObservationHashesAsync(NpgsqlConnection connection,
        BackgroundJobObservationManifest manifest, ObservationHashMutation mutation = ObservationHashMutation.None)
    {
        const string projection = "SELECT family AS observation_family,";
        const string suffix = "FROM output ORDER BY family,label,code;";
        var sql = PostgresBackgroundJobControlStore.DurableObservationSql;
        var at = sql.IndexOf(projection, StringComparison.Ordinal);
        at.Should().BeGreaterThan(0);
        sql.EndsWith(suffix, StringComparison.Ordinal).Should().BeTrue();
        sql.LastIndexOf(projection, StringComparison.Ordinal).Should().Be(at);
        // Only the terminal projection changes. All hash/validity/compatibility
        // CTEs and typed metadata binding are the production statement itself.
        sql = sql[..at] + "SELECT recurrence_key,revision,atoms_ok,calculated_hash,manifest_hash,disposition "
            + "FROM manifest_class ORDER BY recurrence_key,revision;";
        if (mutation == ObservationHashMutation.EpochOffset)
        {
            sql.Split("621355968000000000", StringSplitOptions.None).Should().HaveCount(3);
            sql = sql.Replace("621355968000000000", "621355968000000001", StringComparison.Ordinal);
        }
        else if (mutation == ObservationHashMutation.RepeatActivationForAnchor)
        {
            sql.Split("extract(epoch FROM m.anchor_at)", StringSplitOptions.None).Should().HaveCount(2);
            sql = sql.Replace("extract(epoch FROM m.anchor_at)", "extract(epoch FROM m.activates_at)", StringComparison.Ordinal);
        }
        else if (mutation == ObservationHashMutation.OmitOccurrenceHistory)
        {
            const string arm = "UNION SELECT recurrence_key,revision FROM occurrence_base";
            sql.Split(arm, StringSplitOptions.None).Should().HaveCount(2);
            sql = sql.Replace(arm, "", StringComparison.Ordinal);
        }
        else if (mutation == ObservationHashMutation.IgnoreDateBounds)
        {
            foreach (var atom in new[]
                     {
                         "m.activates_at>=timestamptz '0001-01-01 00:00:00+00'",
                         "m.activates_at<timestamptz '10000-01-01 00:00:00+00'",
                         "m.anchor_at>=timestamptz '0001-01-01 00:00:00+00'",
                         "m.anchor_at<timestamptz '10000-01-01 00:00:00+00'",
                     })
            {
                sql.Split(atom, StringSplitOptions.None).Should().HaveCount(2);
                sql = sql.Replace(atom, "true", StringComparison.Ordinal);
            }
        }
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = 5 };
        manifest.Bind(command, []);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = new List<ObservationHashRow>();
        await using (var rows = await command.ExecuteReaderAsync(deadline.Token))
            while (await rows.ReadAsync(deadline.Token))
                result.Add(new(rows.GetString(0), rows.GetInt64(1), rows.GetBoolean(2),
                    rows.IsDBNull(3) ? null : rows.GetString(3), rows.GetString(4), rows.GetInt32(5)));
        await transaction.RollbackAsync(deadline.Token);
        return result;
    }
}
