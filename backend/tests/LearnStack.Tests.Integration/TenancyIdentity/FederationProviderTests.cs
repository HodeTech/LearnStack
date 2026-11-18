using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using LearnStack.Infrastructure.Identity.Federation;
using LearnStack.Modules.Identity.Application.Federation;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Resilience;
using LearnStack.SharedKernel.Secrets;
using LearnStack.Tests.Integration.Database;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.IdentityModel.Tokens;
using Polly;
using Xunit;

namespace LearnStack.Tests.Integration.TenancyIdentity;

/// <summary>One owned provider for ADR-0099 Step 3: the committed realm plus two test IdP realms.</summary>
public sealed class FederationKeycloakFixture : IAsyncLifetime
{
    internal const string IdpSecretA = "federation-idp-a-client-secret";
    internal const string IdpSecretB = "federation-idp-b-client-secret";
    internal const string UnknownPerson = "unknown-person@federation.test";
    internal const string ResetPerson = "reset-person@tenant-a.test";
    internal const string SamlPerson = "saml-person@tenant-a.test";
    internal KeycloakFixture Keycloak { get; private set; } = null!;
    internal FederationBrokerOwner OwnerA { get; } = Owner();
    internal FederationBrokerOwner OwnerB { get; } = Owner();

    /// <summary>Tenant A's SAML connection: the same tenant as <see cref="OwnerA"/>, its own connection and alias.</summary>
    internal FederationBrokerOwner OwnerSaml { get; }

    public FederationKeycloakFixture()
    {
        var connection = Guid.CreateVersion7();
        OwnerSaml = new(OwnerA.TenantId, connection, FederationBrokerOwner.AliasPrefix + connection.ToString("N"));
    }

    public async Task InitializeAsync()
    {
        // The owned mailbox carries the reset-link resumption qualification (ADR-0099 § Linking residuals).
        Keycloak = await KeycloakFixture.StartWithInvitationMailAsync();
        await Keycloak.CreateIdentityProviderRealmAsync("federation-idp-a", OwnerA.Alias, IdpSecretA,
            UnknownPerson, "demo-learner@tenant-a.test", "demo-admin@tenant-a.test", ResetPerson);
        await Keycloak.CreateIdentityProviderRealmAsync("federation-idp-b", OwnerB.Alias, IdpSecretB, "demo-learner@tenant-b.test");
        await Keycloak.CreateSamlIdentityProviderRealmAsync(OwnerSaml.Alias, SamlPerson);
    }

    public async Task DisposeAsync() => await Keycloak.DisposeAsync();

    internal static FederationBrokerOwner Owner()
    {
        var connection = Guid.CreateVersion7();
        return new(TenantId.From(Guid.CreateVersion7()), connection, FederationBrokerOwner.AliasPrefix + connection.ToString("N"));
    }
}

