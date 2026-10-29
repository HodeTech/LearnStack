using System.Text.Json.Nodes;
using FluentAssertions;
using Xunit;

namespace LearnStack.Tests.Contract;

public sealed partial class OpenApiContractTests
{
    // These are independent wire expectations, never populated from the production classifier,
    // protected-operation descriptors, DTO reflection, or document transformer.
    private sealed record AdministrationContract(string Path, string Method, string Id, string Response,
        string? Input = null, bool ETag = false, bool Paged = false, bool Creates = false);

    private static readonly AdministrationContract[] AdministrationOperations =
    [
        new("/api/v1/identity/organization-memberships", "get", "ListOrganizationMemberships", "PageOfMembershipDirectoryItem", Paged: true),
        new("/api/v1/identity/memberships/{id}", "get", "GetMembershipDetail", "MembershipDetailResponse", ETag: true),
        new("/api/v1/identity/memberships/{id}/profile", "put", "SetMembershipProfile", "AdministrationVersionReceipt", "MembershipProfileInput", true),
        new("/api/v1/identity/memberships/{id}/role-assignments", "put", "ReplaceMembershipRoleAssignments", "AdministrationVersionReceipt", "MembershipRolesInput", true),
        new("/api/v1/identity/memberships/{id}/status", "put", "ChangeMembershipStatus", "AdministrationVersionReceipt", "MembershipStatusInput", true),
        new("/api/v1/identity/me/profile", "get", "GetOwnMembershipProfile", "OwnMembershipProfileResponse", ETag: true),
        new("/api/v1/identity/me/profile", "put", "SetOwnMembershipProfile", "AdministrationVersionReceipt", "MembershipProfileInput", true),
        new("/api/v1/identity/me/contexts", "get", "ListOwnMembershipContexts", "OwnMembershipContextPage", Paged: true),
        new("/api/v1/identity/me/capabilities", "get", "GetOwnCapabilities", "OwnCapabilitiesDto"),
        new("/api/v1/identity/roles", "get", "ListRoles", "RoleDirectoryResponse"),
        new("/api/v1/identity/roles/{id}", "get", "GetRoleDetail", "RoleDetailResponse", ETag: true),
        new("/api/v1/identity/permissions", "get", "GetPermissionRegistry", "PermissionRegistryResponse"),
        new("/api/v1/identity/roles/{id}/grants", "put", "ChangeRoleGrants", "AdministrationVersionReceipt", "RoleGrantsInput", true),
        new("/api/v1/identity/roles/{id}/manifest-adoptions", "post", "AdoptRoleGrantManifest", "AdministrationVersionReceipt", "RoleManifestInput", true),
        new("/api/v1/tenancy/organizations", "get", "ListOrganizations", "OrganizationDirectoryResponse", Paged: true),
        new("/api/v1/tenancy/organizations/{id}", "get", "GetOrganizationDetail", "OrganizationResponse", ETag: true),
        new("/api/v1/tenancy/organizations", "post", "CreateManagedOrganization", "AdministrationVersionReceipt", "OrganizationCreateInput", true, Creates: true),
        new("/api/v1/tenancy/organizations/{id}", "put", "RenameOrganization", "AdministrationVersionReceipt", "OrganizationRenameInput", true),
        new("/api/v1/tenancy/organizations/{id}/archive", "post", "ArchiveOrganization", "AdministrationVersionReceipt", "EmptyAdministrationInput", true),
        new("/api/v1/tenancy/default-organization", "put", "SelectDefaultOrganization", "AdministrationVersionReceipt", "DefaultOrganizationInput", true),
    ];
    private static readonly string[] AdministrationErrors = ["400", "401", "403", "404", "405", "413", "429", "500", "503"];
    private static readonly string[] AdministrationConflicts = ["409"];
    private static readonly string[] AdministrationPairs =
    [
        "Tenant:identity.membership.admin", "Tenant:identity.role.admin", "Tenant:identity.membership_profile.write",
        "Tenant:tenancy.organization.admin", "Tenant:tenancy.locale.write", "Tenant:tenancy.setting.write",
        "Organization:identity.membership.admin", "Organization:identity.membership_profile.write",
    ];
    private static readonly string[] AdministrationFlags =
    ["canReadTenantMembers", "canReadOrganizationMembers", "canWriteMemberProfiles", "canManageMemberships",
        "canReadRoles", "canChangeRoleGrants", "canAdoptRoleRelease", "canWriteOwnProfile", "canManageOrganizations"];
    private const string AdministrationUuidPattern = "^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$";
    private const string AdministrationLocationPattern = "^/api/v1/tenancy/organizations/[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$";
    private static readonly Dictionary<string, JsonNode> AdministrationSchemas = new(StringComparer.Ordinal)
    {
        ["AdministrationVersionReceipt"] = AdminObject(("version", AdminVersion())),
        ["MembershipDetailResponse"] = AdminObject(("membershipId", AdminUuid()), ("userId", AdminUuid()),
            ("organizationId", AdminUuid(true)), ("status", AdminEnum("Active", "Suspended", "Revoked")),
            ("displayName", AdminText(true, 128)), ("preferredLocale", AdminText(true, 35)),
            ("roleIds", AdminArray(AdminUuid(), 8, true)), ("version", AdminVersion())),
        ["OwnMembershipProfileResponse"] = AdminObject(("displayName", AdminText(true, 128)),
            ("preferredLocale", AdminText(true, 35)), ("version", AdminVersion())),
        ["RoleSummaryResponse"] = AdminObject(("roleId", AdminUuid()), ("builtInKey", AdminRole()),
            ("capabilityScope", AdminEnum("Tenant", "Organization")), ("version", AdminVersion())),
        ["RoleDirectoryResponse"] = AdminObject(("items", AdminArray(AdminRef("RoleSummaryResponse"), 8))),
        ["RoleDetailResponse"] = AdminObject(("roleId", AdminUuid()), ("builtInKey", AdminRole()),
            ("capabilityScope", AdminEnum("Tenant", "Organization")), ("version", AdminVersion()),
            ("grants", AdminArray(AdminRef("AdministrationGrantResponse"), 8, true)),
            ("adoptedReleaseVersion", AdminInteger()), ("adoptedReleaseFingerprint", AdminDigest())),
        ["PermissionRegistryItemResponse"] = AdminObject(("scope", AdminEnum("Tenant", "Organization")),
            ("key", AdminText(false, 128)), ("labelKey", AdminText()), ("canGrant", AdminBoolean())),
        ["RoleReleaseResponse"] = AdminObject(("version", AdminInteger()), ("fingerprint", AdminDigest()),
            ("tenantAdminDelta", AdminArray(AdminRef("AdministrationGrantResponse"), 8, true))),
        ["PermissionRegistryResponse"] = AdminObject(("items", AdminArray(AdminRef("PermissionRegistryItemResponse"), 8)),
            ("release", AdminRef("RoleReleaseResponse"))),
        ["OwnCapabilitiesDto"] = AdminObject(AdministrationFlags.Select(name => (name, (JsonNode)AdminBoolean())).ToArray()),
        ["OwnMembershipContextResponse"] = AdminObject(("tenantId", AdminUuid()), ("organizationId", AdminUuid(true)),
            ("tenantName", AdminText()), ("organizationName", AdminText(true)),
            ("entryOrigin", new JsonObject { ["type"] = "string", ["format"] = "uri" }), ("canUseCurrentHost", AdminBoolean())),
        ["OwnMembershipContextPage"] = AdminObject(("items", AdminArray(AdminRef("OwnMembershipContextResponse"), 100)), ("pageInfo", AdminRef("PageInfo"))),
        ["OrganizationResponse"] = AdminObject(("organizationId", AdminUuid()), ("slug", AdminText(false, 63)),
            ("displayName", AdminText(false, 200)), ("status", AdminEnum("Active", "Suspended", "Archived")),
            ("isDefault", AdminBoolean()), ("version", AdminVersion())),
        ["OrganizationDirectoryResponse"] = AdminObject(("items", AdminArray(AdminRef("OrganizationResponse"), 100)),
            ("defaultOrganizationId", AdminUuid(true)), ("tenantVersion", AdminVersion()), ("pageInfo", AdminRef("PageInfo"))),
        ["MembershipProfileInput"] = AdminObject(("displayName", AdminText(true, 128)), ("preferredLocale", AdminText(true, 35))),
        ["MembershipRolesInput"] = AdminObject(("roleIds", AdminArray(AdminUuid(), 8, true))),
        ["MembershipStatusInput"] = AdminObject(("status", AdminEnum("Active", "Suspended", "Revoked"))),
        ["RoleGrantsInput"] = AdminObject(("grants", AdminArray(AdminRef("AdministrationGrantResponse"), 8, true))),
        ["RoleManifestInput"] = AdminObject(("manifestVersion", AdminInteger()), ("manifestFingerprint", AdminDigest())),
        ["OrganizationCreateInput"] = AdminObject(("slug", AdminText(false, 63)), ("displayName", AdminText(false, 200))),
        ["OrganizationRenameInput"] = AdminObject(("displayName", AdminText(false, 200))),
        ["EmptyAdministrationInput"] = AdminObject(),
        ["DefaultOrganizationInput"] = AdminObject(("organizationId", AdminUuid())),
    };

