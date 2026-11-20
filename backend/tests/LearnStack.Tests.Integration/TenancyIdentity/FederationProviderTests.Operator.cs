using System.Net;
using System.Text.Json.Nodes;
using FluentAssertions;
using LearnStack.Infrastructure.Identity.Federation;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Resilience;
using Polly;
using Xunit;

namespace LearnStack.Tests.Integration.TenancyIdentity;

/// <summary>
/// ADR-0099 Amendment 10 against the pinned provider: the operator's listing, inspection, remove and disable through the
/// same confined admin client, which holds <c>manage-identity-providers</c> and nothing wider. Each case builds its own
/// brokers and removes them, so the realm the other cases share ends as it began.
/// </summary>
public sealed partial class FederationProviderTests
{
    private KeycloakFederationProvider OperatorAdapter() =>
        new(new KeycloakFederationOptions(Keycloak.Issuer), new Secrets(KeycloakFixture.FederationAdminSecret), new OperatorResilience(),
            KeycloakHttp.CreateHandler(Keycloak.RootCertificate));

    private sealed class OperatorResilience : IProviderResilience<IFederationOperator>
    {
        public ResiliencePipeline Pipeline => ResiliencePipeline.Empty;
        public string PortName => "federation";
    }

    private async Task<FederationBrokerOwner> OwnedBrokerAsync(KeycloakFederationProvider tenant, bool unreadable)
    {
        var owner = FederationKeycloakFixture.Owner();
        (await tenant.ApplyAsync(new(Oidc(owner, "federation-idp-a", enabled: true), Secret(FederationKeycloakFixture.IdpSecretA))))
            .Should().Be(FederationProviderResult.Applied);
        if (unreadable)
        {
            // Out of band, as an operator in the provider's own console might: the broker keeps its marker and loses its issuer.
            using var admin = await Keycloak.CreateAdminClientAsync();
            var damaged = JsonNode.Parse((await admin.GetAsync("identity-provider/instances/" + owner.Alias)).GetRawText())!.AsObject();
            damaged["config"]!.AsObject().Remove("issuer");
            (await admin.PutAsync("identity-provider/instances/" + owner.Alias, damaged)).Should().Be(HttpStatusCode.NoContent);
        }

        return owner;
    }

    [Fact]
    public async Task The_operator_lists_the_realm_in_bounded_pages_and_classifies_owned_unreadable_and_foreign_instances()
    {
        using var tenant = Adapter();
        using var op = OperatorAdapter();
        using var admin = await Keycloak.CreateAdminClientAsync();
        var healthy = await OwnedBrokerAsync(tenant, unreadable: false);
        var broken = await OwnedBrokerAsync(tenant, unreadable: true);
        var unmarked = FederationKeycloakFixture.Owner();
        var manual = KeycloakFederationProfile.Representation(Oidc(unmarked, "federation-idp-a", enabled: true), Secret(FederationKeycloakFixture.IdpSecretA));
        manual["config"]!.AsObject().Remove(KeycloakFederationProfile.OwnerKey);
        (await admin.PostAsync("identity-provider/instances", manual)).Should().Be(HttpStatusCode.Created);
        try
        {
            var owned = new Dictionary<string, FederationBrokerFacts>(StringComparer.Ordinal);
            var ignored = 0;
            for (var offset = 0; ; offset += 2)
            {
                var page = await op.ListAsync(offset, 2);
                page.Status.Should().Be(FederationOperatorStatus.Ok, "the federation client's one role is enough to list");
                page.Scanned.Should().BeLessThanOrEqualTo(2);
                foreach (var facts in page.Owned) owned[facts.Owner.Alias] = facts;
                ignored += page.Ignored;
                if (page.Scanned < 2) break;
            }

            owned[healthy.Alias].Should().Match<FederationBrokerFacts>(facts => facts.Readable && facts.Conforms && facts.Enabled,
                "the pinned provider masks the secret in the listing, so a healthy broker reads as conforming");
            owned[broken.Alias].Should().Match<FederationBrokerFacts>(facts => !facts.Readable && !facts.Conforms);
            owned.Should().NotContainKey(unmarked.Alias, "an unmarked instance is never reported");
            ignored.Should().BeGreaterThanOrEqualTo(1);
            owned.Values.Should().OnlyContain(facts => facts.Owner.Alias.StartsWith("lsf-", StringComparison.Ordinal));

            var inspection = await op.InspectAsync(healthy.Alias);
            (inspection.Status, inspection.Facts!.Owner.TenantId, inspection.MapperCount).Should().Be((FederationInspectionStatus.Owned, healthy.TenantId, 0));
            (await op.InspectAsync(unmarked.Alias)).Should().Be(FederationBrokerInspection.NotOwned);
            (await op.InspectAsync(FederationKeycloakFixture.Owner().Alias)).Should().Be(FederationBrokerInspection.Absent);
        }
        finally
        {
            foreach (var alias in new[] { healthy.Alias, broken.Alias, unmarked.Alias })
                await admin.DeleteAsync("identity-provider/instances/" + alias);
        }
    }

