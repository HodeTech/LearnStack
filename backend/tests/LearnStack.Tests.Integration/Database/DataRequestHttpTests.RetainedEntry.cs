using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using LearnStack.Api.Authentication;
using LearnStack.Api.Tenancy;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.Tools.Seeder;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Serilog.Core;
using Serilog.Events;
using Xunit;
using static LearnStack.Tests.Integration.Database.IdentityAuthorizationHttpProof;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// ADR-0106 § 9 and ADR-0107 § 4 through the actual Program, signed provider tokens, the native session source and real
/// PostgreSQL as <c>learnstack_app</c>: the retained own-scope native entry of data requests for a Suspended person, a
/// person whose only Membership is Revoked and a person in several groups, with its closed-set and isolation refusals.
/// </summary>
public sealed partial class DataRequestHttpTests
{
    private const string EntryPath = "/api/v1/identity/data-request-entry";
    private const string RetainedHop = "data-request-entry-proof-public-hop-separate-purpose";
    private static readonly string RetainedNative = TenantAuthenticationIssuer.Base64Url(Enumerable.Range(81, 32).Select(value => (byte)value).ToArray());
    private static Guid LearnerMembershipId => SeedData.IdentityMemberships[1].MembershipId;

    [Fact]
    public async Task A_suspended_person_lists_files_cancels_and_downloads_through_the_retained_entry_only()
    {
        await using var proof = await CreatePreparedAsync();
        var logs = new RetainedLogCapture();
        using var client = RetainedClient(proof, logs);
        var tenant = English.TenantId.Value;
        var live = proof.Token(EnglishLearner, English);
        var native = NativeToken(proof, EnglishLearner);
        await ChangeStatusAsync(client, proof, LearnerMembershipId, "Suspended");

        // A Suspended person has no live data-request authority at all.
        using (var refused = await SendAsync(client, live, OwnPath))
            await ProblemAsync(refused, HttpStatusCode.NotFound, "not_found");

        // Discovery: the tenant-wide group on the real host, nothing else.
        using (var scopes = await EntrySendAsync(client, native, "/scopes"))
        {
            var page = await JsonAsync(scopes);
            page.GetProperty("items").EnumerateArray().Select(item => (item.GetProperty("tenantId").GetString(),
                item.GetProperty("organizationId").ValueKind)).Should().Equal((tenant.ToString("D"), JsonValueKind.Null));
            page.GetProperty("pageInfo").GetProperty("hasNext").GetBoolean().Should().BeFalse();
        }

        using (var empty = await EntrySendAsync(client, native, "/requests?group=tenant"))
            (await JsonAsync(empty)).GetProperty("items").GetArrayLength().Should().Be(0);

        // Creation is keyed and answers 202 with the resource and its ETag, and no Location; an exact replay files nothing.
        var key = Key();
        var (cancelled, version) = await EntryFileAsync(client, native, key);
        using (var replay = await EntrySendAsync(client, native, "/exports?group=tenant", "{}", key))
        {
            (await JsonAsync(replay, HttpStatusCode.Accepted)).GetProperty("id").GetString().Should().Be(cancelled.ToString("D"));
            replay.Headers.GetValues("Idempotency-Replayed").Should().Equal("true");
            replay.Headers.Location.Should().BeNull();
        }

        (await AuditCountAsync(proof, tenant, null, "identity.data_request.create_export", cancelled.ToString("D"))).Should().Be(1);

        // Cancellation is conditional: a missing precondition is 400, a stale one 409, the current one cancels.
        var target = JsonSerializer.Serialize(new { requestId = cancelled });
        using (var unconditional = await EntrySendAsync(client, native, "/cancellations?group=tenant", target, Key()))
            await ProblemAsync(unconditional, HttpStatusCode.BadRequest, "validation_failed");
        using (var stale = await EntrySendAsync(client, native, "/cancellations?group=tenant", target, Key(), "\"" + (Number(version) + 1) + "\""))
            await ProblemAsync(stale, HttpStatusCode.Conflict, "concurrency_conflict");
        var cancelKey = Key();
        string after;
        using (var cancel = await EntrySendAsync(client, native, "/cancellations?group=tenant", target, cancelKey, "\"" + version + "\""))
        {
            after = Version(await JsonAsync(cancel));
            cancel.Headers.ETag!.Tag.Should().Be("\"" + after + "\"");
        }

        using (var cancelReplay = await EntrySendAsync(client, native, "/cancellations?group=tenant", target, cancelKey, "\"" + version + "\""))
        {
            Version(await JsonAsync(cancelReplay)).Should().Be(after);
            cancelReplay.Headers.GetValues("Idempotency-Replayed").Should().Equal("true");
        }

        (await AuditCountAsync(proof, tenant, null, "identity.data_request.cancel", cancelled.ToString("D"))).Should().Be(1);

        // A new export runs, and its artifact downloads through the retained entry with its MUST rows.
        var (exported, _) = await EntryFileAsync(client, native, Key());
        await RunExportAsync(proof, tenant, null, exported);
        using (var list = await EntrySendAsync(client, native, "/requests?group=tenant"))
        {
            var items = (await JsonAsync(list)).GetProperty("items").EnumerateArray().ToArray();
            items.Select(item => item.GetProperty("id").GetString()).Should().Equal(exported.ToString("D"), cancelled.ToString("D"));
            items[0].GetProperty("status").GetString().Should().Be("completed");
            items[0].GetProperty("artifact").GetProperty("readable").GetBoolean().Should().BeTrue();
            items[1].GetProperty("status").GetString().Should().Be("cancelled");
            items[0].TryGetProperty("membershipIds", out _).Should().BeFalse();
        }

        var (ticket, bundle, chunks) = await EntryDownloadAsync(client, native, exported);
        using (var document = JsonDocument.Parse(bundle))
            document.RootElement.GetProperty("request").GetProperty("id").GetString().Should().Be(exported.ToString("D"));
        (await AuditCountAsync(proof, tenant, null, "identity.data_export.mint_ticket")).Should().Be(1);
        (await AuditCountAsync(proof, tenant, null, "identity.data_export.read_chunk")).Should().Be(chunks);
        using (var consumed = await EntryChunkAsync(client, native, exported, ticket, 0))
            await ProblemAsync(consumed, HttpStatusCode.NotFound, "not_found");

        // Nothing secret reached a log or an idempotency receipt.
        var rendered = string.Join('\n', logs.Entries.Select(entry => entry.RenderMessage(System.Globalization.CultureInfo.InvariantCulture) + " "
            + string.Join(' ', entry.Properties.Select(property => property.Key + "=" + property.Value))));
        rendered.Should().NotContain(ticket);
        rendered.Should().NotContain(Convert.ToBase64String(bundle.AsSpan(0, Math.Min(bundle.Length, 48))));
        (await CountAsync(proof, tenant, null, "SELECT count(*) FROM public.idempotency_keys WHERE convert_from(body, 'UTF8') LIKE '%' || @ticket || '%'",
            ("ticket", ticket))).Should().Be(0);
    }

