using FluentAssertions;
using LearnStack.Modules.Identity.Infrastructure.Bindings;
using LearnStack.Tools.IdentityAuthorityRebind;
using Xunit;

namespace LearnStack.Tests.Integration;

/// <summary>
/// The tool's reader of the new realm (ADR-0104 § 4, § 6) against a loopback realm that behaves like Keycloak's
/// administration API: bounded, same-origin, paged, and complete or refused.
/// </summary>
/// <remarks>
/// A real Keycloak answers the same questions in <see cref="TenancyIdentity.KeycloakEnumerationTests"/>; here the
/// failure modes a real one cannot be made to produce are driven on purpose.
/// </remarks>
public sealed class KeycloakRealmContinuitySourceTests
{
    private const string ClientId = FakeKeycloakRealm.ClientId;
    private const string ClientSecret = FakeKeycloakRealm.ClientSecret;

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(99)]
    [InlineData(100)]
    [InlineData(101)]
    [InlineData(250)]
    public async Task Every_User_Of_Every_Page_Is_Reported_And_The_Snapshot_Is_Complete(int users)
    {
        await using var realm = await FakeKeycloakRealm.StartAsync(users);

        var snapshot = await realm.ReadAsync();

        snapshot.Complete.Should().BeTrue();
        snapshot.DiscoveredIssuer.Should().Be(realm.Issuer);
        snapshot.SigningKeyCount.Should().Be(2, "only signature keys count, an encryption key does not");
        snapshot.Subjects.Should().BeEquivalentTo(realm.Users);
        realm.Pages.Should().Be(users / KeycloakRealmContinuitySource.PageSize + 1,
            "a page shorter than the page size ends the enumeration, and an exact multiple needs one empty page");
    }

    [Fact]
    public async Task The_Realm_Credential_Is_Sent_Only_As_A_Client_Credentials_Grant_And_The_Token_Only_As_A_Bearer()
    {
        await using var realm = await FakeKeycloakRealm.StartAsync(3);

        await realm.ReadAsync();

        realm.TokenRequests.Should().ContainSingle().Which.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = ClientId,
            ["client_secret"] = ClientSecret,
        });
        realm.AdminAuthorization.Should().OnlyContain(header => header == "Bearer realm-token");
    }

    [Fact]
    public async Task A_Realm_That_Changes_While_It_Is_Read_Is_Not_Complete()
    {
        await using var realm = await FakeKeycloakRealm.StartAsync(5);
        realm.CountAfterRead = 6;

        var snapshot = await realm.ReadAsync();

        snapshot.Complete.Should().BeFalse("the count before and after the pages differ");
    }

    [Fact]
    public async Task A_Reported_Count_That_The_Pages_Do_Not_Add_Up_To_Is_Not_Complete()
    {
        await using var realm = await FakeKeycloakRealm.StartAsync(5);
        realm.ReportedCount = 7;

        (await realm.ReadAsync()).Complete.Should().BeFalse();
    }

    [Fact]
    public async Task A_Discovery_Issuer_That_Differs_Is_Reported_As_It_Is_For_The_Proof_To_Refuse()
    {
        await using var realm = await FakeKeycloakRealm.StartAsync(1);
        realm.DiscoveryIssuer = "https://elsewhere.example.test/realms/learnstack";

        (await realm.ReadAsync()).DiscoveredIssuer.Should().Be("https://elsewhere.example.test/realms/learnstack");
    }

    [Theory]
    [InlineData("jwks-other-origin")]
    [InlineData("token-other-origin")]
    [InlineData("token-denied")]
    [InlineData("admin-denied")]
    [InlineData("users-redirect")]
    [InlineData("discovery-redirect")]
    [InlineData("users-not-an-array")]
    [InlineData("user-without-id")]
    [InlineData("count-not-a-number")]
    [InlineData("oversized-token-answer")]
    public async Task Any_Transport_Or_Shape_Failure_Is_One_Fixed_Refusal_That_Names_Nothing(string failure)
    {
        await using var realm = await FakeKeycloakRealm.StartAsync(3);
        realm.Fail(failure);

        var act = async () => await realm.ReadAsync();

        var thrown = (await act.Should().ThrowAsync<NewRealmUnavailableException>()).Which;
        thrown.Message.Should().Be("The new realm could not be read completely.");
        thrown.Message.Should().NotContain(ClientSecret).And.NotContain("127.0.0.1").And.NotContain("realm-token");
        thrown.InnerException.Should().BeNull("no transport exception is carried out");
    }

    [Fact]
    public async Task A_Credential_Replacing_The_Certificate_Check_Is_Refused()
    {
        var handler = new SocketsHttpHandler();
#pragma warning disable CA5359 // The case proves that a replaced certificate check is refused; it is never used.
        handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
#pragma warning restore CA5359

        var act = () => KeycloakRealmContinuitySource.CreateClient(handler);

        act.Should().Throw<InvalidOperationException>();
        await Task.CompletedTask;
    }
}