/// <summary>
/// ADR-0099 Step 3 real-provider controls through the confined adapter, the committed realm's link-only first-broker
/// flow and the owned provider's test IdP realms. Each case carries its dirty control.
/// </summary>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed partial class FederationProviderTests(FederationKeycloakFixture federation) : IClassFixture<FederationKeycloakFixture>
{
    private const string Callback = "https://demo-english.learnstack.local:3000/api/auth/callback/keycloak";
    private KeycloakFixture Keycloak => federation.Keycloak;

    [Fact]
    public async Task Two_tenant_brokers_coexist_with_the_closed_profile_and_neither_owner_reads_or_changes_the_other()
    {
        using var adapter = Adapter();
        (await adapter.ApplyAsync(new(Oidc(federation.OwnerA, "federation-idp-a", enabled: false), Secret(FederationKeycloakFixture.IdpSecretA))))
            .Should().Be(FederationProviderResult.Applied);
        (await adapter.ApplyAsync(new(Oidc(federation.OwnerB, "federation-idp-b", enabled: false), Secret(FederationKeycloakFixture.IdpSecretB))))
            .Should().Be(FederationProviderResult.Applied);
        foreach (var owner in new[] { federation.OwnerA, federation.OwnerB })
        {
            var readback = await adapter.ReadAsync(owner);
            readback.Status.Should().Be(FederationReadbackStatus.Present);
            readback.ProfileConforms.Should().BeTrue("the pinned provider stores exactly the closed profile");
            readback.MapperCount.Should().Be(0);
        }
        using var admin = await Keycloak.CreateAdminClientAsync();
        var storedA = await admin.GetAsync("identity-provider/instances/" + federation.OwnerA.Alias);
        storedA.GetProperty("config").GetProperty("clientSecret").GetString().Should().Be(FederationClientSecret.MaskLiteral,
            "the secret is written but never read back");
        // Tenant B naming tenant A's connection and alias: the owner marker refuses every call.
        var forged = new FederationBrokerOwner(federation.OwnerB.TenantId, federation.OwnerA.ConnectionId, federation.OwnerA.Alias);
        (await adapter.ReadAsync(forged)).Should().Be(FederationBrokerReadback.Foreign);
        (await adapter.ApplyAsync(new(Oidc(forged, "federation-idp-b", enabled: true), Secret("attacker-replaced-secret"))))
            .Should().Be(FederationProviderResult.Foreign);
        (await adapter.DeleteAsync(forged)).Should().Be(FederationProviderResult.Foreign);
        var after = await admin.GetAsync("identity-provider/instances/" + federation.OwnerA.Alias);
        JsonNode.DeepEquals(JsonNode.Parse(after.GetRawText()), JsonNode.Parse(storedA.GetRawText())).Should().BeTrue(
            "a foreign owner neither changed nor removed the broker");
        // Dirty control: the real owner can still read its own broker.
        (await adapter.ReadAsync(federation.OwnerA)).Status.Should().Be(FederationReadbackStatus.Present);
    }

    [Fact]
    public async Task An_unknown_address_ends_in_the_closed_error_and_creates_no_account()
    {
        await EnableAsync(federation.OwnerA, "federation-idp-a", FederationKeycloakFixture.IdpSecretA);
        using var admin = await Keycloak.CreateAdminClientAsync();
        var before = (await admin.GetAsync("users/count")).GetInt32();
        using var browser = Keycloak.Browser();
        var result = await browser.BrokeredAuthorizeAsync(federation.OwnerA.Alias, FederationKeycloakFixture.UnknownPerson,
            KeycloakFixture.IdentityProviderPassword, null, null, Callback);
        result.ReachedIdentityProvider.Should().BeTrue();
        result.Stop.Should().Be(BrokeredStop.ErrorPage);
        result.ReachedConfirmation.Should().BeFalse();
        (await admin.GetAsync("users/count")).GetInt32().Should().Be(before, "the broker creates no account");
        (await admin.GetAsync("users?email=" + Uri.EscapeDataString(FederationKeycloakFixture.UnknownPerson) + "&exact=true"))
            .GetArrayLength().Should().Be(0);
        // Dirty control: an address with an account reaches the link confirmation instead.
        using var control = Keycloak.Browser();
        (await control.BrokeredAuthorizeAsync(federation.OwnerA.Alias, "demo-admin@tenant-a.test", KeycloakFixture.IdentityProviderPassword,
            null, null, Callback, confirmLink: false)).Stop.Should().Be(BrokeredStop.ConfirmationAbandoned);
    }

    [Fact]
    public async Task A_matching_address_links_only_with_the_owners_credentials_and_the_brokered_token_carries_idp()
    {
        await EnableAsync(federation.OwnerA, "federation-idp-a", FederationKeycloakFixture.IdpSecretA);
        using var admin = await Keycloak.CreateAdminClientAsync();
        const string person = "demo-learner@tenant-a.test";
        var user = await admin.UserAsync(person);
        var links = "users/" + user.GetProperty("id").GetString() + "/federated-identity";
        (await admin.GetAsync(links)).GetArrayLength().Should().Be(0);
        using (var abandon = Keycloak.Browser())
            (await abandon.BrokeredAuthorizeAsync(federation.OwnerA.Alias, person, KeycloakFixture.IdentityProviderPassword, null, null,
                Callback, confirmLink: false)).Stop.Should().Be(BrokeredStop.ConfirmationAbandoned);
        (await admin.GetAsync(links)).GetArrayLength().Should().Be(0, "the asserted address alone links nothing");
        using (var wrong = Keycloak.Browser())
        {
            var refused = await wrong.BrokeredAuthorizeAsync(federation.OwnerA.Alias, person, KeycloakFixture.IdentityProviderPassword,
                person, "not-the-owners-password", Callback);
            refused.Stop.Should().Be(BrokeredStop.ReauthenticationRefused);
            refused.PrefilledUsername.Should().BeTrue("the form pre-fills the detected account, which is editable");
        }
        (await admin.GetAsync(links)).GetArrayLength().Should().Be(0, "a failed re-authentication links nothing");
        using var browser = Keycloak.Browser();
        var linked = await browser.BrokeredAuthorizeAsync(federation.OwnerA.Alias, person, KeycloakFixture.IdentityProviderPassword,
            person, KeycloakFixture.DemoPassword, Callback);
        linked.Stop.Should().Be(BrokeredStop.Authorized);
        var identities = await admin.GetAsync(links);
        identities.GetArrayLength().Should().Be(1);
        identities[0].GetProperty("identityProvider").GetString().Should().Be(federation.OwnerA.Alias);
        var exchange = await browser.RedeemAsync(linked.Authorization!);
        exchange.StatusCode.Should().Be(HttpStatusCode.OK);
        Claims(exchange.IdToken).GetProperty("idp").GetString().Should().Be(federation.OwnerA.Alias);
        Claims(exchange.AccessToken).TryGetProperty("idp", out _).Should().BeFalse("the closed access-token profile carries no idp");
        // Dirty control: a local sign-in of the same person carries no idp claim.
        using var local = Keycloak.Browser();
        var authorization = await local.AuthorizeAsync(person, KeycloakFixture.DemoPassword, Callback);
        var localTokens = await local.RedeemAsync(authorization);
        Claims(localTokens.IdToken).TryGetProperty("idp", out _).Should().BeFalse();
        // The account-console and linking surfaces refuse the brokered session (ADR-0099 § Realm invariants).
        using var console = KeycloakHttp.Create(Keycloak.BaseAddress, Keycloak.RootCertificate);
        console.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", exchange.AccessToken);
        foreach (var path in new[] { "realms/learnstack/account/linked-accounts", "realms/learnstack/account/linked-accounts/" + federation.OwnerA.Alias + "?providerId=" + federation.OwnerA.Alias + "&redirectUri=" + Uri.EscapeDataString(Callback), "realms/learnstack/account/credentials" })
        {
            using var response = await KeycloakHttp.SendAsync(console, HttpMethod.Get, path);
            ((int)response.StatusCode).Should().BeOneOf([401, 403, 404], path);
        }
        using var page = await KeycloakHttp.SendAsync(console, HttpMethod.Get, "realms/learnstack/account/");
        page.StatusCode.Should().Be(HttpStatusCode.NotFound, "the pinned console answers 404 when the account-console client is disabled");
        await admin.DeleteAsync(links + "/" + federation.OwnerA.Alias);
        (await admin.GetAsync(links)).GetArrayLength().Should().Be(0);

        // The idp_link application-initiated action is disabled: kc_action, sent as the authorization parameter, never
        // reaches a broker and adds no link.
        using (var action = Keycloak.Browser())
        {
            var refused = await action.ApplicationActionAsync(person, KeycloakFixture.DemoPassword, Callback, "idp_link:" + federation.OwnerA.Alias);
            refused.ReachedIdentityProvider.Should().BeFalse("the disabled idp_link action never redirects to the broker");
            refused.ReachedCallback.Should().BeTrue("the sign-in itself completes without the action");
            refused.ActionStatus.Should().Be("error", "the pinned provider reports the refused action to the client");
        }
        (await admin.GetAsync(links)).GetArrayLength().Should().Be(0);
        // Controls on the pinned IdpLinkAction: it runs only when the action is enabled AND the user holds manage-account
        // or manage-account-links within the client's scope. The realm removes both, so each alone still refuses; with
        // both restored for a moment the same request reaches the tenant's IdP, which proves the refusals above are the
        // realm's invariants and not a request that could never run.
        var idpLink = JsonNode.Parse((await admin.GetAsync("authentication/required-actions/idp_link")).GetRawText())!.AsObject();
        idpLink["enabled"]!.GetValue<bool>().Should().BeFalse();
        var accountClient = (await admin.ClientAsync("account")).GetProperty("id").GetString()!;
        var webClient = (await admin.ClientAsync("learnstack-web")).GetProperty("id").GetString()!;
        var linkRole = JsonNode.Parse((await admin.GetAsync("clients/" + accountClient + "/roles/manage-account-links")).GetRawText())!;
        var userRoles = "users/" + user.GetProperty("id").GetString() + "/role-mappings/clients/" + accountClient;
        var webScope = "clients/" + webClient + "/scope-mappings/clients/" + accountClient;
        try
        {
            idpLink["enabled"] = true;
            (await admin.PutAsync("authentication/required-actions/idp_link", idpLink)).Should().Be(HttpStatusCode.NoContent);
            using (var enabledOnly = Keycloak.Browser())
            {
                var refused = await enabledOnly.ApplicationActionAsync(person, KeycloakFixture.DemoPassword, Callback, "idp_link:" + federation.OwnerA.Alias);
                refused.ReachedIdentityProvider.Should().BeFalse("without an account-link role the enabled action is still not allowed");
                refused.ActionStatus.Should().Be("error");
            }
            (await admin.PostAsync(userRoles, new JsonArray(linkRole.DeepClone()))).Should().Be(HttpStatusCode.NoContent);
            (await admin.PostAsync(webScope, new JsonArray(linkRole.DeepClone()))).Should().Be(HttpStatusCode.NoContent);
            using var enabledAction = Keycloak.Browser();
            (await enabledAction.ApplicationActionAsync(person, KeycloakFixture.DemoPassword, Callback, "idp_link:" + federation.OwnerA.Alias))
                .ReachedIdentityProvider.Should().BeTrue("an enabled action with the link role redirects the signed-in user to the broker");
        }
        finally
        {
            (await admin.DeleteAsync(webScope, new JsonArray(linkRole.DeepClone()))).Should().Be(HttpStatusCode.NoContent);
            (await admin.DeleteAsync(userRoles, new JsonArray(linkRole.DeepClone()))).Should().Be(HttpStatusCode.NoContent);
            idpLink["enabled"] = false;
            (await admin.PutAsync("authentication/required-actions/idp_link", idpLink)).Should().Be(HttpStatusCode.NoContent);
        }
        (await admin.GetAsync("authentication/required-actions/idp_link")).GetProperty("enabled").GetBoolean().Should().BeFalse();
        (await admin.GetAsync(userRoles)).GetArrayLength().Should().Be(0, "the persona holds no account-client role again");
        (await admin.GetAsync(webScope)).GetArrayLength().Should().Be(0);
        (await admin.GetAsync(links)).GetArrayLength().Should().Be(0, "the positive control stopped at the IdP and linked nothing");

        // ADR-0099 residual, observed on the pinned build: the pre-filled username is editable, and the account that is
        // linked is whichever account's credentials are entered, never the one the asserted address selected.
        const string other = "demo-admin@tenant-a.test";
        var otherUser = await admin.UserAsync(other);
        var otherLinks = "users/" + otherUser.GetProperty("id").GetString() + "/federated-identity";
        using (var substituted = Keycloak.Browser())
            (await substituted.BrokeredAuthorizeAsync(federation.OwnerA.Alias, person, KeycloakFixture.IdentityProviderPassword,
                other, KeycloakFixture.DemoPassword, Callback)).Stop.Should().Be(BrokeredStop.Authorized);
        (await admin.GetAsync(links)).GetArrayLength().Should().Be(0, "the asserted address's account was not linked");
        (await admin.GetAsync(otherLinks)).GetArrayLength().Should().Be(1, "the account whose credentials were entered was linked");
        await admin.DeleteAsync(otherLinks + "/" + federation.OwnerA.Alias);
    }

    [Fact]
    public async Task A_disabled_broker_is_not_reachable_by_hint_while_an_enabled_hidden_one_is()
    {
        using var adapter = Adapter();
        (await adapter.ApplyAsync(new(Oidc(federation.OwnerB, "federation-idp-b", enabled: false), Secret(FederationKeycloakFixture.IdpSecretB))))
            .Should().Be(FederationProviderResult.Applied);
        using (var disabled = Keycloak.Browser())
            (await disabled.BrokeredAuthorizeAsync(federation.OwnerB.Alias, "demo-learner@tenant-b.test", KeycloakFixture.IdentityProviderPassword,
                null, null, Callback, confirmLink: false)).Stop.Should().Be(BrokeredStop.LocalLogin);
        // Control and residual: hideOnLogin hides only the login-page button; an enabled broker starts by hint, which is
        // why ADR-0099 keeps a broker enabled only in a test window or while Active.
        (await adapter.ApplyAsync(new(Oidc(federation.OwnerB, "federation-idp-b", enabled: true), null))).Should().Be(FederationProviderResult.Applied);
        using var enabled = Keycloak.Browser();
        var reached = await enabled.BrokeredAuthorizeAsync(federation.OwnerB.Alias, "demo-learner@tenant-b.test", KeycloakFixture.IdentityProviderPassword,
            null, null, Callback, confirmLink: false);
        reached.ReachedIdentityProvider.Should().BeTrue();
        reached.Stop.Should().Be(BrokeredStop.ConfirmationAbandoned);
        (await adapter.ApplyAsync(new(Oidc(federation.OwnerB, "federation-idp-b", enabled: false), null))).Should().Be(FederationProviderResult.Applied);
    }

    [Theory]
    [InlineData("${vault.federation}")]
    [InlineData("**********")]
    [InlineData("")]
    public async Task An_unusable_admin_or_tenant_secret_refuses_before_any_provider_request(string value)
    {
        var counting = new CountingHandler(KeycloakHttp.CreateHandler(Keycloak.RootCertificate));
        using var adapter = Adapter(value, counting);
        (await adapter.ApplyAsync(new(Oidc(federation.OwnerA, "federation-idp-a", enabled: true), Secret(FederationKeycloakFixture.IdpSecretA))))
            .Should().Be(FederationProviderResult.Unavailable);
        counting.Requests.Should().Be(0);
        FederationClientSecret.TryCreate(value, out _).Should().BeFalse("the tenant secret obeys the same refusal");
        // Dirty control: the configured admin secret reaches the provider.
        var control = new CountingHandler(KeycloakHttp.CreateHandler(Keycloak.RootCertificate));
        using var configured = Adapter(handler: control);
        (await configured.ReadAsync(federation.OwnerA)).Status.Should().NotBe(FederationReadbackStatus.Unavailable);
        control.Requests.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task A_saml_broker_keeps_signature_validation_and_a_disabled_value_reads_back_as_drift()
    {
        var owner = FederationKeycloakFixture.Owner();
        using var adapter = Adapter();
        var saml = new FederationBrokerState(owner, "Acme SAML", false, new FederationSamlConfiguration("https://saml.federation.test/entity",
            new Uri("https://saml.federation.test/sso"), [Certificate()]));
        (await adapter.ApplyAsync(new(saml, null))).Should().Be(FederationProviderResult.Applied);
        var readback = await adapter.ReadAsync(owner);
        readback.ProfileConforms.Should().BeTrue();
        using var admin = await Keycloak.CreateAdminClientAsync();
        var stored = JsonNode.Parse((await admin.GetAsync("identity-provider/instances/" + owner.Alias)).GetRawText())!.AsObject();
        stored["config"]!["validateSignature"]!.GetValue<string>().Should().Be("true");
        stored["config"]!["wantAssertionsSigned"]!.GetValue<string>().Should().Be("true");
        stored["config"]!["validateSignature"] = "false";
        (await admin.PutAsync("identity-provider/instances/" + owner.Alias, stored)).Should().Be(HttpStatusCode.NoContent);
        var drifted = await adapter.ReadAsync(owner);
        drifted.Status.Should().Be(FederationReadbackStatus.Present);
        drifted.ProfileConforms.Should().BeFalse("signature validation off is drift, never adopted");
        FederationDigest.Readback(owner, drifted).Should().NotBe(FederationDigest.Desired(saml));
        (await adapter.ApplyAsync(new(saml, null))).Should().Be(FederationProviderResult.Applied);
        (await adapter.ReadAsync(owner)).ProfileConforms.Should().BeTrue("a corrective full-state write restores the profile");
        (await adapter.DeleteAsync(owner)).Should().Be(FederationProviderResult.Applied);
        (await adapter.ReadAsync(owner)).Should().Be(FederationBrokerReadback.Absent);
    }

    [Fact]
    public async Task Egress_refuses_loopback_private_and_metadata_endpoints_and_admits_an_allow_listed_issuer()
    {
        // A DNS name for the owned loopback origin: the guard refuses IP-literal hosts by rule.
        var external = new UriBuilder(new Uri(Keycloak.ExternalIssuerFor("federation-idp-a") + "/.well-known/openid-configuration"))
        { Host = "localhost" }.Uri;
        var unlisted = new FederationEgressFetcher(FederationEgressOptions.Empty, System.Net.Dns.GetHostAddressesAsync, [Keycloak.RootCertificate]);
        (await unlisted.FetchOidcDiscoveryAsync(external)).Status.Should().Be(FederationEgressStatus.Refused, "loopback and port are not admitted");
        (await unlisted.CheckEndpointAsync(new Uri("https://169.254.169.254/latest/meta-data"))).Status.Should().Be(FederationEgressStatus.Refused);
        var privateHost = new FederationEgressFetcher(FederationEgressOptions.Empty,
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("10.0.0.7") }), [Keycloak.RootCertificate]);
        (await privateHost.CheckEndpointAsync(new Uri("https://idp.corp.example/token"))).Status.Should().Be(FederationEgressStatus.Refused);
        var metadataHost = new FederationEgressFetcher(FederationEgressOptions.Empty,
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("169.254.169.254") }), [Keycloak.RootCertificate]);
        (await metadataHost.CheckEndpointAsync(new Uri("https://metadata.example/computeMetadata"))).Status.Should().Be(FederationEgressStatus.Refused);
        // An allow-list entry admits a private address and the named port, never loopback: the listed host resolved by
        // real DNS to the owned loopback origin still refuses (ADR-0099 Amendment 5).
        var allowList = new FederationEgressOptions([external.Host + ":" + external.Port]);
        var loopback = new FederationEgressFetcher(allowList, System.Net.Dns.GetHostAddressesAsync, [Keycloak.RootCertificate]);
        (await loopback.FetchOidcDiscoveryAsync(external)).Status.Should().Be(FederationEgressStatus.Refused, "loopback is never admitted");
        // Dirty control: the listed host answering a private address is admitted, and the real discovery document and JWKS
        // are read through it. The test route dials the owned origin's loopback port in place of the validated address;
        // TLS still verifies the provider's certificate for the original host name.
        var validated = IPAddress.Parse("10.255.0.9");
        var listed = new FederationEgressFetcher(allowList, (_, _) => Task.FromResult(new[] { validated }), [Keycloak.RootCertificate],
            address => address.Equals(validated) ? IPAddress.Loopback : throw new InvalidOperationException("An unvalidated address was dialled."));
        var discovery = await listed.FetchOidcDiscoveryAsync(external);
        discovery.Status.Should().Be(FederationEgressStatus.Admitted);
        discovery.Value!.Issuer.Should().Be(external.AbsoluteUri[..^"/.well-known/openid-configuration".Length]);
        var keys = await listed.FetchJwksAsync(discovery.Value.JwksEndpoint);
        keys.Status.Should().Be(FederationEgressStatus.Admitted);
        keys.Value!.SigningKeyCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Readback_detects_a_changed_representation_and_a_delayed_write_that_lands_after_its_caller_gave_up()
    {
        var owner = FederationKeycloakFixture.Owner();
        using var adapter = Adapter();
        var desired = Oidc(owner, "federation-idp-a", enabled: false);
        (await adapter.ApplyAsync(new(desired, Secret(FederationKeycloakFixture.IdpSecretA)))).Should().Be(FederationProviderResult.Applied);
        FederationDigest.Readback(owner, await adapter.ReadAsync(owner)).Should().Be(FederationDigest.Desired(desired));
        using var admin = await Keycloak.CreateAdminClientAsync();
        var stored = JsonNode.Parse((await admin.GetAsync("identity-provider/instances/" + owner.Alias)).GetRawText())!.AsObject();
        stored["trustEmail"] = true;
        (await admin.PutAsync("identity-provider/instances/" + owner.Alias, stored)).Should().Be(HttpStatusCode.NoContent);
        var drift = await adapter.ReadAsync(owner);
        drift.ProfileConforms.Should().BeFalse();
        FederationDigest.Readback(owner, drift).Should().NotBe(FederationDigest.Desired(desired), "a changed representation is drift");
        (await adapter.ApplyAsync(new(desired, null))).Should().Be(FederationProviderResult.Applied);

        // ADR-0099 residual: a controlled proxy holds one PUT past the adapter's 2.5-second ceiling, so the caller records
        // an unknown outcome; a later write applies; then the held request lands. The next readback detects it.
        var proxy = new DelayingProxy(KeycloakHttp.CreateHandler(Keycloak.RootCertificate), TimeSpan.FromSeconds(4));
        using var delayed = Adapter(handler: proxy);
        var stale = desired with { DisplayName = "Stale delayed write" };
        proxy.HoldNextPut = true;
        (await delayed.ApplyAsync(new(stale, null))).Should().Be(FederationProviderResult.Unavailable, "the caller gave up: uncertain");
        var current = desired with { DisplayName = "Current settled write" };
        (await adapter.ApplyAsync(new(current, null))).Should().Be(FederationProviderResult.Applied);
        FederationDigest.Readback(owner, await adapter.ReadAsync(owner)).Should().Be(FederationDigest.Desired(current));
        (await proxy.Landed).Should().Be(HttpStatusCode.NoContent, "the held request still reached the provider");
        var late = await adapter.ReadAsync(owner);
        FederationDigest.Readback(owner, late).Should().NotBe(FederationDigest.Desired(current), "the next readback detects the late write");
        late.State!.DisplayName.Should().Be("Stale delayed write");
        (await adapter.DeleteAsync(owner)).Should().Be(FederationProviderResult.Applied);
    }

    [Fact]
    public async Task An_orphaned_unmarked_or_unreadable_broker_is_foreign_and_never_touched()
    {
        using var adapter = Adapter();
        using var admin = await Keycloak.CreateAdminClientAsync();
        // An orphan: a broker whose owner marker names a connection no LearnStack row holds. It is created through the
        // adapter for an owner that is then forgotten, so its representation is exactly what LearnStack would write.
        var orphan = FederationKeycloakFixture.Owner();
        (await adapter.ApplyAsync(new(Oidc(orphan, "federation-idp-a", enabled: true), Secret(FederationKeycloakFixture.IdpSecretA))))
            .Should().Be(FederationProviderResult.Applied);
        // An unmarked broker at a derived alias, as an operator might create by hand.
        var unmarked = FederationKeycloakFixture.Owner();
        var manual = KeycloakFederationProfile.Representation(Oidc(unmarked, "federation-idp-a", enabled: true), Secret(FederationKeycloakFixture.IdpSecretA));
        manual["config"]!.AsObject().Remove(KeycloakFederationProfile.OwnerKey);
        (await admin.PostAsync("identity-provider/instances", manual)).Should().Be(HttpStatusCode.Created);
        // An owned broker whose representation no longer reads as an admitted protocol (ADR-0099 residual, recorded for
        // the P03-9 operator sweep): its marker matches, its issuer was removed out of band.
        var unreadable = FederationKeycloakFixture.Owner();
        (await adapter.ApplyAsync(new(Oidc(unreadable, "federation-idp-a", enabled: true), Secret(FederationKeycloakFixture.IdpSecretA))))
            .Should().Be(FederationProviderResult.Applied);
        var damaged = JsonNode.Parse((await admin.GetAsync("identity-provider/instances/" + unreadable.Alias)).GetRawText())!.AsObject();
        damaged["config"]!.AsObject().Remove("issuer");
        (await admin.PutAsync("identity-provider/instances/" + unreadable.Alias, damaged)).Should().Be(HttpStatusCode.NoContent);
        try
        {
            var callers = new[]
            {
                // Another tenant naming the orphan's alias and connection: the marker names someone else.
                new FederationBrokerOwner(federation.OwnerA.TenantId, orphan.ConnectionId, orphan.Alias),
                // Any owner of the unmarked alias: no marker at all.
                unmarked,
                // The unreadable broker's own owner: the representation is not an admitted shape.
                unreadable,
            };
            foreach (var caller in callers)
            {
                var stored = (await admin.GetAsync("identity-provider/instances/" + caller.Alias)).GetRawText();
                (await adapter.ReadAsync(caller)).Should().Be(FederationBrokerReadback.Foreign);
                (await adapter.ApplyAsync(new(Oidc(caller, "federation-idp-a", enabled: false), Secret(FederationKeycloakFixture.IdpSecretA))))
                    .Should().Be(FederationProviderResult.Foreign, "not even a corrective disable is written");
                (await adapter.DeleteAsync(caller)).Should().Be(FederationProviderResult.Foreign);
                var after = (await admin.GetAsync("identity-provider/instances/" + caller.Alias)).GetRawText();
                JsonNode.DeepEquals(JsonNode.Parse(after), JsonNode.Parse(stored)).Should().BeTrue("the adapter left the broker byte-for-byte as found");
            }
            // Dirty control: the orphan's original owner still reads and manages it.
            (await adapter.ReadAsync(orphan)).Status.Should().Be(FederationReadbackStatus.Present);
            (await adapter.DeleteAsync(orphan)).Should().Be(FederationProviderResult.Applied);
            (await adapter.ReadAsync(orphan)).Should().Be(FederationBrokerReadback.Absent);
        }
        finally
        {
            foreach (var alias in new[] { orphan.Alias, unmarked.Alias, unreadable.Alias })
                await admin.DeleteAsync("identity-provider/instances/" + alias);
        }
    }

    [Fact]
    public async Task A_mapper_added_to_an_owned_alias_is_read_back_as_drift_that_no_activation_digest_equals()
    {
        var owner = FederationKeycloakFixture.Owner();
        using var adapter = Adapter();
        using var admin = await Keycloak.CreateAdminClientAsync();
        var desired = Oidc(owner, "federation-idp-a", enabled: false);
        (await adapter.ApplyAsync(new(desired, Secret(FederationKeycloakFixture.IdpSecretA)))).Should().Be(FederationProviderResult.Applied);
        var clean = await adapter.ReadAsync(owner);
        clean.MapperCount.Should().Be(0);
        FederationDigest.Readback(owner, clean).Should().Be(FederationDigest.Desired(desired), "the dirty control: a clean broker digests as desired");
        // An attribute importer that would let the tenant's IdP assert a protected attribute.
        var mapper = new JsonObject
        {
            ["name"] = "injected-tenant-id",
            ["identityProviderAlias"] = owner.Alias,
            ["identityProviderMapper"] = "oidc-user-attribute-idp-mapper",
            ["config"] = new JsonObject { ["syncMode"] = "FORCE", ["claim"] = "tenant_id", ["user.attribute"] = "tenant_id" },
        };
        (await admin.PostAsync("identity-provider/instances/" + owner.Alias + "/mappers", mapper)).Should().Be(HttpStatusCode.Created);
        try
        {
            var drifted = await adapter.ReadAsync(owner);
            drifted.Status.Should().Be(FederationReadbackStatus.Present);
            drifted.MapperCount.Should().Be(1, "the real mapper list is read back");
            drifted.ProfileConforms.Should().BeTrue("the broker representation itself is unchanged");
            // Activate and every readback require the readback digest to equal the desired digest (ADR-0099 § Lifecycle);
            // a mapper makes that impossible for every desired state, enabled or not.
            FederationDigest.Readback(owner, drifted).Should().NotBe(FederationDigest.Desired(desired));
            FederationDigest.Readback(owner, drifted).Should().NotBe(FederationDigest.Desired(desired with { Enabled = true }));
            // The adapter creates and deletes no mapper: a corrective full-state write leaves it, and the drift stays.
            (await adapter.ApplyAsync(new(desired, null))).Should().Be(FederationProviderResult.Applied);
            var corrected = await adapter.ReadAsync(owner);
            corrected.MapperCount.Should().Be(1);
            FederationDigest.Readback(owner, corrected).Should().NotBe(FederationDigest.Desired(desired));
        }
        finally
        {
            var mappers = await admin.GetAsync("identity-provider/instances/" + owner.Alias + "/mappers");
            foreach (var item in mappers.EnumerateArray())
                await admin.DeleteAsync("identity-provider/instances/" + owner.Alias + "/mappers/" + item.GetProperty("id").GetString());
        }
        FederationDigest.Readback(owner, await adapter.ReadAsync(owner)).Should().Be(FederationDigest.Desired(desired),
            "removing the mapper clears the drift");
        (await adapter.DeleteAsync(owner)).Should().Be(FederationProviderResult.Applied);
    }

    [Fact]
    public async Task Asserted_identity_attributes_never_reach_the_protected_claims_of_the_tenant_token()
    {
        await EnableAsync(federation.OwnerA, "federation-idp-a", FederationKeycloakFixture.IdpSecretA);
        using var admin = await Keycloak.CreateAdminClientAsync();
        using var idpAdmin = await Keycloak.CreateIdentityProviderAdminClientAsync("federation-idp-a");
        const string person = "demo-learner@tenant-a.test";
        var user = await admin.UserAsync(person);
        var links = "users/" + user.GetProperty("id").GetString() + "/federated-identity";
        var storedAttributes = user.GetProperty("attributes").GetRawText();
        var userId = user.GetProperty("attributes").GetProperty("user_id")[0].GetString()!;
        var client = (await idpAdmin.ClientAsync(KeycloakFixture.IdentityProviderClientId)).GetProperty("id").GetString()!;
        var hostile = new Dictionary<string, string>
        {
            ["user_id"] = Guid.NewGuid().ToString("D"),
            ["tenant_id"] = Guid.NewGuid().ToString("D"),
            ["organization_id"] = Guid.NewGuid().ToString("D"),
        };
        var created = new List<string>();
        try
        {
            foreach (var (claim, value) in hostile)
            {
                (await idpAdmin.PostAsync("clients/" + client + "/protocol-mappers/models", new JsonObject
                {
                    ["name"] = "hostile-" + claim,
                    ["protocol"] = "openid-connect",
                    ["protocolMapper"] = "oidc-hardcoded-claim-mapper",
                    ["config"] = new JsonObject
                    {
                        ["claim.name"] = claim,
                        ["claim.value"] = value,
                        ["jsonType.label"] = "String",
                        ["id.token.claim"] = "true",
                        ["access.token.claim"] = "true",
                        ["userinfo.token.claim"] = "true",
                    },
                })).Should().Be(HttpStatusCode.Created);
                created.Add("hostile-" + claim);
            }
            // Dirty control: the tenant's IdP really asserts the hostile values in the ID token it issues for this person.
            var idpUser = await idpAdmin.UserAsync(person);
            var example = await idpAdmin.GetAsync("clients/" + client + "/evaluate-scopes/generate-example-id-token?scope=openid&userId="
                + idpUser.GetProperty("id").GetString());
            foreach (var (claim, value) in hostile) example.GetProperty(claim).GetString().Should().Be(value);

            // First brokered sign-in links with the owner's credentials; the second reuses the link.
            foreach (var first in new[] { true, false })
            {
                using var browser = Keycloak.Browser();
                var result = first
                    ? await browser.BrokeredAuthorizeAsync(federation.OwnerA.Alias, person, KeycloakFixture.IdentityProviderPassword,
                        person, KeycloakFixture.DemoPassword, Callback)
                    : await browser.BrokeredAuthorizeAsync(federation.OwnerA.Alias, person, KeycloakFixture.IdentityProviderPassword,
                        null, null, Callback);
                result.Stop.Should().Be(BrokeredStop.Authorized, first ? "the first sign-in links" : "the linked sign-in needs no re-authentication");
                var exchange = await browser.RedeemAsync(result.Authorization!);
                exchange.StatusCode.Should().Be(HttpStatusCode.OK);
                foreach (var token in new[] { Claims(exchange.IdToken), Claims(exchange.AccessToken) })
                {
                    token.GetProperty("user_id").GetString().Should().Be(userId, "the tenant token carries the stored identity, not the asserted one");
                    token.TryGetProperty("tenant_id", out _).Should().BeFalse();
                    token.TryGetProperty("organization_id", out _).Should().BeFalse();
                }
                Claims(exchange.IdToken).GetProperty("idp").GetString().Should().Be(federation.OwnerA.Alias, "the token is brokered");
                var after = await admin.UserAsync(person);
                JsonNode.DeepEquals(JsonNode.Parse(after.GetProperty("attributes").GetRawText()), JsonNode.Parse(storedAttributes))
                    .Should().BeTrue("syncMode IMPORT, no broker mapper and no create step leave the stored attributes unchanged");
            }
        }
        finally
        {
            var models = await idpAdmin.GetAsync("clients/" + client + "/protocol-mappers/models");
            foreach (var model in models.EnumerateArray().Where(model => created.Contains(model.GetProperty("name").GetString()!)))
                await idpAdmin.DeleteAsync("clients/" + client + "/protocol-mappers/models/" + model.GetProperty("id").GetString());
            if ((await admin.GetAsync(links)).GetArrayLength() != 0) await admin.DeleteAsync(links + "/" + federation.OwnerA.Alias);
        }
    }

    [Fact]
    public async Task A_reset_requested_from_the_link_form_resumes_the_pending_link_only_in_the_browser_that_holds_it()
    {
        await EnableAsync(federation.OwnerA, "federation-idp-a", FederationKeycloakFixture.IdpSecretA);
        using var admin = await Keycloak.CreateAdminClientAsync();
        const string person = FederationKeycloakFixture.ResetPerson;
        (await admin.PostAsync("users", new
        {
            username = person,
            email = person,
            emailVerified = true,
            enabled = true,
            firstName = "Reset",
            lastName = "Qualification",
            credentials = new[] { new { type = "password", value = KeycloakFixture.DemoPassword, temporary = false } },
        })).Should().Be(HttpStatusCode.Created);
        var user = await admin.UserAsync(person);
        var links = "users/" + user.GetProperty("id").GetString() + "/federated-identity";
        try
        {
            // Another browser: the hostile-IdP case. The pending link is started in browser A; the mailbox owner opens the
            // emailed link in browser B. The password changes, but browser B holds no brokered session and links nothing.
            using (var pending = Keycloak.Browser())
            {
                (await pending.RequestResetFromReauthenticationAsync(federation.OwnerA.Alias, person, KeycloakFixture.IdentityProviderPassword,
                    person, Callback)).Should().BeTrue("the re-authentication form offers the reset action");
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var link = await Keycloak.ReadInvitationMailAsync(person, "/realms/learnstack/login-actions/action-token", deadline.Token);
                using var other = Keycloak.Browser();
                (await other.CompleteResetAsync(link, "Reset-Other-Browser-1!", Callback)).Should().Be(BrokeredResetEnd.ProviderPage,
                    "the link opened elsewhere starts a fresh reset session with no brokered context");
            }
            (await admin.GetAsync(links)).GetArrayLength().Should().Be(0, "a reset completed in another browser links nothing");
            using (var proof = Keycloak.Browser())
                (await proof.AuthorizeAsync(person, "Reset-Other-Browser-1!", Callback)).Authorized.Should().BeTrue("the reset itself took effect");

            // Same browser: the dirty control. The browser that holds the pending session resumes the first-broker flow after
            // the reset, and the link is completed for the account whose mailbox received the token.
            using (var holder = Keycloak.Browser())
            {
                (await holder.RequestResetFromReauthenticationAsync(federation.OwnerA.Alias, person, KeycloakFixture.IdentityProviderPassword,
                    person, Callback)).Should().BeTrue();
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var link = await Keycloak.ReadInvitationMailAsync(person, "/realms/learnstack/login-actions/action-token", deadline.Token);
                (await holder.CompleteResetAsync(link, "Reset-Same-Browser-2!", Callback)).Should().Be(BrokeredResetEnd.ReturnedToClient);
            }
            var linked = await admin.GetAsync(links);
            linked.GetArrayLength().Should().Be(1, "the holding browser resumed and completed the pending link");
            linked[0].GetProperty("identityProvider").GetString().Should().Be(federation.OwnerA.Alias);
        }
        finally
        {
            await admin.DeleteAsync("users/" + user.GetProperty("id").GetString());
        }
    }

    private async Task EnableAsync(FederationBrokerOwner owner, string idpRealm, string idpSecret)
    {
        using var adapter = Adapter();
        (await adapter.ApplyAsync(new(Oidc(owner, idpRealm, enabled: true), Secret(idpSecret)))).Should().Be(FederationProviderResult.Applied);
        var readback = await adapter.ReadAsync(owner);
        readback.ProfileConforms.Should().BeTrue();
        readback.State!.Enabled.Should().BeTrue();
    }

    private KeycloakFederationProvider Adapter(string secret = KeycloakFixture.FederationAdminSecret, HttpMessageHandler? handler = null) =>
        new(new KeycloakFederationOptions(Keycloak.Issuer), new Secrets(secret), new Resilience(),
            handler ?? KeycloakHttp.CreateHandler(Keycloak.RootCertificate));

    private FederationBrokerState Oidc(FederationBrokerOwner owner, string idpRealm, bool enabled)
    {
        // The IdP issues tokens under the browser-facing issuer; the tenant realm's back-channel reaches the token and JWKS
        // endpoints inside the container. UserInfo is not configured: the pinned UserInfo endpoint refuses a token whose
        // issuer names another origin, and the ID token already carries the profile claims.
        var browser = Keycloak.ExternalIssuerFor(idpRealm);
        var internalIssuer = KeycloakFixture.InternalIssuerFor(idpRealm);
        return new(owner, "Tenant SSO", enabled, new FederationOidcConfiguration(browser, KeycloakFixture.IdentityProviderClientId,
            FederationClientAuthentication.ClientSecretPost, new Uri(browser + "/protocol/openid-connect/auth"),
            new Uri(internalIssuer + "/protocol/openid-connect/token"), new Uri(internalIssuer + "/protocol/openid-connect/certs"), null));
    }

    private static FederationClientSecret Secret(string value) =>
        FederationClientSecret.TryCreate(value, out var secret) ? secret! : throw new InvalidOperationException("Test secret is invalid.");

    private static JsonElement Claims(string token)
    {
        using var document = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(token.Split('.')[1]));
        return document.RootElement.Clone();
    }

    private static string Certificate()
    {
        using var key = System.Security.Cryptography.RSA.Create(2048);
        using var certificate = new CertificateRequest("CN=federation-saml-test", key, System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.RSASignaturePadding.Pkcs1).CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        return certificate.ExportCertificatePem();
    }

    private sealed class Secrets(string value) : ISecretProvider
    {
        public string? GetSecret(string key) => key == "Federation:ClientSecret" ? value : null;
    }

    private sealed class Resilience : IProviderResilience<IFederationProvider>
    {
        public ResiliencePipeline Pipeline => ResiliencePipeline.Empty;
        public string PortName => "federation";
    }

    private sealed class CountingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        private int _requests;
        internal int Requests => _requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            return base.SendAsync(request, cancellationToken);
        }
    }

    /// <summary>
    /// A controlled proxy: it copies one identity-provider PUT, reports nothing to the caller until the caller's own
    /// ceiling expires, and forwards the copy after <paramref name="delay"/> on its own connection.
    /// </summary>
    private sealed class DelayingProxy(HttpMessageHandler inner, TimeSpan delay) : DelegatingHandler(inner)
    {
        private readonly TaskCompletionSource<HttpStatusCode> _landed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool HoldNextPut { get; set; }
        internal Task<HttpStatusCode> Landed => _landed.Task;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!HoldNextPut || request.Method != HttpMethod.Put) return await base.SendAsync(request, cancellationToken);
            HoldNextPut = false;
            var body = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            var uri = request.RequestUri!;
            var authorization = request.Headers.Authorization!;
            _ = Task.Run(async () =>
            {
                await Task.Delay(delay, CancellationToken.None);
                using var copy = new HttpRequestMessage(HttpMethod.Put, uri) { Content = new ByteArrayContent(body) };
                copy.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                copy.Headers.Authorization = authorization;
                using var response = await base.SendAsync(copy, CancellationToken.None);
                _landed.TrySetResult(response.StatusCode);
            }, CancellationToken.None);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new OperationCanceledException(cancellationToken);
        }
    }
}