    [Fact]
    public async Task A_person_whose_only_membership_is_revoked_reads_and_downloads_an_existing_export_and_may_file_again()
    {
        await using var proof = await CreatePreparedAsync();
        using var client = RetainedClient(proof);
        var tenant = English.TenantId.Value;
        var live = proof.Token(EnglishLearner, English);
        var (existing, _) = await FileExportAsync(client, live);
        await RunExportAsync(proof, tenant, null, existing);
        await ChangeStatusAsync(client, proof, LearnerMembershipId, "Revoked");
        using (var gone = await SendAsync(client, live, OwnPath + "/" + existing.ToString("D")))
            await ProblemAsync(gone, HttpStatusCode.NotFound, "not_found");

        var native = NativeToken(proof, EnglishLearner);
        using (var scopes = await EntrySendAsync(client, native, "/scopes"))
            (await JsonAsync(scopes)).GetProperty("items").GetArrayLength().Should().Be(1, "a Revoked tombstone retains its group");
        using (var list = await EntrySendAsync(client, native, "/requests?group=tenant"))
            (await JsonAsync(list)).GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetString())
                .Should().Equal(existing.ToString("D"));
        var (_, bundle, _) = await EntryDownloadAsync(client, native, existing);
        bundle.Should().NotBeEmpty();

