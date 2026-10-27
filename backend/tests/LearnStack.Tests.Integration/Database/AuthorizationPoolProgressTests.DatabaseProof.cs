using FluentAssertions;
using LearnStack.Infrastructure.Persistence;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class AuthorizationPoolProgressTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Live_business_database_marker_qualifies_both_independent_lanes_through_direct_and_transaction_pooler_aliases(
        bool transactionPooled)
    {
        var businessConnection = fixture.ConnectionString(transactionPooled, "business", "database-proof-business");
        var authorizationConnection = fixture.ConnectionString(transactionPooled, "authorization", "database-proof-authorization");
        var evidenceConnection = fixture.ConnectionString(transactionPooled, "evidence", "database-proof-evidence");
        var database = new NpgsqlConnectionStringBuilder(fixture.DirectConnectionString).Database;
        if (transactionPooled)
        {
            new NpgsqlConnectionStringBuilder(authorizationConnection).Database.Should().NotBe(database);
            new NpgsqlConnectionStringBuilder(evidenceConnection).Database.Should().NotBe(database);
        }
        await using var business = ApplicationDataSource.Build(businessConnection);
        await using var authorization = new IdentityAuthorizationReadSource(authorizationConnection);
        await using var evidence = new AuthorizationEvidenceSource(evidenceConnection);
        await using var proof = await DatabaseQualificationProof.OpenAsync(business);
        var authority = IdentityMembershipFixture.Authority;

        await authorization.QualifyAsync(authority.AuthorityId, authority.Issuer, proof, proof.DeadlineToken);
        await evidence.QualifyAsync(authority.AuthorityId, authority.Issuer, proof, proof.DeadlineToken);

        authorization.IsQualified.Should().BeTrue();
        evidence.IsQualified.Should().BeTrue();
        authorization.QualifiedDatabaseName.Should().Be(database);
        evidence.QualifiedDatabaseName.Should().Be(database);
        await proof.DisposeAsync();
        authorization.IsQualified.Should().BeTrue();
        evidence.IsQualified.Should().BeTrue();
    }
}
