using System.Net;
using LearnStack.SharedKernel.Identity;
using Npgsql;
using Xunit;
using static LearnStack.Tests.Integration.Database.IdentityAuthorizationHttpProof;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BoundedAdministrationHttpTests
{
    [Fact]
    public async Task Actual_invitation_entry_accept_accept_race_has_one_committed_winner_and_explicit_natural_convergence()
    {
        await using var proof = await CreatePreparedAsync();
        var offer = await SeedEntryOfferAsync(proof);
        SetEntryRecipient(proof, YogaAdmin);
        using var client = EntryClient(proof);
        var token = EntryToken(proof, YogaAdmin);
        var bothAtProvider = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observations = 0;
        using var observationGate = new SemaphoreSlim(1);
        proof.Issuer.OnUserInfo = async () =>
        {
            await observationGate.WaitAsync();
            try
            {
                // Test the actual User/Tenant locks from an independent app-role transaction.
                // Provider observation cannot be hidden behind any of these business fences.
                await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(proof.Database.AppConnectionString);
                await using var tx = await app.BeginTransactionAsync();
                foreach (var key in new[] { IdentityAdvisoryLockKey.ForUser(YogaAdmin), IdentityAdvisoryLockKey.ForUser(EnglishAdmin), IdentityAdvisoryLockKey.ForTenant(Tenant.TenantId) })
                    Assert.True(await ScalarAsync<bool>(app, tx, "SELECT pg_try_advisory_xact_lock(@key)", ("key", key)));
                await tx.RollbackAsync();
                if (Interlocked.Increment(ref observations) == 2) bothAtProvider.TrySetResult();
            }
            finally { observationGate.Release(); }
        };
        proof.Issuer.UserInfoRelease = release.Task;
        var first = EntrySendAsync(client, token, "acceptances", offer, "\"0\"");
        var second = EntrySendAsync(client, token, "acceptances", offer, "\"0\"");
        HttpResponseMessage[] responses;
        try
        {
            await bothAtProvider.Task.WaitAsync(TimeSpan.FromSeconds(4));
            Assert.False(first.IsCompleted); Assert.False(second.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            responses = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(15));
        }
        using var winner = Assert.Single(responses, item => item.StatusCode == HttpStatusCode.OK);
        using var loser = Assert.Single(responses, item => item.StatusCode == HttpStatusCode.Conflict);
        var accepted = await JsonAsync(winner);
        await ProblemAsync(loser, HttpStatusCode.Conflict, "concurrency_conflict");
        Assert.Equal(2, (await proof.AuditAsync(winner, Tenant)).Count);
        Assert.All(await proof.AuditAsync(loser, Tenant), row => Assert.NotEqual("success", row.Outcome));
        await AssertEntryStateAsync(proof, offer, "Accepted", 1);
        proof.Issuer.OnUserInfo = null;
        proof.Issuer.UserInfoStatusCode = 503;
        using var recovered = await EntrySendAsync(client, token, "results", offer);
        Assert.Equal(accepted.GetRawText(), (await JsonAsync(recovered)).GetRawText());
        Assert.Equal(2, proof.Issuer.UserInfoRequests);
    }

    [Fact]
    public async Task Actual_invitation_entry_success_waits_for_both_MUST_rows_physical_commit()
    {
        await using var proof = await CreatePreparedAsync();
        var offer = await SeedEntryOfferAsync(proof);
        SetEntryRecipient(proof, YogaAdmin);
        using var client = EntryClient(proof);
        const long gate = 7_803_004_501;
        await proof.OwnerAsync($$"""
            CREATE FUNCTION public.fn_invitation_entry_commit_proof() RETURNS trigger LANGUAGE plpgsql AS $proof$
            BEGIN IF NEW.operation='identity.membership.admit_invitation' THEN PERFORM pg_advisory_xact_lock({{gate}}); END IF;
            RETURN NEW; END $proof$;
            CREATE CONSTRAINT TRIGGER tg_invitation_entry_commit_proof AFTER INSERT ON public.audit_log
                DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.fn_invitation_entry_commit_proof();
            CREATE FUNCTION public.fn_invitation_entry_commit_defer() RETURNS trigger LANGUAGE plpgsql AS $proof$
            BEGIN SET CONSTRAINTS tg_invitation_entry_commit_proof DEFERRED; RETURN NEW; END $proof$;
            CREATE TRIGGER tg_invitation_entry_commit_defer BEFORE INSERT ON public.audit_log
                FOR EACH ROW EXECUTE FUNCTION public.fn_invitation_entry_commit_defer();
            """);
        try
        {
            await using var blocker = (NpgsqlConnection)await PostgresFixture.OpenAsync(proof.Database.AppConnectionString);
            await using var tx = await blocker.BeginTransactionAsync();
            await ExecuteAsync(blocker, tx, "SELECT pg_advisory_xact_lock(@key)", ("key", gate));
            var request = EntrySendAsync(client, EntryToken(proof, YogaAdmin), "acceptances", offer, "\"0\"");
            HttpResponseMessage response;
            try
            {
                await using var monitor = (NpgsqlConnection)await PostgresFixture.OpenAsync(proof.Database.AppConnectionString);
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                while (!await ScalarAsync<bool>(monitor, null, "SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE @blocker=ANY(pg_blocking_pids(pid)) AND query='COMMIT' AND state='active')", ("blocker", blocker.ProcessID)))
                { Assert.False(request.IsCompleted); await Task.Delay(20, deadline.Token); }
                Assert.False(request.IsCompleted);
                await AssertEntryStateAsync(proof, offer, "Pending", 0);
            }
            finally
            {
                await tx.RollbackAsync();
                response = await request.WaitAsync(TimeSpan.FromSeconds(15));
            }
            using (response)
            {
                await JsonAsync(response);
                Assert.Equal(2, (await proof.AuditAsync(response, Tenant)).Count);
            }
            await AssertEntryStateAsync(proof, offer, "Accepted", 1);
        }
        finally { await proof.OwnerAsync("DROP TRIGGER tg_invitation_entry_commit_defer ON public.audit_log; DROP FUNCTION public.fn_invitation_entry_commit_defer(); DROP TRIGGER tg_invitation_entry_commit_proof ON public.audit_log; DROP FUNCTION public.fn_invitation_entry_commit_proof();"); }
    }
}
