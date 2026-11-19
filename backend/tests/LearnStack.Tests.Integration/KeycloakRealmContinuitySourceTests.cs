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
        realm.Pages.Should().Be(2 * (users / KeycloakRealmContinuitySource.PageSize + 1),
            "two full passes, each ended by a page shorter than the page size, an exact multiple needing one empty page");
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
    public async Task A_User_Served_Twice_And_Another_Never_Is_Not_Complete_Although_The_Count_Matches()
    {
        await using var realm = await FakeKeycloakRealm.StartAsync(5);
        realm.Fail("duplicate-user");

        (await realm.ReadAsync()).Complete.Should().BeFalse();
    }

    [Fact]
    public async Task A_Realm_That_Reports_Another_Set_On_The_Second_Pass_Is_Not_Complete()
    {
        await using var realm = await FakeKeycloakRealm.StartAsync(5);
        realm.Fail("second-pass-differs");

        (await realm.ReadAsync()).Complete.Should().BeFalse("a user deleted and another created between the passes keeps the count");
    }

    [Fact]
    public async Task A_Token_That_Expires_During_The_Enumeration_Is_Obtained_Again_Once_And_The_Read_Completes()
    {
        await using var realm = await FakeKeycloakRealm.StartAsync(250);
        realm.Fail("token-expires");

        var snapshot = await realm.ReadAsync();

        snapshot.Complete.Should().BeTrue();
        realm.TokenRequests.Should().HaveCount(2, "one more token, not one per request");
        realm.AdminAuthorization.Should().Contain("Bearer realm-token-2");
    }

    [Fact]
    public async Task A_Credential_That_Is_Refused_Even_With_A_New_Token_Is_A_Refusal_After_One_Retry()
    {
        await using var realm = await FakeKeycloakRealm.StartAsync(5);
        realm.Fail("always-unauthorized");

        var act = async () => await realm.ReadAsync();

        await act.Should().ThrowAsync<NewRealmUnavailableException>();
        realm.TokenRequests.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_Path_Prefixed_Issuer_Reads_Its_Own_Administration_Path()
    {
        await using var realm = await FakeKeycloakRealm.StartAsync(3, prefix: "/auth");

        var snapshot = await realm.ReadAsync();

        snapshot.Complete.Should().BeTrue();
        snapshot.Subjects.Should().BeEquivalentTo(realm.Users);
    }

    [Fact]
    public async Task A_Reported_Count_That_The_Pages_Do_Not_Add_Up_To_Is_Not_Complete()
    {
        await using var realm = await FakeKeycloakRealm.StartAsync(5);
        realm.ReportedCount = 7;

        (await realm.ReadAsync()).Complete.Should().BeFalse();
    }

    [Fact]
    public async Task A_Discovery_Issuer_That_Differs_Is_Reported_As_It_Is_And_No_Credential_Is_Sent_To_That_Realm()
    {
        await using var realm = await FakeKeycloakRealm.StartAsync(1);
        realm.DiscoveryIssuer = "https://elsewhere.example.test/realms/learnstack";

        var snapshot = await realm.ReadAsync();

        snapshot.DiscoveredIssuer.Should().Be("https://elsewhere.example.test/realms/learnstack");
        realm.TokenRequests.Should().BeEmpty("a realm that is not the target never receives the client secret");
        realm.Pages.Should().Be(0);
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

    [Theory]
    [InlineData("token-other-origin")]
    [InlineData("jwks-other-origin")]
    public async Task A_Reachable_Foreign_Origin_Named_By_Discovery_Is_Never_Called_And_Never_Gets_The_Credential(string failure)
    {
        await using var foreign = await FakeKeycloakRealm.StartAsync(3);
        await using var realm = await FakeKeycloakRealm.StartAsync(3);
        realm.ForeignOrigin = foreign.Issuer[..foreign.Issuer.IndexOf("/realms/", StringComparison.Ordinal)];
        realm.Fail(failure);

        var act = async () => await realm.ReadAsync();

        await act.Should().ThrowAsync<NewRealmUnavailableException>();
        foreign.TokenRequests.Should().BeEmpty("the client secret never leaves the issuer's own origin");
        foreign.AdminAuthorization.Should().BeEmpty();
    }

    [Fact]
    public void A_Credential_Replacing_The_Certificate_Check_Is_Refused()
    {
        var handler = new SocketsHttpHandler();
#pragma warning disable CA5359 // The case proves that a replaced certificate check is refused; it is never used.
        handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
#pragma warning restore CA5359

        var act = () => KeycloakRealmContinuitySource.CreateClient(handler);

        act.Should().Throw<InvalidOperationException>();
    }
}