    [Fact]
    public async Task Administration_contract_pins_twenty_actions_seventeen_paths_and_exact_lossless_closed_shapes()
    {
        var served = await Served();
        AdministrationOperations.Should().HaveCount(20);
        AdministrationOperations.Select(operation => operation.Path).Distinct(StringComparer.Ordinal).Should().HaveCount(17);
        AdministrationOperations.Count(operation => operation.Input is not null).Should().Be(10);
        AdministrationSchemas.Should().HaveCount(23);
        ContractErrors(served).Should().BeEmpty();
        SchemaErrors(served).Should().BeEmpty();
    }

    [Fact]
    public async Task Administration_operation_controls_detect_route_security_body_precondition_and_header_drift()
    {
        var clean = await Served();
        ContractErrors(clean).Should().BeEmpty();
        foreach (var contract in AdministrationOperations)
        {
            AssertAdminMutation(clean, "administration-operations", document =>
                document["paths"]![contract.Path]!.AsObject().Remove(contract.Method));
            AssertAdminMutation(clean, "administration-operations", document =>
                document["paths"]![contract.Path]!["head"] = document["paths"]![contract.Path]![contract.Method]!.DeepClone());
            AssertAdminMutation(clean, "administration-operations", document =>
                document["paths"]![contract.Path]![contract.Method]!["operationId"] = "GetAccountSite");
            AssertAdminMutation(clean, "administration-security", document =>
                document["paths"]![contract.Path]![contract.Method]!.AsObject().Remove("security"));
            AssertAdminMutation(clean, "administration-responses", document =>
                document["paths"]![contract.Path]![contract.Method]!["responses"]!.AsObject().Remove("403"));
            AssertAdminMutation(clean, "administration-success", document =>
                document["paths"]![contract.Path]![contract.Method]!["responses"]![contract.Creates ? "201" : "200"]!["content"]!
                    ["application/json"]!["schema"]!["$ref"] = "#/components/schemas/AccountSite");
            AssertAdminMutation(clean, "administration-headers", document =>
                document["paths"]![contract.Path]![contract.Method]!["responses"]!["503"]!["headers"]!["Retry-After"] =
                    new JsonObject { ["required"] = true, ["schema"] = new JsonObject { ["type"] = "integer" } });
            AssertAdminMutation(clean, "administration-body", document =>
            {
                var operation = document["paths"]![contract.Path]![contract.Method]!;
                if (contract.Input is null) operation["requestBody"] = new JsonObject();
                else operation.AsObject().Remove("requestBody");
            });
            AssertAdminMutation(clean, "administration-parameters", document =>
            {
                var operation = document["paths"]![contract.Path]![contract.Method]!;
                operation["parameters"] ??= new JsonArray();
                operation["parameters"]!.AsArray().Add(new JsonObject
                { ["name"] = "tenantId", ["in"] = "query", ["schema"] = AdminUuid() });
            });
            var parameters = clean["paths"]![contract.Path]![contract.Method]!["parameters"] as JsonArray ?? [];
            foreach (var (index, parameter) in parameters.Index())
            {
                AssertAdminMutation(clean, "administration-parameters", document =>
                    document["paths"]![contract.Path]![contract.Method]!["parameters"]![index]!["required"] =
                        !(parameter!["required"]?.GetValue<bool>() ?? false));
                AssertAdminMutation(clean, "administration-parameters", document =>
                    document["paths"]![contract.Path]![contract.Method]!["parameters"]!.AsArray().Add(parameter!.DeepClone()));
                AssertAdminMutation(clean, "administration-parameters", document =>
                    document["paths"]![contract.Path]![contract.Method]!["parameters"]![index]!["schema"]!["type"] =
                        parameter!["schema"]!["type"]!.GetValue<string>() == "string" ? "integer" : "string");
            }
            var headers = clean["paths"]![contract.Path]![contract.Method]!["responses"]![contract.Creates ? "201" : "200"]!["headers"]!.AsObject();
            foreach (var header in headers.Select(pair => pair.Key))
                AssertAdminMutation(clean, "administration-headers", document =>
                    document["paths"]![contract.Path]![contract.Method]!["responses"]![contract.Creates ? "201" : "200"]!["headers"]!.AsObject().Remove(header));
            if (contract.Input is not null)
            {
                AssertAdminMutation(clean, "administration-responses", document =>
                    document["paths"]![contract.Path]![contract.Method]!["responses"]!.AsObject().Remove("409"));
                AssertAdminMutation(clean, "administration-body", document =>
                    document["paths"]![contract.Path]![contract.Method]!["requestBody"]!["required"] = false);
                AssertAdminMutation(clean, "administration-headers", document =>
                    document["paths"]![contract.Path]![contract.Method]!["responses"]![contract.Creates ? "201" : "200"]!
                        ["headers"]!["Idempotency-Replayed"]!["schema"]!["type"] = "boolean");
            }
        }
        AssertAdminMutation(clean, "administration-parameters", document =>
            document["paths"]!["/api/v1/tenancy/organizations"]!["post"]!["parameters"]!.AsArray().Add(new JsonObject
            { ["name"] = "If-Match", ["in"] = "header", ["required"] = true, ["schema"] = AdminEntityTag() }));
        AssertAdminMutation(clean, "administration-headers", document =>
            document["paths"]!["/api/v1/tenancy/organizations"]!["post"]!["responses"]!["201"]!["headers"]!["Location"]!["schema"]!["pattern"] = "^https://");
        AssertAdminMutation(clean, "administration-headers", document =>
            document["paths"]!["/api/v1/identity/me/capabilities"]!["get"]!["responses"]!["200"]!["headers"]!["ETag"] =
                new JsonObject { ["required"] = true, ["schema"] = AdminEntityTag() });
        AssertAdminMutation(clean, "administration-responses", document =>
        {
            var responses = document["paths"]!["/api/v1/tenancy/organizations"]!["post"]!["responses"]!.AsObject();
            responses["200"] = responses["201"]!.DeepClone(); responses.Remove("201");
        });
    }

