using FluentAssertions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class ProviderEventStorageTests
{
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
