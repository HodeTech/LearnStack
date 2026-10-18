using FluentAssertions;
using LearnStack.Infrastructure.Jobs;
using System.Text.Json;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobControlTests
{
    [Fact]
    public async Task Actual_quarantine_observation_seeks_its_partial_index_and_detects_its_absence()
    {
        await using var database = await DatabaseAsync();
        await SeedControlFutureAsync(database, "retirement");
        await using var owner = await PostgresFixture.OpenAsync(database.MigrationConnectionString);
        await using var dispatcher = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        for (var quarantines = 0; quarantines <= 1; quarantines++)
        {
            if (quarantines == 1)
                await SchemaQueries.ExecuteAsync(dispatcher, null, """
                    WITH chosen AS (SELECT id FROM background_jobs ORDER BY id LIMIT 1)
                    UPDATE background_jobs j SET phase='quarantined', quarantine_reason='unknown_outcome'
                    FROM chosen WHERE j.id=chosen.id
                    """);
            await SchemaQueries.ExecuteAsync(owner, null, "ANALYZE background_jobs");
            var clean = PlanNodes(await BackgroundJobObservationQueries.ExplainAsync((Npgsql.NpgsqlConnection)dispatcher,
                BackgroundJobObservationQueries.Manifest(Fixture.Registry)));
            AssertJobQuarantinePlan(clean);
            if (quarantines == 0)
            {
                await SchemaQueries.ExecuteAsync(owner, null, "DROP INDEX ix_background_jobs_quarantined");
                try
                {
                    var dirty = PlanNodes(await BackgroundJobObservationQueries.ExplainAsync((Npgsql.NpgsqlConnection)dispatcher,
                        BackgroundJobObservationQueries.Manifest(Fixture.Registry)));
                    Action sameDetector = () => AssertJobQuarantinePlan(dirty);
                    sameDetector.Should().Throw<Xunit.Sdk.XunitException>();
                    Removed(dirty.Where(IsJobQuarantineScan)).Should().BeGreaterThanOrEqualTo(6_000,
                        "the actual aggregate's quarantine arm must expose its backlog scan without the index");
                }
                finally
                {
                    await SchemaQueries.ExecuteAsync(owner, null,
                        "CREATE INDEX ix_background_jobs_quarantined ON background_jobs(id) WHERE phase='quarantined'");
                }
                AssertJobQuarantinePlan(PlanNodes(await BackgroundJobObservationQueries.ExplainAsync((Npgsql.NpgsqlConnection)dispatcher,
                    BackgroundJobObservationQueries.Manifest(Fixture.Registry))));
            }
        }
    }

    private static void AssertJobQuarantinePlan(IEnumerable<JsonElement> nodes)
    {
        var scans = nodes.Where(IsJobQuarantineScan).ToArray();
        scans.Where(node => node.TryGetProperty("Index Name", out _))
            .Select(node => node.GetProperty("Index Name").GetString()).Should().Contain("ix_background_jobs_quarantined");
        Removed(scans).Should().BeLessThan(100,
            "this bounds the quarantine arm, not the separate exact live-backlog aggregate");
    }

    private static bool IsJobQuarantineScan(JsonElement node) =>
        node.TryGetProperty("Relation Name", out var relation) && relation.GetString() == "background_jobs"
        && (node.TryGetProperty("Index Name", out var index) && index.GetString() == "ix_background_jobs_quarantined"
            || node.TryGetProperty("Filter", out var filter) && filter.GetString()!.Contains("'quarantined'", StringComparison.Ordinal));
}
