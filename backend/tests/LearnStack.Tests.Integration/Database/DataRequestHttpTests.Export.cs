using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using LearnStack.Modules.Identity.Application.DataRequests;
using LearnStack.SharedKernel.Identifiers;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Core;
using Serilog.Events;
using Xunit;
using static LearnStack.Tests.Integration.Database.IdentityAuthorizationHttpProof;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class DataRequestHttpTests
{
    [Theory]
    [InlineData("tenant")]
    [InlineData("organization")]
    public async Task A_subject_exports_their_own_data_through_the_api_the_job_runtime_and_a_ticketed_download(string scope)
    {
        await using var proof = await CreatePreparedAsync();
        var organization = scope == "organization" ? English.DefaultOrganization.OrganizationId : (OrganizationId?)null;
        if (organization is { } branch)
        {
            await proof.Database.AddMembershipAsync(English, EnglishLearner, branch);
        }

        var logs = new LogCapture();
        using var client = proof.Client(services => services.AddSingleton<ILogEventSink>(logs));
        var token = organization is null ? proof.Token(EnglishLearner, English)
            : proof.Token(EnglishLearner, English, organization: true, claimedOrganization: organization);
        var tenant = English.TenantId.Value;
        var key = Key();

        var (id, version) = await FileExportAsync(client, token, key);
        version.Should().Be("0");
        (await AuditCountAsync(proof, tenant, organization?.Value, "identity.data_request.create_export", id.ToString("D"))).Should().Be(1);

        // An exact-key replay discloses the original request again after a fresh receipt read; it never files a second.
        using (var replay = await SendAsync(client, token, OwnPath + "/exports", "POST", "{}", key: key))
        {
            var replayed = await JsonAsync(replay, HttpStatusCode.Accepted);
            replayed.GetProperty("id").GetString().Should().Be(id.ToString("D"));
            replay.Headers.GetValues("Idempotency-Replayed").Should().Equal("true");
        }

        (await CountAsync(proof, tenant, organization?.Value,
            "SELECT count(*) FROM public.data_subject_requests WHERE organization_id IS NOT DISTINCT FROM @organization::uuid",
            ("organization", (object?)organization?.Value ?? DBNull.Value))).Should().Be(1, "a replay files nothing");

        // ADR-0106 § 7: one active export per subject and group.
        using (var conflict = await SendAsync(client, token, OwnPath + "/exports", "POST", "{}", key: Key()))
        {
            var problem = await ProblemAsync(conflict, HttpStatusCode.Conflict, "business_rule_violation");
            problem.GetProperty("errors").GetProperty("$")[0].GetProperty("key").GetString().Should().Be("lockey_request_already_active");
        }

        var pending = await ReceiptAsync(client, token, id);
        pending.GetProperty("status").GetString().Should().Be("requested");
        pending.GetProperty("origin").GetString().Should().Be("self");
        pending.GetProperty("artifact").ValueKind.Should().Be(JsonValueKind.Null);
        pending.TryGetProperty("membershipIds", out _).Should().BeFalse("the subject's receipt names no internal identifier");

        var jobLogs = new LogCapture();
        await RunExportAsync(proof, tenant, organization?.Value, id, jobLogs);

        var completed = await ReceiptAsync(client, token, id);
        completed.GetProperty("status").GetString().Should().Be("completed");
        completed.GetProperty("completedAt").GetString().Should().NotBeNull();
        completed.GetProperty("retained").EnumerateArray().Select(item => item.GetProperty("kind").GetString())
            .Should().Contain("request_receipt");
        var artifact = completed.GetProperty("artifact");
        artifact.GetProperty("status").GetString().Should().Be("available");
        artifact.GetProperty("readable").GetBoolean().Should().BeTrue();
        artifact.TryGetProperty("keyVersion", out _).Should().BeFalse("the receipt names no key material or label");

        using (var list = await SendAsync(client, token, OwnPath))
        {
            var page = await JsonAsync(list);
            page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetString()).Should().Equal(id.ToString("D"));
        }

        var (ticket, bundle) = await DownloadAsync(client, token, id);
        using (var document = JsonDocument.Parse(bundle))
        {
            document.RootElement.GetProperty("request").GetProperty("id").GetString().Should().Be(id.ToString("D"));
            document.RootElement.GetProperty("request").GetProperty("scope").GetString().Should().Be(scope);
            document.RootElement.GetProperty("sections").GetProperty("identity").GetProperty("memberships").GetArrayLength().Should().Be(1);
        }

        var artifactId = artifact.GetProperty("id").GetString()!;
        var chunks = artifact.GetProperty("chunkCount").GetInt32();
        (await AuditCountAsync(proof, tenant, organization?.Value, "identity.data_export.mint_ticket")).Should().Be(1);
        (await AuditCountAsync(proof, tenant, organization?.Value, "identity.data_export.read_chunk")).Should().Be(chunks,
            "every chunk read is its own MUST row");
        (await CountAsync(proof, tenant, organization?.Value,
            "SELECT count(*) FROM public.data_export_tickets WHERE artifact_id=@artifact AND consumed_at IS NOT NULL",
            ("artifact", Guid.Parse(artifactId)))).Should().Be(1, "serving the final chunk consumed the ticket");

        // A consumed cursor reads nothing more; a repeated download mints a new ticket.
        using (var reused = await ChunkAsync(client, token, id, ticket, 0))
            await ProblemAsync(reused, HttpStatusCode.NotFound, "not_found");
        var (_, again) = await DownloadAsync(client, token, id);
        again.Should().Equal(bundle);

        // Nothing secret or personal reached a log of the API or of the job's host, nor the state, before or change
        // columns of the export's audit rows: no ticket, no ticket or session-binding digest, no ciphertext and no
        // distinctive plaintext (the subject's profile name). The subject's UserId is checked in the logs only: the audit
        // rows record the request and artifact aggregates, whose subject and requester references are the audit trail.
        string canary;
        using (var document = JsonDocument.Parse(bundle))
            canary = document.RootElement.GetProperty("sections").GetProperty("identity").GetProperty("memberships")[0]
                .GetProperty("profile").GetProperty("display_name").GetString()!;
        canary.Should().NotBeNullOrWhiteSpace("the subject's profile name is in the bundle, so its absence elsewhere means something");
        var secrets = await SecretsAsync(proof, tenant, organization?.Value, Guid.Parse(artifactId), ticket);
        var rendered = Render(logs) + "\n" + Render(jobLogs);
        jobLogs.Entries.Should().NotBeEmpty("the job host's own log is captured");
        var audit = await AuditPayloadsAsync(proof, tenant, organization?.Value, "identity.data_request.create_export",
            "identity.data_request.fulfill_export", "identity.data_export.store", "identity.data_export.mint_ticket",
            "identity.data_export.read_chunk");
        foreach (var (where, text) in new[] { ("a log", rendered), ("an export audit row's state or changes", audit) })
        {
            text.Should().NotContain(ticket, where);
            foreach (var secret in secrets)
                text.Should().NotContainEquivalentOf(secret, where);
            text.Should().NotContain(Convert.ToBase64String(bundle.AsSpan(0, Math.Min(bundle.Length, 48))), where);
            text.Should().NotContain(canary, where);
        }

        rendered.Should().NotContainEquivalentOf(EnglishLearner.Value.ToString("D"),
            "no log names the subject beyond the actor enrichment every request carries");

        // The receipt and the idempotency row hold no ticket or content.
        (await CountAsync(proof, tenant, null, "SELECT count(*) FROM public.idempotency_keys WHERE convert_from(body, 'UTF8') LIKE '%' || @ticket || '%'",
            ("ticket", ticket))).Should().Be(0);
    }

    /// <summary>
    /// Every captured event, message, properties and exception. The actor enrichment (<c>user.id</c> and <c>UserId</c>)
    /// is left out: it is stamped on every request's events by the observability pipeline (Observability Standards), not
    /// written by the export, and the subject is the actor here. Every other property and every message is kept.
    /// </summary>
    private static string Render(LogCapture logs) => string.Join('\n', logs.Entries.Select(entry =>
        entry.RenderMessage(System.Globalization.CultureInfo.InvariantCulture) + " " + string.Join(' ',
            entry.Properties.Where(property => property.Key is not ("user.id" or "UserId"))
                .Select(property => property.Key + "=" + property.Value))
        + " " + entry.Exception));

    /// <summary>The digests and ciphertext prefixes the database holds for the artifact, in hex and base64.</summary>
    private static async Task<string[]> SecretsAsync(IdentityAuthorizationHttpProof proof, Guid tenant, Guid? organization, Guid artifact,
        string ticket)
    {
        await using var app = await OpenAppAsync(proof);
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, tenant, organization);
        var stored = await StringsAsync(app, transaction, """
            SELECT encode(session_binding_digest, 'hex') FROM public.data_export_tickets WHERE artifact_id = @artifact
            UNION ALL SELECT encode(digest, 'hex') FROM public.data_export_tickets WHERE artifact_id = @artifact
            UNION ALL SELECT encode(substring(ciphertext FROM 1 FOR 24), 'hex') FROM public.data_export_chunks WHERE artifact_id = @artifact
            UNION ALL SELECT encode(substring(ciphertext FROM 1 FOR 24), 'base64') FROM public.data_export_chunks WHERE artifact_id = @artifact
            UNION ALL SELECT encode(nonce, 'hex') FROM public.data_export_chunks WHERE artifact_id = @artifact
            """, ("artifact", artifact));
        stored.Should().Contain(DataExportTicketCodec.TryDigest(ticket)!, "the ticket's stored digest is among the values checked");
        return [.. stored];
    }

    /// <summary>The before, after and changes columns of the tenant's audit rows of the given operations, concatenated.</summary>
    private static async Task<string> AuditPayloadsAsync(IdentityAuthorizationHttpProof proof, Guid tenant, Guid? organization,
        params string[] operations)
    {
        await using var app = await OpenAppAsync(proof);
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, tenant, organization);
        var rows = await StringsAsync(app, transaction, """
            SELECT operation || ' ' || coalesce(before_state::text, '') || ' ' || coalesce(after_state::text, '') || ' '
                || coalesce(changes::text, '')
            FROM public.audit_log WHERE operation = ANY(@operations)
            """, ("operations", operations));
        rows.Select(row => row.Split(' ')[0]).Distinct().Should().Contain(operations, "every export operation wrote its row");
        return string.Join('\n', rows);
    }

    private static async Task<List<string>> StringsAsync(Npgsql.NpgsqlConnection connection, Npgsql.NpgsqlTransaction transaction,
        string sql, params (string Name, object Value)[] values)
    {
        await using var command = new Npgsql.NpgsqlCommand(sql, connection, transaction);
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value);
        var found = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) found.Add(reader.GetString(0));
        return found;
    }

    private sealed class LogCapture : ILogEventSink
    {
        internal ConcurrentQueue<LogEvent> Entries { get; } = new();
        public void Emit(LogEvent logEvent) => Entries.Enqueue(logEvent);
    }
}
