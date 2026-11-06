using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using LearnStack.Modules.Identity.Application.Contracts.Invitations;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Identifiers;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class EducationStructureTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Invitation_parent_lookup_locks_before_FK_and_unlocked_twin_does_not(bool envelope, bool removeLock)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(_schema.Postgres);
        var table = envelope ? "invitation_email_envelopes" : "invitations";
        var parentTable = envelope ? "background_jobs" : "memberships";
        var function = envelope ? "fn_invitation_email_envelope_guard" : "fn_identity_invitation_guard";
        var id = Guid.CreateVersion7();
        var invitation = Guid.CreateVersion7();
        var generation = Guid.CreateVersion7();
        Guid parent;
        if (envelope)
        {
            await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
            parent = (await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString,
                "identity.invitation_email_tenant.v1", profile: "external", capacityGroup: "identity.invitation_email",
                parameters: JsonSerializer.Serialize(new InvitationEmailParameters(invitation, generation, id)))).Value;
        }
        else
        {
            await IdentityMembershipFixture.SeedSchemaAsync(database.MigrationConnectionString, database.AppConnectionString);
            parent = IdentityMembershipFixture.LearnerMembershipA;
        }

        await using (var owner = await PostgresFixture.OpenAsync(database.MigrationConnectionString))
        {
            if (removeLock)
            {
                var definition = (await SchemaQueries.ReadStringsAsync(owner,
                    $"SELECT pg_get_functiondef('public.{function}()'::regprocedure)")).Single();
                var weakened = definition.Replace(" FOR KEY SHARE", "", StringComparison.Ordinal);
                weakened.Should().NotBe(definition);
                await SchemaQueries.ExecuteAsync(owner, null, weakened);
            }
            // Trigger names sort alphabetically. This gate is after the real
            // BEFORE guard but before FK enforcement; no production guard moves.
            await SchemaQueries.ExecuteAsync(owner, null, $"""
                CREATE FUNCTION public.fn_invitation_parent_test_gate() RETURNS trigger
                    LANGUAGE plpgsql SECURITY INVOKER SET search_path=pg_catalog AS $$
                BEGIN PERFORM pg_advisory_xact_lock(714202693); RETURN NEW; END; $$;
                CREATE TRIGGER zz_invitation_parent_test_gate BEFORE {(envelope ? "INSERT" : "UPDATE")} ON public.{table}
                    FOR EACH ROW EXECUTE FUNCTION public.fn_invitation_parent_test_gate();
                """);
        }
        await using var gate = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var child = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var probe = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(child);
        await SchemaQueries.AssertApplicationRoleAsync(probe);
        await using var gateTransaction = await gate.BeginTransactionAsync();
        await using var childTransaction = await child.BeginTransactionAsync();
        await using var probeTransaction = await probe.BeginTransactionAsync();
        await EducationSchemaSeed.AnnounceAsync(child, childTransaction, SchemaFixture.TenantA, null);
        await EducationSchemaSeed.AnnounceAsync(probe, probeTransaction, SchemaFixture.TenantA, null);
        await SchemaQueries.ExecuteAsync(gate, gateTransaction, "SELECT pg_advisory_xact_lock(714202693)");
        if (!envelope)
        {
            foreach (var key in new[] { IdentityMembershipFixture.AdminA, IdentityMembershipFixture.PersonA }
                .Select(user => IdentityAdvisoryLockKey.ForUser(UserId.From(user))).Order())
                await SchemaQueries.ExecuteAsync(child, childTransaction, "SELECT pg_advisory_xact_lock(@key)", ("key", key));
            await IdentityMembershipFixture.LockAsync(child, childTransaction, SchemaFixture.TenantA, null, false);
        }
        await using var write = new NpgsqlCommand(envelope ? """
            INSERT INTO public.invitation_email_envelopes
              (id,tenant_id,invitation_id,generation_id,job_id,format_version,key_version,nonce,ciphertext,tag)
            VALUES(@id,@tenant,@invitation,@generation,@parent,1,'fixture',decode(repeat('01',12),'hex'),
              decode('01','hex'),decode(repeat('02',16),'hex'))
            """ : """
            UPDATE public.invitations SET status='Accepted',consumed_verifier_digest=verifier_digest,verifier_digest=NULL,
              accepted_by=@person,result_membership_id=@parent,acceptance_disposition='AlreadyMember',accepted_at=clock_timestamp(),
              row_version=row_version+1,updated_at=clock_timestamp(),updated_by=@actor WHERE id=@offer
            """, child, childTransaction);
        write.Parameters.AddWithValue("id", id);
        write.Parameters.AddWithValue("tenant", SchemaFixture.TenantA);
        write.Parameters.AddWithValue("invitation", invitation);
        write.Parameters.AddWithValue("generation", generation);
        write.Parameters.AddWithValue("parent", parent);
        write.Parameters.AddWithValue("person", IdentityMembershipFixture.PersonA);
        write.Parameters.AddWithValue("actor", IdentityMembershipFixture.AdminA);
        write.Parameters.AddWithValue("offer", InvitationSchemaSeed.TenantOfferA);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var pending = write.ExecuteNonQueryAsync(timeout.Token);
        var gateReleased = false;
        try
        {
            var elapsed = Stopwatch.StartNew();
            var blocked = false;
            while (elapsed.Elapsed < TimeSpan.FromSeconds(5))
            {
                await using var observe = new NpgsqlCommand("SELECT @blocker=ANY(pg_blocking_pids(@waiter))", probe, probeTransaction);
                observe.Parameters.AddWithValue("blocker", gate.ProcessID);
                observe.Parameters.AddWithValue("waiter", child.ProcessID);
                blocked = (bool)(await observe.ExecuteScalarAsync(timeout.Token))!;
                if (blocked) break;
                pending.IsCompleted.Should().BeFalse("the real BEFORE guard must reach the fixture gate");
                await Task.Delay(TimeSpan.FromMilliseconds(20), timeout.Token);
            }
            blocked.Should().BeTrue("the child must be inside BEFORE, before its FK can take a parent lock");
            await using var parentLock = new NpgsqlCommand(
                $"SELECT id FROM public.{parentTable} WHERE id=@id FOR UPDATE NOWAIT", probe, probeTransaction);
            parentLock.Parameters.AddWithValue("id", parent);
            if (removeLock)
                (await parentLock.ExecuteScalarAsync(timeout.Token)).Should().Be(parent,
                    "the dirty twin proves an ordinary FK has not yet supplied the missing parent lock");
            else
            {
                var lockParent = () => parentLock.ExecuteScalarAsync(timeout.Token);
                (await lockParent.Should().ThrowAsync<PostgresException>()).Which.SqlState
                    .Should().Be(PostgresErrorCodes.LockNotAvailable);
            }
            await probeTransaction.RollbackAsync(timeout.Token);
            await gateTransaction.RollbackAsync(timeout.Token);
            gateReleased = true;
            (await pending).Should().Be(1);
        }
        finally
        {
            if (!gateReleased) await gateTransaction.RollbackAsync();
            await timeout.CancelAsync();
            try { await pending; }
            catch (Exception exception) when (exception is NpgsqlException or OperationCanceledException) { }
        }
    }
}