    [Fact]
    public async Task Administration_schema_controls_detect_extra_fields_missing_nullability_unbounded_lists_numeric_versions_and_forged_grants()
    {
        var clean = await Served();
        SchemaErrors(clean).Should().BeEmpty();
        foreach (var (name, _) in AdministrationSchemas)
        {
            AssertAdminMutation(clean, "administration-schema:" + name, document =>
                document["components"]!["schemas"]![name]!["additionalProperties"] = true, schema: true);
            AssertAdminMutation(clean, "administration-schema:" + name, document =>
            {
                var shape = document["components"]!["schemas"]![name]!;
                shape["properties"] ??= new JsonObject();
                shape["properties"]!["subjectKey"] = AdminText();
            }, schema: true);
        }
        foreach (var (name, field) in new[]
        {
            ("AdministrationVersionReceipt", "version"), ("MembershipDetailResponse", "version"),
            ("OwnMembershipProfileResponse", "version"), ("RoleSummaryResponse", "version"), ("RoleDetailResponse", "version"),
            ("OrganizationResponse", "version"), ("OrganizationDirectoryResponse", "tenantVersion"),
        })
            AssertAdminMutation(clean, "administration-schema:" + name, document =>
                document["components"]!["schemas"]![name]!["properties"]![field] = new JsonObject { ["type"] = "integer", ["format"] = "int64" }, schema: true);
        foreach (var field in new[] { "displayName", "preferredLocale" })
        {
            AssertAdminMutation(clean, "administration-schema:MembershipProfileInput", document =>
                document["components"]!["schemas"]!["MembershipProfileInput"]!["required"]!.AsArray().Remove(
                    document["components"]!["schemas"]!["MembershipProfileInput"]!["required"]!.AsArray().Single(value => value!.GetValue<string>() == field)), schema: true);
            AssertAdminMutation(clean, "administration-schema:MembershipProfileInput", document =>
                document["components"]!["schemas"]!["MembershipProfileInput"]!["properties"]![field]!["type"] = "string", schema: true);
        }
        foreach (var (name, field) in new[] { ("MembershipRolesInput", "roleIds"), ("RoleGrantsInput", "grants") })
        {
            AssertAdminMutation(clean, "administration-schema:" + name, document =>
                document["components"]!["schemas"]![name]!["properties"]![field]!["maxItems"] = 9, schema: true);
            AssertAdminMutation(clean, "administration-schema:" + name, document =>
                document["components"]!["schemas"]![name]!["properties"]![field]!.AsObject().Remove("uniqueItems"), schema: true);
        }
        AssertAdminMutation(clean, "administration-schema:MembershipStatusInput", document =>
            document["components"]!["schemas"]!["MembershipStatusInput"]!["properties"]!["status"] = new JsonObject { ["type"] = "integer" }, schema: true);
        AssertAdminMutation(clean, "administration-schema:OwnCapabilitiesDto", document =>
            document["components"]!["schemas"]!["OwnCapabilitiesDto"]!["properties"]!["canManageRoles"] = AdminBoolean(), schema: true);
        AssertAdminMutation(clean, "administration-schema:OwnMembershipContextResponse", document =>
            document["components"]!["schemas"]!["OwnMembershipContextResponse"]!["properties"]!["description"] = AdminText(), schema: true);
        AssertAdminMutation(clean, "administration-schema:RoleManifestInput", document =>
            document["components"]!["schemas"]!["RoleManifestInput"]!["properties"]!["manifestFingerprint"]!.AsObject().Remove("pattern"), schema: true);
        AssertAdminMutation(clean, "administration-grants", document =>
            document["components"]!["schemas"]!["AdministrationGrantResponse"]!["oneOf"]!.AsArray().Add(
                AdminObject(("scope", AdminEnum("Organization")), ("key", AdminEnum("identity.role.admin")))), schema: true);
        AssertAdminMutation(clean, "administration-grants", document =>
        {
            var pairs = document["components"]!["schemas"]!["AdministrationGrantResponse"]!["oneOf"]!.AsArray();
            pairs[1] = pairs[0]!.DeepClone();
        }, schema: true);
        AssertAdminMutation(clean, "administration-grants", document =>
            document["components"]!["schemas"]!["AdministrationGrantResponse"]!["oneOf"]![0]!["properties"]!["scope"] = new JsonObject { ["type"] = "integer" }, schema: true);
        AssertAdminMutation(clean, "schema-census", document =>
            document["components"]!["schemas"]!["UnpublishedAdministrationPayload"] = AdminObject(("token", AdminText())), schema: true);
    }

