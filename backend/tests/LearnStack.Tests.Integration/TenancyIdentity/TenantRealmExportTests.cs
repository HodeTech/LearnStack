using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FluentAssertions;
using LearnStack.Tools.Seeder;
using Xunit;

namespace LearnStack.Tests.Integration.TenancyIdentity;

/// <summary>Offline drift controls; live provider behavior belongs to the Docker issuance proofs.</summary>
public sealed partial class TenantRealmExportTests
{
    private static readonly string[] ReservedClaims =
        ["iss", "sub", "aud", "azp", "typ", "scope", "iat", "exp", "nbf"];
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
            ("wrong realm TTL", realm => realm["accessTokenLifespan"] = 1800),
            ("client TTL override", realm => Client(realm, "learnstack-web")["attributes"]!["access.token.lifespan"] = "900"),
            ("public client", realm => Client(realm, "learnstack-web")["publicClient"] = true),
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
            or FormatException or ArgumentException or NullReferenceException)
        {
            failures.Add("invalid or ambiguous export shape");
        }
        return failures;
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
