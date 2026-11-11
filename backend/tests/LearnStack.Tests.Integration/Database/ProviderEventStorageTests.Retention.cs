using FluentAssertions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class ProviderEventStorageTests
{
    [Fact]
    public async Task Retirement_drains_multiple_bounded_commits_and_exposes_remaining_backlog_under_new_arrivals()
    {
        await using var source = await QualifiedSourceAsync();
        var prefix = "retirement-load-" + Guid.NewGuid().ToString("N");
        await AddAgedRetirementRowsAsync(prefix, 1705, receipts: true);
        var before = await source.ReadHealthAsync();
        before.RetirableReceipts.Should().BeGreaterThanOrEqualTo(1705);
        before.RetirableProblems.Should().BeGreaterThanOrEqualTo(1705);
        before.OldestRetirementOverdueSeconds.Should().BeGreaterThan(86_000);
        before.HasRetirementBacklog.Should().BeTrue();

        (await source.RetireAsync()).Should().Be(100 * LearnStack.Infrastructure.Identity.ProviderEvents.ProviderEventIntakeSource.RetirementBatchesPerSlice);
        var remaining = await source.ReadHealthAsync();
        remaining.RetirableReceipts.Should().BeGreaterThan(0, "a finite slice yields instead of monopolizing the worker");
        remaining.RetirableProblems.Should().BeGreaterThan(0);
        remaining.HasRetirementBacklog.Should().BeTrue();

        // More old work arrives between fair worker turns. Ordinary new intake
        // remains usable while retirement catches up on its next turn.
        await AddAgedRetirementRowsAsync(prefix + "-next", 200, receipts: true);
        var current = await source.AcceptAsync(Guid.CreateVersion7(), DateTimeOffset.UtcNow.AddMinutes(-1), "LOGIN", null);
        current.Accepted.Should().BeTrue();
        (await source.RetireAsync()).Should().BeGreaterThan(100);
        var after = await source.ReadHealthAsync();
        after.RetirableReceipts.Should().Be(0);
        after.RetirableProblems.Should().Be(0);
        after.OldestRetirementOverdueSeconds.Should().Be(0);
        after.HasRetirementBacklog.Should().BeFalse();
        await using var machine = await MachineAsync();
        (await IdentityMembershipFixture.ScalarAsync<long>(machine, null,
            "SELECT count(*) FROM public.identity_provider_event_receipts WHERE id=@id", ("id", current.ReceiptId))).Should().Be(1);
        (await source.ReadScanAsync()).HistoricalGap.Should().BeTrue("retirement never erases the missing-history signal");
    }

    [Fact]
    public async Task Problem_only_retirement_does_not_stop_at_zero_removed_receipts()
    {
        await using var source = await QualifiedSourceAsync();
        var prefix = "retirement-problems-" + Guid.NewGuid().ToString("N");
        await AddAgedRetirementRowsAsync(prefix, 305, receipts: false);
        (await source.ReadHealthAsync()).RetirableProblems.Should().BeGreaterThanOrEqualTo(305);
        (await source.RetireAsync()).Should().Be(0);
        var after = await source.ReadHealthAsync();
        after.RetirableProblems.Should().Be(0);
        after.HasRetirementBacklog.Should().BeFalse();
        after.Critical.Should().BeTrue("unresolved evidence expires into a durable historical gap");
    }

    private async Task AddAgedRetirementRowsAsync(string prefix, int count, bool receipts)
    {
        await using var setup = await OpenAsync(fixture.Postgres.MigrationConnectionString);
        await using var insert = new NpgsqlCommand((receipts ? """
            INSERT INTO public.identity_provider_event_receipts(id,authority_id,realm_id,provider_event_id,
                source_occurred_at,source_kind,fingerprint,received_at,accepted_at,disposition)
            SELECT uuidv7(),@authority,'learnstack',uuidv7(),clock_timestamp()-interval '33 days',
                'LOGIN',decode(repeat('00',32),'hex'),clock_timestamp()-interval '33 days',
                clock_timestamp()-interval '33 days','subject_absent' FROM generate_series(1,@count);
            """ : "") + """
            INSERT INTO public.identity_provider_event_problems(problem_key,authority_id,realm_id,range_from,range_to,
                reason,first_seen_at)
            SELECT @prefix||':'||n::text,@authority,'learnstack',clock_timestamp()-interval '34 days',
                clock_timestamp()-interval '34 days','malformed',clock_timestamp()-interval '33 days'
            FROM generate_series(1,@count) n;
            """, setup);
        insert.Parameters.AddWithValue("authority", IdentityMembershipFixture.Authority.AuthorityId.Value);
        insert.Parameters.AddWithValue("prefix", prefix);
        insert.Parameters.AddWithValue("count", count);
        await insert.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Global_retirement_keeps_independent_target_evidence_and_a_durable_historical_gap()
    {
        var id = Guid.CreateVersion7();
        var accepted = await AcceptAsync(id, "LOGIN", SubjectKey(IdentityMembershipFixture.PersonA));
        var problem = "retention-" + Guid.NewGuid().ToString("N");
        await using (var setup = await OpenAsync(fixture.Postgres.MigrationConnectionString))
        {
            // Model aged committed storage without changing the runtime clock or
            // shortening the real retention allocation.
            await using var age = new NpgsqlCommand("""
                UPDATE public.identity_provider_event_receipts SET received_at=clock_timestamp()-interval '33 days' WHERE id=@id;
                INSERT INTO public.identity_provider_event_problems(problem_key,authority_id,realm_id,
                    range_from,range_to,reason,first_seen_at)
                VALUES(@problem,@authority,'learnstack',clock_timestamp()-interval '34 days',
                    clock_timestamp()-interval '34 days','malformed',clock_timestamp()-interval '33 days');
                """, setup);
            age.Parameters.AddWithValue("id", accepted.Id);
            age.Parameters.AddWithValue("problem", problem);
            age.Parameters.AddWithValue("authority", IdentityMembershipFixture.Authority.AuthorityId.Value);
            await age.ExecuteNonQueryAsync();
        }
        await using var source = await QualifiedSourceAsync();
        (await source.RetireAsync()).Should().Be(1);
        await using var machine = await MachineAsync();
        (await IdentityMembershipFixture.ScalarAsync<long>(machine, null,
            "SELECT count(*) FROM public.identity_provider_event_receipts WHERE id=@id", ("id", accepted.Id))).Should().Be(0);
        (await IdentityMembershipFixture.ScalarAsync<long>(machine, null,
            "SELECT count(*) FROM public.identity_provider_event_problems WHERE problem_key=@key", ("key", problem))).Should().Be(0);
        (await IdentityMembershipFixture.ScalarAsync<bool>(machine, null,
            "SELECT historical_gap AND earliest_gap_at IS NOT NULL FROM public.identity_provider_event_scan WHERE id=1")).Should().BeTrue();
        await using var app = await OpenAsync(fixture.Postgres.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await IdentityMembershipFixture.ScopeAsync(app, transaction, SchemaFixture.TenantA);
        (await IdentityMembershipFixture.ScalarAsync<long>(app, transaction,
            "SELECT count(*) FROM public.identity_provider_event_targets WHERE provider_event_id=@id", ("id", id)))
            .Should().Be(1, "global receipt age does not authorize deleting replay-dependent tenant evidence");
    }
}
