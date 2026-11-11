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
        (await shorter.AcceptAsync(id, at, "LOGIN", key)).Should().Be(committed,
            "source expiry cannot erase acknowledgement of an already committed exact fingerprint");
        var expiredNewId = Guid.CreateVersion7();
        await Assert.ThrowsAsync<InvalidOperationException>(() => shorter.AcceptAsync(expiredNewId, at, "LOGIN", key));
        await using var machine = await OpenAsync(connection);
        (await IdentityMembershipFixture.ScalarAsync<long>(machine, null,
            "SELECT count(*) FROM public.identity_provider_event_receipts WHERE provider_event_id=@id", ("id", expiredNewId)))
            .Should().Be(0, "the replay path must not admit or stage a new expired fact");
        (await shorter.AcceptAsync(id, at, "LOGIN", key)).Should().Be(committed);
    }
}
