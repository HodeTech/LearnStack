using System.Net;
using LearnStack.SharedKernel.Identity;
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
    [Fact]
    public async Task Actual_invitation_entry_successful_commit_with_lost_HTTP_observation_recovers_only_original_result()
    {
        await using var proof = await CreatePreparedAsync();
        var offer = await SeedEntryOfferAsync(proof);
        SetEntryRecipient(proof, YogaAdmin);
        var fault = new EntryPostCommitObservationFault();
        var token = EntryToken(proof, YogaAdmin);
        using (var client = EntryClient(proof, configure: services => services.AddSingleton<IStartupFilter>(fault)))
            await Assert.ThrowsAnyAsync<IOException>(async () =>
            {
                using var response = await EntrySendAsync(client, token, "acceptances", offer, "\"0\"");
                await response.Content.ReadAsByteArrayAsync();
            });
        Assert.Equal(200, fault.CompletedStatus);
        Assert.Equal(1, proof.Issuer.UserInfoRequests);
        Guid originalMember;
        await using (var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(proof.Database.AppConnectionString))
        await using (var tx = await app.BeginTransactionAsync())
        {
            await ScopeAsync(app, tx, Tenant.TenantId.Value);
            originalMember = await ScalarAsync<Guid>(app, tx, "SELECT result_membership_id FROM invitations WHERE id=@id AND status='Accepted'", ("id", offer.Id));
            Assert.Equal(2L, await ScalarAsync<long>(app, tx,
                "SELECT count(*) FROM audit_log WHERE entity_id IN (@offer,@member) AND outcome='success' AND operation IN ('identity.invitation.accept','identity.membership.admit_invitation')",
                ("offer", offer.Id.ToString("D")), ("member", originalMember.ToString("D"))));
        }
        proof.Issuer.UserInfoStatusCode = 503;
        using var recovery = EntryClient(proof);
        using var result = await EntrySendAsync(recovery, token, "results", offer);
        Assert.Equal(originalMember.ToString("D"), (await JsonAsync(result)).GetProperty("membershipId").GetString());
        using var retry = await EntrySendAsync(recovery, token, "acceptances", offer, "old malformed precondition");
        Assert.Equal(originalMember.ToString("D"), (await JsonAsync(retry)).GetProperty("membershipId").GetString());
        Assert.Equal(1, proof.Issuer.UserInfoRequests);
        Assert.Empty(await proof.AuditAsync(result, Tenant));
        Assert.Empty(await proof.AuditAsync(retry, Tenant));
    }

    [Fact]
    public async Task Actual_invitation_entry_committed_acceptance_refuses_later_revoke_and_reissue_without_replacing_original_result()
    {
        await using var proof = await CreatePreparedAsync();
        var offer = await SeedEntryOfferAsync(proof);
        SetEntryRecipient(proof, YogaAdmin);
        using var entry = EntryClient(proof);
        var token = EntryToken(proof, YogaAdmin);
        using var accepted = await EntrySendAsync(entry, token, "acceptances", offer, "\"0\"");
        var original = await JsonAsync(accepted);
        using var admin = proof.Client();
        foreach (var suffix in new[] { "revocations", "reissues" })
        {
            using var refused = await SendAsync(admin, proof.Token(EnglishAdmin, Tenant),
                "/api/v1/identity/invitations/" + offer.Id.ToString("D") + "/" + suffix,
                "POST", "{}", original.GetProperty("version").GetString(), Key());
            await ProblemAsync(refused, HttpStatusCode.Conflict, "concurrency_conflict");
            Assert.All(await proof.AuditAsync(refused, Tenant), row => Assert.NotEqual("success", row.Outcome));
        }
        using var recovered = await EntrySendAsync(entry, token, "results", offer);
        Assert.Equal(original.GetRawText(), (await JsonAsync(recovered)).GetRawText());
        Assert.Equal(1, proof.Issuer.UserInfoRequests);
        await AssertEntryStateAsync(proof, offer, "Accepted", 1);
    }

    // This faults HTTP observation after the real application pipeline has completed.
    // It proves successful COMMIT plus lost response, not a simulated driver COMMIT acknowledgment.
    private sealed class EntryPostCommitObservationFault : IStartupFilter
    {
        internal int CompletedStatus { get; private set; }
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, following) =>
            {
                if (context.Request.Path != EntryBase + "acceptances") { await following(context); return; }
                var original = context.Response.Body;
                await using var buffered = new MemoryStream();
                context.Response.Body = buffered;
                try
                {
                    await following(context);
                    CompletedStatus = context.Response.StatusCode;
                    throw new IOException("Synthetic invitation response observation failure.");
                }
                finally { context.Response.Body = original; }
            });
            next(app);
        };
    }
}
