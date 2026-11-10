using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LearnStack.Api.Authentication;
using LearnStack.Api.Tenancy;
using LearnStack.Modules.Tenancy.Application.Contracts.FieldConsents;
using LearnStack.Tools.Seeder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using static LearnStack.Tests.Integration.Database.IdentityAuthorizationHttpProof;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BoundedAdministrationHttpTests
{
    private const string ConsentApi = "/api/v1/tenancy/membership-field-consents";
    private static readonly string ConsentTestKey = Convert.ToBase64String(Enumerable.Range(1, 32).Select(x => (byte)x).ToArray());

    [Fact]
    public async Task Real_native_consent_entry_discovers_history_withdraws_and_replays_original_receipt()
    {
        await using var proof = await CreatePreparedAsync();
        var definition = await SeedConsentDefinitionAsync(proof);
        var membership = SeedData.IdentityMemberships[1].MembershipId;
        var grantKey = Key();
        var grant = JsonSerializer.Serialize(new
        {
            definitionId = definition,
            definitionVersion = 1,
            noticeLocale = "en",
            expectedNoticeHash = MembershipFieldConsentNotice.ComputeHash("Consent fixture notice"),
            expectedSequence = 0,
            expectedEvidenceId = (Guid?)null,
            confirmed = true
        });
        using var client = ConsentClient(proof);
        var ordinary = proof.Token(EnglishLearner, Tenant);
        JsonElement granted;
        using (var response = await SendAsync(client, ordinary, ConsentApi + "/grants", "POST", grant, key: grantKey))
            granted = await JsonAsync(response);
        var grantId = granted.GetProperty("evidenceId").GetString()!;
        Assert.True(granted.GetProperty("isGranted").GetBoolean());
        using (var response = await SendAsync(client, ordinary, ConsentApi + "/own?membershipId=" + membership.ToString("D")
            + "&definitionId=" + definition.ToString("D") + "&definitionVersion=1"))
            Assert.Equal(grantId, (await JsonAsync(response)).GetProperty("consent").GetProperty("evidenceId").GetString());
        using (var replay = await SendAsync(client, ordinary, ConsentApi + "/grants", "POST", grant, key: grantKey))
        {
            Assert.Equal(grantId, (await JsonAsync(replay)).GetProperty("evidenceId").GetString());
            Assert.Equal("true", Assert.Single(replay.Headers.GetValues("Idempotency-Replayed")));
            Assert.Equal("tenancy.membership_field_consent.read_grant_receipt", Assert.Single(await proof.AuditAsync(replay, Tenant)).Operation);
        }
        var native = EntryToken(proof, EnglishLearner);
        using (var scopes = await ConsentEntrySendAsync(client, native, "/own/scopes"))
            Assert.Contains((await JsonAsync(scopes)).GetProperty("items").EnumerateArray(), item => item.GetProperty("membershipId").GetString() == membership.ToString("D"));
        using (var history = await ConsentEntrySendAsync(client, native, "/own/history?membershipId=" + membership.ToString("D")))
            Assert.Contains((await JsonAsync(history)).GetProperty("items").EnumerateArray(), item => item.GetProperty("evidenceId").GetString() == grantId);
        var administrator = proof.Token(EnglishAdmin, Tenant);
        var memberPath = "/api/v1/identity/memberships/" + membership.ToString("D");
        var version = await ResourceVersionAsync(client, administrator, memberPath);
        await MutateAsync(client, administrator, "PUT", memberPath + "/status", "{\"status\":\"Suspended\"}", version, Key());
        var body = JsonSerializer.Serialize(new { evidenceId = grantId, expectedSequence = 1, expectedEvidenceId = grantId, confirmed = true });
        var withdrawalKey = Key();
        JsonElement withdrawn;
        var path = "/withdrawals?membershipId=" + membership.ToString("D");
        using (var response = await ConsentEntrySendAsync(client, native, path, body, withdrawalKey)) withdrawn = await JsonAsync(response);
        var withdrawnId = withdrawn.GetProperty("evidenceId").GetString()!;
        Assert.False(withdrawn.GetProperty("isGranted").GetBoolean());
        Assert.Equal("2", withdrawn.GetProperty("sequence").GetString());
        using (var replay = await ConsentEntrySendAsync(client, native, path, body, withdrawalKey))
        {
            Assert.Equal(withdrawnId, (await JsonAsync(replay)).GetProperty("evidenceId").GetString());
            Assert.Equal("true", Assert.Single(replay.Headers.GetValues("Idempotency-Replayed")));
            Assert.Equal("tenancy.membership_field_consent.read_withdraw_receipt", Assert.Single(await proof.AuditAsync(replay, Tenant)).Operation);
        }
        using (var rotated = ConsentClient(proof, "next"))
        using (var rotatedReplay = await ConsentEntrySendAsync(rotated, native, path, body, withdrawalKey))
        {
            Assert.Equal(withdrawnId, (await JsonAsync(rotatedReplay)).GetProperty("evidenceId").GetString());
            Assert.Equal("true", Assert.Single(rotatedReplay.Headers.GetValues("Idempotency-Replayed")));
        }
        using (var retained = await ConsentEntrySendAsync(client, native, "/own/history?membershipId=" + membership.ToString("D")))
            Assert.Contains((await JsonAsync(retained)).GetProperty("items").EnumerateArray(), item => item.GetProperty("evidenceId").GetString() == withdrawnId);
        using (var replayAfterSuspension = await ConsentEntrySendAsync(client, native, path, body, withdrawalKey))
            Assert.Equal(withdrawnId, (await JsonAsync(replayAfterSuspension)).GetProperty("evidenceId").GetString());
        using (var newGrant = await SendAsync(client, ordinary, ConsentApi + "/grants", "POST", grant, key: Key()))
            Assert.Equal(HttpStatusCode.NotFound, newGrant.StatusCode);
        using var other = await ConsentEntrySendAsync(client, EntryToken(proof, EnglishAdmin), path, body, withdrawalKey);
        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
        Assert.DoesNotContain(withdrawnId, await other.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    private static HttpClient ConsentClient(IdentityAuthorizationHttpProof proof, string currentKey = "test") => proof.Database.ClientFor(Tenant.Host,
        services => services.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter, EntryLoopbackPeer>(), builder =>
        {
            builder.UseSetting("Tenancy:TrustedHop:Networks:0", "127.0.0.1/32");
            builder.UseSetting("Tenancy:TrustedHop:Secrets:0", EntryHop);
            builder.UseSetting("Identity:NativeSession:CurrentSecret", EntryNative);
            builder.UseSetting("Idempotency:CustomFields:CurrentKeyId", currentKey);
            builder.UseSetting("Idempotency:CustomFields:Keys:test", ConsentTestKey);
            builder.UseSetting("Idempotency:CustomFields:Keys:next", Convert.ToBase64String(Enumerable.Range(33, 32).Select(x => (byte)x).ToArray()));
        });

    private static async Task<HttpResponseMessage> ConsentEntrySendAsync(HttpClient client, string token, string path,
        string? body = null, string? key = null)
    {
        using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, ConsentApi + path);
        request.Headers.Host = Tenant.Host;
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add(TrustedHopOptions.HostHeaderName, Tenant.Host);
        request.Headers.Add(TrustedHopOptions.SecretHeaderName, EntryHop);
        request.Headers.Add("X-LearnStack-Visitor-Address", "198.51.100.24");
        request.Headers.Add(NativeSessionSourcePolicy.SecretHeader, EntryNative);
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await client.SendAsync(request);
    }

    private static async Task<Guid> SeedConsentDefinitionAsync(IdentityAuthorizationHttpProof proof)
    {
        var definition = Guid.CreateVersion7();
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(proof.Database.AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(app);
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, Tenant.TenantId.Value, null);
        await LockAsync(app, transaction, Tenant.TenantId.Value, null, true);
        await ExecuteAsync(app, transaction, """
            INSERT INTO public.tenant_custom_field_defs
              (id,tenant_id,target_entity,key,schema_version,status,json_schema,labels_json,pii_category,is_required,sort,
               maximum_value_bytes,purpose_key,notices_json,created_at,created_by,row_version)
            VALUES(@id,@tenant,'Membership','consent-http-proof',1,'Draft',
              '{"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object","properties":{"value":{"type":"boolean"}},"required":["value"],"additionalProperties":false}',
              '{"en":"Consent fixture"}','Sensitive',false,0,5,'fixture-purpose',
              '{"en":"Consent fixture notice"}',clock_timestamp(),@actor,0);
            UPDATE public.tenant_custom_field_defs SET status='Active',row_version=1 WHERE id=@id;
            """, ("id", definition), ("tenant", Tenant.TenantId.Value), ("actor", EnglishAdmin.Value));
        await transaction.CommitAsync();
        return definition;
    }
}
