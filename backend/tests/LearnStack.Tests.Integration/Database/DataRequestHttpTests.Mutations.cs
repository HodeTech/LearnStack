using System.Net;
using FluentAssertions;
using LearnStack.SharedKernel.Identifiers;
using Npgsql;
using Xunit;
using static LearnStack.Tests.Integration.Database.IdentityAuthorizationHttpProof;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class DataRequestHttpTests
{
    [Fact]
    public async Task A_requested_export_is_cancelled_only_with_its_current_version_and_its_job_then_completes_as_a_no_op()
    {
        await using var proof = await CreatePreparedAsync();
        using var client = proof.Client();
        var token = proof.Token(EnglishLearner, English);
        var tenant = English.TenantId.Value;
        var (id, version) = await FileExportAsync(client, token);
        var cancel = OwnPath + "/" + id.ToString("D") + "/cancellations";

        // If-Match is required, canonical and current.
        using (var missing = await SendAsync(client, token, cancel, "POST", "{}", key: Key()))
            await ProblemAsync(missing, HttpStatusCode.BadRequest, "validation_failed");
        using (var stale = await SendAsync(client, token, cancel, "POST", "{}", version: (Number(version) + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), key: Key()))
            await ProblemAsync(stale, HttpStatusCode.Conflict, "concurrency_conflict");

        var key = Key();
        string cancelled;
        using (var response = await SendAsync(client, token, cancel, "POST", "{}", version: version, key: key))
        {
            var receipt = await JsonAsync(response);
            receipt.EnumerateObject().Select(property => property.Name).Should().Equal("version");
            cancelled = Version(receipt);
            Number(cancelled).Should().Be(Number(version) + 1);
            response.Headers.ETag!.Tag.Should().Be("\"" + cancelled + "\"");
        }

        using (var replay = await SendAsync(client, token, cancel, "POST", "{}", version: version, key: key))
        {
            Version(await JsonAsync(replay)).Should().Be(cancelled, "the replay discloses the original receipt");
            replay.Headers.GetValues("Idempotency-Replayed").Should().Equal("true");
        }

        using (var again = await SendAsync(client, token, cancel, "POST", "{}", version: cancelled, key: Key()))
            await ProblemAsync(again, HttpStatusCode.Conflict, "business_rule_violation");
        (await ReceiptAsync(client, token, id)).GetProperty("status").GetString().Should().Be("cancelled");
        (await AuditCountAsync(proof, tenant, null, "identity.data_request.cancel", id.ToString("D"))).Should().Be(1);

        await RunExportAsync(proof, tenant, null, id);
        var after = await ReceiptAsync(client, token, id);
        after.GetProperty("status").GetString().Should().Be("cancelled", "a cancelled request's job is a recorded no-op");
        after.GetProperty("artifact").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);

        // A terminal request frees the slot.
        await FileExportAsync(client, token);
    }

    [Fact]
    public async Task Revocation_deletes_the_chunks_at_once_and_an_expired_artifact_is_unreadable_and_cannot_be_revoked()
    {
        await using var proof = await CreatePreparedAsync();
        using var client = proof.Client();
        var token = proof.Token(EnglishLearner, English);
        var tenant = English.TenantId.Value;

        var (first, _) = await FileExportAsync(client, token);
        await RunExportAsync(proof, tenant, null, first);
        var artifact = (await ReceiptAsync(client, token, first)).GetProperty("artifact");
        var artifactId = Guid.Parse(artifact.GetProperty("id").GetString()!);
        var revoke = OwnPath + "/" + first.ToString("D") + "/artifact/revocations";
        using (var stale = await SendAsync(client, token, revoke, "POST", "{}", version: "99", key: Key()))
            await ProblemAsync(stale, HttpStatusCode.Conflict, "concurrency_conflict");
        using (var revoked = await SendAsync(client, token, revoke, "POST", "{}", version: Version(artifact), key: Key()))
            Number(Version(await JsonAsync(revoked))).Should().Be(Number(Version(artifact)) + 1, "the receipt carries the artifact's version");
        (await CountAsync(proof, tenant, null, "SELECT count(*) FROM public.data_export_chunks WHERE artifact_id=@artifact",
            ("artifact", artifactId))).Should().Be(0, "revocation purges the chunks at once");
        var receipt = (await ReceiptAsync(client, token, first)).GetProperty("artifact");
        receipt.GetProperty("status").GetString().Should().Be("revoked");
        receipt.GetProperty("readable").GetBoolean().Should().BeFalse();
        using (var ticket = await SendAsync(client, token, OwnPath + "/" + first.ToString("D") + "/artifact/tickets", "POST", "{}"))
        {
            var problem = await ProblemAsync(ticket, HttpStatusCode.Conflict, "business_rule_violation");
            problem.GetProperty("errors").GetProperty("artifact")[0].GetProperty("key").GetString().Should().Be("lockey_data_export_unavailable");
        }
        (await AuditCountAsync(proof, tenant, null, "identity.data_export.revoke", artifactId.ToString("D"))).Should().Be(1);

        // A second export, then its artifact moved past its expiry: expiry is a time check whether or not the purge ran.
        var (second, _) = await FileExportAsync(client, token);
        await RunExportAsync(proof, tenant, null, second);
        var current = (await ReceiptAsync(client, token, second)).GetProperty("artifact");
        await BackdateArtifactAsync(proof, tenant, Guid.Parse(current.GetProperty("id").GetString()!));
        var expired = (await ReceiptAsync(client, token, second)).GetProperty("artifact");
        expired.GetProperty("status").GetString().Should().Be("expired", "an artifact past its expiry is displayed expired");
        expired.GetProperty("readable").GetBoolean().Should().BeFalse();
        using (var ticket = await SendAsync(client, token, OwnPath + "/" + second.ToString("D") + "/artifact/tickets", "POST", "{}"))
            await ProblemAsync(ticket, HttpStatusCode.Conflict, "business_rule_violation");
        using (var late = await SendAsync(client, token, OwnPath + "/" + second.ToString("D") + "/artifact/revocations", "POST", "{}",
            version: Version(expired), key: Key()))
            await ProblemAsync(late, HttpStatusCode.Conflict, "business_rule_violation");
    }

    [Fact]
    public async Task A_dead_job_reads_as_failed_without_a_write_and_the_next_creation_stores_the_failure_and_files_anew()
    {
        await using var proof = await CreatePreparedAsync();
        using var client = proof.Client();
        var token = proof.Token(EnglishLearner, English);
        var tenant = English.TenantId.Value;
        var (id, version) = await FileExportAsync(client, token);
        await using (var dispatcher = (NpgsqlConnection)await PostgresFixture.OpenAsync(proof.Database.OutboxConnectionString))
            await ExecuteAsync(dispatcher, null, """
                UPDATE background_jobs SET phase='quarantined', quarantine_reason='unknown_outcome'
                WHERE definition_key='identity.data_export_tenant.v1' AND tenant_id=@tenant
                """, ("tenant", tenant));

        var shown = await ReceiptAsync(client, token, id);
        shown.GetProperty("status").GetString().Should().Be("failed", "the receipt reads failed from the dead job");
        shown.GetProperty("failureReason").GetString().Should().Be("execution_failed");
        Version(shown).Should().Be(version, "displaying failed writes nothing");

        var (next, _) = await FileExportAsync(client, token);
        next.Should().NotBe(id);
        var stored = await ReceiptAsync(client, token, id);
        stored.GetProperty("status").GetString().Should().Be("failed");
        Number(Version(stored)).Should().Be(Number(version) + 2, "the creation stored requested → fulfilling → failed before filing");
    }

    /// <summary>Moves an artifact eight days into the past as the owner, with its guard paused and restored.</summary>
    private static async Task BackdateArtifactAsync(IdentityAuthorizationHttpProof proof, Guid tenant, Guid artifact)
    {
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(proof.Database.MigrationConnectionString);
        await using var transaction = await owner.BeginTransactionAsync();
        await ExecuteAsync(owner, transaction, "ALTER TABLE public.data_export_artifacts DISABLE TRIGGER tg_data_export_artifacts_mutation_guard");
        await ScopeAsync(owner, transaction, tenant);
        await ExecuteAsync(owner, transaction, """
            UPDATE public.data_export_artifacts SET created_at=created_at - interval '8 days', expires_at=expires_at - interval '8 days'
            WHERE id=@id
            """, ("id", artifact));
        await ExecuteAsync(owner, transaction, "SET CONSTRAINTS ALL IMMEDIATE");
        await ExecuteAsync(owner, transaction, "ALTER TABLE public.data_export_artifacts ENABLE TRIGGER tg_data_export_artifacts_mutation_guard");
        await transaction.CommitAsync();
    }
}
