using System.Text.Json.Nodes;
using FluentAssertions;
using Xunit;

namespace LearnStack.Tests.Integration.TenancyIdentity;

/// <summary>
/// Properties of the shipped operator realm export that need no running Keycloak (ADR-0101 § 3).
/// </summary>
/// <remarks>
/// The behavioural proof against pinned Keycloak is <see cref="PlatformOperatorRealmTests"/>. These
/// cases pin the settings whose effect is a refusal that cannot be provoked from a headless login.
/// </remarks>
public sealed class PlatformOperatorRealmFileTests
{
    private static JsonObject Realm() =>
        JsonNode.Parse(File.ReadAllText(RealmFiles.RealmPath("learnstack-hub")))!.AsObject();

    [Fact]
    public void Password_Reset_Is_Off_So_No_Second_Path_To_A_Password_Skips_The_Second_Factor() =>
        Realm()["resetPasswordAllowed"]!.GetValue<bool>().Should().BeFalse();

    [Fact]
    public void The_Operator_Client_Is_Public_Pkce_With_No_Direct_Grant_And_No_Realm_Roles_In_Its_Token()
    {
        var client = Realm()["clients"]!.AsArray().Select(node => node!.AsObject())
            .Single(candidate => candidate["clientId"]!.GetValue<string>() == "learnstack-operator");

        client["publicClient"]!.GetValue<bool>().Should().BeTrue();
        client["standardFlowEnabled"]!.GetValue<bool>().Should().BeTrue();
        client["implicitFlowEnabled"]!.GetValue<bool>().Should().BeFalse();
        client["directAccessGrantsEnabled"]!.GetValue<bool>().Should().BeFalse();
        client["serviceAccountsEnabled"]!.GetValue<bool>().Should().BeFalse();
        client["fullScopeAllowed"]!.GetValue<bool>().Should().BeFalse();
        client["attributes"]!["pkce.code.challenge.method"]!.GetValue<string>().Should().Be("S256");
        client["attributes"]!["access.token.lifespan"]!.GetValue<string>().Should().Be("900");
    }

    [Fact]
    public void The_Realm_Browser_Flow_Is_The_Step_Up_Flow_And_Its_Second_Level_Asks_For_The_Factor_Every_Time()
    {
        var realm = Realm();

        realm["browserFlow"]!.GetValue<string>().Should().Be("browser-loa");
        var level2 = realm["authenticatorConfig"]!.AsArray().Select(node => node!.AsObject())
            .Single(config => config["alias"]!.GetValue<string>() == "loa-2");
        level2["config"]!["loa-max-age"]!.GetValue<string>().Should().Be("0", "level 2 never rides an earlier session");
    }
}
