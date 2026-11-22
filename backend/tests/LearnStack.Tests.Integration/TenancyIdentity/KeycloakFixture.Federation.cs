using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace LearnStack.Tests.Integration.TenancyIdentity;

/// <summary>
/// ADR-0099 Step 3 test-only external identity providers. Each is a separate realm in the owned provider container,
/// standing in for a tenant's IdP: a closed name set, never the tenant realm and never a substitute for it.
/// Browser traffic reaches it through the mapped loopback origin; the tenant realm's back-channel (token, JWKS,
/// UserInfo) reaches it inside the container on its own HTTPS port, with the same owned CA.
/// </summary>
public sealed partial class KeycloakFixture
{
    internal const string IdentityProviderClientId = "learnstack-tenant-broker";
    internal const string IdentityProviderPassword = "federation-idp-user-password";
    internal const string SamlIdentityProviderRealm = "federation-idp-saml";
    internal static readonly string[] IdentityProviderRealms = ["federation-idp-a", "federation-idp-b", SamlIdentityProviderRealm];

    /// <summary>The container-internal origin the tenant realm uses for back-channel calls to a test IdP realm.</summary>
    internal static readonly Uri InternalOrigin = new("https://localhost:8443/");

    internal string ExternalIssuerFor(string idpRealm) => new Uri(BaseAddress, "realms/" + CheckedIdentityProviderRealm(idpRealm)).AbsoluteUri;
    internal static string InternalIssuerFor(string idpRealm) => new Uri(InternalOrigin, "realms/" + CheckedIdentityProviderRealm(idpRealm)).AbsoluteUri;

    internal static string CheckedIdentityProviderRealm(string realm) => IdentityProviderRealms.Contains(realm, StringComparer.Ordinal)
        ? realm : throw new InvalidOperationException("Federation test IdP realm is outside the closed fixture set.");

    /// <summary>
    /// Creates one test IdP realm with a confidential OIDC client whose only redirect is the tenant broker endpoint for
    /// <paramref name="alias"/>, and the named users with a fixed password and verified email.
    /// </summary>
    internal async Task CreateIdentityProviderRealmAsync(string idpRealm, string alias, string clientSecret, params string[] emails)
    {
        CheckedIdentityProviderRealm(idpRealm);
        var redirect = new Uri(BaseAddress, "realms/learnstack/broker/" + alias + "/endpoint").AbsoluteUri;
        var realm = new JsonObject
        {
            ["realm"] = idpRealm,
            ["enabled"] = true,
            ["sslRequired"] = "external",
            ["registrationAllowed"] = false,
            ["clients"] = new JsonArray(new JsonObject
            {
                ["clientId"] = IdentityProviderClientId,
                ["enabled"] = true,
                ["publicClient"] = false,
                ["secret"] = clientSecret,
                ["standardFlowEnabled"] = true,
                ["directAccessGrantsEnabled"] = false,
                ["redirectUris"] = new JsonArray(redirect),
                ["webOrigins"] = new JsonArray(),
                ["protocol"] = "openid-connect",
                ["attributes"] = new JsonObject { ["pkce.code.challenge.method"] = "S256" },
            }),
            ["users"] = new JsonArray([.. emails.Select(email => (JsonNode)new JsonObject
            {
                ["username"] = email,
                ["email"] = email,
                ["emailVerified"] = true,
                ["enabled"] = true,
                ["firstName"] = "Federated",
                ["lastName"] = "Person",
                ["credentials"] = new JsonArray(new JsonObject
                {
                    ["type"] = "password", ["value"] = IdentityProviderPassword, ["temporary"] = false,
                }),
            })]),
        };
        await CreateRealmAsync(realm);
    }

