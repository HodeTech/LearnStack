using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FluentAssertions;
using LearnStack.Tools.Seeder;
using Xunit;

namespace LearnStack.Tests.Integration.TenancyIdentity;

/// <summary>Offline drift controls; live provider behavior belongs to the Docker issuance proofs.</summary>
public sealed partial class TenantRealmExportTests
{
    [Fact]
    public void Identity_manifest_rejects_reassignment_legacy_expansion_and_nonstable_actor_ids()
    {
        var project = typeof(SeedIdentityManifest).GetMethod("Project",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var users = SeedData.IdentityUsers;
        project.Invoke(null, [users]).Should().NotBeNull();
        var nonlegacy = users[2];
        nonlegacy.LegacyTenantAttribute.Should().BeNull("actor-format controls must not trip legacy identity binding");
        var variants = new[]
        {
            users.SetItem(2, users[2] with { LegacyTenantAttribute = "tenant-a" }),
            users.SetItem(0, users[0] with { LegacyTenantAttribute = "another-legacy-tenant" }),
            users.SetItem(0, users[0] with { Username = "unowned@tenant-a.test" }),
            // Preserve both legacy identities so only username uniqueness rejects this variant.
            users.SetItem(3, users[3] with { Username = users[2].Username }),
            users.SetItem(0, users[0] with { Tenant = SeedData.Yoga }),
            // A valid replacement v7 actor violates only this legacy identity's binding.
            users.SetItem(0, users[0] with { UserId = LearnStack.SharedKernel.Identifiers.UserId.From(
                Guid.Parse("01930000-0000-7000-8000-00000000a003")) }),
            // Nonlegacy actors isolate version, reserved-actor, uniqueness and scope collisions.
            users.SetItem(2, nonlegacy with { UserId = LearnStack.SharedKernel.Identifiers.UserId.From(
                Guid.Parse("01930000-0000-4000-8000-00000000b003")) }),
            users.SetItem(2, nonlegacy with { UserId = LearnStack.SharedKernel.Identifiers.UserId.SystemActor }),
            users.SetItem(2, nonlegacy with { UserId = users[3].UserId }),
            users.SetItem(2, nonlegacy with { UserId = LearnStack.SharedKernel.Identifiers.UserId.From(SeedData.English.TenantId.Value) }),
            users.SetItem(2, nonlegacy with { UserId = LearnStack.SharedKernel.Identifiers.UserId.From(
                SeedData.Yoga.DefaultOrganization.OrganizationId.Value) }),
        };
        foreach (var variant in variants)
        {
            var act = () => project.Invoke(null, [variant]);
            act.Should().Throw<System.Reflection.TargetInvocationException>()
                .Which.InnerException.Should().BeOfType<InvalidOperationException>();
        }
        project.Invoke(null, [users]).Should().NotBeNull("rejected declarations cannot alter the committed declaration");
    }

    private static readonly string[] ReservedClaims =
        ["iss", "sub", "aud", "azp", "typ", "scope", "iat", "exp", "nbf"];
    private static readonly string[] RequiredDevelopmentEvents =
        ["LOGIN", "LOGIN_ERROR", "LOGOUT", "LOGOUT_ERROR", "CODE_TO_TOKEN", "CODE_TO_TOKEN_ERROR", "REFRESH_TOKEN", "REFRESH_TOKEN_ERROR"];
    private static readonly string[] StockAccountRoles =
        ["view-profile", "manage-account", "manage-account-links", "view-applications", "view-consent", "manage-consent", "delete-account", "view-groups"];
    private static readonly string[] StockDefaultAccountRoles = ["view-profile", "manage-account"];
    private static readonly string[] StockConsoleScopeRoles = ["manage-account", "view-groups"];
    private static readonly string[] UnsafeOptionalDestinations =
    [
        "sub", "aud", "azp", "iss", "iat", "exp", "nbf", "typ", "scope",
        "user_id.nested", "tenant_id.nested", "organization_id.nested", @"user\_id", @"tenant\_id.nested",
    ];
    private static readonly string[] KnownMapperTypes =
    [
        "oidc-acr-mapper", "oidc-address-mapper", "oidc-allowed-origins-mapper",
        "oidc-audience-mapper", "oidc-audience-resolve-mapper", "oidc-full-name-mapper",
        "oidc-organization-membership-mapper", "oidc-sub-mapper", "oidc-usermodel-attribute-mapper",
        "oidc-usermodel-client-role-mapper", "oidc-usermodel-property-mapper",
        "oidc-usermodel-realm-role-mapper", "oidc-usersessionmodel-note-mapper",
    ];
    [Fact]
    public void The_committed_realm_matches_typed_identity_and_security_declarations()
    {
        Violations(ReadRealm()).Should().BeEmpty();
        var manifest = SeedIdentityManifest.Create();
        manifest.Users.Should().HaveCount(4);
        manifest.Users.Select(user => user.UserId).Distinct().Should().HaveCount(4);
        foreach (var user in manifest.Users)
        {
            var tenant = SeedData.All.Single(value => value.TenantId.Value == user.TenantId);
            user.Host.Should().Be(tenant.Host);
            user.OrganizationId.Should().Be(tenant.MapHostToDefaultOrganization
                ? tenant.DefaultOrganization.OrganizationId.Value : null);
        }
    }

    [Fact]
    public void Export_controls_reject_unsafe_mapper_profile_client_and_identity_variants()
    {
        var original = ReadRealm();
        var mutations = new List<(string Name, Action<JsonObject> Apply)>
        {
            ("disabled event storage", realm => realm["eventsEnabled"] = false),
            ("short event retention", realm => realm["eventsExpiration"] = 60),
            ("indefinite event retention", realm => realm["eventsExpiration"] = 0),
            ("missing refresh error storage", realm => realm["enabledEventTypes"] = new JsonArray("LOGIN", "LOGOUT")),
            ("wrong realm TTL", realm => realm["accessTokenLifespan"] = 1800),
            ("unmanaged profile enabled", realm => SetProfilePolicy(realm, "ENABLED")),
            ("malformed profile configuration", realm => ProfileValue(realm)[0] = "{"),
            ("default application role composite", realm => realm["roles"]!["realm"]!.AsArray()
                .Single(role => Text(role?["name"]) == "default-roles-learnstack")!["composites"]!["realm"] = new JsonArray("tenant-admin")),
            ("missing account client", realm => realm["clients"]!.AsArray().Remove(Client(realm, "account"))),
            ("console full scope", realm => Client(realm, "account-console")["fullScopeAllowed"] = true),
            ("console default broad roles", realm => Client(realm, "account-console")["defaultClientScopes"]!.AsArray().Add("roles")),
            ("console optional broad roles", realm => Client(realm, "account-console")["optionalClientScopes"]!.AsArray().Add("roles")),
            ("console mapper missing", realm => Client(realm, "account-console")["protocolMappers"]!.AsArray().RemoveAt(1)),
            ("console dynamic role mapper", realm => Client(realm, "account-console")["protocolMappers"]![1]!["config"]!
                .AsObject().Remove("usermodel.clientRoleMapping.clientId")),
            ("console ID token roles", realm => Client(realm, "account-console")["protocolMappers"]![1]!["config"]!["id.token.claim"] = "true"),
            ("missing account grant", realm => realm["users"]![0]!.AsObject().Remove("clientRoles")),
            ("wider account grant", realm => realm["users"]![0]!["clientRoles"]!["account"]!.AsArray().Add("view-profile")),
            ("default realm grant", realm => realm["users"]![0]!["realmRoles"]!.AsArray().Add("default-roles-learnstack")),
            ("wider account composite", realm => realm["roles"]!["client"]!["account"]!.AsArray()
                .Single(role => Text(role?["name"]) == "manage-account")!["composites"]!["client"]!["account"]!.AsArray().Add("view-profile")),
            ("missing stock default role", realm => realm.Remove("defaultRole")),
            ("client TTL override", realm => Client(realm, "learnstack-web")["attributes"]!["access.token.lifespan"] = "900"),
            ("public client", realm => Client(realm, "learnstack-web")["publicClient"] = true),
            ("changed API service secret", realm => Client(realm, "learnstack-api")["secret"] = "different-service-secret"),
            ("disabled web client", realm => Client(realm, "learnstack-web")["enabled"] = false),
            ("web client consent", realm => Client(realm, "learnstack-web")["consentRequired"] = true),
            ("disabled persona", realm => realm["users"]![0]!["enabled"] = false),
            ("unverified persona", realm => realm["users"]![0]!["emailVerified"] = false),
            ("temporary persona credential", realm => realm["users"]![0]!["credentials"]![0]!["temporary"] = true),
            ("literal secret", realm => Client(realm, "learnstack-web")["secret"] = "unowned-source"),
            ("password grant", realm => Client(realm, "learnstack-web")["directAccessGrantsEnabled"] = true),
            ("API browser flow", realm => Client(realm, "learnstack-api")["standardFlowEnabled"] = true),
            ("wildcard callback", realm => Client(realm, "learnstack-web")["redirectUris"]![0] = "https://*.invalid/*"),
            ("missing attached scope", realm =>
            {
                var scopes = Client(realm, "learnstack-web")["defaultClientScopes"]!.AsArray();
                scopes.Remove(scopes.Single(scope => Text(scope) == "learnstack-tenant-api"));
            }),
            ("missing basic scope", realm => Scopes(realm).Remove(Scope(realm, "basic"))),
            ("missing sub mapper", realm => Scope(realm, "basic")["protocolMappers"] = new JsonArray()),
            ("missing actor mapper", realm => Mappers(realm).RemoveAt(0)),
            ("array identity", realm => Mapper(realm, "user_id")["config"]!["multivalued"] = "true"),
            ("nonstring identity", realm => Mapper(realm, "tenant_id")["config"]!["jsonType.label"] = "long"),
            ("ID token API audience", realm => Mapper(realm, "learnstack-api-audience")["config"]!["id.token.claim"] = "true"),
            ("missing access audience", realm => Mapper(realm, "learnstack-api-audience")["config"]!["access.token.claim"] = "false"),
            ("duplicate identity mapper", realm => Mappers(realm).Add(Mapper(realm, "user_id").DeepClone())),
            ("direct client identity collision", realm => Client(realm, "learnstack-web")["protocolMappers"] =
                new JsonArray(Mapper(realm, "user_id").DeepClone())),
            ("optional scope identity collision", realm => Scope(realm, "phone")["protocolMappers"]!.AsArray()
                .Add(Mapper(realm, "tenant_id").DeepClone())),
            ("wrong actor UUID", realm => realm["users"]![0]!["attributes"]!["user_id"]![0] = Guid.Empty.ToString("D")),
            ("array tenant identity", realm => realm["users"]![0]!["attributes"]!["tenant_id"]!.AsArray().Add("other")),
            ("missing user", realm => realm["users"]!.AsArray().RemoveAt(3)),
            ("writable actor", realm => ChangeProfile(realm, "user_id", "edit")),
            ("visible tenant", realm => ChangeProfile(realm, "tenant_id", "view")),
            ("writable organization", realm => ChangeProfile(realm, "organization_id", "edit")),
            ("optional implicit subject", realm => Scope(realm, "phone")["protocolMappers"]!.AsArray().Add(
                Scope(realm, "basic")["protocolMappers"]!.AsArray().Single(mapper =>
                    Text(mapper?["protocolMapper"]) == "oidc-sub-mapper")!.DeepClone())),
            ("optional implicit API audience", realm => Scope(realm, "phone")["protocolMappers"]!.AsArray()
                .Add(Mapper(realm, "learnstack-api-audience").DeepClone())),
            ("optional audience resolver", realm => Scope(realm, "phone")["protocolMappers"]!.AsArray().Add(
                Scope(realm, "roles")["protocolMappers"]!.AsArray().Single(mapper =>
                    Text(mapper?["protocolMapper"]) == "oidc-audience-resolve-mapper")!.DeepClone())),
            ("role audience in ID token", realm => Scope(realm, "roles")["protocolMappers"]!.AsArray().Single(mapper =>
                Text(mapper?["protocolMapper"]) == "oidc-audience-resolve-mapper")!["config"]!["id.token.claim"] = "true"),
            ("unknown implicit mapper", realm => Scope(realm, "phone")["protocolMappers"]!.AsArray().Add(
                new JsonObject { ["name"] = "unqualified", ["protocolMapper"] = "unqualified-provider-mapper" })),
        };
        foreach (var destination in UnsafeOptionalDestinations)
            mutations.Add(("optional reserved destination " + destination, realm => AddOptionalClaim(realm, destination)));
        foreach (var clientId in new[] { "user_id", "sub", "aud" })
            mutations.Add(("interpolated client-role destination " + clientId, realm =>
            {
                realm["clients"]!.AsArray().Add(new JsonObject { ["clientId"] = clientId });
                AddOptionalClientRoleClaim(realm, "${client_id}", clientId);
            }
            ));
        Violations(original).Should().BeEmpty("the control input is the real export");
        foreach (var (name, apply) in mutations)
        {
            var mutant = original.DeepClone().AsObject();
            apply(mutant);
            Violations(mutant).Should().NotBeEmpty(name);
        }
        Violations(original).Should().BeEmpty("mutants cannot contaminate the control");
        var escapedLiteral = original.DeepClone().AsObject();
        AddOptionalClaim(escapedLiteral, @"user_id\.nested");
        Violations(escapedLiteral).Should().BeEmpty("an escaped dot produces a separate literal key, not an identity object");
        var nestedClientRoles = original.DeepClone().AsObject();
        AddOptionalClientRoleClaim(nestedClientRoles, "resource_access.${client_id}.roles", "learnstack-api");
        Violations(nestedClientRoles).Should().BeEmpty("the pinned built-in client-role path has a fixed safe root");
    }

    internal static IReadOnlyList<string> Violations(JsonObject realm)
    {
        var failures = new List<string>();
        void Require(bool value, string reason) { if (!value) failures.Add(reason); }
        try
        {
            Require(Text(realm["realm"]) == "learnstack", "tenant realm");
            Require(realm["accessTokenLifespan"]?.GetValue<int>() == 900, "realm lifespan");
            Require(realm["eventsEnabled"]?.GetValue<bool>() == true, "development user-event storage");
            Require(realm["eventsExpiration"]?.GetValue<int>() == 604800, "seven-day development retention");
            var eventTypes = Strings(realm["enabledEventTypes"]);
            Require(RequiredDevelopmentEvents.All(value => eventTypes.Contains(value, StringComparer.Ordinal)),
                "explicit required authentication events including default-unsaved refresh");
            Require(eventTypes.Distinct(StringComparer.Ordinal).Count() == eventTypes.Length, "unique saved event types");
            ValidateAccountDeclaration(realm, Require);
            var scopeNames = Scopes(realm).Select(scope => Text(scope?["name"])).ToArray();
            Require(scopeNames.Distinct(StringComparer.Ordinal).Count() == scopeNames.Length, "unique scope names");
            var identityNames = new[] { "user_id", "tenant_id", "organization_id" };
            foreach (var clientId in new[] { "learnstack-web", "learnstack-api" })
            {
                var client = Client(realm, clientId);
                Require(client["publicClient"]?.GetValue<bool>() == false, "confidential client");
                Require(client["implicitFlowEnabled"]?.GetValue<bool>() == false, "implicit flow disabled");
                Require(client["directAccessGrantsEnabled"]?.GetValue<bool>() == false, "password grant disabled");
                Require(client["attributes"]?["access.token.lifespan"] is null, "no client TTL override");
                Require(Strings(client["webOrigins"]).Length == 0, "server-only client");
                var defaults = Strings(client["defaultClientScopes"]);
                Require(defaults.Contains("basic", StringComparer.Ordinal)
                    && defaults.Contains("learnstack-tenant-api", StringComparer.Ordinal), "required default scopes");
                var effectiveScopes = defaults.Concat(Strings(client["optionalClientScopes"])).ToArray();
                Require(effectiveScopes.All(scopeNames.Contains), "all attached scopes actually declared");
                var effectiveMappers = effectiveScopes.SelectMany(name =>
                    Scope(realm, name)["protocolMappers"]?.AsArray() ?? []).Concat(
                    client["protocolMappers"]?.AsArray() ?? []).ToArray();
                foreach (var name in identityNames)
                    Require(effectiveMappers.Count(mapper => ClaimRoot(Text(mapper?["config"]?["claim.name"])) == name) == 1,
                        "one effective identity mapper");
                Require(effectiveMappers.All(mapper => KnownMapperTypes.Contains(Text(mapper?["protocolMapper"]), StringComparer.Ordinal)),
                    "every effective mapper type is qualified against the pinned provider");
                Require(effectiveMappers.All(mapper => !ReservedClaims.Contains(
                    ClaimRoot(Text(mapper?["config"]?["claim.name"])), StringComparer.Ordinal)),
                    "no configurable mapper overrides a reserved token property");
                Require(effectiveMappers.All(mapper =>
                    Text(mapper?["protocolMapper"]) != "oidc-usermodel-client-role-mapper"
                    || !ClaimRoot(Text(mapper?["config"]?["claim.name"])).Contains("${client_id}", StringComparison.Ordinal)),
                    "client-role interpolation cannot select a dynamic top-level claim");
                foreach (var type in new[] { "oidc-sub-mapper", "oidc-audience-mapper", "oidc-audience-resolve-mapper" })
                    Require(effectiveMappers.Count(mapper => Text(mapper?["protocolMapper"]) == type) == 1,
                        "one qualified implicit subject/audience writer");
                var resolver = effectiveMappers.Single(mapper => Text(mapper?["protocolMapper"]) == "oidc-audience-resolve-mapper");
                Require(Text(resolver?["config"]?["access.token.claim"]) == "true"
                    && Text(resolver?["config"]?["id.token.claim"]) != "true", "role audience resolution is access-only");
                if (clientId == "learnstack-web")
                {
                    Require(client["enabled"]?.GetValue<bool>() == true
                        && client["consentRequired"]?.GetValue<bool>() != true, "available nonconsent web login");
                    Require(client["standardFlowEnabled"]?.GetValue<bool>() == true, "web code flow");
                    Require(client["serviceAccountsEnabled"]?.GetValue<bool>() == false, "no web service account");
                    Require(Text(client["secret"]) == "${KEYCLOAK_WEB_CLIENT_SECRET}", "private secret placeholder");
                    Require(Text(client["attributes"]?["pkce.code.challenge.method"]) == "S256", "S256 required");
                    Require(Strings(client["redirectUris"]).Order().SequenceEqual(SeedData.All.Select(tenant =>
                        "https://" + tenant.Host + ":3000/api/auth/callback/keycloak").Order()), "exact callbacks");
                    Require(Text(client["attributes"]?["post.logout.redirect.uris"]).Split("##").Order()
                        .SequenceEqual(SeedData.All.Select(tenant => "https://" + tenant.Host + ":3000/").Order()),
                        "exact logout roots");
                }
                else
                {
                    Require(Text(client["secret"]) == "learnstack-api-dev-secret", "retained development service credential");
                    Require(client["standardFlowEnabled"]?.GetValue<bool>() == false, "no API code flow");
                    Require(Strings(client["redirectUris"]).Length == 0, "no API callbacks");
                    Require(client["serviceAccountsEnabled"]?.GetValue<bool>() == true, "retain legacy service account");
                }
            }
            Require(Scope(realm, "basic")["protocolMappers"]!.AsArray().Any(mapper =>
                Text(mapper?["protocolMapper"]) == "oidc-sub-mapper"
                && Text(mapper?["config"]?["access.token.claim"]) == "true"), "actual subject mapper");
            Require(Mappers(realm).Count == 4, "closed managed scope");
            foreach (var name in identityNames)
            {
                var mapper = Mapper(realm, name);
                Require(Text(mapper["protocolMapper"]) == "oidc-usermodel-attribute-mapper", "attribute mapper type");
                var config = mapper["config"]!;
                Require(Text(config["user.attribute"]) == name && Text(config["claim.name"]) == name,
                    "identity source and claim");
                Require(Text(config["jsonType.label"]) == "String" && Text(config["multivalued"]) == "false",
                    "scalar string mapper");
                Require(Text(config["access.token.claim"]) == "true" && Text(config["id.token.claim"]) == "true"
                    && Text(config["userinfo.token.claim"]) == "false", "identity token destinations");
            }
            var audience = Mapper(realm, "learnstack-api-audience");
            Require(Text(audience["protocolMapper"]) == "oidc-audience-mapper"
                && Text(audience["config"]?["included.client.audience"]) == "learnstack-api"
                && Text(audience["config"]?["access.token.claim"]) == "true"
                && Text(audience["config"]?["id.token.claim"]) == "false", "access-only API audience");
            var profile = Profile(realm);
            Require(profile["unmanagedAttributePolicy"] is null, "disabled unmanaged profile policy");
            foreach (var name in identityNames)
            {
                var attribute = profile["attributes"]!.AsArray().Single(value => Text(value?["name"]) == name)!;
                Require(attribute["multivalued"]?.GetValue<bool>() == false, "single-valued profile identity");
                foreach (var permission in new[] { "view", "edit" })
                    Require(Strings(attribute["permissions"]?[permission]).SequenceEqual(["admin"]), "admin-only identity");
            }
            var users = realm["users"]!.AsArray();
            Require(users.Count == SeedData.IdentityUsers.Length, "exact seed population");
            foreach (var identity in SeedIdentityManifest.Create().Users)
            {
                var user = users.Single(value => Text(value?["username"]) == identity.Username)!;
                Require(Text(user["email"]) == identity.Email, "declared email");
                Require(user["enabled"]?.GetValue<bool>() == true && user["emailVerified"]?.GetValue<bool>() == true,
                    "enabled verified development persona");
                var credentials = user["credentials"]!.AsArray();
                Require(credentials.Count == 1 && Text(credentials[0]?["type"]) == "password"
                    && Text(credentials[0]?["value"]) == "demo-dev-secret"
                    && credentials[0]?["temporary"]?.GetValue<bool>() == false, "one create-only development password");
                Require(identity.AccountCapability == new SeedAccountCapability("account", "manage-account")
                    && Strings(user["clientRoles"]?[identity.AccountCapability.ClientId]).SequenceEqual([identity.AccountCapability.Role])
                    && user["clientRoles"]!.AsObject().Count == 1, "exact declared self-service capability");
                Require(Strings(user["realmRoles"]).SequenceEqual([identity.RealmRole]), "no implicit default or application role grant");
                Require(user["id"] is null, "provider owns account ID");
                var attributes = user["attributes"]!;
                Require(Strings(attributes["user_id"]).SequenceEqual([identity.UserId.ToString("D")]), "fixed actor");
                Require(Strings(attributes["tenant_id"]).SequenceEqual([identity.TenantId.ToString("D")]), "tenant identity");
                Require(identity.OrganizationId is { } organization
                    ? Strings(attributes["organization_id"]).SequenceEqual([organization.ToString("D")])
                    : attributes["organization_id"] is null, "organization identity");
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or KeyNotFoundException
            or FormatException or ArgumentException or NullReferenceException or System.Text.Json.JsonException)
        {
            failures.Add("invalid or ambiguous export shape");
        }
        return failures;
    }

    private static void ValidateAccountDeclaration(JsonObject realm, Action<bool, string> require)
    {
        foreach (var name in new[] { "account", "account-console" })
        {
            var client = Client(realm, name);
            require(client["publicClient"]!.GetValue<bool>() && client["enabled"]!.GetValue<bool>()
                && client["standardFlowEnabled"]!.GetValue<bool>() && !client["fullScopeAllowed"]!.GetValue<bool>()
                && !client["directAccessGrantsEnabled"]!.GetValue<bool>() && !client["implicitFlowEnabled"]!.GetValue<bool>()
                && !client["serviceAccountsEnabled"]!.GetValue<bool>(), "stock account client modes");
            require(Text(client["rootUrl"]) == "${authBaseUrl}" && Text(client["baseUrl"]) == "/realms/learnstack/account/"
                && Strings(client["redirectUris"]).SequenceEqual(["/realms/learnstack/account/*"])
                && Strings(client["webOrigins"]).Length == 0
                && Text(client["attributes"]?["post.logout.redirect.uris"]) == "+", "stock account callbacks");
            require(Strings(client["defaultClientScopes"]).Length == 0 && Strings(client["optionalClientScopes"]).Length == 0,
                "no broad account role scopes");
        }
        var console = Client(realm, "account-console");
        require(Text(console["attributes"]?["pkce.code.challenge.method"]) == "S256", "account S256");
        var mappers = console["protocolMappers"]!.AsArray();
        require(mappers.Count == 2 && mappers.Count(value => Text(value?["protocolMapper"]) == "oidc-audience-resolve-mapper") == 1,
            "stock account resolver and one fixed emitter");
        var mapper = mappers.Single(value => Text(value?["name"]) == "learnstack-account-self-service-roles")!;
        var expected = new Dictionary<string, string>
        {
            ["usermodel.clientRoleMapping.clientId"] = "account",
            ["usermodel.clientRoleMapping.rolePrefix"] = "",
            ["claim.name"] = "resource_access.account.roles",
            ["jsonType.label"] = "String",
            ["multivalued"] = "true",
            ["access.token.claim"] = "true",
            ["introspection.token.claim"] = "true",
            ["id.token.claim"] = "false",
            ["userinfo.token.claim"] = "false",
            ["lightweight.claim"] = "false",
        };
        require(Text(mapper["protocolMapper"]) == "oidc-usermodel-client-role-mapper"
            && mapper["config"]!.AsObject().Count == expected.Count
            && expected.All(entry => Text(mapper["config"]?[entry.Key]) == entry.Value), "closed fixed-account emitter");
        var roles = realm["roles"]!["client"]!["account"]!.AsArray();
        require(roles.Select(role => Text(role?["name"])).Order().SequenceEqual(StockAccountRoles.Order()),
            "eight stock account roles");
        foreach (var role in roles)
        {
            var name = Text(role?["name"]);
            var child = name switch { "manage-account" => "manage-account-links", "manage-consent" => "view-consent", _ => null };
            require(role!["clientRole"]!.GetValue<bool>() && role["composite"]!.GetValue<bool>() == (child is not null)
                && Text(role["description"]) == "${role_" + name + "}", "stock account role definitions");
            require(child is null ? role["composites"] is null :
                role["composites"]!.AsObject().Count == 1 && role["composites"]!["client"]!.AsObject().Count == 1
                && Strings(role["composites"]!["client"]!["account"]).SequenceEqual([child]), "bounded stock account composites");
        }
        require(Text(realm["defaultRole"]?["name"]) == "default-roles-learnstack", "stock default role identity");
        var defaults = realm["roles"]!["realm"]!.AsArray().Single(role => Text(role?["name"]) == "default-roles-learnstack")!;
        require(Strings(defaults["composites"]?["client"]?["account"]).Order().SequenceEqual(StockDefaultAccountRoles.Order()),
            "stock registration default account roles");
        require(Strings(defaults["composites"]?["realm"]).Length == 0
            && defaults["composites"]?["client"]?.AsObject().Count == 1, "no default application composites");
        var allowance = realm["clientScopeMappings"]!["account"]!.AsArray();
        require(allowance.Count == 1 && Text(allowance[0]?["client"]) == "account-console"
            && Strings(allowance[0]?["roles"]).Order().SequenceEqual(StockConsoleScopeRoles.Order()),
            "stock console scope allowance");
    }

    private static string Text(JsonNode? node) => node?.GetValue<string>() ?? "";
    private static string[] Strings(JsonNode? node) => node?.AsArray().Select(Text).ToArray() ?? [];
    private static JsonArray Scopes(JsonObject realm) => realm["clientScopes"]!.AsArray();
    private static JsonObject Scope(JsonObject realm, string name) =>
        Scopes(realm).Single(scope => Text(scope?["name"]) == name)!.AsObject();
    private static JsonObject Client(JsonObject realm, string name) => realm["clients"]!.AsArray()
        .Single(client => Text(client?["clientId"]) == name)!.AsObject();
    private static JsonArray Mappers(JsonObject realm) => Scope(realm, "learnstack-tenant-api")["protocolMappers"]!.AsArray();
    private static JsonObject Mapper(JsonObject realm, string name) =>
        Mappers(realm).Single(mapper => Text(mapper?["name"]) == name)!.AsObject();
    private static void AddOptionalClaim(JsonObject realm, string destination)
    {
        var mapper = Mapper(realm, "user_id").DeepClone().AsObject();
        mapper["name"] = "additional-optional-claim";
        mapper["config"]!["claim.name"] = destination;
        Scope(realm, "phone")["protocolMappers"]!.AsArray().Add(mapper);
    }

    private static void AddOptionalClientRoleClaim(JsonObject realm, string destination, string clientId)
    {
        var mapper = Scope(realm, "roles")["protocolMappers"]!.AsArray().Single(value =>
            Text(value?["protocolMapper"]) == "oidc-usermodel-client-role-mapper")!.DeepClone().AsObject();
        mapper["name"] = "additional-optional-client-role";
        mapper["config"]!["claim.name"] = destination;
        mapper["config"]!["usermodel.clientRoleMapping.clientId"] = clientId;
        Scope(realm, "phone")["protocolMappers"]!.AsArray().Add(mapper);
    }

    // Keycloak 26.6.2 JsonUtils.splitClaimPath: backslash escapes any character,
    // and an unescaped dot terminates a nonempty component. Leading dots stay literal.
    private static string ClaimRoot(string claim)
    {
        var component = ClaimComponent().Match(claim);
        return BackslashedCharacter().Replace(component.Success ? component.Groups[1].Value : claim, "$1");
    }

    [GeneratedRegex(@"^((\\.|[^\\.])+?)\.", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex ClaimComponent();

    [GeneratedRegex(@"\\(.)", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex BackslashedCharacter();
    private static JsonNode ProfileValue(JsonObject realm) => realm["components"]!
        ["org.keycloak.userprofile.UserProfileProvider"]![0]!["config"]!["kc.user.profile.config"]!;
    private static JsonObject Profile(JsonObject realm) => JsonNode.Parse(Text(ProfileValue(realm)[0]))!.AsObject();
    private static void SetProfilePolicy(JsonObject realm, string policy)
    {
        var profile = Profile(realm);
        profile["unmanagedAttributePolicy"] = policy;
        ProfileValue(realm)[0] = profile.ToJsonString();
    }

    private static void ChangeProfile(JsonObject realm, string name, string permission)
    {
        var profile = Profile(realm);
        profile["attributes"]!.AsArray().Single(value => Text(value?["name"]) == name)!
            ["permissions"]![permission] = new JsonArray("admin", "user");
        ProfileValue(realm)[0] = profile.ToJsonString();
    }

    private static JsonObject ReadRealm()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(directory.FullName, "infra", "keycloak", "realms", "learnstack.json");
            if (File.Exists(path)) return JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            directory = directory.Parent;
        }
        throw new InvalidOperationException("Repository realm export is missing.");
    }
}
