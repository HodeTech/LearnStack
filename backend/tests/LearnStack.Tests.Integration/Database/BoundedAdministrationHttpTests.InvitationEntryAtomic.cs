using System.Net;
using LearnStack.Tools.Seeder;
using Npgsql;
using Xunit;
using static LearnStack.Tests.Integration.Database.IdentityAuthorizationHttpProof;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BoundedAdministrationHttpTests
{
    [Theory]
    [InlineData("identity.invitation.accept", false)]
    [InlineData("identity.membership.admit_invitation", false)]
    [InlineData("identity.invitation.accept", true)]
    public async Task Actual_invitation_entry_withholds_success_and_rolls_back_both_roots_on_each_MUST_or_commit_failure(string operation, bool deferred)
    {
        await using var proof = await CreatePreparedAsync();
        var offer = await SeedEntryOfferAsync(proof);
        SetEntryRecipient(proof, YogaAdmin);
        using var client = EntryClient(proof);
        var token = EntryToken(proof, YogaAdmin);
        await proof.OwnerAsync($$"""
            CREATE FUNCTION public.fn_invitation_entry_audit_proof() RETURNS trigger LANGUAGE plpgsql AS $proof$
            BEGIN IF NEW.operation='{{operation}}' THEN
                RAISE EXCEPTION 'Invitation audit proof refusal' USING ERRCODE='P0399';
            END IF; RETURN NEW; END $proof$;
            """);
        await proof.OwnerAsync(deferred ? """
            CREATE CONSTRAINT TRIGGER tg_invitation_entry_audit_proof AFTER INSERT ON public.audit_log
                DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.fn_invitation_entry_audit_proof()
            """ : """
            CREATE TRIGGER tg_invitation_entry_audit_proof BEFORE INSERT ON public.audit_log
                FOR EACH ROW EXECUTE FUNCTION public.fn_invitation_entry_audit_proof()
            """);
        if (deferred) await proof.OwnerAsync("""
            CREATE FUNCTION public.fn_invitation_entry_audit_defer() RETURNS trigger LANGUAGE plpgsql AS $proof$
            BEGIN SET CONSTRAINTS tg_invitation_entry_audit_proof DEFERRED; RETURN NEW; END $proof$;
            CREATE TRIGGER tg_invitation_entry_audit_defer BEFORE INSERT ON public.audit_log
                FOR EACH ROW EXECUTE FUNCTION public.fn_invitation_entry_audit_defer();
            """);
        try
        {
            using var failed = await EntrySendAsync(client, token, "acceptances", offer, "\"0\"");
            await ProblemAsync(failed, deferred ? HttpStatusCode.InternalServerError : HttpStatusCode.ServiceUnavailable,
                deferred ? "internal_error" : "audit_unavailable");
            Assert.All(await proof.AuditAsync(failed, Tenant), row => Assert.NotEqual("success", row.Outcome));
            await AssertEntryStateAsync(proof, offer, "Pending", 0);
        }
        finally
        {
            if (deferred) await proof.OwnerAsync("DROP TRIGGER tg_invitation_entry_audit_defer ON public.audit_log; DROP FUNCTION public.fn_invitation_entry_audit_defer();");
            await proof.OwnerAsync("DROP TRIGGER tg_invitation_entry_audit_proof ON public.audit_log; DROP FUNCTION public.fn_invitation_entry_audit_proof();");
        }
        using var retry = await EntrySendAsync(client, token, "acceptances", offer, "\"0\"");
        Assert.Equal("created", (await JsonAsync(retry)).GetProperty("disposition").GetString());
        await AssertEntryStateAsync(proof, offer, "Accepted", 1);
    }

    [Theory]
    [InlineData("Suspended", HttpStatusCode.Conflict)]
    [InlineData("Revoked", HttpStatusCode.OK)]
    public async Task Actual_invitation_entry_never_reactivates_suspended_or_reuses_revoked_membership(string state, HttpStatusCode expected)
    {
        await using var proof = await CreatePreparedAsync();
        var original = SeedData.IdentityMemberships[1].MembershipId;
        await proof.SetMembershipStatusAsync(Tenant, EnglishLearner, original, state);
        var offer = await SeedEntryOfferAsync(proof);
        SetEntryRecipient(proof, EnglishLearner);
        using var client = EntryClient(proof);
        using var response = await EntrySendAsync(client, EntryToken(proof, EnglishLearner), "acceptances", offer, "\"0\"");
        if (expected == HttpStatusCode.Conflict)
        {
            await ProblemAsync(response, expected, "concurrency_conflict");
            using var result = await EntrySendAsync(client, EntryToken(proof, EnglishLearner), "results", offer);
            await ProblemAsync(result, HttpStatusCode.NotFound, "not_found");
        }
        else
        {
            var value = await JsonAsync(response);
            Assert.Equal("created", value.GetProperty("disposition").GetString());
            Assert.NotEqual(original, Guid.Parse(value.GetProperty("membershipId").GetString()!));
        }
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(proof.Database.AppConnectionString);
        await using var tx = await app.BeginTransactionAsync();
        await ScopeAsync(app, tx, Tenant.TenantId.Value);
        Assert.Equal(state, await ScalarAsync<string>(app, tx, "SELECT status FROM memberships WHERE id=@id", ("id", original)));
    }

    [Fact]
    public async Task Actual_invitation_entry_result_uses_original_membership_and_rejects_a_revoked_result_even_with_active_successor()
    {
        await using var proof = await CreatePreparedAsync();
        var offer = await SeedEntryOfferAsync(proof);
        SetEntryRecipient(proof, YogaAdmin);
        using var client = EntryClient(proof);
        var token = EntryToken(proof, YogaAdmin);
        using var response = await EntrySendAsync(client, token, "acceptances", offer, "\"0\"");
        var accepted = await JsonAsync(response);
        var member = Guid.Parse(accepted.GetProperty("membershipId").GetString()!);
        await proof.SetMembershipStatusAsync(Tenant, YogaAdmin, member, "Revoked");
        await proof.Database.AddMembershipAsync(Tenant, YogaAdmin);
        proof.Issuer.UserInfoStatusCode = 503;
        using var result = await EntrySendAsync(client, token, "results", offer);
        await ProblemAsync(result, HttpStatusCode.NotFound, "not_found");
        Assert.Equal(1, proof.Issuer.UserInfoRequests);
    }

    private static async Task AssertEntryStateAsync(IdentityAuthorizationHttpProof proof, EntryOffer offer, string state, long members)
    {
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(proof.Database.AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(app);
        await using var tx = await app.BeginTransactionAsync();
        await ScopeAsync(app, tx, Tenant.TenantId.Value);
        Assert.Equal(state, await ScalarAsync<string>(app, tx, "SELECT status FROM invitations WHERE id=@id", ("id", offer.Id)));
        Assert.Equal(members, await ScalarAsync<long>(app, tx,
            "SELECT count(*) FROM memberships WHERE user_id=@actor AND organization_id IS NULL AND status='Active' AND deleted_at IS NULL", ("actor", YogaAdmin.Value)));
    }
}