    /// <summary>
    /// Creates the test SAML IdP realm: one SAML client whose entity id is the tenant realm's own issuer (the broker's
    /// default SP entity id, since the closed profile sets none) and whose only assertion consumer is the tenant broker
    /// endpoint for <paramref name="alias"/>. It signs both the response and the assertion with the realm's own RS256
    /// key, accepts unsigned AuthnRequests (the profile sends none signed), always issues a persistent NameID, and
    /// asserts the address only as the X.500 email attribute the pinned broker reads.
    /// </summary>
    internal async Task CreateSamlIdentityProviderRealmAsync(string alias, params string[] emails)
    {
        var acs = new Uri(BaseAddress, "realms/learnstack/broker/" + alias + "/endpoint").AbsoluteUri;
        var realm = new JsonObject
        {
            ["realm"] = SamlIdentityProviderRealm,
            ["enabled"] = true,
            ["sslRequired"] = "external",
            ["registrationAllowed"] = false,
            ["clients"] = new JsonArray(new JsonObject
            {
                ["clientId"] = Issuer,
                ["enabled"] = true,
                ["protocol"] = "saml",
                ["frontchannelLogout"] = false,
                ["redirectUris"] = new JsonArray(acs),
                ["attributes"] = new JsonObject
                {
                    ["saml.server.signature"] = "true",
                    ["saml.assertion.signature"] = "true",
                    ["saml.client.signature"] = "false",
                    ["saml.encrypt"] = "false",
                    ["saml.signature.algorithm"] = "RSA_SHA256",
                    ["saml.authnstatement"] = "true",
                    ["saml_name_id_format"] = "persistent",
                    ["saml_force_name_id_format"] = "true",
                    ["saml_assertion_consumer_url_post"] = acs,
                },
                ["protocolMappers"] = new JsonArray(new JsonObject
                {
                    ["name"] = "X500 email",
                    ["protocol"] = "saml",
                    ["protocolMapper"] = "saml-user-property-mapper",
                    ["consentRequired"] = false,
                    ["config"] = new JsonObject
                    {
                        ["attribute.nameformat"] = "urn:oasis:names:tc:SAML:2.0:attrname-format:uri",
                        ["user.attribute"] = "email",
                        ["friendly.name"] = "email",
                        ["attribute.name"] = "urn:oid:1.2.840.113549.1.9.1",
                    },
                }),
            }),
            ["users"] = new JsonArray([.. emails.Select(email => (JsonNode)new JsonObject
            {
                ["username"] = email,
                ["email"] = email,
                ["emailVerified"] = true,
                ["enabled"] = true,
                ["firstName"] = "Federated",
                ["lastName"] = "Person",
                ["credentials"] = new JsonArray(new JsonObject
                {
                    ["type"] = "password", ["value"] = IdentityProviderPassword, ["temporary"] = false,
                }),
            })]),
        };
        await CreateRealmAsync(realm);
    }

    private async Task CreateRealmAsync(JsonObject realm)
    {
        using var client = KeycloakHttp.Create(new Uri(BaseAddress, "admin/"), RootCertificate);
        var credential = await AuthenticateAdministratorAsync(CancellationToken.None);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential.AccessToken);
        using var response = await KeycloakHttp.SendAsync(client, HttpMethod.Post, "realms", JsonContent.Create(realm));
        if (response.StatusCode != HttpStatusCode.Created)
            throw new InvalidOperationException("Federation test IdP realm creation was refused.");
    }

    /// <summary>
    /// The committed export as it stood before P03-8 Step 3, for warm-upgrade proofs: no federation admin or account-removal
    /// client or their service accounts, no link-only first-broker flow or declared required actions (so idp_link is the stock enabled
    /// action), no idp mapper, and the stock account self-service (enabled console, manage-account in the default role and
    /// on each persona).
    /// </summary>
    internal static void ToPreStepThree(JsonObject realm)
    {
        var clients = realm["clients"]!.AsArray();
        clients.Remove(clients.Single(client => client!["clientId"]!.GetValue<string>() == "learnstack-federation-admin"));
        // ADR-0107 § 7 added the account-removal client later still: the pre-Step-3 realm has neither machine client.
        clients.Remove(clients.Single(client => client!["clientId"]!.GetValue<string>() == "learnstack-account-removal"));
        foreach (var client in clients)
        {
            var name = client!["clientId"]!.GetValue<string>();
            if (name is "account" or "account-console") client["enabled"] = true;
            if (name == "learnstack-web") client.AsObject().Remove("protocolMappers");
        }
        var users = realm["users"]!.AsArray();
        foreach (var service in users.Where(user => user!["serviceAccountClientId"] is not null).ToArray()) users.Remove(service);
        foreach (var user in users) user!["clientRoles"] = new JsonObject { ["account"] = new JsonArray("manage-account") };
        realm["roles"]!["realm"]!.AsArray().Single(role => role!["name"]!.GetValue<string>() == "default-roles-learnstack")!
            ["composites"]!["client"]!["account"] = new JsonArray("view-profile", "manage-account");
        realm["clientScopeMappings"]!.AsObject().Remove("realm-management");
        realm.Remove("authenticationFlows");
        realm.Remove("requiredActions");
    }

    /// <summary>Admin reads and writes on a test IdP realm only (for example, creating a user late).</summary>
    internal async Task<KeycloakAdminClient> CreateIdentityProviderAdminClientAsync(string idpRealm)
    {
        var client = new KeycloakAdminClient(BaseAddress, RootCertificate, CheckedIdentityProviderRealm(idpRealm), AuthenticateAdministratorAsync);
        try { await client.AuthenticateAsync(); return client; }
        catch { client.Dispose(); throw; }
    }
}
