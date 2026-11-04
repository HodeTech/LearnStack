using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Permissions;
using LearnStack.Tools.Seeder;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;
using static LearnStack.Tests.Integration.Database.IdentityAuthorizationHttpProof;

namespace LearnStack.Tests.Integration.Database;

/// <summary>Actual Program, signed provider identity, app-role state and durable HTTP receipts.</summary>
[Collection(PublicReadTestGroup.Name)]
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed partial class BoundedAdministrationHttpTests(PublicReadFixture parent)
{
    private static readonly SeedTenant Tenant = SeedData.English;
    private static readonly string[] KernelAndTenancyKeys =
    ["identity.membership.admin", "identity.role.admin", "tenancy.organization.admin", "tenancy.locale.write", "tenancy.setting.write"];

    [Fact]
    public async Task Actual_selected_writes_preserve_headers_and_replay_original_terminal_target_after_write_grant_loss()
    {
        await using var proof = await CreatePreparedAsync();
        using var client = proof.Client();
        var token = proof.Token(EnglishAdmin, Tenant);
        var learner = SeedData.IdentityMemberships[1].MembershipId.ToString("D");
        var role = SeedData.IdentityRoles.Single(value => value.Tenant == Tenant
            && value.BuiltInKey == BuiltInRoleKey.TenantAdmin).RoleId.ToString("D");
        using (var before = await SendAsync(client, token, "/api/v1/identity/me/capabilities"))
            Assert.False((await JsonAsync(before)).GetProperty("canManageOrganizations").GetBoolean());
        using (var roles = await SendAsync(client, token, "/api/v1/identity/roles"))
            Assert.Equal(8, (await JsonAsync(roles)).GetProperty("items").GetArrayLength());
        JsonElement release;
        using (var registry = await SendAsync(client, token, "/api/v1/identity/permissions"))
        {
            var body = await JsonAsync(registry);
            Assert.Equal(8, body.GetProperty("items").GetArrayLength());
            release = body.GetProperty("release");
        }
        var roleVersion = await ResourceVersionAsync(client, token, "/api/v1/identity/roles/" + role);
        var adoptionBody = JsonSerializer.Serialize(new
        {
            manifestVersion = release.GetProperty("version").GetInt32(),
            manifestFingerprint = release.GetProperty("fingerprint").GetString()
        });
        var adoptionKey = Key();
        var adopted = await MutateAsync(client, token, "POST", "/api/v1/identity/roles/" + role + "/manifest-adoptions",
            adoptionBody, roleVersion, adoptionKey);
        using (var after = await SendAsync(client, token, "/api/v1/identity/me/capabilities"))
            Assert.True((await JsonAsync(after)).GetProperty("canManageOrganizations").GetBoolean());

        var createKey = Key();
        using var created = await SendAsync(client, token, "/api/v1/tenancy/organizations", "POST",
            "{\"slug\":\"http-created\",\"displayName\":\"HTTP Studio\"}", key: createKey);
        var createReceipt = await JsonAsync(created, HttpStatusCode.Created);
        AssertVersionHeader(created, createReceipt);
        var location = Assert.IsType<Uri>(created.Headers.Location).OriginalString;
        Assert.StartsWith("/api/v1/tenancy/organizations/", location, StringComparison.Ordinal);
        var createdId = location["/api/v1/tenancy/organizations/".Length..];
        Assert.Equal(7, Guid.Parse(createdId).Version);
        var renamed = await MutateAsync(client, token, "PUT", location, "{\"displayName\":\"Renamed Studio\"}",
            createReceipt.GetProperty("version").GetString()!, Key());
        using (var page = await SendAsync(client, token, "/api/v1/tenancy/organizations"))
        {
            var directory = await JsonAsync(page);
            var tenantVersion = directory.GetProperty("tenantVersion").GetString()!;
            await MutateAsync(client, token, "PUT", "/api/v1/tenancy/default-organization",
                JsonSerializer.Serialize(new { organizationId = Tenant.SecondOrganization.OrganizationId.Value }), tenantVersion, Key());
        }
        await MutateAsync(client, token, "POST", location + "/archive", "{}", renamed, Key());

        var memberPath = "/api/v1/identity/memberships/" + learner;
        var memberVersion = await ResourceVersionAsync(client, token, memberPath);
        const string ProfileBody = "{\"displayName\":\"HTTP learner\",\"preferredLocale\":\"en\"}";
        var profileKey = Key();
        var updatedProfile = await MutateAsync(client, token, "PUT", memberPath + "/profile", ProfileBody, memberVersion, profileKey);
        var ownVersion = await ResourceVersionAsync(client, token, "/api/v1/identity/me/profile");
        await MutateAsync(client, token, "PUT", "/api/v1/identity/me/profile",
            "{\"displayName\":\"HTTP admin\",\"preferredLocale\":\"en\"}", ownVersion, Key());
        var learnerRole = SeedData.IdentityRoles.Single(value => value.Tenant == Tenant
            && value.BuiltInKey == BuiltInRoleKey.Learner && value.Scope == PermissionScope.Tenant).RoleId;
        var assigned = await MutateAsync(client, token, "PUT", memberPath + "/role-assignments",
            JsonSerializer.Serialize(new { roleIds = new[] { learnerRole } }), updatedProfile, Key());
        var suspended = await MutateAsync(client, token, "PUT", memberPath + "/status", "{\"status\":\"Suspended\"}", assigned, Key());
        var active = await MutateAsync(client, token, "PUT", memberPath + "/status", "{\"status\":\"Active\"}", suspended, Key());
        await MutateAsync(client, token, "PUT", memberPath + "/status", "{\"status\":\"Revoked\"}", active, Key());

        // Remove only a nonkernel write grant. Current disclosure remains permitted.
        using (var current = await SendAsync(client, token, "/api/v1/identity/roles/" + role))
        {
            var detail = await JsonAsync(current);
            var retained = detail.GetProperty("grants").EnumerateArray()
                .Where(grant => KernelAndTenancyKeys.Contains(grant.GetProperty("key").GetString(), StringComparer.Ordinal))
                .Select(grant => new { scope = grant.GetProperty("scope").GetString(), key = grant.GetProperty("key").GetString() }).ToArray();
            Assert.Equal(5, retained.Length);
            await MutateAsync(client, token, "PUT", "/api/v1/identity/roles/" + role + "/grants",
                JsonSerializer.Serialize(new { grants = retained }), detail.GetProperty("version").GetString()!, Key());
        }
        using (var hidden = await SendAsync(client, token, memberPath))
            await ProblemAsync(hidden, HttpStatusCode.NotFound, "not_found");
        var beforeReplay = await StoredAsync(proof, profileKey);
        Assert.NotNull(beforeReplay);
        Assert.Equal("completed", beforeReplay.State);
        Assert.Equal("Membership", beforeReplay.Kind);
        Assert.Equal(Guid.Parse(learner), beforeReplay.Target);
        using (var replay = await SendAsync(client, token, memberPath + "/profile", "PUT", ProfileBody, memberVersion, profileKey))
        {
            var body = await JsonAsync(replay);
            Assert.Equal(updatedProfile, body.GetProperty("version").GetString());
            Assert.Equal("true", Assert.Single(replay.Headers.GetValues("Idempotency-Replayed")));
            AssertVersionHeader(replay, body);
            var audit = Assert.Single(await proof.AuditAsync(replay, Tenant));
            Assert.Equal("identity.membership_profile.read_receipt", audit.Operation);
            Assert.Equal("success", audit.Outcome);
        }
        Assert.Equal(beforeReplay, await StoredAsync(proof, profileKey));
        var refusedKey = Key();
        using (var refused = await SendAsync(client, token, memberPath + "/profile", "PUT", ProfileBody, memberVersion, refusedKey))
            await ProblemAsync(refused, HttpStatusCode.Forbidden, "forbidden");
        Assert.Null(await StoredAsync(proof, refusedKey));

        // A consumed first-adoption marker and stale original precondition do not
        // rerun adoption; the successful historical Role receipt is disclosed.
        using (var replay = await SendAsync(client, token, "/api/v1/identity/roles/" + role + "/manifest-adoptions", "POST",
            adoptionBody, roleVersion, adoptionKey))
        {
            Assert.Equal(adopted, (await JsonAsync(replay)).GetProperty("version").GetString());
            Assert.Equal("true", Assert.Single(replay.Headers.GetValues("Idempotency-Replayed")));
            Assert.Equal("identity.role.read_receipt", Assert.Single(await proof.AuditAsync(replay, Tenant)).Operation);
        }
    }

    private async Task<IdentityAuthorizationHttpProof> CreatePreparedAsync()
    {
        var proof = await CreateAsync(parent);
        try
        {
            await using var source = ApplicationDataSource.Build(proof.Database.AppConnectionString);
            var runner = new SeedAdministrationOriginRunner(context => SeedComposition.Build(source, context,
                NullLoggerFactory.Instance, identityAuthority: proof.Database.Registration,
                seedOriginAdmission: new FixedSeedAdministrationOriginAdmission()));
            Assert.Equal(2, await runner.RunAsync(SeedData.All.Select(tenant => tenant.TenantId)));
            return proof;
        }
        catch { await proof.DisposeAsync(); throw; }
    }

    private static string Key() => "http-admin-" + Guid.NewGuid().ToString("N");

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string? token, string path,
        string method = "GET", string? body = null, string? version = null, string? key = null,
        string? host = null, string contentType = "application/json")
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Host = host ?? Tenant.Host;
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (version is not null) request.Headers.TryAddWithoutValidation("If-Match", "\"" + version + "\"");
        if (key is not null) request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, contentType);
        return await client.SendAsync(request);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        Assert.Equal(expected, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Null(response.Content.Headers.LastModified);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static void AssertVersionHeader(HttpResponseMessage response, JsonElement body)
    {
        var value = body.GetProperty("version");
        Assert.Equal(JsonValueKind.String, value.ValueKind);
        Assert.True(long.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0);
        Assert.Equal("\"" + value.GetString() + "\"", Assert.IsType<EntityTagHeaderValue>(response.Headers.ETag).ToString());
    }

    private static async Task<string> ResourceVersionAsync(HttpClient client, string token, string path)
    {
        using var response = await SendAsync(client, token, path);
        var body = await JsonAsync(response);
        AssertVersionHeader(response, body);
        return body.GetProperty("version").GetString()!;
    }

    private static async Task<string> MutateAsync(HttpClient client, string token, string method, string path,
        string body, string version, string key)
    {
        using var response = await SendAsync(client, token, path, method, body, version, key);
        var receipt = await JsonAsync(response);
        AssertVersionHeader(response, receipt);
        Assert.Single(receipt.EnumerateObject());
        return receipt.GetProperty("version").GetString()!;
    }

    private sealed record StoredOutcome(string State, DateTimeOffset ExpiresAt, string? Kind, Guid? Target);
    private static async Task<StoredOutcome?> StoredAsync(IdentityAuthorizationHttpProof proof, string key)
    {
        await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(proof.Database.AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(connection);
        await using var transaction = await connection.BeginTransactionAsync();
        await SchemaQueries.SetTenantAsync(connection, transaction, Tenant.TenantId.Value);
        await using var command = new NpgsqlCommand("""
            SELECT state,expires_at,replay_target_kind,replay_target_id FROM public.idempotency_keys WHERE key=@key
            """, connection, transaction);
        command.Parameters.AddWithValue("key", key);
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? new(reader.GetString(0), reader.GetFieldValue<DateTimeOffset>(1),
            reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetGuid(3)) : null;
    }
}
