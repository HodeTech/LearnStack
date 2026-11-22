using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
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

        await RunExportAsync(proof, tenant, organization?.Value, id);

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

        // Nothing secret or personal reached a log: no ticket, no plaintext, no ciphertext.
        var rendered = string.Join('\n', logs.Entries.Select(entry => entry.RenderMessage(System.Globalization.CultureInfo.InvariantCulture) + " " + string.Join(' ',
            entry.Properties.Select(property => property.Key + "=" + property.Value))));
        rendered.Should().NotContain(ticket);
        rendered.Should().NotContain(Convert.ToBase64String(bundle.AsSpan(0, Math.Min(bundle.Length, 48))));

        // The receipt and the idempotency row hold no ticket or content.
        (await CountAsync(proof, tenant, null, "SELECT count(*) FROM public.idempotency_keys WHERE convert_from(body, 'UTF8') LIKE '%' || @ticket || '%'",
            ("ticket", ticket))).Should().Be(0);
    }

    private sealed class LogCapture : ILogEventSink
    {
        internal ConcurrentQueue<LogEvent> Entries { get; } = new();
        public void Emit(LogEvent logEvent) => Entries.Enqueue(logEvent);
    }
}
