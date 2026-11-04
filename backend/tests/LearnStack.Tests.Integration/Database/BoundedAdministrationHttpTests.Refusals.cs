using System.Net;
using LearnStack.Tools.Seeder;
using Xunit;
using static LearnStack.Tests.Integration.Database.IdentityAuthorizationHttpProof;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BoundedAdministrationHttpTests
{
    [Fact]
    public async Task Every_new_action_masks_missing_credentials_and_challenges_invalid_credentials_before_dispatch()
    {
        await using var proof = await CreatePreparedAsync();
        using var client = proof.Client();
        var member = SeedData.IdentityMemberships[1].MembershipId.ToString("D");
        var role = SeedData.IdentityRoles[0].RoleId.ToString("D");
        var organization = Tenant.DefaultOrganization.OrganizationId.Value.ToString("D");
        foreach (var (method, path) in new (string, string)[]
        {
            ("GET", "identity/organization-memberships"), ("GET", "identity/memberships/" + member),
            ("PUT", "identity/memberships/" + member + "/profile"),
            ("PUT", "identity/memberships/" + member + "/role-assignments"),
            ("PUT", "identity/memberships/" + member + "/status"),
            ("GET", "identity/me/profile"), ("PUT", "identity/me/profile"),
            ("GET", "identity/me/contexts"), ("GET", "identity/me/capabilities"),
            ("GET", "identity/roles"), ("GET", "identity/roles/" + role),
            ("GET", "identity/permissions"), ("PUT", "identity/roles/" + role + "/grants"),
            ("POST", "identity/roles/" + role + "/manifest-adoptions"),
            ("GET", "tenancy/organizations"), ("GET", "tenancy/organizations/" + organization),
            ("POST", "tenancy/organizations"), ("PUT", "tenancy/organizations/" + organization),
            ("POST", "tenancy/organizations/" + organization + "/archive"), ("PUT", "tenancy/default-organization"),
        })
        {
            using var missing = await SendAsync(client, null, "/api/v1/" + path, method);
            await ProblemAsync(missing, HttpStatusCode.NotFound, "not_found");
            Assert.Empty(await proof.AuditAsync(missing, Tenant));
            using var invalid = await SendAsync(client, "not-a-token", "/api/v1/" + path, method);
            await ProblemAsync(invalid, HttpStatusCode.Unauthorized, "unauthorized");
            Assert.Equal("Bearer", Assert.Single(invalid.Headers.WwwAuthenticate).Scheme);
        }
        Assert.Equal(0, proof.Observation.BindingReads);
        Assert.Equal(0, proof.Observation.AuthorizationReads);
    }

    [Fact]
    public async Task Malformed_writes_and_stale_preconditions_leave_the_original_resource_unchanged()
    {
        await using var proof = await CreatePreparedAsync();
        using var client = proof.Client();
        var token = proof.Token(EnglishAdmin, Tenant);
        var path = "/api/v1/identity/memberships/" + SeedData.IdentityMemberships[1].MembershipId.ToString("D");
        var version = await ResourceVersionAsync(client, token, path);
        const string Valid = "{\"displayName\":\"Changed\",\"preferredLocale\":\"en\"}";
        foreach (var body in new[]
        {
            "{}", "{\"displayName\":null}",
            "{\"displayName\":null,\"displayName\":\"duplicate\",\"preferredLocale\":null}",
            "{\"displayName\":null,\"preferredLocale\":null,\"userId\":\"injected\"}",
            "{\"displayName\":[],\"preferredLocale\":null}", "[]", "{",
        })
        {
            using var response = await SendAsync(client, token, path + "/profile", "PUT", body, version, Key());
            await ProblemAsync(response, HttpStatusCode.BadRequest, "validation_failed");
        }
        using (var missingKey = await SendAsync(client, token, path + "/profile", "PUT", Valid, version))
            await ProblemAsync(missingKey, HttpStatusCode.BadRequest, "validation_failed");
        using (var missingVersion = await SendAsync(client, token, path + "/profile", "PUT", Valid, key: Key()))
            await ProblemAsync(missingVersion, HttpStatusCode.BadRequest, "validation_failed");
        foreach (var invalid in new[] { "01", "-1", "*", "9223372036854775808", "1,2" })
        {
            var key = Key();
            using var response = await SendAsync(client, token, path + "/profile", "PUT", Valid, invalid, key);
            await ProblemAsync(response, HttpStatusCode.BadRequest, "validation_failed");
            Assert.Null(await StoredAsync(proof, key));
        }
        using (var stale = await SendAsync(client, token, path + "/profile", "PUT", Valid, "9223372036854775807", Key()))
            await ProblemAsync(stale, HttpStatusCode.Conflict, "concurrency_conflict");
        using (var oversized = await SendAsync(client, token, path + "/profile", "PUT", new string(' ', 16385), version, Key()))
            await ProblemAsync(oversized, HttpStatusCode.RequestEntityTooLarge, "payload_too_large");
        using (var wrongType = await SendAsync(client, token, path + "/profile", "PUT", Valid, version, Key(), contentType: "text/plain"))
            await ProblemAsync(wrongType, HttpStatusCode.BadRequest, "validation_failed");
        Assert.Equal(version, await ResourceVersionAsync(client, token, path));
    }

    [Fact]
    public async Task Own_navigation_is_Off_and_other_person_or_configuration_reads_require_current_authority_and_MUST_evidence()
    {
        await using var proof = await CreatePreparedAsync();
        using var client = proof.Client();
        var admin = proof.Token(EnglishAdmin, Tenant);
        var learner = proof.Token(EnglishLearner, Tenant);
        using (var preview = await SendAsync(client, learner, "/api/v1/identity/me/capabilities"))
        {
            var body = await JsonAsync(preview);
            Assert.Equal(9, body.EnumerateObject().Count());
            Assert.False(body.GetProperty("canReadRoles").GetBoolean());
            Assert.Null(preview.Headers.ETag);
            Assert.Empty(await proof.AuditAsync(preview, Tenant));
        }
        using (var contexts = await SendAsync(client, learner, "/api/v1/identity/me/contexts?limit=1"))
        {
            Assert.Single((await JsonAsync(contexts)).GetProperty("items").EnumerateArray());
            Assert.Null(contexts.Headers.ETag);
            Assert.Empty(await proof.AuditAsync(contexts, Tenant));
        }
        using (var forbidden = await SendAsync(client, learner, "/api/v1/identity/roles"))
            await ProblemAsync(forbidden, HttpStatusCode.Forbidden, "forbidden");
        foreach (var path in new[] { "/api/v1/identity/roles", "/api/v1/identity/permissions" })
        {
            using var response = await SendAsync(client, admin, path);
            _ = await JsonAsync(response);
            Assert.Equal("success", Assert.Single(await proof.AuditAsync(response, Tenant)).Outcome);
        }
        var foreign = SeedData.IdentityMemberships.Single(member => member.Identity.UserId == YogaAdmin).MembershipId;
        using (var response = await SendAsync(client, admin, "/api/v1/identity/memberships/" + foreign.ToString("D")))
            await ProblemAsync(response, HttpStatusCode.NotFound, "not_found");
        using (var badQuery = await SendAsync(client, admin, "/api/v1/identity/roles?tenantId=foreign"))
            await ProblemAsync(badQuery, HttpStatusCode.BadRequest, "validation_failed");
        using (var bodyOnRead = await SendAsync(client, admin, "/api/v1/identity/roles", body: "{}"))
            await ProblemAsync(bodyOnRead, HttpStatusCode.BadRequest, "validation_failed");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_read_withholds_HTTP_success_when_MUST_audit_INSERT_or_constraint_commit_refuses(bool deferred)
    {
        await using var proof = await CreatePreparedAsync();
        await proof.OwnerAsync("""
            CREATE FUNCTION bounded_http_audit_refusal() RETURNS trigger LANGUAGE plpgsql SECURITY INVOKER SET search_path=pg_catalog AS $$
            BEGIN
                IF NEW.operation='identity.role.list' AND NEW.outcome='success' THEN
                    RAISE EXCEPTION 'fixed HTTP audit refusal' USING ERRCODE='23514';
                END IF;
                RETURN NEW;
            END $$;
            """
            + (deferred ? "CREATE CONSTRAINT TRIGGER tg_bounded_http_audit_refusal AFTER INSERT ON public.audit_log DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION bounded_http_audit_refusal();"
                : "CREATE TRIGGER tg_bounded_http_audit_refusal BEFORE INSERT ON public.audit_log FOR EACH ROW EXECUTE FUNCTION bounded_http_audit_refusal();"));
        try
        {
            using var client = proof.Client();
            using var response = await SendAsync(client, proof.Token(EnglishAdmin, Tenant), "/api/v1/identity/roles");
            await ProblemAsync(response, HttpStatusCode.ServiceUnavailable, "audit_unavailable");
            Assert.Null(response.Headers.ETag);
            Assert.DoesNotContain(await proof.AuditAsync(response, Tenant), row => row.Outcome == "success");
        }
        finally
        {
            await proof.OwnerAsync("DROP TRIGGER tg_bounded_http_audit_refusal ON public.audit_log; DROP FUNCTION bounded_http_audit_refusal()");
        }
    }
}
