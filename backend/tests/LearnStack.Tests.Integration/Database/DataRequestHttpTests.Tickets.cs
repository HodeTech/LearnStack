using System.Net;
using System.Text.Json;
using FluentAssertions;
using Npgsql;
using Xunit;
using static LearnStack.Tests.Integration.Database.IdentityAuthorizationHttpProof;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class DataRequestHttpTests
{
    [Fact]
    public async Task A_ticket_reads_only_in_its_own_session_for_its_own_artifact_until_it_expires_and_never_when_tampered()
    {
        await using var proof = await CreatePreparedAsync();
        using var client = proof.Client();
        var session = proof.Token(EnglishLearner, English);
        var tenant = English.TenantId.Value;
        var (first, _) = await FileExportAsync(client, session);
        await RunExportAsync(proof, tenant, null, first);

        using var minted = await SendAsync(client, session, OwnPath + "/" + first.ToString("D") + "/artifact/tickets", "POST", "{}");
        var ticket = (await JsonAsync(minted)).GetProperty("ticket").GetString()!;
        minted.Headers.CacheControl!.NoStore.Should().BeTrue();

        // Another session of the same person: a different validated credential.
        var other = AccountSiteHttpTests.Token(proof.Issuer, English, actor: EnglishLearner.Value,
            subject: IdentityPublicReadFixture.FoundationSubject(EnglishLearner), tokenId: "another-session");
        other.Should().NotBe(session);
        using (var foreignSession = await ChunkAsync(client, other, first, ticket, 0))
            await ProblemAsync(foreignSession, HttpStatusCode.NotFound, "not_found");

        // A tampered ticket is a well-formed unknown one; a malformed one never reaches a lookup.
        var tampered = (ticket[0] == 'A' ? 'B' : 'A') + ticket[1..];
        using (var forged = await ChunkAsync(client, session, first, tampered, 0))
            await ProblemAsync(forged, HttpStatusCode.NotFound, "not_found");
        using (var malformed = await ChunkAsync(client, session, first, ticket + "=", 0))
            await ProblemAsync(malformed, HttpStatusCode.BadRequest, "validation_failed");
        using (var negative = await ChunkAsync(client, session, first, ticket, -1))
            await ProblemAsync(negative, HttpStatusCode.BadRequest, "validation_failed");
        using (var beyond = await ChunkAsync(client, session, first, ticket, 512))
            await ProblemAsync(beyond, HttpStatusCode.NotFound, "not_found");

        // A ticket never travels in a URL, and the ticket operations take no key or precondition.
        using (var query = await SendAsync(client, session, OwnPath + "/" + first.ToString("D") + "/artifact/chunks?ticket=" + ticket, "POST",
            JsonSerializer.Serialize(new { ticket, index = 0 })))
            await ProblemAsync(query, HttpStatusCode.BadRequest, "validation_failed");
        using (var keyed = await SendAsync(client, session, OwnPath + "/" + first.ToString("D") + "/artifact/tickets", "POST", "{}", key: Key()))
            await ProblemAsync(keyed, HttpStatusCode.BadRequest, "validation_failed");

        // The ticket binds its artifact: presented for another request of the same person it is absent.
        await using (var app = await OpenAppAsync(proof))
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await ScopeAsync(app, transaction, tenant);
            (await ScalarAsync<long>(app, transaction, "SELECT count(*) FROM public.data_export_tickets WHERE consumed_at IS NULL"))
                .Should().Be(1);
        }

        var (second, _) = await FileExportAsync(client, session, path: OwnPath + "/exports");
        await RunExportAsync(proof, tenant, null, second);
        using (var wrongArtifact = await ChunkAsync(client, session, second, ticket, 0))
            await ProblemAsync(wrongArtifact, HttpStatusCode.NotFound, "not_found");

        // The second export revoked the first artifact on completion; its ticket reads nothing now.
        using (var revoked = await ChunkAsync(client, session, first, ticket, 0))
            await ProblemAsync(revoked, HttpStatusCode.NotFound, "not_found");

        // An expired ticket refuses although unconsumed.
        using var fresh = await SendAsync(client, session, OwnPath + "/" + second.ToString("D") + "/artifact/tickets", "POST", "{}");
        var current = (await JsonAsync(fresh)).GetProperty("ticket").GetString()!;
        using (var ok = await ChunkAsync(client, session, second, current, 0))
            await JsonAsync(ok);
        await BackdateTicketsAsync(proof, tenant);
        using (var late = await ChunkAsync(client, session, second, current, 0))
            await ProblemAsync(late, HttpStatusCode.NotFound, "not_found");

        // An administrator who files nothing and is not the subject reaches no ticket.
        using (var admin = await SendAsync(client, proof.Token(EnglishAdmin, English), OwnPath + "/" + second.ToString("D") + "/artifact/tickets", "POST", "{}"))
            await ProblemAsync(admin, HttpStatusCode.NotFound, "not_found");
    }

    [Theory]
    [InlineData("identity.data_export.mint_ticket", "live")]
    [InlineData("identity.data_export.read_chunk", "live")]
    [InlineData("identity.data_request.read", "live")]
    [InlineData("identity.data_request.list", "live")]
    [InlineData("identity.data_export.mint_ticket", "retained")]
    [InlineData("identity.data_export.read_chunk", "retained")]
    public async Task A_disclosure_whose_MUST_audit_row_cannot_be_written_is_refused(string operation, string entry)
    {
        await using var proof = await CreatePreparedAsync();
        using var client = entry == "retained" ? RetainedClient(proof) : proof.Client();
        var token = proof.Token(EnglishLearner, English);
        var native = NativeToken(proof, EnglishLearner);
        var tenant = English.TenantId.Value;
        var (id, _) = await FileExportAsync(client, token);
        await RunExportAsync(proof, tenant, null, id);
        if (entry == "retained")
            await ChangeStatusAsync(client, proof, LearnerMembershipId, "Suspended");
        string? ticket = null;
        if (operation == "identity.data_export.read_chunk")
        {
            using var minted = entry == "retained"
                ? await EntrySendAsync(client, native, "/tickets?group=tenant", JsonSerializer.Serialize(new { requestId = id }))
                : await SendAsync(client, token, OwnPath + "/" + id.ToString("D") + "/artifact/tickets", "POST", "{}");
            ticket = (await JsonAsync(minted)).GetProperty("ticket").GetString();
        }

        await proof.OwnerAsync($$"""
            CREATE FUNCTION data_request_audit_refusal() RETURNS trigger LANGUAGE plpgsql SECURITY INVOKER SET search_path=pg_catalog AS $$
            BEGIN
                IF NEW.operation='{{operation}}' AND NEW.outcome='success' THEN
                    RAISE EXCEPTION 'fixed data request audit refusal' USING ERRCODE='23514';
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER tg_data_request_audit_refusal BEFORE INSERT ON public.audit_log FOR EACH ROW EXECUTE FUNCTION data_request_audit_refusal();
            """);
        try
        {
            using var refused = (operation, entry) switch
            {
                ("identity.data_export.mint_ticket", "retained") => await EntrySendAsync(client, native, "/tickets?group=tenant",
                    JsonSerializer.Serialize(new { requestId = id })),
                ("identity.data_export.read_chunk", "retained") => await EntryChunkAsync(client, native, id, ticket!, 0),
                ("identity.data_export.mint_ticket", _) => await SendAsync(client, token, OwnPath + "/" + id.ToString("D") + "/artifact/tickets", "POST", "{}"),
                ("identity.data_export.read_chunk", _) => await ChunkAsync(client, token, id, ticket!, 0),
                ("identity.data_request.list", _) => await SendAsync(client, proof.Token(EnglishAdmin, English), AdminPath),
                _ => await SendAsync(client, proof.Token(EnglishAdmin, English), AdminPath + "/" + id.ToString("D")),
            };
            var text = await refused.Content.ReadAsStringAsync();
            refused.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, text);
            text.Should().Contain("audit_unavailable");
            text.Should().NotContain("\"ticket\"").And.NotContain("\"bytes\"").And.NotContain("\"status\":\"completed\"")
                .And.NotContain("\"items\"");
            if (operation == "identity.data_export.mint_ticket")
                (await CountAsync(proof, tenant, null, "SELECT count(*) FROM public.data_export_tickets")).Should().Be(0, "nothing committed");
        }
        finally
        {
            await proof.OwnerAsync("DROP TRIGGER tg_data_request_audit_refusal ON public.audit_log; DROP FUNCTION data_request_audit_refusal();");
        }
    }

    /// <summary>Moves every ticket of the tenant past its fifteen minutes as the owner, with its guard paused and restored.</summary>
    private static async Task BackdateTicketsAsync(IdentityAuthorizationHttpProof proof, Guid tenant)
    {
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(proof.Database.MigrationConnectionString);
        await using var transaction = await owner.BeginTransactionAsync();
        await ExecuteAsync(owner, transaction, "ALTER TABLE public.data_export_tickets DISABLE TRIGGER tg_data_export_tickets_mutation_guard");
        await ScopeAsync(owner, transaction, tenant);
        await ExecuteAsync(owner, transaction, """
            UPDATE public.data_export_tickets SET created_at=created_at - interval '1 hour', expires_at=expires_at - interval '1 hour'
            WHERE consumed_at IS NULL
            """);
        await ExecuteAsync(owner, transaction, "ALTER TABLE public.data_export_tickets ENABLE TRIGGER tg_data_export_tickets_mutation_guard");
        await transaction.CommitAsync();
    }
}
