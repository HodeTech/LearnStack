using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using LearnStack.Infrastructure.Identity.Federation;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Resilience;
using LearnStack.SharedKernel.Secrets;
using Polly;
using Xunit;

namespace LearnStack.Tests.Unit.Infrastructure.Identity;

/// <summary>The confined Keycloak adapter against a scripted provider: admitted calls, ownership, the profile and the secret.</summary>
public sealed class KeycloakFederationProviderTests
{
    private const string Issuer = "https://provider.invalid/realms/learnstack";
    private const string AdminSecret = "federation-admin-secret-canary";
    private const string TenantSecret = "tenant-idp-secret-canary";
    private static readonly TenantId Tenant = TenantId.From(Guid.CreateVersion7());
    private static readonly Guid Connection = Guid.CreateVersion7();
    private static readonly FederationBrokerOwner Owner = new(Tenant, Connection, FederationBrokerOwner.AliasPrefix + Connection.ToString("N"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("**********")]
    [InlineData("${vault.federation}")]
    public async Task An_unusable_admin_secret_refuses_before_any_provider_call(string? secret)
    {
        var provider = new FakeKeycloak();
        using var adapter = Create(provider, secret);
        (await adapter.ApplyAsync(new(State(), Fresh()))).Should().Be(FederationProviderResult.Unavailable);
        (await adapter.ReadAsync(Owner)).Should().Be(FederationBrokerReadback.Unavailable);
        (await adapter.DeleteAsync(Owner)).Should().Be(FederationProviderResult.Unavailable);
        provider.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task An_oversized_admin_secret_refuses_before_any_provider_call()
    {
        var provider = new FakeKeycloak();
        using var adapter = Create(provider, new string('s', 1025));
        (await adapter.ReadAsync(Owner)).Should().Be(FederationBrokerReadback.Unavailable);
        provider.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Create_writes_the_closed_profile_once_and_the_readback_conforms_without_returning_the_secret()
    {
        var provider = new FakeKeycloak();
        using var adapter = Create(provider);
        (await adapter.ApplyAsync(new(State(), Fresh()))).Should().Be(FederationProviderResult.Applied);
        provider.Requests.Select(request => request.Method + " " + request.Path).Should().Equal(
            "POST /realms/learnstack/protocol/openid-connect/token",
            "GET /admin/realms/learnstack/identity-provider/instances/" + Owner.Alias,
            "POST /admin/realms/learnstack/identity-provider/instances");
        var created = provider.Brokers[Owner.Alias];
        created["config"]!["clientSecret"]!.GetValue<string>().Should().Be(TenantSecret);
        created["config"]![KeycloakFederationProfile.OwnerKey]!.GetValue<string>().Should().Be($"{Tenant.Value:D}:{Connection:D}");
        created["firstBrokerLoginFlowAlias"]!.GetValue<string>().Should().Be("learnstack-first-broker-login");
        created["trustEmail"]!.GetValue<bool>().Should().BeFalse();
        created["hideOnLogin"]!.GetValue<bool>().Should().BeTrue();
        created["config"]!["showInAccountConsole"]!.GetValue<string>().Should().Be("NEVER");
        created.ContainsKey("postBrokerLoginFlowAlias").Should().BeFalse();

        var readback = await adapter.ReadAsync(Owner);
        readback.Status.Should().Be(FederationReadbackStatus.Present);
        readback.ProfileConforms.Should().BeTrue();
        readback.MapperCount.Should().Be(0);
        readback.State.Should().Be(State());
        JsonSerializer.Serialize(readback.State!.Configuration, readback.State.Configuration.GetType()).Should().NotContain(TenantSecret);
        provider.Requests.Should().NotContain(request => request.Body.Contains("fromUrl", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_update_without_a_fresh_secret_sends_the_mask_and_never_creates_a_secretless_broker()
    {
        var provider = new FakeKeycloak();
        using var adapter = Create(provider);
        (await adapter.ApplyAsync(new(State(), null))).Should().Be(FederationProviderResult.Refused);
        provider.Brokers.Should().BeEmpty();
        (await adapter.ApplyAsync(new(State(), Fresh()))).Should().Be(FederationProviderResult.Applied);
        (await adapter.ApplyAsync(new(State(enabled: true), null))).Should().Be(FederationProviderResult.Applied);
        provider.Requests.Last().Method.Should().Be("PUT");
        provider.Requests.Last().Body.Should().Contain("\"clientSecret\":\"**********\"").And.NotContain(TenantSecret);
        (await adapter.ReadAsync(Owner)).State!.Enabled.Should().BeTrue();
    }

    [Theory]
    [InlineData("token-endpoint")]
    [InlineData("client-authentication")]
    [InlineData("client-id")]
    public async Task A_changed_secret_target_without_a_fresh_secret_refuses_before_any_write(string change)
    {
        var provider = new FakeKeycloak();
        using var adapter = Create(provider);
        (await adapter.ApplyAsync(new(State(), Fresh()))).Should().Be(FederationProviderResult.Applied);
        var held = provider.Brokers[Owner.Alias].ToJsonString();
        var oidc = (FederationOidcConfiguration)State().Configuration;
        var changed = change switch
        {
            "token-endpoint" => oidc with { TokenEndpoint = new Uri("https://attacker.example.com/token") },
            "client-authentication" => oidc with { ClientAuthentication = FederationClientAuthentication.ClientSecretBasic },
            _ => oidc with { ClientId = "another-client" },
        };
        var desired = State() with { Configuration = changed };
        var puts = provider.Requests.Count(request => request.Method == "PUT");
        (await adapter.ApplyAsync(new(desired, null))).Should().Be(FederationProviderResult.Refused,
            "the stored secret is never sent to a changed token endpoint, client or authentication method (ADR-0099 Amendment 3)");
        provider.Requests.Count(request => request.Method == "PUT").Should().Be(puts, "no provider write was made");
        provider.Brokers[Owner.Alias].ToJsonString().Should().Be(held);
        // Dirty controls: the same change with a fresh secret is written, and an unchanged configuration keeps the secret.
        (await adapter.ApplyAsync(new(desired, Fresh()))).Should().Be(FederationProviderResult.Applied);
        (await adapter.ApplyAsync(new(desired with { Enabled = true, DisplayName = "Renamed" }, null))).Should().Be(FederationProviderResult.Applied);
        provider.Requests.Count(request => request.Method == "PUT").Should().Be(puts + 2);
    }

    [Fact]
    public async Task An_issuer_or_entity_change_refuses_even_with_a_fresh_secret()
    {
        var provider = new FakeKeycloak();
        using var adapter = Create(provider);
        (await adapter.ApplyAsync(new(State(), Fresh()))).Should().Be(FederationProviderResult.Applied);
        var moved = State() with { Configuration = (FederationOidcConfiguration)State().Configuration with { Issuer = "https://idp.example.com/realms/other" } };
        (await adapter.ApplyAsync(new(moved, Fresh()))).Should().Be(FederationProviderResult.Refused, "a new issuer needs a new connection");
        (await adapter.ApplyAsync(new(moved, null))).Should().Be(FederationProviderResult.Refused);
        provider.Requests.Should().NotContain(request => request.Method == "PUT");
        ((FederationOidcConfiguration)(await adapter.ReadAsync(Owner)).State!.Configuration).Issuer.Should().Be("https://idp.example.com/realms/acme");

        var samlConnection = Guid.CreateVersion7();
        var samlOwner = new FederationBrokerOwner(Tenant, samlConnection, FederationBrokerOwner.AliasPrefix + samlConnection.ToString("N"));
        var saml = new FederationBrokerState(samlOwner, "Acme SAML", false, new FederationSamlConfiguration("https://saml.example.com/entity",
            new Uri("https://saml.example.com/sso"), [Pem()]));
        (await adapter.ApplyAsync(new(saml, null))).Should().Be(FederationProviderResult.Applied);
        var otherEntity = saml with { Configuration = ((FederationSamlConfiguration)saml.Configuration) with { EntityId = "https://saml.example.com/other" } };
        (await adapter.ApplyAsync(new(otherEntity, null))).Should().Be(FederationProviderResult.Refused);
        // Dirty control: the same entity with a new single sign-on endpoint is an edit.
        var moveSso = saml with { Configuration = ((FederationSamlConfiguration)saml.Configuration) with { SingleSignOnEndpoint = new Uri("https://saml.example.com/sso2") } };
        (await adapter.ApplyAsync(new(moveSso, null))).Should().Be(FederationProviderResult.Applied);
    }

    [Fact]
    public async Task A_foreign_owner_marker_is_reported_and_never_written_read_or_deleted()
    {
        var provider = new FakeKeycloak();
        using var adapter = Create(provider);
        (await adapter.ApplyAsync(new(State(), Fresh()))).Should().Be(FederationProviderResult.Applied);
        provider.Brokers[Owner.Alias]["config"]![KeycloakFederationProfile.OwnerKey] = $"{Guid.CreateVersion7():D}:{Connection:D}";
        var writes = provider.Requests.Count(request => request.Method is "PUT" or "POST" or "DELETE" && !request.Path.EndsWith("/token", StringComparison.Ordinal));
        (await adapter.ApplyAsync(new(State(enabled: true), Fresh()))).Should().Be(FederationProviderResult.Foreign);
        (await adapter.ReadAsync(Owner)).Should().Be(FederationBrokerReadback.Foreign);
        (await adapter.DeleteAsync(Owner)).Should().Be(FederationProviderResult.Foreign);
        provider.Requests.Count(request => request.Method is "PUT" or "POST" or "DELETE" && !request.Path.EndsWith("/token", StringComparison.Ordinal))
            .Should().Be(writes);
        provider.Requests.Should().NotContain(request => request.Path.EndsWith("/mappers", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("extra-key")]
    [InlineData("trust-email")]
    [InlineData("signature-off")]
    [InlineData("post-broker-flow")]
    [InlineData("secret-removed")]
    [InlineData("account-console")]
    public async Task Any_profile_difference_reads_back_as_nonconforming(string change)
    {
        var provider = new FakeKeycloak();
        using var adapter = Create(provider);
        (await adapter.ApplyAsync(new(State(), Fresh()))).Should().Be(FederationProviderResult.Applied);
        (await adapter.ReadAsync(Owner)).ProfileConforms.Should().BeTrue("the dirty control conforms");
        var broker = provider.Brokers[Owner.Alias];
        var config = broker["config"]!.AsObject();
        switch (change)
        {
            case "extra-key": config["tokenIntrospectionUrl"] = "https://idp.example.com/introspect"; break;
            case "trust-email": broker["trustEmail"] = true; break;
            case "signature-off": config["validateSignature"] = "false"; break;
            case "post-broker-flow": broker["postBrokerLoginFlowAlias"] = "post"; break;
            case "secret-removed": config.Remove("clientSecret"); break;
            case "account-console": config["showInAccountConsole"] = "ALWAYS"; break;
        }
        var readback = await adapter.ReadAsync(Owner);
        readback.Status.Should().Be(FederationReadbackStatus.Present);
        readback.ProfileConforms.Should().BeFalse();
    }

    [Fact]
    public async Task A_mapper_on_the_owned_alias_is_counted()
    {
        var provider = new FakeKeycloak();
        using var adapter = Create(provider);
        (await adapter.ApplyAsync(new(State(), Fresh()))).Should().Be(FederationProviderResult.Applied);
        provider.Mappers = 1;
        (await adapter.ReadAsync(Owner)).MapperCount.Should().Be(1);
    }

    [Fact]
    public async Task A_saml_broker_round_trips_its_certificates_and_fixed_trust()
    {
        var provider = new FakeKeycloak();
        using var adapter = Create(provider);
        var saml = new FederationBrokerState(Owner, "Acme SAML", false, new FederationSamlConfiguration("https://saml.example.com/entity",
            new Uri("https://saml.example.com/sso"), [Pem()]));
        (await adapter.ApplyAsync(new(saml, null))).Should().Be(FederationProviderResult.Applied);
        var config = provider.Brokers[Owner.Alias]["config"]!.AsObject();
        config["validateSignature"]!.GetValue<string>().Should().Be("true");
        config["wantAssertionsSigned"]!.GetValue<string>().Should().Be("true");
        config.ContainsKey("clientSecret").Should().BeFalse();
        config.ContainsKey("metadataDescriptorUrl").Should().BeFalse();
        var readback = await adapter.ReadAsync(Owner);
        readback.ProfileConforms.Should().BeTrue();
        var configuration = (FederationSamlConfiguration)readback.State!.Configuration;
        configuration.SigningCertificates.Should().ContainSingle();
    }

    [Theory]
    [InlineData("issuer")]
    [InlineData("client")]
    [InlineData("extra-role")]
    [InlineData("realm-role")]
    public async Task A_token_broader_or_foreign_to_the_admin_client_writes_nothing(string defect)
    {
        var provider = new FakeKeycloak { TokenDefect = defect };
        using var adapter = Create(provider);
        (await adapter.ApplyAsync(new(State(), Fresh()))).Should().Be(FederationProviderResult.Unavailable);
        provider.Requests.Should().ContainSingle().Which.Path.Should().EndWith("/token");
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task A_provider_fault_is_unavailable_and_carries_no_provider_text(HttpStatusCode status)
    {
        var provider = new FakeKeycloak { WriteStatus = status };
        using var adapter = Create(provider);
        (await adapter.ApplyAsync(new(State(), Fresh()))).Should().Be(FederationProviderResult.Unavailable);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Conflict)]
    public async Task A_definitive_provider_refusal_is_refused(HttpStatusCode status)
    {
        var provider = new FakeKeycloak { WriteStatus = status };
        using var adapter = Create(provider);
        (await adapter.ApplyAsync(new(State(), Fresh()))).Should().Be(FederationProviderResult.Refused);
    }

    [Fact]
    public async Task Delete_of_an_absent_owned_alias_is_already_applied()
    {
        var provider = new FakeKeycloak();
        using var adapter = Create(provider);
        (await adapter.DeleteAsync(Owner)).Should().Be(FederationProviderResult.Applied);
        provider.Requests.Should().NotContain(request => request.Method == "DELETE");
        (await adapter.ApplyAsync(new(State(), Fresh()))).Should().Be(FederationProviderResult.Applied);
        (await adapter.DeleteAsync(Owner)).Should().Be(FederationProviderResult.Applied);
        provider.Brokers.Should().BeEmpty();
    }

    [Fact]
    public async Task Every_admitted_request_targets_the_enumerated_routes_only()
    {
        var provider = new FakeKeycloak();
        using var adapter = Create(provider);
        await adapter.ApplyAsync(new(State(), Fresh()));
        await adapter.ApplyAsync(new(State(enabled: true), null));
        await adapter.ReadAsync(Owner);
        await adapter.DeleteAsync(Owner);
        using var operatorAdapter = CreateOperator(provider);
        await operatorAdapter.ListAsync(0, 2);
        var admitted = new[]
        {
            "GET /admin/realms/learnstack/identity-provider/instances",
            "POST /realms/learnstack/protocol/openid-connect/token",
            "POST /admin/realms/learnstack/identity-provider/instances",
            "GET /admin/realms/learnstack/identity-provider/instances/" + Owner.Alias,
            "PUT /admin/realms/learnstack/identity-provider/instances/" + Owner.Alias,
            "DELETE /admin/realms/learnstack/identity-provider/instances/" + Owner.Alias,
            "GET /admin/realms/learnstack/identity-provider/instances/" + Owner.Alias + "/mappers",
        };
        provider.Requests.Select(request => request.Method + " " + request.Path).Distinct().Should().BeSubsetOf(admitted);
        // Only the listing carries a query, and it is exactly the bounded page: no search, no filter, no other member.
        provider.Requests.Where(request => request.Query.Length != 0).Should().OnlyContain(
            request => request.Method == "GET" && request.Path == "/admin/realms/learnstack/identity-provider/instances"
                && request.Query == "?first=0&max=2&briefRepresentation=false");
    }

    private static KeycloakFederationProvider Create(FakeKeycloak provider, string? secret = AdminSecret) =>
        new(new KeycloakFederationOptions(Issuer), new Secrets(secret), new Resilience(), provider);

    private static KeycloakFederationProvider CreateOperator(FakeKeycloak provider, string? secret = AdminSecret) =>
        new(new KeycloakFederationOptions(Issuer), new Secrets(secret), new OperatorResilience(), provider);

    private static FederationBrokerOwner OwnerOf(Guid? connection = null, TenantId? tenant = null)
    {
        var id = connection ?? Guid.CreateVersion7();
        return new(tenant ?? Tenant, id, FederationBrokerOwner.AliasPrefix + id.ToString("N"));
    }

    private static FederationBrokerState StateFor(FederationBrokerOwner owner, bool enabled = true) => State(enabled) with { Owner = owner };

    /// <summary>A broker as the provider stores it after a tenant's create: conforming, with its secret.</summary>
    private static JsonObject Stored(FederationBrokerOwner owner, bool enabled = true) =>
        KeycloakFederationProfile.Representation(StateFor(owner, enabled), Fresh());

    /// <summary>An owned broker the adapter cannot read: its issuer is gone, as when an operator edited it in the provider.</summary>
    private static JsonObject Unreadable(FederationBrokerOwner owner, bool enabled = true)
    {
        var broker = Stored(owner, enabled);
        broker["config"]!.AsObject().Remove("issuer");
        return broker;
    }

    private static string Foreign() => "lsf-" + new string('0', 32);

    [Fact]
    public async Task A_listing_classifies_owned_brokers_and_ignores_everything_else()
    {
        var provider = new FakeKeycloak();
        var healthy = OwnerOf();
        var broken = OwnerOf();
        var drifted = OwnerOf();
        provider.Brokers[healthy.Alias] = Stored(healthy);
        provider.Brokers[broken.Alias] = Unreadable(broken, enabled: false);
        provider.Brokers[drifted.Alias] = Stored(drifted);
        provider.Brokers[drifted.Alias]["config"]!["extra"] = "not-in-the-profile";
        // Not LearnStack's: an alias nobody derived, a derived alias with no marker, and a marker for another connection.
        provider.Brokers["corporate-sso"] = new JsonObject { ["alias"] = "corporate-sso", ["config"] = new JsonObject() };
        var unmarked = OwnerOf();
        provider.Brokers[unmarked.Alias] = new JsonObject { ["alias"] = unmarked.Alias, ["config"] = new JsonObject() };
        var mismatched = OwnerOf();
        var stolen = Stored(mismatched);
        stolen["config"]![KeycloakFederationProfile.OwnerKey] = $"{Tenant.Value:D}:{Guid.CreateVersion7():D}";
        provider.Brokers[mismatched.Alias] = stolen;
        using var adapter = CreateOperator(provider);

        var page = await adapter.ListAsync(0, 20);

        page.Status.Should().Be(FederationOperatorStatus.Ok);
        page.Scanned.Should().Be(6);
        page.Ignored.Should().Be(3);
        page.Owned.Select(facts => facts.Owner.Alias).Should().BeEquivalentTo([healthy.Alias, broken.Alias, drifted.Alias]);
        var byAlias = page.Owned.ToDictionary(facts => facts.Owner.Alias);
        byAlias[healthy.Alias].Should().Match<FederationBrokerFacts>(facts => facts.Readable && facts.Conforms && facts.Enabled
            && facts.Protocol == FederationBrokerProtocol.Oidc);
        byAlias[broken.Alias].Should().Match<FederationBrokerFacts>(facts => !facts.Readable && !facts.Conforms && !facts.Enabled
            && facts.Protocol == null);
        byAlias[drifted.Alias].Should().Match<FederationBrokerFacts>(facts => facts.Readable && !facts.Conforms);
        provider.Requests.Where(request => request.Path.Contains("identity-provider", StringComparison.Ordinal))
            .Select(request => request.Method + " " + request.Path + request.Query).Should().Equal(
                "GET /admin/realms/learnstack/identity-provider/instances?first=0&max=20&briefRepresentation=false");
        page.ToString().Should().NotContain(TenantSecret).And.NotContain(AdminSecret).And.NotContain("Acme");
    }

    [Fact]
    public async Task A_listing_pages_by_offset_and_a_provider_that_ignores_the_page_size_is_unavailable()
    {
        var provider = new FakeKeycloak();
        var owners = Enumerable.Range(0, 5).Select(_ => OwnerOf()).OrderBy(owner => owner.Alias, StringComparer.Ordinal).ToArray();
        foreach (var owner in owners) provider.Brokers[owner.Alias] = Unreadable(owner);
        using var adapter = CreateOperator(provider);

        var first = await adapter.ListAsync(0, 2);
        var second = await adapter.ListAsync(2, 2);
        var last = await adapter.ListAsync(4, 2);

        (first.Scanned, second.Scanned, last.Scanned).Should().Be((2, 2, 1));
        first.Owned.Concat(second.Owned).Concat(last.Owned).Select(facts => facts.Owner.Alias).Should().Equal(owners.Select(owner => owner.Alias));

        provider.ListIgnoresPaging = true;
        (await adapter.ListAsync(0, 2)).Status.Should().Be(FederationOperatorStatus.Unavailable);
    }

    [Theory]
    [InlineData("object")]
    [InlineData("5xx")]
    public async Task A_listing_that_is_not_an_array_or_that_the_provider_fails_is_unavailable_and_returns_nothing(string defect)
    {
        var provider = new FakeKeycloak { ListDefect = defect };
        using var adapter = CreateOperator(provider);

        (await adapter.ListAsync(0, 5)).Should().Be(FederationBrokerPage.Unavailable);
    }

    [Fact]
    public async Task An_inspection_reports_absent_foreign_and_owned_with_the_mapper_count_of_a_readable_broker()
    {
        var provider = new FakeKeycloak { Mappers = 2 };
        var mine = OwnerOf();
        var broken = OwnerOf();
        provider.Brokers[mine.Alias] = Stored(mine);
        provider.Brokers[broken.Alias] = Unreadable(broken);
        provider.Brokers[Foreign()] = new JsonObject { ["alias"] = Foreign(), ["config"] = new JsonObject() };
        using var adapter = CreateOperator(provider);

        (await adapter.InspectAsync(OwnerOf().Alias)).Should().Be(FederationBrokerInspection.Absent);
        (await adapter.InspectAsync(Foreign())).Should().Be(FederationBrokerInspection.NotOwned);
        var owned = await adapter.InspectAsync(mine.Alias);
        (owned.Status, owned.MapperCount, owned.Facts!.Owner.TenantId).Should().Be((FederationInspectionStatus.Owned, 2, Tenant));
        var unreadable = await adapter.InspectAsync(broken.Alias);
        (unreadable.Status, unreadable.MapperCount, unreadable.Facts!.Readable).Should().Be((FederationInspectionStatus.Owned, 0, false));
        var notDerived = async () => await adapter.InspectAsync("not-a-derived-alias");
        await notDerived.Should().ThrowAsync<ArgumentException>("no route is built from an alias that is not the derived form");
    }

    [Fact]
    public async Task A_remove_deletes_an_owned_broker_readable_or_not_and_confirms_its_absence()
    {
        var provider = new FakeKeycloak();
        var readable = OwnerOf();
        var broken = OwnerOf();
        provider.Brokers[readable.Alias] = Stored(readable);
        provider.Brokers[broken.Alias] = Unreadable(broken);
        using var adapter = CreateOperator(provider);

        (await adapter.RemoveAsync(readable)).Should().Be(FederationOperatorOutcome.Applied);
        (await adapter.RemoveAsync(broken)).Should().Be(FederationOperatorOutcome.Applied);

        provider.Brokers.Should().BeEmpty();
        provider.Requests.Count(request => request.Method == "DELETE").Should().Be(2);
        // The provider is read before the delete and again after it, so the outcome is the absence and not a status.
        provider.Requests.Where(request => request.Path.EndsWith(readable.Alias, StringComparison.Ordinal))
            .Select(request => request.Method).Should().Equal("GET", "DELETE", "GET");
    }

    [Fact]
    public async Task A_remove_of_an_absent_broker_is_already_applied_and_sends_nothing()
    {
        var provider = new FakeKeycloak();
        using var adapter = CreateOperator(provider);

        (await adapter.RemoveAsync(OwnerOf())).Should().Be(FederationOperatorOutcome.AlreadyApplied);

        provider.Requests.Where(request => !request.Path.EndsWith("/token", StringComparison.Ordinal)).Select(request => request.Method)
            .Should().Equal("GET");
    }

    [Fact]
    public async Task A_remove_never_touches_an_instance_whose_marker_is_not_the_owner_the_caller_proved()
    {
        var provider = new FakeKeycloak();
        var claimed = OwnerOf();
        var other = Stored(claimed);
        other["config"]![KeycloakFederationProfile.OwnerKey] = $"{Guid.CreateVersion7():D}:{claimed.ConnectionId:D}";
        provider.Brokers[claimed.Alias] = other;
        var unmarked = OwnerOf();
        provider.Brokers[unmarked.Alias] = new JsonObject { ["alias"] = unmarked.Alias, ["config"] = new JsonObject() };
        using var adapter = CreateOperator(provider);

        // Another tenant's marker on the claimed connection, and an unmarked derived alias.
        (await adapter.RemoveAsync(claimed)).Should().Be(FederationOperatorOutcome.NotOwned);
        (await adapter.RemoveAsync(unmarked)).Should().Be(FederationOperatorOutcome.NotOwned);

        provider.Brokers.Should().HaveCount(2);
        provider.Requests.Should().NotContain(request => request.Method == "DELETE");
    }

    [Theory]
    [InlineData("refused", FederationOperatorOutcome.Refused)]
    [InlineData("fails", FederationOperatorOutcome.Uncertain)]
    public async Task A_delete_the_provider_refuses_changes_nothing_and_one_that_fails_after_it_was_sent_is_uncertain(
        string kind, FederationOperatorOutcome expected)
    {
        var owner = OwnerOf();
        var provider = new FakeKeycloak { DeleteStatus = kind == "refused" ? HttpStatusCode.Conflict : HttpStatusCode.InternalServerError };
        provider.Brokers[owner.Alias] = Unreadable(owner);
        using var adapter = CreateOperator(provider);

        (await adapter.RemoveAsync(owner)).Should().Be(expected);

        provider.Brokers.Should().ContainKey(owner.Alias);
    }

    [Fact]
    public async Task A_delete_the_provider_accepts_and_does_not_perform_is_uncertain_because_absence_is_the_confirmation()
    {
        var owner = OwnerOf();
        var provider = new FakeKeycloak { IgnoreDelete = true };
        provider.Brokers[owner.Alias] = Unreadable(owner);
        using var adapter = CreateOperator(provider);

        (await adapter.RemoveAsync(owner)).Should().Be(FederationOperatorOutcome.Uncertain);
    }

    [Theory]
    [InlineData("019a1929-5450-7bbb-8df1-516e8e002501:019a1929-5450-7bbb-8df1-516e8e002502", true)]
    [InlineData("019A1929-5450-7BBB-8DF1-516E8E002501:019a1929-5450-7bbb-8df1-516e8e002502", false)]
    [InlineData("019a1929-5450-7bbb-8df1-516e8e002501:019A1929-5450-7BBB-8DF1-516E8E002502", false)]
    [InlineData("019a1929-5450-7bbb-8df1-516e8e002501:019a1929-5450-7bbb-8df1-516e8e002502 ", false)]
    [InlineData(" 019a1929-5450-7bbb-8df1-516e8e002501:019a1929-5450-7bbb-8df1-516e8e002502", false)]
    [InlineData("019a1929-5450-7bbb-8df1-516e8e002501;019a1929-5450-7bbb-8df1-516e8e002502", false)]
    [InlineData("019a1929545 07bbb8df1516e8e002501:019a1929-5450-7bbb-8df1-516e8e002502", false)]
    [InlineData("00000000-0000-0000-0000-000000000000:019a1929-5450-7bbb-8df1-516e8e002502", false)] // leakwatch:ignore
    [InlineData("019a1929-5450-7bbb-8df1-516e8e002501:019a1929-5450-4bbb-8df1-516e8e002502", false)]
    [InlineData("019a1929-5450-7bbb-8df1-516e8e002501:019a1929-5450-7bbb-8df1-516e8e002502:x", false)]
    [InlineData("", false)]
    public void The_owner_marker_is_parsed_strictly_and_names_only_the_connections_own_derived_alias(string marker, bool valid)
    {
        var alias = "lsf-019a192954507bbb8df1516e8e002502";

        KeycloakFederationProfile.TryOwnerFromMarker(marker, alias, out var owner).Should().Be(valid, marker);

        if (valid)
        {
            (owner.TenantId.Value, owner.ConnectionId).Should().Be(
                (Guid.Parse("019a1929-5450-7bbb-8df1-516e8e002501"), Guid.Parse("019a1929-5450-7bbb-8df1-516e8e002502")));
        }

        // Another connection's alias never matches this marker, however well formed it is.
        KeycloakFederationProfile.TryOwnerFromMarker(
            "019a1929-5450-7bbb-8df1-516e8e002501:019a1929-5450-7bbb-8df1-516e8e002502", "lsf-019a192954507bbb8df1516e8e002503", out _)
            .Should().BeFalse();
        KeycloakFederationProfile.TryOwnerFromMarker(null, alias, out _).Should().BeFalse();
        KeycloakFederationProfile.TryOwnerFromMarker("019a1929-5450-7bbb-8df1-516e8e002501:019a1929-5450-7bbb-8df1-516e8e002502", null, out _)
            .Should().BeFalse();
    }

    [Fact]
    public async Task A_provider_that_cannot_be_reached_before_any_change_is_sent_is_unavailable_and_not_uncertain()
    {
        var owner = OwnerOf();
        var provider = new FakeKeycloak { TokenDefect = "extra-role" };
        provider.Brokers[owner.Alias] = Unreadable(owner);
        using var adapter = CreateOperator(provider);

        (await adapter.RemoveAsync(owner)).Should().Be(FederationOperatorOutcome.Unavailable);
        (await adapter.DisableAsync(owner)).Should().Be(FederationOperatorOutcome.Unavailable);
        provider.Requests.Select(request => request.Method).Should().NotContain(["DELETE", "PUT"]);
    }

    [Fact]
    public async Task A_disable_sends_back_what_the_provider_returned_with_only_enabled_changed_and_keeps_the_stored_secret()
    {
        var provider = new FakeKeycloak();
        var broken = OwnerOf();
        provider.Brokers[broken.Alias] = Unreadable(broken, enabled: true);
        var before = provider.Brokers[broken.Alias].DeepClone().AsObject();
        using var adapter = CreateOperator(provider);

        (await adapter.DisableAsync(broken)).Should().Be(FederationOperatorOutcome.Applied);

        var after = provider.Brokers[broken.Alias];
        after["enabled"]!.GetValue<bool>().Should().BeFalse();
        // Nothing else moved, the computed members were never sent, and the secret the provider holds is the one it held.
        before["enabled"] = false;
        JsonNode.DeepEquals(after, before).Should().BeTrue("only enabled differs from what the provider held");
        after.ContainsKey("internalId").Should().BeFalse();
        after.ContainsKey("types").Should().BeFalse();
        after["config"]!["clientSecret"]!.GetValue<string>().Should().Be(TenantSecret);
        var put = provider.Requests.Single(request => request.Method == "PUT");
        put.Body.Should().NotContain(TenantSecret, "the secret travels as the provider's own mask, never as a value this adapter read");
        put.Body.Should().Contain("**********");
    }

    [Fact]
    public async Task A_disable_of_a_disabled_broker_sends_nothing_and_one_that_is_not_the_proved_owners_is_never_written()
    {
        var provider = new FakeKeycloak();
        var off = OwnerOf();
        provider.Brokers[off.Alias] = Unreadable(off, enabled: false);
        var claimed = OwnerOf();
        var other = Stored(claimed);
        other["config"]![KeycloakFederationProfile.OwnerKey] = $"{Guid.CreateVersion7():D}:{claimed.ConnectionId:D}";
        provider.Brokers[claimed.Alias] = other;
        using var adapter = CreateOperator(provider);

        (await adapter.DisableAsync(off)).Should().Be(FederationOperatorOutcome.AlreadyApplied);
        (await adapter.DisableAsync(claimed)).Should().Be(FederationOperatorOutcome.NotOwned);
        (await adapter.DisableAsync(OwnerOf())).Should().Be(FederationOperatorOutcome.Refused, "there is nothing to disable");

        provider.Requests.Should().NotContain(request => request.Method == "PUT");
    }

    [Theory]
    [InlineData("fails", FederationOperatorOutcome.Uncertain)]
    [InlineData("refused", FederationOperatorOutcome.Refused)]
    [InlineData("ignored", FederationOperatorOutcome.Uncertain)]
    public async Task A_disable_is_confirmed_by_reading_the_instance_again(string kind, FederationOperatorOutcome expected)
    {
        var owner = OwnerOf();
        var provider = new FakeKeycloak
        {
            WriteStatus = kind == "fails" ? HttpStatusCode.InternalServerError : kind == "refused" ? HttpStatusCode.Conflict : null,
            IgnorePut = kind == "ignored",
        };
        provider.Brokers[owner.Alias] = Unreadable(owner, enabled: true);
        using var adapter = CreateOperator(provider);

        (await adapter.DisableAsync(owner)).Should().Be(expected);

        provider.Brokers[owner.Alias]["enabled"]!.GetValue<bool>().Should().BeTrue("the provider did not take the change");
    }

    [Fact]
    public async Task The_operator_port_never_returns_a_secret_or_provider_text()
    {
        var owner = OwnerOf();
        var provider = new FakeKeycloak { Mappers = 1 };
        provider.Brokers[owner.Alias] = Stored(owner);
        using var adapter = CreateOperator(provider);

        var results = new object?[]
        {
            await adapter.ListAsync(0, 5), await adapter.InspectAsync(owner.Alias), await adapter.CountMappersAsync(owner),
            await adapter.DisableAsync(owner), await adapter.RemoveAsync(owner),
        };

        string.Join('|', results.Select(result => result?.ToString())).Should()
            .NotContain(TenantSecret).And.NotContain(AdminSecret).And.NotContain("Acme").And.NotContain("idp.example.com");
    }

    [Fact]
    public void The_operator_ports_result_types_can_carry_no_secret_or_provider_text_because_they_declare_no_member_that_could()
    {
        // ToString is a constant on these records, so a text check cannot see a new member. The members can be seen: each is an
        // owner, a flag, a count, a closed status or a protocol, and there is no string for a display name, issuer or secret.
        var closed = new HashSet<Type>
        {
            typeof(FederationBrokerOwner), typeof(bool), typeof(int), typeof(FederationBrokerProtocol?), typeof(FederationOperatorStatus),
            typeof(FederationInspectionStatus), typeof(FederationBrokerFacts), typeof(IReadOnlyList<FederationBrokerFacts>),
        };
        const BindingFlags declared = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;

        foreach (var type in new[] { typeof(FederationBrokerFacts), typeof(FederationBrokerPage), typeof(FederationBrokerInspection) })
            type.GetProperties(declared).Select(property => property.PropertyType).Should().BeSubsetOf(closed, type.Name);
    }

    private static FederationClientSecret Fresh() =>
        FederationClientSecret.TryCreate(TenantSecret, out var secret) ? secret! : throw new InvalidOperationException();

    private static FederationBrokerState State(bool enabled = false) => new(Owner, "Acme SSO", enabled, new FederationOidcConfiguration(
        "https://idp.example.com/realms/acme", "acme-client", FederationClientAuthentication.ClientSecretPost,
        new Uri("https://idp.example.com/realms/acme/protocol/openid-connect/auth"), new Uri("https://idp.example.com/realms/acme/protocol/openid-connect/token"),
        new Uri("https://idp.example.com/realms/acme/protocol/openid-connect/certs"), new Uri("https://idp.example.com/realms/acme/protocol/openid-connect/userinfo")));

    private static string Pem()
    {
        using var key = System.Security.Cryptography.ECDsa.Create();
        using var certificate = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=saml", key,
            System.Security.Cryptography.HashAlgorithmName.SHA256).CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return certificate.ExportCertificatePem();
    }

    private sealed class Secrets(string? value) : ISecretProvider
    {
        public string? GetSecret(string key) => key == "Federation:ClientSecret" ? value : null;
    }

    private sealed class Resilience : IProviderResilience<IFederationProvider>
    {
        public ResiliencePipeline Pipeline => ResiliencePipeline.Empty;
        public string PortName => "federation";
    }

    private sealed class OperatorResilience : IProviderResilience<IFederationOperator>
    {
        public ResiliencePipeline Pipeline => ResiliencePipeline.Empty;
        public string PortName => "federation";
    }

    internal sealed record Recorded(string Method, string Path, string Query, string Body);

    /// <summary>A scripted provider holding brokers by alias, as the pinned Admin REST API stores and masks them.</summary>
    private sealed class FakeKeycloak : HttpMessageHandler
    {
        internal ConcurrentDictionary<string, JsonObject> Brokers { get; } = new(StringComparer.Ordinal);
        internal List<Recorded> Requests { get; } = [];
        internal int Mappers { get; set; }
        internal string? TokenDefect { get; init; }
        internal HttpStatusCode? WriteStatus { get; init; }
        internal HttpStatusCode? DeleteStatus { get; init; }
        internal string? ListDefect { get; init; }
        internal bool ListIgnoresPaging { get; set; }
        internal bool IgnorePut { get; init; }
        internal bool IgnoreDelete { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add(new(request.Method.Method, path, request.RequestUri.Query, body));
            if (path.EndsWith("/protocol/openid-connect/token", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, new JsonObject { ["access_token"] = Token(TokenDefect), ["token_type"] = "Bearer", ["expires_in"] = 60 });
            if (request.Headers.Authorization?.Scheme != "Bearer") return new(HttpStatusCode.Unauthorized);
            const string prefix = "/admin/realms/learnstack/identity-provider/instances";
            if (request.Method == HttpMethod.Post && path == prefix)
            {
                if (WriteStatus is { } status) return new(status);
                var created = JsonNode.Parse(body)!.AsObject();
                var alias = created["alias"]!.GetValue<string>();
                if (Brokers.ContainsKey(alias)) return new(HttpStatusCode.Conflict);
                Brokers[alias] = created;
                return new(HttpStatusCode.Created);
            }
            if (request.Method == HttpMethod.Get && path == prefix)
            {
                if (ListDefect == "5xx") return new(HttpStatusCode.InternalServerError);
                if (ListDefect == "object") return Json(HttpStatusCode.OK, new JsonObject());
                var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
                var page = Brokers.Values.OrderBy(broker => broker["alias"]!.GetValue<string>(), StringComparer.Ordinal)
                    .Skip(int.Parse(query["first"]!, System.Globalization.CultureInfo.InvariantCulture));
                if (!ListIgnoresPaging) page = page.Take(int.Parse(query["max"]!, System.Globalization.CultureInfo.InvariantCulture));
                return Json(HttpStatusCode.OK, new JsonArray([.. page.Select(View)]));
            }
            var rest = path[(prefix.Length + 1)..];
            var mappers = rest.EndsWith("/mappers", StringComparison.Ordinal);
            var key = mappers ? rest[..^"/mappers".Length] : rest;
            if (mappers) return Json(HttpStatusCode.OK, new JsonArray([.. Enumerable.Range(0, Mappers).Select(_ => (JsonNode)new JsonObject { ["name"] = "m" })]));
            if (request.Method == HttpMethod.Get)
            {
                if (!Brokers.TryGetValue(key, out var broker)) return new(HttpStatusCode.NotFound);
                return Json(HttpStatusCode.OK, View(broker));
            }
            if (request.Method == HttpMethod.Put)
            {
                if (WriteStatus is { } status) return new(status);
                if (IgnorePut) return new(HttpStatusCode.NoContent);
                if (!Brokers.TryGetValue(key, out var existing)) return new(HttpStatusCode.NotFound);
                var replacement = JsonNode.Parse(body)!.AsObject();
                var config = replacement["config"]!.AsObject();
                if (config["clientSecret"]?.GetValue<string>() == "**********")
                    config["clientSecret"] = existing["config"]!["clientSecret"]!.GetValue<string>();
                Brokers[key] = replacement;
                return new(HttpStatusCode.NoContent);
            }
            if (request.Method == HttpMethod.Delete)
            {
                if (DeleteStatus is { } deleteStatus) return new(deleteStatus);
                if (IgnoreDelete) return new(HttpStatusCode.NoContent);
                return new(Brokers.TryRemove(key, out _) ? HttpStatusCode.NoContent : HttpStatusCode.NotFound);
            }
            return new(HttpStatusCode.MethodNotAllowed);
        }

        /// <summary>What the provider returns for a stored broker: the secret masked and the computed members added.</summary>
        private static JsonNode View(JsonObject broker)
        {
            var view = broker.DeepClone().AsObject();
            if (view["config"]!.AsObject().ContainsKey("clientSecret")) view["config"]!["clientSecret"] = "**********";
            view["internalId"] = Guid.NewGuid().ToString();
            view["types"] = new JsonArray("USER_AUTHENTICATION");
            return view;
        }

        private static HttpResponseMessage Json(HttpStatusCode status, JsonNode value) =>
            new(status) { Content = new StringContent(value.ToJsonString(), Encoding.UTF8, "application/json") };

        private static string Token(string? defect)
        {
            var roles = defect == "extra-role" ? new JsonArray("manage-identity-providers", "manage-users") : new JsonArray("manage-identity-providers");
            var payload = new JsonObject
            {
                ["iss"] = defect == "issuer" ? "https://other.invalid/realms/learnstack" : Issuer,
                ["azp"] = defect == "client" ? "learnstack-web" : "learnstack-federation-admin",
                ["resource_access"] = new JsonObject { ["realm-management"] = new JsonObject { ["roles"] = roles } },
            };
            if (defect == "realm-role") payload["realm_access"] = new JsonObject { ["roles"] = new JsonArray("default-roles-learnstack") };
            static string Part(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            return Part("{\"alg\":\"none\"}") + "." + Part(payload.ToJsonString()) + ".c2ln";
        }
    }
}
