using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using LearnStack.Tools.Seeder;
using Xunit;
using static LearnStack.Tests.Integration.Database.IdentityAuthorizationHttpProof;
namespace LearnStack.Tests.Integration.Database;

public sealed partial class BoundedAdministrationHttpTests
{
    private static readonly string[] CustomFieldEnvelopeRequired = ["value"];
    [Fact]
    public async Task Actual_v3_opt_in_definition_lifecycle_member_value_and_discarded_original_receipts_are_coupled()
    {
        await using var proof = await CreatePreparedAsync();
        using var client = ConsentClient(proof);
        var admin = proof.Token(EnglishAdmin, Tenant); var learner = proof.Token(EnglishLearner, Tenant);
        const string definitions = "/api/v1/customization/custom-field-definitions";
        var role = SeedData.IdentityRoles.Single(x => x.Tenant == Tenant && x.BuiltInKey == LearnStack.SharedKernel.Permissions.BuiltInRoleKey.TenantAdmin).RoleId.ToString("D");
        var input = DefinitionBody(1);
        using (var before = await SendAsync(client, admin, "/api/v1/identity/me/capabilities"))
            Assert.False((await JsonAsync(before)).GetProperty("canManageCustomFields").GetBoolean());
        using (var denied = await SendAsync(client, admin, definitions, "POST", input, key: Key())) Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using (var registry = await SendAsync(client, admin, "/api/v1/identity/permissions"))
        {
            var actual = await JsonAsync(registry);
            AssertRegistryRuntime(actual, 2, 8);
            // The same JSON/OpenAPI shape can carry the wrong release semantics.
            var counterfeit = JsonNode.Parse(actual.GetRawText())!;
            counterfeit["release"]!["version"] = 3;
            var altered = JsonSerializer.SerializeToElement(counterfeit);
            Assert.Equal(actual.EnumerateObject().Select(x => x.Name), altered.EnumerateObject().Select(x => x.Name));
            Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertRegistryRuntime(altered, 2, 8));
        }
        for (var source = 1; source <= 2; source++)
        {
            using var registry = await SendAsync(client, admin, "/api/v1/identity/permissions?sourceReleaseVersion=" + source);
            var registryBody = await JsonAsync(registry);
            AssertRegistryRuntime(registryBody, source + 1, source == 1 ? 8 : 9);
            var release = registryBody.GetProperty("release");
            var body = JsonSerializer.Serialize(new { manifestVersion = source + 1, manifestFingerprint = release.GetProperty("fingerprint").GetString() });
            await MutateAsync(client, admin, "POST", "/api/v1/identity/roles/" + role + "/manifest-adoptions", body,
                await ResourceVersionAsync(client, admin, "/api/v1/identity/roles/" + role), Key());
        }
        using (var after = await SendAsync(client, admin, "/api/v1/identity/me/capabilities"))
        {
            var capabilities = await JsonAsync(after);
            Assert.Equal(10, capabilities.EnumerateObject().Count());
            Assert.True(capabilities.GetProperty("canManageCustomFields").GetBoolean());
        }
        using (var currentRole = await SendAsync(client, admin, "/api/v1/identity/roles/" + role))
        {
            var detail = await JsonAsync(currentRole);
            Assert.Equal(3, detail.GetProperty("adoptedReleaseVersion").GetInt32());
            var actual = detail.GetProperty("grants").EnumerateArray().Select(x => x.GetProperty("key").GetString()).ToArray();
            Assert.Equal(KernelAndTenancyKeys.Append("identity.membership_profile.write").Append("customization.custom_field.admin").Order(StringComparer.Ordinal), actual.Order(StringComparer.Ordinal));
        }
        var boundary = AuthoringBoundaryBody();
        Assert.Equal(4096, System.Text.Encoding.UTF8.GetByteCount(boundary));
        using (var wire = JsonDocument.Parse(boundary))
        {
            Assert.True(LearnStack.Api.Customization.CustomFieldHttpInput.TryDefinition(wire.RootElement, out var inputBoundary));
            var historicalInternalFrame = new LearnStack.Modules.Customization.Application.Contracts.CustomFields.RegisterCustomFieldDefinitionCommand(Guid.CreateVersion7(), inputBoundary!);
            Assert.True(System.Text.Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(historicalInternalFrame)) > 4096);
        }
        using (var acceptedBoundary = await SendAsync(client, admin, definitions, "POST", boundary, key: Key()))
            Assert.Equal(2, (await JsonAsync(acceptedBoundary, HttpStatusCode.Created)).EnumerateObject().Count());
        var oversizedKey = Key();
        using (var refusedBoundary = await SendAsync(client, admin, definitions, "POST", boundary + " ", key: oversizedKey))
            Assert.Equal(HttpStatusCode.BadRequest, refusedBoundary.StatusCode);
        var abandoned = Assert.IsType<StoredOutcome>(await StoredAsync(proof, oversizedKey));
        Assert.Equal("in_flight", abandoned.State);
        Assert.Null(abandoned.Target);
        Assert.True(abandoned.ExpiresAt < DateTimeOffset.UtcNow);
        var createKey = Key(); JsonElement created;
        using (var response = await SendAsync(client, admin, definitions, "POST", input, key: createKey))
        { created = await JsonAsync(response, HttpStatusCode.Created); AssertVersionHeader(response, created); Assert.Equal(2, created.EnumerateObject().Count()); }
        var id = created.GetProperty("id").GetString()!; var path = definitions + "/" + id;
        JsonElement revised;
        using (var response = await SendAsync(client, admin, path, "PUT", DefinitionBody(1, "Revised label"), created.GetProperty("version").GetString(), Key())) revised = await JsonAsync(response);
        JsonElement published;
        using (var response = await SendAsync(client, admin, path + "/publication", "POST", "{\"expectedIncumbentId\":null,\"expectedIncumbentVersion\":null}", revised.GetProperty("version").GetString(), Key())) published = await JsonAsync(response);
        using (var response = await SendAsync(client, admin, path))
        {
            var body = await JsonAsync(response); Assert.Equal(JsonValueKind.Object, body.GetProperty("schema").ValueKind);
            Assert.Equal("Revised label", body.GetProperty("labels").GetProperty("en").GetString());
            Assert.Equal(id, body.GetProperty("activeRevision").GetProperty("id").GetString());
        }
        JsonElement fields;
        using (var response = await SendAsync(client, learner, "/api/v1/identity/me/custom-fields")) fields = await JsonAsync(response);
        var missing = Assert.Single(fields.GetProperty("fields").EnumerateArray()); Assert.Equal("Missing", missing.GetProperty("state").GetString());
        var patch = JsonSerializer.Serialize(new { set = new[] { new { conceptKey = "http-boolean", definitionId = id, schemaVersion = 1, envelope = new { value = false }, consentEvidenceId = (Guid?)null } }, remove = Array.Empty<object>() });
        var writeKey = Key(); JsonElement saved;
        using (var response = await SendAsync(client, learner, "/api/v1/identity/me/custom-fields", "PATCH", patch, fields.GetProperty("version").GetString(), writeKey))
        { saved = await JsonAsync(response); Assert.Equal(2, saved.EnumerateObject().Count()); AssertVersionHeader(response, saved); Assert.Equal(fields.GetProperty("membershipId").GetString(), saved.GetProperty("id").GetString()); }
        using (var response = await SendAsync(client, learner, "/api/v1/identity/me/custom-fields", "PATCH", patch, fields.GetProperty("version").GetString(), writeKey))
        { Assert.Equal(saved.GetRawText(), (await JsonAsync(response)).GetRawText()); Assert.Equal("true", Assert.Single(response.Headers.GetValues("Idempotency-Replayed"))); }
        using (var response = await SendAsync(client, admin, path + "/retirement", "POST", "{}", published.GetProperty("version").GetString(), Key())) await JsonAsync(response);
        using (var response = await SendAsync(client, learner, "/api/v1/identity/me/custom-fields"))
        {
            var retained = Assert.Single((await JsonAsync(response)).GetProperty("fields").EnumerateArray());
            Assert.Equal("Retired", retained.GetProperty("state").GetString()); Assert.False(retained.GetProperty("envelope").GetProperty("value").GetBoolean());
        }
        using (var replay = await SendAsync(client, admin, definitions, "POST", input, key: createKey))
        { Assert.Equal(created.GetRawText(), (await JsonAsync(replay, HttpStatusCode.Created)).GetRawText()); Assert.Equal("customization.custom_field.read_receipt", Assert.Single(await proof.AuditAsync(replay, Tenant)).Operation); }
        var draftKey = Key(); JsonElement draft;
        using (var response = await SendAsync(client, admin, definitions, "POST", DefinitionBody(2), key: draftKey)) draft = await JsonAsync(response, HttpStatusCode.Created);
        using (var response = await SendAsync(client, admin, definitions + "/" + draft.GetProperty("id").GetString() + "/discard", "POST", "{}", draft.GetProperty("version").GetString(), Key())) await JsonAsync(response);
        using (var replay = await SendAsync(client, admin, definitions, "POST", DefinitionBody(2), key: draftKey))
        { Assert.Equal(draft.GetRawText(), (await JsonAsync(replay, HttpStatusCode.Created)).GetRawText()); Assert.Equal("customization.custom_field.read_receipt", Assert.Single(await proof.AuditAsync(replay, Tenant)).Operation); }

        // Replacement is complete: omitting the v3 grant removes its actual authority.
        using (var currentRole = await SendAsync(client, admin, "/api/v1/identity/roles/" + role))
        {
            var detail = await JsonAsync(currentRole);
            var retained = detail.GetProperty("grants").EnumerateArray()
                .Where(grant => grant.GetProperty("key").GetString() != "customization.custom_field.admin")
                .Select(grant => new { scope = grant.GetProperty("scope").GetString(), key = grant.GetProperty("key").GetString() }).ToArray();
            await MutateAsync(client, admin, "PUT", "/api/v1/identity/roles/" + role + "/grants",
                JsonSerializer.Serialize(new { grants = retained }), detail.GetProperty("version").GetString()!, Key());
        }
        using (var afterRemoval = await SendAsync(client, admin, "/api/v1/identity/me/capabilities"))
            Assert.False((await JsonAsync(afterRemoval)).GetProperty("canManageCustomFields").GetBoolean());
        using (var deniedAgain = await SendAsync(client, admin, definitions, "POST", input, key: Key()))
            Assert.Equal(HttpStatusCode.Forbidden, deniedAgain.StatusCode);
    }
    private static void AssertRegistryRuntime(JsonElement registry, int releaseVersion, int count)
    {
        Assert.Equal(count, registry.GetProperty("items").GetArrayLength());
        Assert.Equal(releaseVersion, registry.GetProperty("release").GetProperty("version").GetInt32());
        var custom = registry.GetProperty("items").EnumerateArray().Where(x =>
            x.GetProperty("key").GetString() == "customization.custom_field.admin").ToArray();
        Assert.Equal(releaseVersion == 3 ? 1 : 0, custom.Length);
        if (custom.Length != 0) Assert.Equal("Tenant", custom[0].GetProperty("scope").GetString());
    }
    private static string AuthoringBoundaryBody()
    {
        var choices = Enumerable.Range(0, 20).Select(n => n.ToString("D2", System.Globalization.CultureInfo.InvariantCulture) + new string('x', 87)).ToArray();
        var schema = JsonSerializer.SerializeToElement(new Dictionary<string, object>
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["type"] = "object",
            ["properties"] = new { value = new { type = "string", minLength = 1, maxLength = 100, @enum = choices } },
            ["required"] = CustomFieldEnvelopeRequired,
            ["additionalProperties"] = false,
        });
        var labels = Enumerable.Range(0, 20).Select(_ => new Dictionary<string, string> { { "en", "x" } }).ToArray();
        string Body() => JsonSerializer.Serialize(new
        {
            targetEntity = "Membership",
            key = "http-boundary",
            schemaVersion = 1,
            schema,
            labels = new Dictionary<string, string> { { "en", "Boundary field" } },
            piiCategory = "None",
            isRequired = false,
            sort = 0,
            purposeKey = (string?)null,
            notices = (object?)null,
            formLabels = new[] { new { location = "/", labels = (object?)null, choices = labels } },
        });
        var width = 1;
        for (; width <= 128; width++)
        {
            foreach (var label in labels) label["en"] = new string('x', width);
            if (System.Text.Encoding.UTF8.GetByteCount(Body()) > 4096) break;
        }
        Assert.InRange(width, 2, 128);
        foreach (var label in labels) label["en"] = new string('x', width - 1);
        var remainder = 4096 - System.Text.Encoding.UTF8.GetByteCount(Body());
        labels[0]["en"] += new string('x', remainder);
        Assert.InRange(labels[0]["en"].Length, 1, 128);
        return Body();
    }

    private static string DefinitionBody(int schemaVersion, string label = "HTTP field")
    {
        using var schema = JsonDocument.Parse("""
            {"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object","properties":{"value":{"type":"boolean"}},"required":["value"],"additionalProperties":false}
            """);
        return JsonSerializer.Serialize(new
        {
            targetEntity = "Membership",
            key = "http-boolean",
            schemaVersion,
            schema = schema.RootElement,
            labels = new Dictionary<string, string> { { "en", label } },
            piiCategory = "None",
            isRequired = false,
            sort = 0,
            purposeKey = (string?)null,
            notices = (object?)null,
            formLabels = (object?)null,
        });
    }
}
