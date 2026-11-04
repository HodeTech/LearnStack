using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LearnStack.Api.Authentication;
using LearnStack.Api.Tenancy;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Permissions;
using LearnStack.Tools.Seeder;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using static LearnStack.Tests.Integration.Database.IdentityAuthorizationHttpProof;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BoundedAdministrationHttpTests
{
    private const string EntryBase = "/api/v1/identity/invitation-entry/";
    private const string EntryHop = "invitation-entry-proof-public-hop-separate-purpose";
    private const string EntryEmail = "recipient@example.test";
    private static readonly string EntryNative = TenantAuthenticationIssuer.Base64Url(Enumerable.Range(41, 32).Select(value => (byte)value).ToArray());

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_invitation_entry_accepts_atomic_roots_and_converges_without_provider_or_new_audit(bool existing)
    {
        await using var proof = await CreatePreparedAsync();
        var actor = existing ? EnglishLearner : YogaAdmin;
        var offer = await SeedEntryOfferAsync(proof);
        var token = EntryToken(proof, actor);
        SetEntryRecipient(proof, actor);
        using var client = EntryClient(proof);
        using (var inspected = await EntrySendAsync(client, token, "inspections", offer))
        {
            var body = await JsonAsync(inspected);
            Assert.Equal(EntryEmail, body.GetProperty("verifiedEmail").GetString());
            AssertVersionHeader(inspected, body);
            Assert.Empty(await proof.AuditAsync(inspected, Tenant));
        }
        Assert.Equal(1, proof.Issuer.UserInfoRequests);
        using var accepted = await EntrySendAsync(client, token, "acceptances", offer, "\"0\"");
        var result = await JsonAsync(accepted);
        Assert.Equal(existing ? "already_member" : "created", result.GetProperty("disposition").GetString());
        AssertVersionHeader(accepted, result);
        Assert.Equal(2, proof.Issuer.UserInfoRequests);
        var audit = await proof.AuditAsync(accepted, Tenant);
        Assert.Equal(2, audit.Count);
        Assert.Contains(audit, row => row.Operation == "identity.invitation.accept" && row.Outcome == "success");
        Assert.Contains(audit, row => row.Operation == "identity.membership.admit_invitation" && row.Outcome == "success");
        Assert.False(accepted.Headers.Contains("Set-Cookie"));
        Assert.Null(accepted.Headers.Location);
        proof.Issuer.UserInfoStatusCode = 503;
        proof.Issuer.UserInfoResponse = "not JSON";
        using var recovered = await EntrySendAsync(client, token, "acceptances", offer, "garbage-old-precondition");
        Assert.Equal(result.GetRawText(), (await JsonAsync(recovered)).GetRawText());
        Assert.Empty(await proof.AuditAsync(recovered, Tenant));
        using var read = await EntrySendAsync(client, token, "results", offer);
        Assert.Equal(result.GetRawText(), (await JsonAsync(read)).GetRawText());
        Assert.Equal(2, proof.Issuer.UserInfoRequests);
        Assert.Empty(await proof.AuditAsync(read, Tenant));
        using var repeatedHeader = await EntrySendAsync(client, token, "acceptances", offer,
            mutate: request => { request.Headers.TryAddWithoutValidation("If-Match", "old-first"); request.Headers.TryAddWithoutValidation("If-Match", "old-second"); });
        Assert.Equal(result.GetRawText(), (await JsonAsync(repeatedHeader)).GetRawText());
        Assert.Empty(await proof.AuditAsync(repeatedHeader, Tenant));
        using var otherActor = await EntrySendAsync(client, EntryToken(proof, existing ? YogaAdmin : EnglishLearner), "results", offer);
        await ProblemAsync(otherActor, HttpStatusCode.NotFound, "not_found");
        Assert.Equal(2, proof.Issuer.UserInfoRequests);
    }

    [Fact]
    public async Task Actual_invitation_entry_requires_current_verified_mailbox_and_strict_conditional_transport()
    {
        await using var proof = await CreatePreparedAsync();
        var offer = await SeedEntryOfferAsync(proof);
        var token = EntryToken(proof, YogaAdmin);
        SetEntryRecipient(proof, YogaAdmin, "different@example.test");
        using var client = EntryClient(proof);
        using (var wrong = await EntrySendAsync(client, token, "inspections", offer))
        {
            await ProblemAsync(wrong, HttpStatusCode.Forbidden, "forbidden");
            Assert.DoesNotContain(EntryEmail, await wrong.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        foreach (var version in new string?[] { null, "W/\"0\"", "*", "\"00\"", "\"0\",\"1\"" })
        {
            using var malformed = await EntrySendAsync(client, token, "acceptances", offer, version);
            await ProblemAsync(malformed, HttpStatusCode.BadRequest, "validation_failed");
        }
        using (var repeatedHeader = await EntrySendAsync(client, token, "acceptances", offer,
            mutate: request => { request.Headers.TryAddWithoutValidation("If-Match", "\"0\""); request.Headers.TryAddWithoutValidation("If-Match", "\"1\""); }))
            await ProblemAsync(repeatedHeader, HttpStatusCode.BadRequest, "validation_failed");
        using (var invalidRead = await EntrySendAsync(client, token, "inspections", offer, "\"0\""))
            await ProblemAsync(invalidRead, HttpStatusCode.BadRequest, "validation_failed");
        SetEntryRecipient(proof, YogaAdmin);
        using var correct = await EntrySendAsync(client, token, "inspections", offer);
        Assert.Equal(EntryEmail, (await JsonAsync(correct)).GetProperty("verifiedEmail").GetString());
        Assert.Equal(2, proof.Issuer.UserInfoRequests);
    }

    private static HttpClient EntryClient(IdentityAuthorizationHttpProof proof, bool freshConnections = false, Action<IServiceCollection>? configure = null, int? defaultPoolSize = null) => proof.Database.ClientFor(Tenant.Host,
        services => { services.AddSingleton<IStartupFilter, EntryLoopbackPeer>(); configure?.Invoke(services); }, builder =>
        {
            builder.UseSetting("Tenancy:TrustedHop:Networks:0", "127.0.0.1/32");
            builder.UseSetting("Tenancy:TrustedHop:Secrets:0", EntryHop);
            builder.UseSetting("Identity:NativeSession:CurrentSecret", EntryNative);
            if (defaultPoolSize is { } poolSize)
                builder.UseSetting("ConnectionStrings:Default", new NpgsqlConnectionStringBuilder(proof.Database.AppConnectionString)
                { Pooling = true, MaxPoolSize = poolSize }.ConnectionString);
            if (freshConnections)
            {
                var fresh = new NpgsqlConnectionStringBuilder(proof.Database.AppConnectionString) { Pooling = false }.ConnectionString;
                builder.UseSetting("ConnectionStrings:Default", fresh);
                builder.UseSetting("ConnectionStrings:IdentityAuthorization", fresh);
                builder.UseSetting("ConnectionStrings:AuthorizationEvidence", fresh);
            }
        });

    private static string EntryToken(IdentityAuthorizationHttpProof proof, UserId actor)
    {
        using var source = JsonDocument.Parse(proof.Issuer.Payload(("sub", IdentityPublicReadFixture.FoundationSubject(actor)),
            ("user_id", actor.Value.ToString("D"))));
        var claims = source.RootElement.EnumerateObject().Where(property => property.Name is not ("tenant_id" or "organization_id"))
            .ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal);
        return proof.Issuer.Token(JsonSerializer.Serialize(claims));
    }
    private static void SetEntryRecipient(IdentityAuthorizationHttpProof proof, UserId actor, string email = EntryEmail) =>
        proof.Issuer.UserInfoResponse = JsonSerializer.Serialize(new
        { sub = IdentityPublicReadFixture.FoundationSubject(actor), email, email_verified = true });
    private sealed record EntryOffer(Guid Id, string Code)
    { public override string ToString() => nameof(EntryOffer); }
    private static async Task<EntryOffer> SeedEntryOfferAsync(IdentityAuthorizationHttpProof proof,
        OrganizationId? organization = null, BuiltInRoleKey role = BuiltInRoleKey.Learner, string email = EntryEmail, DateTimeOffset? expiresAt = null, UserId? inviter = null)
    {
        var at = expiresAt?.AddDays(-14) ?? DateTimeOffset.UtcNow;
        var id = Guid.CreateVersion7();
        var secret = RandomNumberGenerator.GetBytes(32);
        var code = id.ToString("D") + "." + TenantAuthenticationIssuer.Base64Url(secret);
        var digest = InvitationVerifier.Compute(id, secret);
        CryptographicOperations.ZeroMemory(secret);
        var roleId = SeedData.IdentityRoles.Single(value => value.Tenant == Tenant && value.BuiltInKey == role
            && value.Scope == (organization is null ? PermissionScope.Tenant : PermissionScope.Organization)).RoleId;
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(proof.Database.AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(app);
        await using var tx = await app.BeginTransactionAsync();
        await ScopeAsync(app, tx, Tenant.TenantId.Value, organization?.Value);
        await LockAsync(app, tx, Tenant.TenantId.Value, null, false);
        await ExecuteAsync(app, tx, """
            INSERT INTO invitations(id,tenant_id,organization_id,recipient_email,inviter_user_id,role_id,pinned_role_version,
                status,generation_id,generation,generation_created_at,expires_at,verifier_digest,entry_origin,entry_locale,
                current_delivery_job_id,submission_status,created_at,created_by)
            SELECT @id,@tenant,@organization,@email,@inviter,id,row_version,'Pending',@generation,1,@at,
                @at+interval '14 days',@digest,@origin,'en',@job,'Queued',@at,@inviter
            FROM roles WHERE id=@role
            """, ("id", id), ("tenant", Tenant.TenantId.Value), ("organization", organization?.Value),
            ("email", email), ("inviter", (inviter ?? EnglishAdmin).Value), ("generation", Guid.CreateVersion7()),
            ("at", at), ("digest", digest), ("origin", "https://" + Tenant.Host), ("job", Guid.CreateVersion7()), ("role", roleId));
        await tx.CommitAsync();
        CryptographicOperations.ZeroMemory(digest);
        return new(id, code);
    }
    private static async Task<HttpResponseMessage> EntrySendAsync(HttpClient client, string token, string action,
        EntryOffer offer, string? ifMatch = null, Action<HttpRequestMessage>? mutate = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, EntryBase + action);
        request.Headers.Host = Tenant.Host;
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add(TrustedHopOptions.HostHeaderName, Tenant.Host);
        request.Headers.Add(TrustedHopOptions.SecretHeaderName, EntryHop);
        request.Headers.Add("X-LearnStack-Visitor-Address", "198.51.100.24");
        request.Headers.Add(NativeSessionSourcePolicy.SecretHeader, EntryNative);
        if (ifMatch is not null) request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        request.Content = new StringContent(JsonSerializer.Serialize(new { invitationId = offer.Id, code = offer.Code }), Encoding.UTF8, "application/json");
        mutate?.Invoke(request);
        return await client.SendAsync(request);
    }
    private sealed class EntryLoopbackPeer : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, following) => { context.Connection.RemoteIpAddress = IPAddress.Loopback; return following(context); });
            next(app);
        };
    }
}