        // ADR-0106 § 7: a Revoked tombstone counts as a Membership, so the person may file again in the group.
        var (again, _) = await EntryFileAsync(client, native, Key());
        again.Should().NotBe(existing);
        (await CountAsync(proof, tenant, null, "SELECT count(*) FROM public.data_subject_requests WHERE subject_user_id=@subject",
            ("subject", EnglishLearner.Value))).Should().Be(2);
    }

    [Fact]
    public async Task The_retained_entry_reaches_only_the_subjects_own_groups_on_the_real_host_and_only_its_own_requests()
    {
        await using var proof = await CreatePreparedAsync();
        using var client = RetainedClient(proof);
        var tenant = English.TenantId.Value;
        var branch = English.DefaultOrganization.OrganizationId;
        var sibling = English.SecondOrganization.OrganizationId;
        await proof.Database.AddMembershipAsync(English, EnglishLearner, branch);
        var native = NativeToken(proof, EnglishLearner);

        // A tenant host lists every retained group of the tenant, the tenant-wide group first.
        using (var scopes = await EntrySendAsync(client, native, "/scopes"))
            (await JsonAsync(scopes)).GetProperty("items").EnumerateArray()
                .Select(item => item.GetProperty("organizationId").ValueKind == JsonValueKind.Null ? "tenant" : item.GetProperty("organizationId").GetString())
                .Should().Equal("tenant", branch.Value.ToString("D"));
        using (var paged = await EntrySendAsync(client, native, "/scopes?limit=1"))
        {
            var page = await JsonAsync(paged);
            page.GetProperty("items").GetArrayLength().Should().Be(1);
            page.GetProperty("pageInfo").GetProperty("nextCursor").GetString().Should().Be("tenant");
        }

        using (var next = await EntrySendAsync(client, native, "/scopes?cursor=tenant&limit=1"))
            (await JsonAsync(next)).GetProperty("items")[0].GetProperty("organizationId").GetString().Should().Be(branch.Value.ToString("D"));

        // A group the person never held is not found, and so is the tenant-wide group of another tenant.
        using (var foreignBranch = await EntrySendAsync(client, native, "/requests?group=" + sibling.Value.ToString("D")))
            await ProblemAsync(foreignBranch, HttpStatusCode.NotFound, "not_found");
        using (var foreignTenant = await EntrySendAsync(client, native, "/requests?group=tenant", host: SeedData.Yoga.Host))
            await ProblemAsync(foreignTenant, HttpStatusCode.NotFound, "not_found");
        using (var foreignScopes = await EntrySendAsync(client, native, "/scopes", host: SeedData.Yoga.Host))
            (await JsonAsync(foreignScopes)).GetProperty("items").GetArrayLength().Should().Be(0);

        // An organization host caps discovery and selection to its own organization.
        const string branchHost = "retained-branch.learnstack.local";
        await proof.Database.ExecuteAsync(English, """
            INSERT INTO public.platform_host_to_tenant(host,tenant_id,organization_id,is_active,is_publicly_live)
            VALUES(@host,@tenant,@organization,true,true)
            """, new Dictionary<string, object> { ["host"] = branchHost, ["tenant"] = tenant, ["organization"] = branch.Value });
        using (var capped = await EntrySendAsync(client, native, "/scopes", host: branchHost))
            (await JsonAsync(capped)).GetProperty("items").EnumerateArray().Select(item => item.GetProperty("organizationId").GetString())
                .Should().Equal(branch.Value.ToString("D"));
        using (var tenantWide = await EntrySendAsync(client, native, "/requests?group=tenant", host: branchHost))
            await ProblemAsync(tenantWide, HttpStatusCode.NotFound, "not_found");
        using (var branchList = await EntrySendAsync(client, native, "/requests?group=" + branch.Value.ToString("D"), host: branchHost))
            await JsonAsync(branchList);

        // Another person's request in the same group is absent to every retained operation.
        var (foreign, foreignVersion) = await FileExportAsync(client, proof.Token(EnglishAdmin, English));
        await RunExportAsync(proof, tenant, null, foreign);
        var target = JsonSerializer.Serialize(new { requestId = foreign });
        using (var cancel = await EntrySendAsync(client, native, "/cancellations?group=tenant", target, Key(), "\"" + foreignVersion + "\""))
            await ProblemAsync(cancel, HttpStatusCode.NotFound, "not_found");
        using (var ticket = await EntrySendAsync(client, native, "/tickets?group=tenant", target))
            await ProblemAsync(ticket, HttpStatusCode.NotFound, "not_found");
        using (var listed = await EntrySendAsync(client, native, "/requests?group=tenant"))
            (await JsonAsync(listed)).GetProperty("items").GetArrayLength().Should().Be(0);

        // A request of another group is absent in this one.
        var (own, _) = await EntryFileAsync(client, native, Key(), "group=" + branch.Value.ToString("D"));
        using (var elsewhere = await EntrySendAsync(client, native, "/tickets?group=tenant", JsonSerializer.Serialize(new { requestId = own })))
            await ProblemAsync(elsewhere, HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task The_retained_entry_admits_only_its_closed_shape_credential_and_binding_and_a_ticket_never_crosses_paths()
    {
        await using var proof = await CreatePreparedAsync();
        using var client = RetainedClient(proof);
        var tenant = English.TenantId.Value;
        var live = proof.Token(EnglishLearner, English);
        var (id, _) = await FileExportAsync(client, live);
        await RunExportAsync(proof, tenant, null, id);
        var native = NativeToken(proof, EnglishLearner);

        // Without the native session source the retained entry does not exist; with a tenant selector it is malformed.
        using (var plain = await SendAsync(client, native, EntryPath + "/scopes"))
            await ProblemAsync(plain, HttpStatusCode.NotFound, "not_found");
        using (var selected = await EntrySendAsync(client, native, "/scopes",
                   mutate: request => request.Headers.Add(NativeSessionSourcePolicy.TenantHeader, tenant.ToString("D"))))
            selected.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        // The selector-free native credential reaches no live operation.
        using (var liveOperation = await EntrySendAsync(client, native, "", path: OwnPath))
            liveOperation.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        // Closed shape: no group, an unknown key, a key or precondition where none is taken.
        using (var groupless = await EntrySendAsync(client, native, "/requests"))
            await ProblemAsync(groupless, HttpStatusCode.BadRequest, "validation_failed");
        using (var extra = await EntrySendAsync(client, native, "/requests?group=tenant&membershipId=" + LearnerMembershipId.ToString("D")))
            await ProblemAsync(extra, HttpStatusCode.BadRequest, "validation_failed");
        var target = JsonSerializer.Serialize(new { requestId = id });
        using (var keyedTicket = await EntrySendAsync(client, native, "/tickets?group=tenant", target, Key()))
            await ProblemAsync(keyedTicket, HttpStatusCode.BadRequest, "validation_failed");

        // A ticket minted through the retained entry is bound to it, and a live ticket to the live path, for the same credential.
        using (var retained = await EntrySendAsync(client, live, "/tickets?group=tenant", target))
        {
            var ticket = (await JsonAsync(retained)).GetProperty("ticket").GetString()!;
            using (var crossed = await ChunkAsync(client, live, id, ticket, 0))
                await ProblemAsync(crossed, HttpStatusCode.NotFound, "not_found");
            var otherSession = AccountSiteHttpTests.Token(proof.Issuer, English, actor: EnglishLearner.Value,
                subject: IdentityPublicReadFixture.FoundationSubject(EnglishLearner), tokenId: "another-retained-session");
            using (var foreignSession = await EntryChunkAsync(client, otherSession, id, ticket, 0))
                await ProblemAsync(foreignSession, HttpStatusCode.NotFound, "not_found");
            using (var own = await EntryChunkAsync(client, live, id, ticket, 0))
                await JsonAsync(own);
        }

        using (var minted = await SendAsync(client, live, OwnPath + "/" + id.ToString("D") + "/artifact/tickets", "POST", "{}"))
        {
            var ticket = (await JsonAsync(minted)).GetProperty("ticket").GetString()!;
            using (var crossed = await EntryChunkAsync(client, live, id, ticket, 0))
                await ProblemAsync(crossed, HttpStatusCode.NotFound, "not_found");
        }

        // A person whose binding is no longer Active cannot enter at all.
        await TransitionUserAsync(proof, EnglishLearner, "Closing");
        using (var closing = await EntrySendAsync(client, native, "/scopes"))
            closing.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private static HttpClient RetainedClient(IdentityAuthorizationHttpProof proof, RetainedLogCapture? logs = null) =>
        proof.Database.ClientFor(English.Host, services =>
        {
            services.AddSingleton<IStartupFilter, RetainedLoopbackPeer>();
            if (logs is not null) services.AddSingleton<ILogEventSink>(logs);
        }, builder =>
        {
            builder.UseSetting("Tenancy:TrustedHop:Networks:0", "127.0.0.1/32");
            builder.UseSetting("Tenancy:TrustedHop:Secrets:0", RetainedHop);
            builder.UseSetting("Identity:NativeSession:CurrentSecret", RetainedNative);
        });

    /// <summary>A selector-free credential: the verified subject and actor, no tenant or organization claim.</summary>
    private static string NativeToken(IdentityAuthorizationHttpProof proof, UserId actor)
    {
        using var source = JsonDocument.Parse(proof.Issuer.Payload(("sub", IdentityPublicReadFixture.FoundationSubject(actor)),
            ("user_id", actor.Value.ToString("D"))));
        var claims = source.RootElement.EnumerateObject().Where(property => property.Name is not ("tenant_id" or "organization_id"))
            .ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal);
        return proof.Issuer.Token(JsonSerializer.Serialize(claims));
    }

    private static async Task<HttpResponseMessage> EntrySendAsync(HttpClient client, string token, string relative, string? body = null,
        string? key = null, string? ifMatch = null, string? host = null, string? path = null, Action<HttpRequestMessage>? mutate = null)
    {
        using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, (path ?? EntryPath) + relative);
        var effective = host ?? English.Host;
        request.Headers.Host = effective;
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add(TrustedHopOptions.HostHeaderName, effective);
        request.Headers.Add(TrustedHopOptions.SecretHeaderName, RetainedHop);
        request.Headers.Add("X-LearnStack-Visitor-Address", "198.51.100.24");
        request.Headers.Add(NativeSessionSourcePolicy.SecretHeader, RetainedNative);
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        if (ifMatch is not null) request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        mutate?.Invoke(request);
        return await client.SendAsync(request);
    }

    private static async Task<(Guid Id, string Version)> EntryFileAsync(HttpClient client, string token, string key, string group = "group=tenant")
    {
        using var response = await EntrySendAsync(client, token, "/exports?" + group, "{}", key);
        var accepted = await JsonAsync(response, HttpStatusCode.Accepted);
        accepted.EnumerateObject().Select(property => property.Name).Should().Equal("id", "kind", "status", "version");
        response.Headers.Location.Should().BeNull("the retained entry has no per-request route");
        response.Headers.ETag!.Tag.Should().Be("\"" + Version(accepted) + "\"");
        return (Guid.Parse(accepted.GetProperty("id").GetString()!), Version(accepted));
    }

    private static Task<HttpResponseMessage> EntryChunkAsync(HttpClient client, string token, Guid request, string ticket, int index) =>
        EntrySendAsync(client, token, "/chunks?group=tenant", JsonSerializer.Serialize(new { requestId = request, ticket, index }));

    private static async Task<(string Ticket, byte[] Bundle, int Chunks)> EntryDownloadAsync(HttpClient client, string token, Guid request)
    {
        using var minted = await EntrySendAsync(client, token, "/tickets?group=tenant", JsonSerializer.Serialize(new { requestId = request }));
        var grant = await JsonAsync(minted);
        grant.EnumerateObject().Select(property => property.Name).Should().Equal("ticket", "expiresAt", "chunkCount", "byteCount");
        var ticket = grant.GetProperty("ticket").GetString()!;
        var count = grant.GetProperty("chunkCount").GetInt32();
        var bytes = new List<byte>();
        for (var index = 0; index < count; index++)
        {
            using var read = await EntryChunkAsync(client, token, request, ticket, index);
            var chunk = await JsonAsync(read);
            chunk.GetProperty("index").GetInt32().Should().Be(index);
            bytes.AddRange(Convert.FromBase64String(chunk.GetProperty("bytes").GetString()!));
        }

        return (ticket, bytes.ToArray(), count);
    }

    /// <summary>The English administrator changes the learner's Membership status through the actual administration API.</summary>
    private static async Task ChangeStatusAsync(HttpClient client, IdentityAuthorizationHttpProof proof, Guid membership, string status)
    {
        var admin = proof.Token(EnglishAdmin, English);
        var path = DirectoryPath + "/" + membership.ToString("D");
        string version;
        using (var read = await SendAsync(client, admin, path))
            version = Version(await JsonAsync(read));
        using var changed = await SendAsync(client, admin, path + "/status", "PUT", "{\"status\":\"" + status + "\"}", version, Key());
        await JsonAsync(changed);
    }

    /// <summary>
    /// The production schema has no User closure writer yet (ADR-0107 § 7 lands it): this owner-only fixture
    /// accommodation moves the binding out of Active and restores the guard and policy exactly.
    /// </summary>
    private static async Task TransitionUserAsync(IdentityAuthorizationHttpProof proof, UserId user, string status)
    {
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(proof.Database.MigrationConnectionString);
        var originalCheck = await ScalarAsync<string>(owner, null,
            "SELECT pg_get_expr(polwithcheck,polrelid) FROM pg_policy WHERE polrelid='public.users'::regclass AND polname='users_isolation'");
        try
        {
            await ExecuteAsync(owner, null, $"""
                ALTER TABLE public.users DISABLE TRIGGER tg_users_no_update;
                ALTER POLICY users_isolation ON public.users WITH CHECK (({originalCheck}) OR
                    (current_user='learnstack_migration' AND id='{user.Value:D}'::uuid AND status='{status}'));
                """);
            await ExecuteAsync(owner, null, "UPDATE public.users SET status=@status WHERE id=@user", ("status", status), ("user", user.Value));
        }
        finally
        {
            await ExecuteAsync(owner, null, $"""
                ALTER POLICY users_isolation ON public.users WITH CHECK ({originalCheck});
                ALTER TABLE public.users ENABLE TRIGGER tg_users_no_update;
                """);
        }
    }

    private sealed class RetainedLoopbackPeer : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, following) => { context.Connection.RemoteIpAddress = IPAddress.Loopback; return following(context); });
            next(app);
        };
    }

    private sealed class RetainedLogCapture : ILogEventSink
    {
        internal ConcurrentQueue<LogEvent> Entries { get; } = new();
        public void Emit(LogEvent logEvent) => Entries.Enqueue(logEvent);
    }
}
