using FluentAssertions;
using LearnStack.Infrastructure.Identity.ProviderEvents;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identity;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class ProviderEventStorageTests
{
    [Fact]
    public async Task A_shorter_source_window_preserves_committed_reconciliation_but_refuses_new_expired_intake()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(fixture.Postgres);
        await IdentityMembershipFixture.SeedSchemaAsync(database.MigrationConnectionString, database.AppConnectionString);
        var connection = new NpgsqlConnectionStringBuilder(fixture.Postgres.IdentityIngressConnectionString)
        { Database = new NpgsqlConnectionStringBuilder(database.AppConnectionString).Database }.ConnectionString;
        await using var business = ApplicationDataSource.Build(database.AppConnectionString);
        await using var proof = await DatabaseQualificationProof.OpenAsync(business);
        var id = Guid.CreateVersion7();
        var at = DateTimeOffset.UtcNow.AddDays(-8);
        var key = IdentitySubjectKey.FromBytes(SubjectKey(IdentityMembershipFixture.PersonA));
        ProviderEventAcceptance committed;
        await using (var longer = new ProviderEventIntakeSource(connection))
        {
            await longer.QualifyAsync(new(IdentityMembershipFixture.Authority.AuthorityId, "learnstack", 30 * 24 * 60 * 60), proof);
            committed = await longer.AcceptAsync(id, at, "LOGIN", key);
            committed.Accepted.Should().BeTrue();
            committed.TargetCount.Should().Be(1);
        }
        await using var shorter = new ProviderEventIntakeSource(connection);
        await shorter.QualifyAsync(new(IdentityMembershipFixture.Authority.AuthorityId, "learnstack", 7 * 24 * 60 * 60), proof);
        var changedRetained = await shorter.AcceptAsync(id, at.AddHours(1), "LOGIN", key);
        changedRetained.Accepted.Should().BeFalse();
        changedRetained.Disposition.Should().Be("source_collision");
        (await shorter.AcceptAsync(id, at, "LOGIN", key)).Should().Be(committed,
            "source expiry cannot erase acknowledgement of an already committed exact fingerprint");
        var expiredNewId = Guid.CreateVersion7();
        var expired = await shorter.AcceptAsync(expiredNewId, at, "LOGIN", key);
        expired.Accepted.Should().BeFalse();
        expired.Disposition.Should().Be("out_of_window");
        await using var machine = await OpenAsync(connection);
        (await IdentityMembershipFixture.ScalarAsync<long>(machine, null,
            "SELECT count(*) FROM public.identity_provider_event_problems WHERE provider_event_id=@id AND reason='source_collision'", ("id", id))).Should().Be(1);
        (await IdentityMembershipFixture.ScalarAsync<long>(machine, null,
            "SELECT count(*) FROM public.identity_provider_event_problems WHERE provider_event_id=@id AND reason='out_of_window'", ("id", id))).Should().Be(0);
        (await IdentityMembershipFixture.ScalarAsync<long>(machine, null,
            "SELECT count(*) FROM public.identity_provider_event_receipts WHERE provider_event_id=@id", ("id", expiredNewId)))
            .Should().Be(0, "the replay path must not admit or stage a new expired fact");
        (await IdentityMembershipFixture.ScalarAsync<long>(machine, null,
            "SELECT count(*) FROM public.identity_provider_event_problems WHERE provider_event_id=@id AND reason='out_of_window'", ("id", expiredNewId))).Should().Be(1);
        (await shorter.AcceptAsync(id, at, "LOGIN", key)).Should().Be(committed);
        await shorter.UpdateSourcePolicyAsync(30 * 24 * 60 * 60);
        var changed = await shorter.AcceptAsync(expiredNewId, at.AddHours(1), "LOGIN", key);
        changed.Accepted.Should().BeFalse(); changed.Disposition.Should().Be("source_collision");
        (await shorter.AcceptAsync(expiredNewId, at, "LOGIN", key)).Accepted.Should().BeTrue();
        (await IdentityMembershipFixture.ScalarAsync<long>(machine, null,
            "SELECT count(*) FROM public.identity_provider_event_problems WHERE provider_event_id=@id AND reason='out_of_window' AND resolved_at IS NULL", ("id", expiredNewId)))
            .Should().Be(0, "a newly admissible exact fingerprint resolves only its matching recoverable window problem");
    }
}
