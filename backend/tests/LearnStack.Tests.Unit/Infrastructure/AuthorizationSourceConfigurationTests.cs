using FluentAssertions;
using LearnStack.Infrastructure.Persistence;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Unit.Infrastructure;

public sealed class AuthorizationSourceConfigurationTests
{
    private static readonly string Business = Configuration("learnstack");
    private static readonly string Authorization = Configuration("learnstack_identity_authorization");
    private static readonly string Evidence = Configuration("learnstack_authorization_evidence");

    // A nonfunctional diagnostic canary; these configuration tests never connect.
    private static string Configuration(string database) => new NpgsqlConnectionStringBuilder
    {
        Host = "localhost",
        Database = database,
        Username = ApplicationDataSource.RuntimeRole,
        Password = "credential-canary",
    }.ConnectionString;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Partial_configuration_refuses_in_every_environment(bool development)
    {
        var onlyAuthorization = () => AuthorizationSourceConfiguration.Resolve(Business, Authorization, null, development);
        var onlyEvidence = () => AuthorizationSourceConfiguration.Resolve(Business, null, Evidence, development);
        onlyAuthorization.Should().Throw<InvalidOperationException>().Which.Message.Should().NotContain("credential-canary");
        onlyEvidence.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Only_owned_Development_direct_topology_can_derive_the_missing_pair()
    {
        var production = () => AuthorizationSourceConfiguration.Resolve(Business, null, null, false);
        production.Should().Throw<InvalidOperationException>();
        var development = AuthorizationSourceConfiguration.Resolve(Business, null, null, true);
        development.IdentityAuthorization.Should().Be(Business);
        development.AuthorizationEvidence.Should().Be(Business);
        development.ToString().Should().NotContain("credential-canary");
        var aliasAsDefault = () => AuthorizationSourceConfiguration.Resolve(Authorization, null, null, true);
        aliasAsDefault.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Explicit_pair_preserves_environment_credentials_and_refuses_a_bypass_endpoint()
    {
        var pair = AuthorizationSourceConfiguration.Resolve(Business, Authorization, Evidence, false);
        pair.IdentityAuthorization.Should().Be(Authorization);
        pair.AuthorizationEvidence.Should().Be(Evidence);
        var bypass = () => AuthorizationSourceConfiguration.Resolve(Business,
            Authorization.Replace("learnstack_app", "learnstack_platform", StringComparison.Ordinal), Evidence, false);
        bypass.Should().Throw<InvalidOperationException>().Which.Message.Should().NotContain("credential-canary");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void Test_capacity_cannot_widen_the_four_owner_production_bound(int capacity)
    {
        var create = () => new IdentityAuthorizationReadSource(Business, capacity);
        create.Should().Throw<ArgumentOutOfRangeException>();
    }
}
