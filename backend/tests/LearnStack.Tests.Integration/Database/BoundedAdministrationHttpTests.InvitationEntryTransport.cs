using System.Net;
using System.Text;
using System.Text.Json;
using LearnStack.Api.Authentication;
using LearnStack.Api.Tenancy;
using LearnStack.Tools.Seeder;
using Npgsql;
using Xunit;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;
using static LearnStack.Tests.Integration.Database.IdentityAuthorizationHttpProof;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BoundedAdministrationHttpTests
{
    [Fact]
    public async Task Actual_invitation_entry_refuses_extra_body_query_and_conditional_carriers_before_provider()
    {
        await using var proof = await CreatePreparedAsync();
        var offer = await SeedEntryOfferAsync(proof);
        SetEntryRecipient(proof, YogaAdmin);
        using var client = EntryClient(proof);
        var token = EntryToken(proof, YogaAdmin);
        var valid = JsonSerializer.Serialize(new { invitationId = offer.Id, code = offer.Code });
        var invalidBodies = new[]
        {
            valid[..^1] + ",\"version\":\"0\"}",
            valid[..^1] + ",\"tenantId\":\"" + Tenant.TenantId.Value + "\"}",
            valid[..^1] + ",\"code\":\"" + offer.Code + "\"}",
            valid.Replace("invitationId", "InvitationId", StringComparison.Ordinal),
            "[]", valid + "{}",
            JsonSerializer.Serialize(new { invitationId = offer.Id, code = offer.Code + "=" })
        };
        foreach (var body in invalidBodies)
        {
            using var refused = await EntrySendAsync(client, token, "inspections", offer, mutate: request =>
                request.Content = new StringContent(body, Encoding.UTF8, "application/json"));
            await ProblemAsync(refused, HttpStatusCode.BadRequest, "validation_failed");
        }
        foreach (var header in new[] { "Idempotency-Key", "If-None-Match", "If-Modified-Since", "If-Unmodified-Since" })
        {
            using var refused = await EntrySendAsync(client, token, "inspections", offer,
                mutate: request => request.Headers.TryAddWithoutValidation(header, "opaque"));
            await ProblemAsync(refused, HttpStatusCode.BadRequest, "validation_failed");
        }
        using (var refused = await EntrySendAsync(client, token, "inspections", offer,
            mutate: request => request.RequestUri = new Uri(EntryBase + "inspections?code=opaque", UriKind.Relative)))
            await ProblemAsync(refused, HttpStatusCode.BadRequest, "validation_failed");
        using (var refused = await EntrySendAsync(client, token, "results", offer, "\"0\""))
            await ProblemAsync(refused, HttpStatusCode.BadRequest, "validation_failed");
        Assert.Equal(0, proof.Issuer.UserInfoRequests);
        using var positive = await EntrySendAsync(client, token, "inspections", offer);
        await JsonAsync(positive);
        Assert.Equal(1, proof.Issuer.UserInfoRequests);
        Assert.Null(positive.Headers.Location);
        Assert.False(positive.Headers.Contains("Idempotency-Key"));
        Assert.False(positive.Headers.Contains("Idempotency-Replayed"));
    }

    [Fact]
    public async Task Actual_invitation_entry_requires_native_source_without_membership_selectors()
    {
        await using var proof = await CreatePreparedAsync();
        var offer = await SeedEntryOfferAsync(proof);
        SetEntryRecipient(proof, YogaAdmin);
        using var client = EntryClient(proof);
        var token = EntryToken(proof, YogaAdmin);
        foreach (var (invalid, status) in new (Action<HttpRequestMessage>, HttpStatusCode)[]
        {
            (request => request.Headers.Remove(NativeSessionSourcePolicy.SecretHeader), HttpStatusCode.NotFound),
            (request => { request.Headers.Remove(NativeSessionSourcePolicy.SecretHeader); request.Headers.Add(NativeSessionSourcePolicy.SecretHeader, "bad"); }, HttpStatusCode.NotFound),
            (request => request.Headers.Add(NativeSessionSourcePolicy.TenantHeader, Tenant.TenantId.Value.ToString("D")), HttpStatusCode.BadRequest),
            (request => request.Headers.Add(NativeSessionSourcePolicy.OrganizationHeader, Tenant.SecondOrganization.OrganizationId.Value.ToString("D")), HttpStatusCode.BadRequest)
        })
        {
            using var refused = await EntrySendAsync(client, token, "inspections", offer, mutate: invalid);
            await ProblemAsync(refused, status, status == HttpStatusCode.NotFound ? "not_found" : "validation_failed");
        }
        Assert.Equal(0, proof.Issuer.UserInfoRequests);
        using var positive = await EntrySendAsync(client, token, "inspections", offer);
        await JsonAsync(positive);
    }
    [Fact]
    public async Task Actual_invitation_entry_preserves_versions_above_JavaScript_integer_precision()
    {
        await using var proof = await CreatePreparedAsync();
        var offer = await SeedEntryOfferAsync(proof);
        // Isolated fixture only: reaching this version by incremental production
        // mutations is infeasible. Restore the real guard before any HTTP request.
        await proof.OwnerAsync("ALTER TABLE public.invitations DISABLE TRIGGER tg_invitations_mutation_guard");
        try
        {
            await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(proof.Database.AppConnectionString);
            await using var tx = await app.BeginTransactionAsync();
            await ScopeAsync(app, tx, Tenant.TenantId.Value);
            await LockAsync(app, tx, Tenant.TenantId.Value, null, false);
            await ExecuteAsync(app, tx, "UPDATE invitations SET row_version=9007199254740993 WHERE id=@id", ("id", offer.Id));
            await tx.CommitAsync();
        }
        finally { await proof.OwnerAsync("ALTER TABLE public.invitations ENABLE TRIGGER tg_invitations_mutation_guard"); }
        SetEntryRecipient(proof, YogaAdmin);
        using var client = EntryClient(proof);
        var token = EntryToken(proof, YogaAdmin);
        using (var inspection = await EntrySendAsync(client, token, "inspections", offer))
        {
            var body = await JsonAsync(inspection);
            Assert.Equal("9007199254740993", body.GetProperty("version").GetString());
            AssertVersionHeader(inspection, body);
        }
        using (var stale = await EntrySendAsync(client, token, "acceptances", offer, "\"9007199254740992\""))
            await ProblemAsync(stale, HttpStatusCode.Conflict, "concurrency_conflict");
        Assert.Equal(1, proof.Issuer.UserInfoRequests);
        using var accepted = await EntrySendAsync(client, token, "acceptances", offer, "\"9007199254740993\"");
        var result = await JsonAsync(accepted);
        Assert.Equal("9007199254740994", result.GetProperty("version").GetString());
        AssertVersionHeader(accepted, result);
    }

    [Fact]
    public async Task Actual_invitation_entry_masks_foreign_host_and_wrong_verifier_but_preserves_invalid_bearer_401()
    {
        await using var proof = await CreatePreparedAsync();
        var offer = await SeedEntryOfferAsync(proof);
        SetEntryRecipient(proof, YogaAdmin);
        using var client = EntryClient(proof);
        var token = EntryToken(proof, YogaAdmin);
        using (var invalidBearer = await EntrySendAsync(client, "not-a-signed-token", "inspections", offer))
            await ProblemAsync(invalidBearer, HttpStatusCode.Unauthorized, "unauthorized");
        using (var foreign = await EntrySendAsync(client, token, "inspections", offer, mutate: request =>
        {
            request.Headers.Host = SeedData.Yoga.Host;
            request.Headers.Remove(TrustedHopOptions.HostHeaderName);
            request.Headers.Add(TrustedHopOptions.HostHeaderName, SeedData.Yoga.Host);
        }))
            await ProblemAsync(foreign, HttpStatusCode.NotFound, "not_found");
        var wrong = new EntryOffer(offer.Id, offer.Id.ToString("D") + "." + EntryNative);
        using (var invalid = await EntrySendAsync(client, token, "inspections", wrong))
            await ProblemAsync(invalid, HttpStatusCode.NotFound, "not_found");
        Assert.Equal(0, proof.Issuer.UserInfoRequests);
        using var positive = await EntrySendAsync(client, token, "inspections", offer);
        await JsonAsync(positive);
    }

}