    [Fact]
    public async Task The_operator_removes_an_unreadable_orphan_and_the_provider_deletes_its_federated_links_and_nothing_else()
    {
        using var tenant = Adapter();
        using var op = OperatorAdapter();
        using var admin = await Keycloak.CreateAdminClientAsync();
        var broken = await OwnedBrokerAsync(tenant, unreadable: true);
        var unmarked = FederationKeycloakFixture.Owner();
        var manual = KeycloakFederationProfile.Representation(Oidc(unmarked, "federation-idp-a", enabled: true), Secret(FederationKeycloakFixture.IdpSecretA));
        manual["config"]!.AsObject().Remove(KeycloakFederationProfile.OwnerKey);
        (await admin.PostAsync("identity-provider/instances", manual)).Should().Be(HttpStatusCode.Created);
        var user = await admin.UserAsync("demo-learner@tenant-a.test");
        var userId = user.GetProperty("id").GetString()!;
        (await admin.PostAsync($"users/{userId}/federated-identity/{broken.Alias}", new JsonObject { ["userId"] = "external-1", ["userName"] = "external-1" }))
            .Should().Be(HttpStatusCode.NoContent, "a link exists to be lost");
        (await admin.GetAsync($"users/{userId}/federated-identity")).GetArrayLength().Should().BeGreaterThan(0);
        try
        {
            // The tenant adapter will not touch an unreadable broker; the operator removes it on its marker alone.
            (await tenant.DeleteAsync(broken)).Should().Be(FederationProviderResult.Foreign);
            var stored = (await admin.GetAsync("identity-provider/instances/" + unmarked.Alias)).GetRawText();

            (await op.RemoveAsync(broken)).Should().Be(FederationOperatorOutcome.Applied);

            (await admin.GetAsync("identity-provider/instances")).EnumerateArray()
                .Select(instance => instance.GetProperty("alias").GetString()).Should().NotContain(broken.Alias);
            (await admin.GetAsync($"users/{userId}/federated-identity")).EnumerateArray()
                .Select(link => link.GetProperty("identityProvider").GetString()).Should().NotContain(broken.Alias,
                    "the provider deletes the federated links with the broker, which is why an account review is owed");
            (await admin.GetAsync($"users/{userId}")).GetProperty("id").GetString().Should().Be(userId, "the account itself stays");
            (await op.RemoveAsync(broken)).Should().Be(FederationOperatorOutcome.AlreadyApplied);
            // Dirty control: a derived alias with no marker is never deleted, and stays as it was found.
            var other = new FederationBrokerOwner(unmarked.TenantId, unmarked.ConnectionId, unmarked.Alias);
            (await op.RemoveAsync(other)).Should().Be(FederationOperatorOutcome.NotOwned);
            JsonNode.DeepEquals(JsonNode.Parse((await admin.GetAsync("identity-provider/instances/" + unmarked.Alias)).GetRawText()),
                JsonNode.Parse(stored)).Should().BeTrue();
        }
        finally
        {
            foreach (var alias in new[] { broken.Alias, unmarked.Alias })
                await admin.DeleteAsync("identity-provider/instances/" + alias);
        }
    }

    [Fact]
    public async Task The_operator_disables_an_unreadable_broker_by_sending_back_what_the_provider_returned_with_only_enabled_changed()
    {
        using var tenant = Adapter();
        using var op = OperatorAdapter();
        using var admin = await Keycloak.CreateAdminClientAsync();
        var broken = await OwnedBrokerAsync(tenant, unreadable: true);
        try
        {
            var before = JsonNode.Parse((await admin.GetAsync("identity-provider/instances/" + broken.Alias)).GetRawText())!.AsObject();

            (await op.DisableAsync(broken)).Should().Be(FederationOperatorOutcome.Applied);

            var after = JsonNode.Parse((await admin.GetAsync("identity-provider/instances/" + broken.Alias)).GetRawText())!.AsObject();
            after["enabled"]!.GetValue<bool>().Should().BeFalse();
            after["config"]!["clientSecret"]!.GetValue<string>().Should().Be(FederationClientSecret.MaskLiteral,
                "the provider kept the stored secret when it read its own mask back");
            // Nothing else moved: compare everything the provider computes on each read away.
            foreach (var key in new[] { "internalId", "types" })
            {
                before.Remove(key);
                after.Remove(key);
            }

            before["enabled"] = false;
            JsonNode.DeepEquals(after, before).Should().BeTrue("only enabled differs from what the provider held");
            (await op.DisableAsync(broken)).Should().Be(FederationOperatorOutcome.AlreadyApplied);
        }
        finally
        {
            await admin.DeleteAsync("identity-provider/instances/" + broken.Alias);
        }
    }
}