    private static void AssertAdminMutation(JsonNode clean, string error, Action<JsonNode> mutation, bool schema = false)
    {
        var dirty = clean.DeepClone();
        mutation(dirty);
        (schema ? SchemaErrors(dirty) : ContractErrors(dirty)).Should().Contain(error);
    }

    private static List<string> AdministrationContractErrors(JsonNode document)
    {
        var errors = new List<string>();
        foreach (var group in AdministrationOperations.GroupBy(operation => operation.Path, StringComparer.Ordinal))
        {
            if (document["paths"]?[group.Key] is not JsonObject item
                || !item.Select(method => method.Key).ToHashSet(StringComparer.Ordinal).SetEquals(group.Select(operation => operation.Method)))
                errors.Add("administration-operations");
            foreach (var contract in group)
            {
                if (document["paths"]?[contract.Path]?[contract.Method] is not JsonObject operation)
                { errors.Add("administration-operations"); continue; }
                if (operation["operationId"]?.GetValue<string>() != contract.Id
                    || operation["x-version-introduced"]?.GetValue<string>() != "v1") errors.Add("administration-operations");
                if (operation["security"] is not JsonArray { Count: 1 } security || security[0] is not JsonObject { Count: 1 } requirement
                    || requirement["TenantBearer"] is not JsonArray { Count: 0 }) errors.Add("administration-security");
                var expectedParameters = new Dictionary<string, (string Location, bool Required, JsonNode Schema)>(StringComparer.Ordinal);
                if (contract.Path.Contains("{id}", StringComparison.Ordinal)) expectedParameters["id"] = ("path", true, AdminUuid());
                if (contract.Paged)
                {
                    expectedParameters["cursor"] = ("query", false, new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 1024 });
                    var limit = AdminInteger(); limit["default"] = 20;
                    expectedParameters["limit"] = ("query", false, limit);
                }
                if (contract.Input is not null)
                {
                    expectedParameters["Idempotency-Key"] = ("header", true, new JsonObject
                    { ["type"] = "string", ["minLength"] = 8, ["maxLength"] = 128, ["pattern"] = "^[!-~]{8,128}$" });
                    if (!contract.Creates) expectedParameters["If-Match"] = ("header", true, AdminEntityTag());
                    if (operation["requestBody"]?["required"]?.GetValue<bool>() != true
                        || !AdminMediaMatches(operation["requestBody"]?["content"], "application/json", contract.Input)) errors.Add("administration-body");
                    var description = operation["description"]?.GetValue<string>() ?? string.Empty;
                    if (!description.Contains("Strict UTF-8 application/json", StringComparison.Ordinal)
                        || !description.Contains("16 KiB", StringComparison.Ordinal)
                        || !description.Contains("unknown, duplicate and missing properties refuse", StringComparison.Ordinal)
                        || !description.Contains("never automatically retried", StringComparison.Ordinal)) errors.Add("administration-body");
                }
                else if (operation.ContainsKey("requestBody")) errors.Add("administration-body");
                var parameters = operation["parameters"] as JsonArray ?? [];
                if (parameters.Count != expectedParameters.Count || !parameters.Select(parameter => parameter?["name"]?.GetValue<string>())
                    .ToHashSet(StringComparer.Ordinal).SetEquals(expectedParameters.Keys)) errors.Add("administration-parameters");
                foreach (var parameter in parameters)
                    if (parameter?["name"]?.GetValue<string>() is not { } name || !expectedParameters.TryGetValue(name, out var expected)
                        || parameter["in"]?.GetValue<string>() != expected.Location
                        || (parameter["required"]?.GetValue<bool>() ?? false) != expected.Required
                        || !AdminSchemaMatches(parameter["schema"], expected.Schema)) errors.Add("administration-parameters");
                if (operation["responses"] is not JsonObject responses) { errors.Add("administration-responses"); continue; }
                var success = contract.Creates ? "201" : "200";
                var expectedStatuses = AdministrationErrors.Append(success).Concat(contract.Input is null ? [] : AdministrationConflicts);
                if (!responses.Select(response => response.Key).ToHashSet(StringComparer.Ordinal).SetEquals(expectedStatuses)) errors.Add("administration-responses");
                foreach (var (status, response) in responses)
                {
                    var headers = response?["headers"] as JsonObject;
                    var expectedHeaders = new List<string> { "Cache-Control" };
                    if (!AdminLiteralHeader(headers?["Cache-Control"], "no-store", status != "413")) errors.Add("administration-headers");
                    if (status == "401")
                    {
                        expectedHeaders.Add("WWW-Authenticate");
                        if (!AdminLiteralHeader(headers?["WWW-Authenticate"], "Bearer", true)) errors.Add("administration-headers");
                    }
                    if (status == "405")
                    {
                        expectedHeaders.Add("Allow");
                        if (!AdminLiteralHeader(headers?["Allow"], string.Join(", ", group.Select(value => value.Method.ToUpperInvariant()).Order(StringComparer.Ordinal)), true))
                            errors.Add("administration-headers");
                    }
                    if (status == "429")
                    {
                        expectedHeaders.Add("Retry-After");
                        if (headers?["Retry-After"]?["required"]?.GetValue<bool>() != true
                            || !AdminSchemaMatches(headers["Retry-After"]?["schema"], new JsonObject { ["type"] = "integer", ["minimum"] = 0 }))
                            errors.Add("administration-headers");
                    }
                    if (status == success)
                    {
                        if (!AdminMediaMatches(response?["content"], "application/json", contract.Response)) errors.Add("administration-success");
                        if (contract.ETag)
                        {
                            expectedHeaders.Add("ETag");
                            if (headers?["ETag"]?["required"]?.GetValue<bool>() != true
                                || !AdminSchemaMatches(headers["ETag"]?["schema"], AdminEntityTag())) errors.Add("administration-headers");
                        }
                        if (contract.Input is not null)
                        {
                            expectedHeaders.Add("Idempotency-Replayed");
                            if (!AdminLiteralHeader(headers?["Idempotency-Replayed"], "true", false)) errors.Add("administration-headers");
                        }
                        if (contract.Creates)
                        {
                            expectedHeaders.Add("Location");
                            if (headers?["Location"]?["required"]?.GetValue<bool>() != true || !AdminSchemaMatches(headers["Location"]?["schema"],
                                new JsonObject { ["type"] = "string", ["pattern"] = AdministrationLocationPattern })) errors.Add("administration-headers");
                        }
                    }
                    else if (!AdminMediaMatches(response?["content"], "application/problem+json", "ProblemDetails")) errors.Add("administration-problem");
                    if (headers is null || !headers.Select(header => header.Key).ToHashSet(StringComparer.Ordinal).SetEquals(expectedHeaders))
                        errors.Add("administration-headers");
                }
            }
        }
        return errors;
    }

    private static List<string> AdministrationSchemaErrors(JsonNode document)
    {
        var errors = new List<string>();
        var schemas = document["components"]?["schemas"];
        foreach (var (name, expected) in AdministrationSchemas)
            if (!AdminSchemaMatches(schemas?[name], expected)) errors.Add("administration-schema:" + name);
        var grants = schemas?["AdministrationGrantResponse"];
        if (grants is not JsonObject { Count: 1 } || grants["oneOf"] is not JsonArray { Count: 8 } pairs)
            errors.Add("administration-grants");
        else
        {
            var expected = AdministrationPairs.Select(pair => pair.Split(':')).Select(pair => AdminObject(
                ("scope", AdminEnum(pair[0])), ("key", AdminEnum(pair[1])))).ToArray();
            if (pairs.Any(pair => expected.Count(candidate => AdminSchemaMatches(pair, candidate)) != 1)
                || expected.Any(candidate => pairs.Count(pair => AdminSchemaMatches(pair, candidate)) != 1)) errors.Add("administration-grants");
        }
        return errors;
    }

    private static bool AdminMediaMatches(JsonNode? content, string media, string schema) => content is JsonObject { Count: 1 }
        && content[media] is JsonObject { Count: 1 } value && JsonNode.DeepEquals(value["schema"], AdminRef(schema));
    private static bool AdminLiteralHeader(JsonNode? header, string value, bool required) => header is JsonObject
        && (header["required"]?.GetValue<bool>() ?? false) == required && AdminSchemaMatches(header["schema"], AdminEnum(value));
    private static bool AdminSchemaMatches(JsonNode? actual, JsonNode expected) => actual is not null
        && JsonNode.DeepEquals(AdminNormalize(actual), AdminNormalize(expected));

    // JSON Schema required/type arrays are sets; the separately pinned full snapshot still preserves
    // all serialized array order. Descriptions are checked where their behavioral promise matters.
    private static JsonNode AdminNormalize(JsonNode node, bool propertyMap = false)
    {
        if (node is JsonArray array) return new JsonArray(array.Select(item => item is null ? null : AdminNormalize(item)).ToArray());
        if (node is not JsonObject value) return node.DeepClone();
        if (propertyMap) return new JsonObject(value.Select(member => KeyValuePair.Create(member.Key,
            member.Value is null ? null : AdminNormalize(member.Value))));
        var normalized = new JsonObject();
        foreach (var (name, child) in value)
        {
            var empty = child is JsonObject { Count: 0 } or JsonArray { Count: 0 };
            if (name == "description" || child is null || (name is "properties" or "required") && empty) continue;
            normalized[name] = (name is "required" or "type") && child is JsonArray set
                ? new JsonArray(set.Select(item => item!.GetValue<string>()).Order(StringComparer.Ordinal).Select(item => (JsonNode?)JsonValue.Create(item)).ToArray())
                : AdminNormalize(child, name == "properties");
        }
        return normalized;
    }

    private static JsonObject AdminObject(params (string Name, JsonNode Schema)[] fields) => new()
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
        ["required"] = new JsonArray(fields.Select(field => (JsonNode?)JsonValue.Create(field.Name)).ToArray()),
        ["properties"] = new JsonObject(fields.Select(field => KeyValuePair.Create<string, JsonNode?>(field.Name, field.Schema))),
    };
    private static JsonObject AdminText(bool nullable = false, int? maximum = null)
    {
        var schema = new JsonObject { ["type"] = nullable ? new JsonArray("string", "null") : JsonValue.Create("string") };
        if (maximum is { } length) schema["maxLength"] = length;
        return schema;
    }
    private static JsonObject AdminUuid(bool nullable = false)
    { var schema = AdminText(nullable); schema["format"] = "uuid"; schema["pattern"] = AdministrationUuidPattern; return schema; }
    private static JsonObject AdminArray(JsonNode item, int maximum, bool unique = false)
    {
        return new JsonObject { ["type"] = "array", ["items"] = item, ["maxItems"] = maximum, ["uniqueItems"] = unique };
    }
    private static JsonObject AdminRef(string name) => new() { ["$ref"] = "#/components/schemas/" + name };
    private static JsonObject AdminBoolean() => new() { ["type"] = "boolean" };
    private static JsonObject AdminInteger() => new() { ["type"] = "integer", ["format"] = "int32", ["minimum"] = 1, ["maximum"] = int.MaxValue };
    private static JsonObject AdminDigest() => new() { ["type"] = "string", ["minLength"] = 64, ["maxLength"] = 64, ["pattern"] = "^[0-9a-f]{64}$" };
    private static JsonObject AdminVersion() => new() { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 19, ["pattern"] = "^(0|[1-9][0-9]{0,18})$" };
    private static JsonObject AdminEntityTag() => new() { ["type"] = "string", ["minLength"] = 3, ["maxLength"] = 21, ["pattern"] = "^\"(0|[1-9][0-9]{0,18})\"$" };
    private static JsonObject AdminEnum(params string[] values) => new() { ["type"] = "string", ["enum"] = new JsonArray(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()) };
    private static JsonObject AdminRole() => AdminEnum("TenantAdmin", "OrgAdmin", "Instructor", "OrgInstructor", "Learner", "Guardian", "Editor");
}
