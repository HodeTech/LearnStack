using FluentAssertions;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using Npgsql;

namespace LearnStack.Tests.Integration.Database;

/// <summary>Runs the actual complete observation statement with its real frozen metadata bindings.</summary>
internal static class BackgroundJobObservationQueries
{
    internal static BackgroundJobObservationManifest Manifest(BackgroundJobDefinitionRegistry definitions,
        BackgroundJobRecurrenceRegistry? recurrences = null) =>
        BackgroundJobObservationManifest.Create(definitions, recurrences ?? BackgroundJobRecurrenceRegistry.CreateEmpty());

    internal static async Task<BackgroundJobDurableObservation> ReadAsync(PrivilegedDataSource source,
        BackgroundJobDefinitionRegistry definitions, IReadOnlyCollection<Guid>? retained = null)
    {
        var store = new PostgresBackgroundJobControlStore(source, definitions, new(), new SystemGuidFactory());
        var observed = await store.ReadDurableObservationAsync(Manifest(definitions), retained ?? []);
        observed.ThrowIfFailed();
        observed.CleanupConfirmed.Should().BeTrue();
        observed.Value.Should().NotBeNull();
        return observed.Value!;
    }

    internal static BackgroundJobObservationPoint AssertQueue(BackgroundJobDurableObservation sample,
        string profile, long count, long eligible)
    {
        var point = sample.Points.Where(point => point.Family == BackgroundJobObservationFamily.Queue && point.Label == profile)
            .Should().ContainSingle().Subject;
        point.Count.Should().Be(count);
        point.EligibleCount.Should().Be(eligible);
        if (eligible == 0) point.OldestEligibleAgeSeconds.Should().Be(0);
        else point.OldestEligibleAgeSeconds.Should().BeGreaterThan(0);
        return point;
    }

    internal static async Task<string> ExplainAsync(NpgsqlConnection dispatcher, BackgroundJobObservationManifest manifest)
    {
        await using var transaction = await dispatcher.BeginTransactionAsync();
        await using var command = new NpgsqlCommand("EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) "
            + PostgresBackgroundJobControlStore.DurableObservationSql, dispatcher, transaction)
        { CommandTimeout = 5 };
        manifest.Bind(command, []);
        var result = (string)(await command.ExecuteScalarAsync())!;
        await transaction.RollbackAsync();
        return result;
    }
}
