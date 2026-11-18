using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using FluentAssertions;
using LearnStack.Infrastructure.Identity.Federation;
using LearnStack.Modules.Identity.Application.Federation;
using LearnStack.SharedKernel.Identity;
using Xunit;

namespace LearnStack.Tests.Integration.TenancyIdentity;

/// <summary>
/// ADR-0099 Amendment 5 item 8: the brokered SAML sign-in, end to end, through the confined adapter, the committed realm's
/// link-only first-broker flow and a test SAML IdP realm in the same owned provider.
/// </summary>
public sealed partial class FederationProviderTests
{
    [Fact]
    public async Task A_brokered_saml_sign_in_refuses_unsigned_or_foreign_signed_responses_links_only_with_the_owners_credentials_and_carries_idp()
    {
        var owner = federation.OwnerSaml;
        const string person = FederationKeycloakFixture.SamlPerson;
        using var adapter = Adapter();
        using var admin = await Keycloak.CreateAdminClientAsync();
        using var idpAdmin = await Keycloak.CreateIdentityProviderAdminClientAsync(KeycloakFixture.SamlIdentityProviderRealm);
        // The broker is configured exactly as the production runner would: the IdP's real signing certificate, SSO
        // endpoint and entity id, through the port, with the closed profile the adapter writes.
        var desired = Saml(owner, await SigningCertificateAsync(idpAdmin));
        (await admin.PostAsync("users", new
        {
            username = person,
            email = person,
            emailVerified = true,
            enabled = true,
            firstName = "Saml",
            lastName = "Qualification",
            credentials = new[] { new { type = "password", value = KeycloakFixture.DemoPassword, temporary = false } },
        })).Should().Be(HttpStatusCode.Created);
        var user = await admin.UserAsync(person);
        var links = "users/" + user.GetProperty("id").GetString() + "/federated-identity";
        try
        {
            // 1. Signature validation stays on: the broker as read back through the adapter, and as the provider stores it.
            (await adapter.ApplyAsync(new(desired, null))).Should().Be(FederationProviderResult.Applied);
            var readback = await adapter.ReadAsync(owner);
            readback.Status.Should().Be(FederationReadbackStatus.Present);
            readback.ProfileConforms.Should().BeTrue("the pinned provider stores exactly the closed SAML profile");
            readback.MapperCount.Should().Be(0);
            FederationDigest.Readback(owner, readback).Should().Be(FederationDigest.Desired(desired));
            var stored = (await admin.GetAsync("identity-provider/instances/" + owner.Alias)).GetProperty("config");
            stored.GetProperty("validateSignature").GetString().Should().Be("true");
            stored.GetProperty("wantAssertionsSigned").GetString().Should().Be("true");
            (await admin.GetAsync(links)).GetArrayLength().Should().Be(0);

            // A response signed with a key other than the configured certificate is refused: the broker now trusts only a
            // foreign certificate, while the IdP keeps signing with its own key.
            (await adapter.ApplyAsync(new(Saml(owner, Certificate()), null))).Should().Be(FederationProviderResult.Applied);
            (await adapter.ReadAsync(owner)).ProfileConforms.Should().BeTrue("a certificate rotation is a conforming write");
            await RefusedAsync(owner, person, links, "a response signed by a key the broker does not trust");
            (await adapter.ApplyAsync(new(desired, null))).Should().Be(FederationProviderResult.Applied);

            // An unsigned response, and a signed response carrying an unsigned assertion, are refused with the real
            // certificate configured. Only the IdP's own signing switches change.
            var samlClient = (await idpAdmin.ClientAsync(Keycloak.Issuer)).GetProperty("id").GetString()!;
            var original = JsonNode.Parse((await idpAdmin.GetAsync("clients/" + samlClient)).GetRawText())!.AsObject();
            try
            {
                foreach (var (document, assertion, reason) in new[]
                {
                    ("false", "false", "an unsigned response with an unsigned assertion"),
                    ("true", "false", "a signed response whose assertion is unsigned (signed assertions are required)"),
                })
                {
                    var unsigned = original.DeepClone().AsObject();
                    unsigned["attributes"]!["saml.server.signature"] = document;
                    unsigned["attributes"]!["saml.assertion.signature"] = assertion;
                    (await idpAdmin.PutAsync("clients/" + samlClient, unsigned)).Should().Be(HttpStatusCode.NoContent);
                    await RefusedAsync(owner, person, links, reason);
                }
            }
            finally
            {
                (await idpAdmin.PutAsync("clients/" + samlClient, original)).Should().Be(HttpStatusCode.NoContent);
            }
            var restored = (await idpAdmin.GetAsync("clients/" + samlClient)).GetProperty("attributes");
            restored.GetProperty("saml.server.signature").GetString().Should().Be("true");
            restored.GetProperty("saml.assertion.signature").GetString().Should().Be("true");

            // 2. The first-broker link needs the owner's credentials. The dirty control for every refusal above is this
            // same signed response reaching the link confirmation; a wrong password then links nothing.
            using (var wrong = Keycloak.Browser())
            {
                var refused = await wrong.BrokeredAuthorizeAsync(owner.Alias, person, KeycloakFixture.IdentityProviderPassword, person,
                    "not-the-owners-password", Callback);
                refused.Stop.Should().Be(BrokeredStop.ReauthenticationRefused);
                refused.ReachedIdentityProvider.Should().BeTrue();
                refused.DeliveredSamlResponses.Should().Be(1, "the IdP's signed response reached the broker endpoint");
                refused.ReachedConfirmation.Should().BeTrue("the signed assertion's address detected the existing account");
            }
            (await admin.GetAsync(links)).GetArrayLength().Should().Be(0, "a failed re-authentication links nothing");
            using var browser = Keycloak.Browser();
            var linked = await browser.BrokeredAuthorizeAsync(owner.Alias, person, KeycloakFixture.IdentityProviderPassword, person,
                KeycloakFixture.DemoPassword, Callback);
            linked.Stop.Should().Be(BrokeredStop.Authorized);
            linked.DeliveredSamlResponses.Should().Be(1);
            var identities = await admin.GetAsync(links);
            identities.GetArrayLength().Should().Be(1, "the owner's own credentials completed the link");
            identities[0].GetProperty("identityProvider").GetString().Should().Be(owner.Alias);
            identities[0].GetProperty("userName").GetString().Should().NotBe(person,
                "the SAML principal is the persistent NameID subject, not the asserted address");

            // 3. The idp claim is on the brokered ID token, and not on the access token or a local sign-in's ID token. This
            // is the value the BFF's host binding compares with the self read's federationAlias; that comparison runs in
            // the BFF (its session-store tests), not here, and this test does not claim it.
            var exchange = await browser.RedeemAsync(linked.Authorization!);
            exchange.StatusCode.Should().Be(HttpStatusCode.OK);
            var brokeredIdp = Claims(exchange.IdToken).GetProperty("idp").GetString();
            brokeredIdp.Should().Be(owner.Alias);
            Claims(exchange.AccessToken).TryGetProperty("idp", out _).Should().BeFalse("the closed access-token profile carries no idp");
            using (var local = Keycloak.Browser())
            {
                var localTokens = await local.RedeemAsync(await local.AuthorizeAsync(person, KeycloakFixture.DemoPassword, Callback));
                localTokens.StatusCode.Should().Be(HttpStatusCode.OK);
                Claims(localTokens.IdToken).TryGetProperty("idp", out _).Should().BeFalse("a local sign-in is not brokered");
            }
        }
        finally
        {
            await admin.DeleteAsync("users/" + user.GetProperty("id").GetString());
            (await adapter.DeleteAsync(owner)).Should().Be(FederationProviderResult.Applied);
        }
        (await adapter.ReadAsync(owner)).Should().Be(FederationBrokerReadback.Absent);
    }

