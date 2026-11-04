using LearnStack.SharedKernel.Idempotency;
using LearnStack.SharedKernel.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LearnStack.SharedKernel.Permissions;
using LearnStack.Tools.Seeder;
using Npgsql;
using Xunit;
using static LearnStack.Tests.Integration.Database.IdentityAuthorizationHttpProof;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BoundedAdministrationHttpTests
{
    [Fact]
    public async Task Actual_invitation_writers_stage_once_and_preserve_original_receipts_and_conditional_policy_presence()
    {
        await using var proof = await CreatePreparedAsync();
        using var client = proof.Client();
        var token = proof.Token(EnglishAdmin, Tenant);
        using (var policy = await SendAsync(client, token, "/api/v1/tenancy/settings/invitation-policy"))
        {
            var value = await JsonAsync(policy);
            Assert.False(value.GetProperty("present").GetBoolean()); Assert.Equal(14, value.GetProperty("days").GetInt32());
            Assert.False(value.GetProperty("canEdit").GetBoolean()); Assert.Null(policy.Headers.ETag);
        }
        using var choices = await SendAsync(client, token, "/api/v1/identity/invitable-roles");
        var role = (await JsonAsync(choices)).GetProperty("items").EnumerateArray().Single(row => row.GetProperty("builtInKey").GetString() == "Learner");
        var createBody = JsonSerializer.Serialize(new { email = "writer@example.test", roleId = role.GetProperty("roleId").GetString(), roleVersion = role.GetProperty("version").GetString() });
        var createKey = Key();
        using var created = await SendAsync(client, token, "/api/v1/identity/invitations", "POST", createBody, key: createKey);
        var receipt = await JsonAsync(created, HttpStatusCode.Created);
        AssertVersionHeader(created, receipt);
        var location = created.Headers.Location!.OriginalString;
        Assert.StartsWith("/api/v1/identity/invitations/", location, StringComparison.Ordinal);
        Assert.Equal("Invitation", (await StoredAsync(proof, createKey))!.Kind);
        using (var replay = await SendAsync(client, token, "/api/v1/identity/invitations", "POST", createBody, key: createKey))
        {
            Assert.Equal(receipt.GetRawText(), (await JsonAsync(replay, HttpStatusCode.Created)).GetRawText());
            Assert.Equal("true", Assert.Single(replay.Headers.GetValues("Idempotency-Replayed")));
            Assert.Equal("identity.invitation.read_receipt", Assert.Single(await proof.AuditAsync(replay, Tenant)).Operation);
        }
        using (var duplicate = await SendAsync(client, token, "/api/v1/identity/invitations", "POST", createBody, key: Key()))
            await ProblemAsync(duplicate, HttpStatusCode.Conflict, "concurrency_conflict");
        string originalExpiry;
        using (var detail = await SendAsync(client, token, location))
        {
            var value = await JsonAsync(detail);
            Assert.Equal("writer@example.test", value.GetProperty("email").GetString());
            Assert.Equal("Queued", value.GetProperty("deliveryStatus").GetString());
            Assert.Equal("Pending", value.GetProperty("status").GetString());
            Assert.Equal(JsonValueKind.Null, value.GetProperty("acceptanceDisposition").ValueKind);
            originalExpiry = value.GetProperty("expiresAt").GetString()!;
            Assert.DoesNotContain("verifier", value.GetRawText(), StringComparison.OrdinalIgnoreCase);
        }
        var first = receipt.GetProperty("version").GetString()!;
        var reissueKey = Key();
        var reissued = await MutateAsync(client, token, "POST", location + "/reissues", "{}", first, reissueKey);
        using (var replay = await SendAsync(client, token, location + "/reissues", "POST", "{}", first, reissueKey))
            Assert.Equal(reissued, (await JsonAsync(replay)).GetProperty("version").GetString());
        await MutateAsync(client, token, "POST", location + "/revocations", "{}", reissued, Key());
        using (var beforePolicy = await SendAsync(client, token, location))
            originalExpiry = (await JsonAsync(beforePolicy)).GetProperty("expiresAt").GetString()!;
        using (var old = await SendAsync(client, token, "/api/v1/identity/invitations", "POST", createBody, key: createKey))
            Assert.Equal(receipt.GetRawText(), (await JsonAsync(old, HttpStatusCode.Created)).GetRawText());
        // Actual production scheduler/envelope/root cardinalities on an app-role scoped connection.
        await using (var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(proof.Database.AppConnectionString))
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await IdentityMembershipFixture.ScopeAsync(app, transaction, Tenant.TenantId.Value, null);
            await using var count = new NpgsqlCommand("SELECT count(*) FROM background_jobs WHERE definition_key='identity.invitation_email_tenant.v1'", app, transaction);
            Assert.Equal(2L, await count.ExecuteScalarAsync());
        }
        using (var v1 = await PolicyCreateAsync(client, token, 7, Key())) Assert.Equal(HttpStatusCode.Forbidden, v1.StatusCode);
        // Explicit existing v2 adoption authorizes only this additional fixed setting.
        using var registry = await SendAsync(client, token, "/api/v1/identity/permissions");
        var release = (await JsonAsync(registry)).GetProperty("release");
        var admin = SeedData.IdentityRoles.Single(row => row.Tenant == Tenant && row.BuiltInKey == BuiltInRoleKey.TenantAdmin);
        var adminPath = "/api/v1/identity/roles/" + admin.RoleId.ToString("D");
        await MutateAsync(client, token, "POST", adminPath + "/manifest-adoptions", JsonSerializer.Serialize(new
        {
            manifestVersion = release.GetProperty("version").GetInt32(),
            manifestFingerprint = release.GetProperty("fingerprint").GetString()
        }), await ResourceVersionAsync(client, token, adminPath), Key());
        var policyKey = Key();
        using (var set = await PolicyCreateAsync(client, token, 7, policyKey))
        {
            var value = await JsonAsync(set); Assert.Equal("0", value.GetProperty("version").GetString());
            Assert.Equal("TenantSetting", (await StoredAsync(proof, policyKey))!.Kind);
        }
        using (var staleAbsent = await PolicyCreateAsync(client, token, 8, Key()))
            await ProblemAsync(staleAbsent, HttpStatusCode.Conflict, "concurrency_conflict");
        await MutateAsync(client, token, "PUT", "/api/v1/tenancy/settings/invitation-policy", "{\"days\":9}", "0", Key());
        using (var policy = await SendAsync(client, token, "/api/v1/tenancy/settings/invitation-policy"))
        {
            var value = await JsonAsync(policy); Assert.True(value.GetProperty("present").GetBoolean());
            Assert.True(value.GetProperty("canEdit").GetBoolean()); Assert.Equal(9, value.GetProperty("days").GetInt32());
        }
        using (var frozen = await SendAsync(client, token, location))
            Assert.Equal(originalExpiry, (await JsonAsync(frozen)).GetProperty("expiresAt").GetString());
        using (var future = await SendAsync(client, token, "/api/v1/identity/invitations", "POST", JsonSerializer.Serialize(new
        {
            email = "policy-generation@example.test",
            roleId = role.GetProperty("roleId").GetString(),
            roleVersion = role.GetProperty("version").GetString()
        }), key: Key()))
        {
            await JsonAsync(future, HttpStatusCode.Created);
            using var next = await SendAsync(client, token, future.Headers.Location!.OriginalString);
            var expires = DateTimeOffset.Parse((await JsonAsync(next)).GetProperty("expiresAt").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
            Assert.InRange((expires - DateTimeOffset.UtcNow).TotalDays, 8.9, 9.1);
        }
        using (var replay = await PolicyCreateAsync(client, token, 7, policyKey))
        {
            Assert.Equal("0", (await JsonAsync(replay)).GetProperty("version").GetString());
            Assert.Equal("tenancy.setting.read_receipt", Assert.Single(await proof.AuditAsync(replay, Tenant)).Operation);
        }
        using (var adminDetail = await SendAsync(client, token, adminPath))
        {
            var value = await JsonAsync(adminDetail);
            var retained = value.GetProperty("grants").EnumerateArray().Where(grant => grant.GetProperty("key").GetString() != "tenancy.setting.write")
                .Select(grant => new { scope = grant.GetProperty("scope").GetString(), key = grant.GetProperty("key").GetString() }).ToArray();
            await MutateAsync(client, token, "PUT", adminPath + "/grants", JsonSerializer.Serialize(new { grants = retained }),
                value.GetProperty("version").GetString()!, Key());
        }
        using (var replay = await PolicyCreateAsync(client, token, 7, policyKey))
        {
            Assert.Equal("0", (await JsonAsync(replay)).GetProperty("version").GetString());
            Assert.Equal("tenancy.setting.read_receipt", Assert.Single(await proof.AuditAsync(replay, Tenant)).Operation);
        }
        using (var forbidden = await SendAsync(client, token, "/api/v1/tenancy/settings/invitation-policy", "PUT", "{\"days\":8}", "1", Key()))
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        using (var own = await SendAsync(client, token, "/api/v1/tenancy/settings/invitation-policy"))
            Assert.False((await JsonAsync(own)).GetProperty("canEdit").GetBoolean());
    }

    [Fact]
    public async Task Actual_SQL_status_failure_withholds_DTO_and_audit_success()
    {
        await using var proof = await CreatePreparedAsync();
        using var client = proof.Client(); var token = proof.Token(EnglishAdmin, Tenant);
        using var choices = await SendAsync(client, token, "/api/v1/identity/invitable-roles");
        var role = (await JsonAsync(choices)).GetProperty("items")[0];
        using var created = await SendAsync(client, token, "/api/v1/identity/invitations", "POST", JsonSerializer.Serialize(new
        {
            email = "status@example.test",
            roleId = role.GetProperty("roleId").GetString(),
            roleVersion = role.GetProperty("version").GetString()
        }), key: Key());
        await JsonAsync(created, HttpStatusCode.Created); var location = created.Headers.Location!.OriginalString;
        await proof.OwnerAsync("REVOKE SELECT ON public.background_jobs FROM learnstack_app");
        try
        {
            using var failed = await SendAsync(client, token, location);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
            Assert.DoesNotContain("status@example.test", await failed.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            Assert.DoesNotContain(await proof.AuditAsync(failed, Tenant), row => row.Operation == "identity.invitation.read" && row.Outcome == "success");
        }
        finally { await proof.OwnerAsync("GRANT SELECT ON public.background_jobs TO learnstack_app"); }
        using (var recovered = await SendAsync(client, token, location)) await JsonAsync(recovered);
        await using var blocker = (NpgsqlConnection)await PostgresFixture.OpenAsync(proof.Database.MigrationConnectionString);
        await using var blocked = await blocker.BeginTransactionAsync();
        await using (var hold = new NpgsqlCommand("LOCK TABLE public.background_jobs IN ACCESS EXCLUSIVE MODE", blocker, blocked)) await hold.ExecuteNonQueryAsync();
        using var timedOut = await SendAsync(client, token, location);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, timedOut.StatusCode);
        Assert.DoesNotContain("status@example.test", await timedOut.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.DoesNotContain(await proof.AuditAsync(timedOut, Tenant), row => row.Operation == "identity.invitation.read" && row.Outcome == "success");
        await blocked.RollbackAsync();
    }
    [Fact]
    public async Task Actual_Create_and_Reissue_MUST_failure_rolls_back_root_envelope_and_produced_job()
    {
        await using var proof = await CreatePreparedAsync();
        var cleanups = new List<bool>();
        using var client = proof.Client(services => services.Replace(ServiceDescriptor.Scoped<IIdempotencyBusinessOwnerCleanup>(provider =>
            new ObservedInvitationCleanup((IIdempotencyBusinessOwnerCleanup)provider.GetRequiredService<IUnitOfWork>(), cleanups))));
        var token = proof.Token(EnglishAdmin, Tenant);
        using var choices = await SendAsync(client, token, "/api/v1/identity/invitable-roles");
        var role = (await JsonAsync(choices)).GetProperty("items")[0];
        string Body(string email) => JsonSerializer.Serialize(new { email, roleId = role.GetProperty("roleId").GetString(), roleVersion = role.GetProperty("version").GetString() });
        using var baseline = await SendAsync(client, token, "/api/v1/identity/invitations", "POST", Body("baseline@example.test"), key: Key());
        var version = (await JsonAsync(baseline, HttpStatusCode.Created)).GetProperty("version").GetString()!;
        var location = baseline.Headers.Location!.OriginalString;
        await proof.OwnerAsync("""
            CREATE FUNCTION invitation_write_audit_refusal() RETURNS trigger LANGUAGE plpgsql SECURITY INVOKER SET search_path=pg_catalog AS $$
            BEGIN
                IF NEW.operation IN ('identity.invitation.create','identity.invitation.reissue') AND NEW.outcome='success' THEN
                    RAISE EXCEPTION 'fixed invitation audit refusal' USING ERRCODE='23514';
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER tg_invitation_write_audit_refusal BEFORE INSERT ON public.audit_log
                FOR EACH ROW EXECUTE FUNCTION invitation_write_audit_refusal();
            """);
        try
        {
            foreach (var reissue in new[] { false, true })
            {
                var key = Key(); var cleanupCount = cleanups.Count;
                using var refused = await SendAsync(client, token, reissue ? location + "/reissues" : "/api/v1/identity/invitations",
                    "POST", reissue ? "{}" : Body("rollback@example.test"), reissue ? version : null, key);
                Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
                var retained = await StoredAsync(proof, key);
                Assert.True(cleanups.Count > cleanupCount);
                Assert.NotNull(retained); Assert.Equal("in_flight", retained.State); Assert.Null(retained.Kind);
                Assert.True(cleanups[^1], "The actual audit-refusal owner positively completed rollback and cleanup.");
                Assert.True(retained.ExpiresAt <= DateTimeOffset.UtcNow);
                Assert.DoesNotContain(await proof.AuditAsync(refused, Tenant), row => row.Outcome == "success"
                    && row.Operation == (reissue ? "identity.invitation.reissue" : "identity.invitation.create"));
            }
        }
        finally { await proof.OwnerAsync("DROP TRIGGER tg_invitation_write_audit_refusal ON public.audit_log; DROP FUNCTION invitation_write_audit_refusal()"); }
        using (var detail = await SendAsync(client, token, location)) Assert.Equal(version, (await JsonAsync(detail)).GetProperty("version").GetString());
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(proof.Database.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await IdentityMembershipFixture.ScopeAsync(app, transaction, Tenant.TenantId.Value, null);
        await using var count = new NpgsqlCommand("SELECT (SELECT count(*) FROM invitations),(SELECT count(*) FROM background_jobs WHERE definition_key='identity.invitation_email_tenant.v1'),EXISTS(SELECT 1 FROM pg_constraint WHERE conrelid='public.invitation_email_envelopes'::regclass AND conname='fk_invitation_email_envelopes_job' AND confrelid='public.background_jobs'::regclass AND contype='f' AND convalidated)", app, transaction);
        await using var row = await count.ExecuteReaderAsync(); Assert.True(await row.ReadAsync()); Assert.Equal(1, row.GetInt64(0)); Assert.Equal(1, row.GetInt64(1)); Assert.True(row.GetBoolean(2));
        // The exact envelope has a mandatory FK to the rolled-back job; orphan custody cannot survive either failed owner.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Scoped_withdrawal_survives_changed_or_deleted_Role_without_restoring_reissue_authority(bool deleted)
    {
        await using var proof = await CreatePreparedAsync();
        using var client = proof.Client(); var token = proof.Token(EnglishAdmin, Tenant);
        using var choices = await SendAsync(client, token, "/api/v1/identity/invitable-roles");
        var role = (await JsonAsync(choices)).GetProperty("items").EnumerateArray().Single(row => row.GetProperty("builtInKey").GetString() == "Instructor");
        var roleId = Guid.Parse(role.GetProperty("roleId").GetString()!);
        using var created = await SendAsync(client, token, "/api/v1/identity/invitations", "POST", JsonSerializer.Serialize(new
        {
            email = "withdraw@example.test",
            roleId,
            roleVersion = role.GetProperty("version").GetString()
        }), key: Key());
        var version = (await JsonAsync(created, HttpStatusCode.Created)).GetProperty("version").GetString()!;
        var location = created.Headers.Location!.OriginalString;
        await using (var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(proof.Database.AppConnectionString))
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await IdentityMembershipFixture.ScopeAsync(app, transaction, Tenant.TenantId.Value, null);
            await IdentityMembershipFixture.LockAsync(app, transaction, Tenant.TenantId.Value, null, true);
            await using var update = new NpgsqlCommand(deleted ? "UPDATE roles SET row_version=row_version+1,deleted_at=clock_timestamp(),deleted_by=@actor WHERE id=@id"
                : "UPDATE roles SET row_version=row_version+1 WHERE id=@id", app, transaction);
            update.Parameters.AddWithValue("id", roleId); if (deleted) update.Parameters.AddWithValue("actor", EnglishAdmin.Value);
            Assert.Equal(1, await update.ExecuteNonQueryAsync()); await transaction.CommitAsync();
        }
        using (var reissue = await SendAsync(client, token, location + "/reissues", "POST", "{}", version, Key()))
            Assert.Equal(HttpStatusCode.Conflict, reissue.StatusCode);
        using (var stale = await SendAsync(client, token, location + "/revocations", "POST", "{}", "99", Key()))
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var learner = proof.Token(EnglishLearner, Tenant);
        using (var forbidden = await SendAsync(client, learner, location + "/revocations", "POST", "{}", version, Key()))
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        await MutateAsync(client, token, "POST", location + "/revocations", "{}", version, Key());
        using var freshChoices = await SendAsync(client, token, "/api/v1/identity/invitable-roles");
        var replacementRole = (await JsonAsync(freshChoices)).GetProperty("items").EnumerateArray().Single(row => row.GetProperty("builtInKey").GetString() == (deleted ? "Guardian" : "Instructor"));
        using var replacement = await SendAsync(client, token, "/api/v1/identity/invitations", "POST", JsonSerializer.Serialize(new
        {
            email = "withdraw@example.test",
            roleId = replacementRole.GetProperty("roleId").GetString(),
            roleVersion = replacementRole.GetProperty("version").GetString()
        }), key: Key());
        await JsonAsync(replacement, HttpStatusCode.Created); Assert.NotEqual(location, replacement.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Organization_admin_cannot_disclose_or_withdraw_same_tenant_NULL_org_offer()
    {
        await using var proof = await CreatePreparedAsync();
        await proof.Database.AddMembershipAsync(Tenant, EnglishLearner, Tenant.DefaultOrganization.OrganizationId, BuiltInRoleKey.OrgAdmin);
        using var client = proof.Client(); var tenantToken = proof.Token(EnglishAdmin, Tenant);
        using var choices = await SendAsync(client, tenantToken, "/api/v1/identity/invitable-roles");
        var role = (await JsonAsync(choices)).GetProperty("items")[0]; var createKey = Key();
        var body = JsonSerializer.Serialize(new { email = "tenantwide@example.test", roleId = role.GetProperty("roleId").GetString(), roleVersion = role.GetProperty("version").GetString() });
        using var created = await SendAsync(client, tenantToken, "/api/v1/identity/invitations", "POST", body, key: createKey);
        var version = (await JsonAsync(created, HttpStatusCode.Created)).GetProperty("version").GetString()!; var location = created.Headers.Location!.OriginalString;
        var orgToken = proof.Token(EnglishLearner, Tenant, organization: true, claimedOrganization: Tenant.DefaultOrganization.OrganizationId);
        using (var list = await SendAsync(client, orgToken, "/api/v1/identity/invitations"))
            Assert.Empty((await JsonAsync(list)).GetProperty("items").EnumerateArray());
        foreach (var suffix in new[] { "", "/revocations", "/reissues" })
        {
            using var refusal = await SendAsync(client, orgToken, location + suffix, suffix == "" ? "GET" : "POST", suffix == "" ? null : "{}", suffix == "" ? null : version, suffix == "" ? null : Key());
            Assert.Equal(HttpStatusCode.NotFound, refusal.StatusCode);
            Assert.DoesNotContain("tenantwide@example.test", await refusal.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        using var replay = await SendAsync(client, orgToken, "/api/v1/identity/invitations", "POST", body, key: createKey);
        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
        Assert.DoesNotContain("tenantwide@example.test", await replay.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        using var branchRoles = await SendAsync(client, orgToken, "/api/v1/identity/invitable-roles");
        var branchRole = (await JsonAsync(branchRoles)).GetProperty("items")[0];
        using var branchCreated = await SendAsync(client, orgToken, "/api/v1/identity/invitations", "POST", JsonSerializer.Serialize(new
        {
            email = "branch@example.test",
            roleId = branchRole.GetProperty("roleId").GetString(),
            roleVersion = branchRole.GetProperty("version").GetString()
        }), key: Key());
        await JsonAsync(branchCreated, HttpStatusCode.Created);
    }

    private sealed class ObservedInvitationCleanup(IIdempotencyBusinessOwnerCleanup actual, List<bool> results) : IIdempotencyBusinessOwnerCleanup
    {
        public async ValueTask<bool> EndBusinessOwnerAsync()
        {
            var result = await actual.EndBusinessOwnerAsync(); results.Add(result); return result;
        }
    }
    private static async Task<HttpResponseMessage> PolicyCreateAsync(HttpClient client, string token, int days, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/v1/tenancy/settings/invitation-policy");
        request.Headers.Host = Tenant.Host; request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation("If-None-Match", "*"); request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        request.Content = new StringContent(JsonSerializer.Serialize(new { days }), Encoding.UTF8, "application/json");
        return await client.SendAsync(request);
    }
}
