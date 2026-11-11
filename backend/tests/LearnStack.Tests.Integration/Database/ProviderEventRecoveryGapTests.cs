using FluentAssertions;
using LearnStack.Infrastructure.Identity.ProviderEvents;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identity;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed class ProviderEventRecoveryGapTests(IdentityMembershipFixture fixture) : IClassFixture<IdentityMembershipFixture>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Completed_source_bound_survives_restart_and_expired_history_stays_unhealthy_after_a_new_success(bool increaseRetention)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(fixture.Postgres);
        await IdentityMembershipFixture.SeedSchemaAsync(database.MigrationConnectionString, database.AppConnectionString);
        var machineConnection = new NpgsqlConnectionStringBuilder(fixture.Postgres.IdentityIngressConnectionString)
        { Database = new NpgsqlConnectionStringBuilder(database.AppConnectionString).Database }.ConnectionString;
        DateTimeOffset completedSourceTo;
        await using (var first = await SourceAsync(machineConnection, database.AppConnectionString, 604800))
        {
            var page = await first.BeginSweepAsync();
            completedSourceTo = page.To!.Value;
            (await first.AdvanceSweepAsync(new(page.From!.Value, completedSourceTo, page.Offset), 0, true)).Should().BeTrue();
            (await first.ReadHealthAsync()).Critical.Should().BeFalse();
        }
        await using (var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.MigrationConnectionString))
        {
            (await IdentityMembershipFixture.ScalarAsync<DateTime>(owner, null,
                "SELECT recovery_anchor_at FROM public.identity_provider_event_scan WHERE id=1")).Should().Be(completedSourceTo.UtcDateTime);
            // The source frontier is old even though processing timestamps are
            // recent. Comparing only last_completed_at would miss this gap.
            await IdentityMembershipFixture.ExecuteAsync(owner, null, """
                UPDATE public.identity_provider_event_scan SET activation_lower=clock_timestamp()-interval '15 days',
                    recovery_anchor_at=clock_timestamp()-interval '8 days',
                    last_completed_at=clock_timestamp(),last_traversed_at=clock_timestamp() WHERE id=1
                """);
        }
        await using (var restarted = await SourceAsync(machineConnection, database.AppConnectionString, increaseRetention ? 2592000 : 604800))
        {
            var next = await restarted.BeginSweepAsync();
            next.HistoricalGap.Should().BeTrue(); next.EarliestGapAt.Should().NotBeNull();
            (await restarted.AdvanceSweepAsync(new(next.From!.Value, next.To!.Value, next.Offset), 0, true)).Should().BeTrue();
            (await restarted.ReadHealthAsync()).Critical.Should().BeTrue("a later empty traversal cannot recover expired source history");
            await restarted.RetireAsync();
        }
        await using var again = await SourceAsync(machineConnection, database.AppConnectionString, 604800);
        (await again.ReadScanAsync()).HistoricalGap.Should().BeTrue();
        (await again.ReadHealthAsync()).Critical.Should().BeTrue();
    }

    private static async Task<ProviderEventIntakeSource> SourceAsync(string machineConnection, string businessConnection, int retention)
    {
        var source = new ProviderEventIntakeSource(machineConnection);
        try
        {
            await using var business = ApplicationDataSource.Build(businessConnection);
            await using var proof = await DatabaseQualificationProof.OpenAsync(business);
            await source.QualifyAsync(new(IdentityMembershipFixture.Authority.AuthorityId, "learnstack", retention), proof);
            return source;
        }
        catch { await source.DisposeAsync(); throw; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Empty_or_substituted_position_retry_never_clears_an_unidentified_coverage_gap(bool substitutedFact)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(fixture.Postgres);
        await IdentityMembershipFixture.SeedSchemaAsync(database.MigrationConnectionString, database.AppConnectionString);
        var connection = new NpgsqlConnectionStringBuilder(fixture.Postgres.IdentityIngressConnectionString)
        { Database = new NpgsqlConnectionStringBuilder(database.AppConnectionString).Database }.ConnectionString;
        await using var source = await SourceAsync(connection, database.AppConnectionString, 604800);
        var initial = await source.BeginSweepAsync();
        await source.RecordPageProblemAsync(new(initial.From!.Value, initial.To!.Value, 0, 1), IdentityEventPageIssue.Malformed);
        await using (var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.MigrationConnectionString))
            await IdentityMembershipFixture.ExecuteAsync(owner, null,
                "UPDATE public.identity_provider_event_problems SET next_retry_at=clock_timestamp()-interval '1 second'");
        var laterFact = new IdentityProviderEvent(Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(-1), IdentityProviderEventKind.Login, null);
        var scanner = new ProviderEventScanner(new MovedPositionProvider(substitutedFact ? laterFact : null), source);
        await scanner.RetryOneAsync();
        (await scanner.SweepPageAsync()).Should().BeTrue();
        await using var restarted = await SourceAsync(connection, database.AppConnectionString, 604800);
        (await restarted.ReadScanAsync()).LastCompletedAt.Should().BeNull("position movement cannot attest the original unreadable fact");
        (await restarted.ReadHealthAsync()).Problems.Should().ContainSingle(problem => problem.Reason == "malformed" && problem.Count == 1);
        await using var machine = (NpgsqlConnection)await PostgresFixture.OpenAsync(connection);
        (await IdentityMembershipFixture.ScalarAsync<long>(machine, null,
            "SELECT count(*) FROM public.identity_provider_event_receipts WHERE accepted_at IS NOT NULL"))
            .Should().Be(substitutedFact ? 1 : 0, "any readable replacement earns only its own independent acceptance");
    }

    private sealed class MovedPositionProvider(IdentityProviderEvent? replacement) : IIdentityEventProvider
    {
        public Task<IdentityEventSourcePolicy> ReadPolicyAsync(CancellationToken cancellationToken = default) => Task.FromResult(new IdentityEventSourcePolicy(604800));
        public Task<IdentityEventPage> ReadAsync(IdentityEventRead request, CancellationToken cancellationToken = default) =>
            Task.FromResult(IdentityEventPage.Read(request.Maximum == 1 && replacement is not null ? [IdentityEventReadItem.Observed(replacement)] : []));
    }
}