    /// <summary>
    /// One brokered SAML attempt with the owner's right password that must end on the tenant realm's error page after the
    /// IdP's response reached the broker endpoint: no confirmation, no code for learnstack-web, no link.
    /// </summary>
    private async Task RefusedAsync(FederationBrokerOwner owner, string person, string links, string reason)
    {
        using var browser = Keycloak.Browser();
        var result = await browser.BrokeredAuthorizeAsync(owner.Alias, person, KeycloakFixture.IdentityProviderPassword, person,
            KeycloakFixture.DemoPassword, Callback);
        result.ReachedIdentityProvider.Should().BeTrue(reason);
        result.DeliveredSamlResponses.Should().Be(1, reason);
        result.Stop.Should().Be(BrokeredStop.ErrorPage, reason);
        result.SignatureRefused.Should().BeTrue("the pinned broker refused the response's signature: " + reason);
        result.Authorization.Should().BeNull(reason);
        result.ReachedConfirmation.Should().BeFalse(reason);
        using var admin = await Keycloak.CreateAdminClientAsync();
        (await admin.GetAsync(links)).GetArrayLength().Should().Be(0, reason);
    }

    private FederationBrokerState Saml(FederationBrokerOwner owner, string certificate)
    {
        var issuer = Keycloak.ExternalIssuerFor(KeycloakFixture.SamlIdentityProviderRealm);
        return new(owner, "Tenant SAML", true, new FederationSamlConfiguration(issuer, new Uri(issuer + "/protocol/saml"), [certificate]));
    }

    /// <summary>The test IdP realm's active RS256 signing certificate, as public PEM.</summary>
    private static async Task<string> SigningCertificateAsync(KeycloakAdminClient idpAdmin)
    {
        var keys = await idpAdmin.GetAsync("keys");
        var kid = keys.GetProperty("active").GetProperty("RS256").GetString();
        var certificate = keys.GetProperty("keys").EnumerateArray().Single(key => key.GetProperty("kid").GetString() == kid)
            .GetProperty("certificate").GetString()!;
        return PemEncoding.WriteString("CERTIFICATE", Convert.FromBase64String(certificate));
    }
}
