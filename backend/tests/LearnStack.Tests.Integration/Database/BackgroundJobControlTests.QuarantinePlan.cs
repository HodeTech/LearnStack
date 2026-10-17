using FluentAssertions;
using LearnStack.Infrastructure.Jobs;
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
            var clean = PlanNodes(await ExplainControlAsync((Npgsql.NpgsqlConnection)dispatcher,
                PostgresBackgroundJobControlStore.QuarantinePresenceSql));
            clean.Where(node => node.TryGetProperty("Index Name", out _))
                .Select(node => node.GetProperty("Index Name").GetString()).Should().Contain("ix_background_jobs_quarantined");
            Removed(clean).Should().BeLessThan(100);
            if (quarantines == 0)
            {
                await SchemaQueries.ExecuteAsync(owner, null, "DROP INDEX ix_background_jobs_quarantined");
                try
                {
                    var dirty = PlanNodes(await ExplainControlAsync((Npgsql.NpgsqlConnection)dispatcher,
                        PostgresBackgroundJobControlStore.QuarantinePresenceSql));
                    Removed(dirty).Should().BeGreaterThanOrEqualTo(6_000,
                        "the same actual-query detector must expose an empty-observation backlog scan without the index");
                }
                finally
                {
                    await SchemaQueries.ExecuteAsync(owner, null,
                        "CREATE INDEX ix_background_jobs_quarantined ON background_jobs(id) WHERE phase='quarantined'");
                }
            }
        }
    }
}
