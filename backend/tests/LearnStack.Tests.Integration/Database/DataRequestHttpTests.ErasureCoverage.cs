using System.Net;
using System.Text.Json;
using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Modules.Identity.Application.DataRequests;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Permissions;
using LearnStack.Tools.Seeder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;
using static LearnStack.Tests.Integration.Database.IdentityAuthorizationHttpProof;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class DataRequestHttpTests
{
    /// <summary>
    /// ADR-0106 § 3: an organization administrator files an erasure only for a person of their own organization. A
    /// Membership of another branch is as absent as it is for an export, files nothing and writes no row; the own branch's
    /// is accepted, and the request it files is one job of the organization definition, due at the request's execution
    /// time, carrying nothing but the request's identifier.
    /// </summary>
    [Fact]
    public async Task An_organization_administrator_files_an_erasure_for_their_own_branch_only_and_the_job_is_stored_due_at_its_execution_time()
    {
        await using var proof = await CreatePreparedAsync();
        var branch = English.DefaultOrganization.OrganizationId;
        var other = English.SecondOrganization.OrganizationId;
        var tenant = English.TenantId.Value;
        await proof.Database.AddMembershipAsync(English, YogaAdmin, branch, BuiltInRoleKey.OrgAdmin);
        var branchMember = await proof.Database.AddMembershipAsync(English, EnglishLearner, branch);
        var otherMember = await proof.Database.AddMembershipAsync(English, EnglishLearner, other);
        await proof.SetMembershipStatusAsync(English, EnglishAdmin, branchMember, "Revoked", branch);
        await proof.SetMembershipStatusAsync(English, EnglishAdmin, otherMember, "Revoked", other);
        using var client = proof.Client();
        var branchAdmin = proof.Token(YogaAdmin, English, organization: true, claimedOrganization: branch);

        using (var crossed = await SendAsync(client, branchAdmin, AdminPath + "/erasures", "POST",
            JsonSerializer.Serialize(new { membershipId = otherMember }), key: Key()))
            await ProblemAsync(crossed, HttpStatusCode.NotFound, "not_found");
        (await CountAsync(proof, tenant, other.Value, "SELECT count(*) FROM public.data_subject_requests")).Should().Be(0,
            "the refused filing wrote no request in the other branch");
        (await CountAsync(proof, tenant, branch.Value, "SELECT count(*) FROM public.data_subject_requests")).Should().Be(0);
        (await AuditCountAsync(proof, tenant, other.Value, "identity.data_request.create_erasure")).Should().Be(0);
        (await AuditCountAsync(proof, tenant, branch.Value, "identity.data_request.create_erasure")).Should().Be(0);
        (await CountAsync(proof, tenant, other.Value, "SELECT count(*) FROM public.background_jobs")).Should().Be(0, "and scheduled no job");

        var (id, _) = await FileErasureAsync(client, branchAdmin, AdminPath + "/erasures",
            JsonSerializer.Serialize(new { membershipId = branchMember }));
        (await AuditCountAsync(proof, tenant, branch.Value, "identity.data_request.create_erasure", id.ToString("D"))).Should().Be(1);
        var receipt = await ReceiptAsync(client, branchAdmin, id, AdminPath);
        receipt.GetProperty("origin").GetString().Should().Be("administrator");
        await AssertErasureJobAsync(proof, tenant, branch.Value, id, receipt);
        (await CountAsync(proof, tenant, other.Value, "SELECT count(*) FROM public.data_subject_requests")).Should().Be(0);
    }

    /// <summary>
    /// ADR-0107 § 5 and § 9 through the API: a person's own erasure is one tenant-wide job due at its execution time; once the
    /// job has run, the person still has an Active <c>users</c> row (tenant erasure never touches it) but no Active Membership
    /// in the group, so they have no live route there, exactly as for a Suspended person, while the retained entry still
    /// answers with the request, which waits for the operator's privileged step.
    /// </summary>
    [Fact]
    public async Task A_person_erased_by_the_job_keeps_their_user_but_has_no_live_route_and_the_retained_entry_still_answers()
    {
        await using var proof = await CreatePreparedAsync();
        using var client = RetainedClient(proof);
        var tenant = English.TenantId.Value;
        var live = proof.Token(EnglishLearner, English);
        var native = NativeToken(proof, EnglishLearner);
        (await SendAsync(client, live, OwnPath)).StatusCode.Should().Be(HttpStatusCode.OK, "before the erasure the person's live route answers");

        var (id, _) = await FileErasureAsync(client, live, OwnPath + "/erasures", "{}");
        await AssertErasureJobAsync(proof, tenant, null, id, await ReceiptAsync(client, live, id));
        await DataErasureTestKit.BackdateAsync(proof.Database.MigrationConnectionString, tenant, null, id);
        await RunErasureAsync(proof, tenant, null, id);

        // The Membership is now the Revoked tombstone; the User row, which only the operator's closure ends, is Active.
        await using (var owner = (Npgsql.NpgsqlConnection)await PostgresFixture.OpenAsync(proof.Database.MigrationConnectionString))
            (await IdentityMembershipFixture.ScalarAsync<string>(owner, null, "SELECT status FROM public.users WHERE id=@id",
                ("id", EnglishLearner.Value))).Should().Be("Active");
        (await CountAsync(proof, tenant, null, "SELECT count(*) FROM public.memberships WHERE id=@id AND status='Revoked'",
            ("id", LearnerMembershipId))).Should().Be(1);
        using (var list = await SendAsync(client, live, OwnPath))
            await ProblemAsync(list, HttpStatusCode.NotFound, "not_found");
        using (var again = await SendAsync(client, live, OwnPath + "/erasures", "POST", "{}", key: Key()))
            await ProblemAsync(again, HttpStatusCode.NotFound, "not_found");
        using (var exports = await SendAsync(client, live, OwnPath + "/exports", "POST", "{}", key: Key()))
            await ProblemAsync(exports, HttpStatusCode.NotFound, "not_found");

        using (var scopes = await EntrySendAsync(client, native, "/scopes"))
            (await JsonAsync(scopes)).GetProperty("items").GetArrayLength().Should().Be(1, "the group is still the person's to reach");
        using (var requests = await EntrySendAsync(client, native, "/requests?group=tenant"))
        {
            var items = (await JsonAsync(requests)).GetProperty("items").EnumerateArray().ToArray();
            items.Select(item => item.GetProperty("id").GetString()).Should().Equal(id.ToString("D"));
            items[0].GetProperty("status").GetString().Should().Be("awaiting_privileged_step");
        }
    }

    /// <summary>
    /// ADR-0106 § 7: a hold the platform reports refuses creation, on the person's own route and on the administrator's, with
    /// its closed code, and files nothing.
    /// </summary>
    [Fact]
    public async Task A_hold_refuses_an_erasure_on_the_own_and_the_administrator_routes_and_files_nothing()
    {
        await using var proof = await CreatePreparedAsync();
        using var client = proof.Client(services => services.AddSingleton<IDataRequestHoldSource>(new AlwaysHold()));
        var tenant = English.TenantId.Value;
        var learner = proof.Token(EnglishLearner, English);
        var admin = proof.Token(EnglishAdmin, English);

        using (var own = await SendAsync(client, learner, OwnPath + "/erasures", "POST", "{}", key: Key()))
            (await ProblemAsync(own, HttpStatusCode.Conflict, "business_rule_violation")).GetProperty("errors").GetProperty("$")[0]
                .GetProperty("key").GetString().Should().Be("lockey_blocked_hold");
        await ChangeStatusAsync(client, proof, LearnerMembership, "Revoked");
        using (var byAdmin = await SendAsync(client, admin, AdminPath + "/erasures", "POST",
            JsonSerializer.Serialize(new { membershipId = LearnerMembership }), key: Key()))
            (await ProblemAsync(byAdmin, HttpStatusCode.Conflict, "business_rule_violation")).GetProperty("errors").GetProperty("$")[0]
                .GetProperty("key").GetString().Should().Be("lockey_blocked_hold");
        (await CountAsync(proof, tenant, null, "SELECT count(*) FROM public.data_subject_requests")).Should().Be(0);
        (await AuditCountAsync(proof, tenant, null, "identity.data_request.create_erasure")).Should().Be(0);
    }

    /// <summary>
    /// ADR-0106 § 10: a repeated own erasure creation with the same key and body answers the stored receipt and files
    /// nothing again; the same key on another operation is a key reuse, not a replay.
    /// </summary>
    [Fact]
    public async Task An_own_erasure_creation_replays_exactly_and_the_same_key_on_an_export_is_a_key_reuse()
    {
        await using var proof = await CreatePreparedAsync();
        using var client = proof.Client();
        var tenant = English.TenantId.Value;
        var learner = proof.Token(EnglishLearner, English);
        var key = Key();

        string location;
        string etag;
        Guid id;
        using (var first = await SendAsync(client, learner, OwnPath + "/erasures", "POST", "{}", key: key))
        {
            var accepted = await JsonAsync(first, HttpStatusCode.Accepted);
            id = Guid.Parse(accepted.GetProperty("id").GetString()!);
            location = first.Headers.Location!.OriginalString;
            etag = first.Headers.ETag!.Tag;
            first.Headers.Contains("Idempotency-Replayed").Should().BeFalse();
        }

        using (var replay = await SendAsync(client, learner, OwnPath + "/erasures", "POST", "{}", key: key))
        {
            var replayed = await JsonAsync(replay, HttpStatusCode.Accepted);
            replayed.GetProperty("id").GetString().Should().Be(id.ToString("D"));
            replayed.GetProperty("kind").GetString().Should().Be("erasure");
            replayed.GetProperty("status").GetString().Should().Be("scheduled");
            replay.Headers.GetValues("Idempotency-Replayed").Should().Equal("true");
            replay.Headers.Location!.OriginalString.Should().Be(location);
            replay.Headers.ETag!.Tag.Should().Be(etag);
        }

        (await CountAsync(proof, tenant, null, "SELECT count(*) FROM public.data_subject_requests WHERE kind='erasure'")).Should().Be(1);
        (await AuditCountAsync(proof, tenant, null, "identity.data_request.create_erasure")).Should().Be(1, "a replay audits nothing");

        // The same key on /exports: the fingerprint carries the operation, so this is a different request under a used key.
        var exportKey = Key();
        await using (var other = await CreatePreparedAsync())
        {
            using var otherClient = other.Client();
            var otherLearner = other.Token(EnglishLearner, English);
            await FileExportAsync(otherClient, otherLearner, exportKey);
            using var reused = await SendAsync(otherClient, otherLearner, OwnPath + "/erasures", "POST", "{}", key: exportKey);
            await ProblemAsync(reused, HttpStatusCode.Conflict, "idempotency_key_reuse");
            (await CountAsync(other, tenant, null, "SELECT count(*) FROM public.data_subject_requests WHERE kind='erasure'")).Should().Be(0);
        }
    }

    /// <summary>
    /// The administrator's erasure creation replays exactly, and the same key with another <c>membershipId</c> is a key reuse
    /// that files nothing.
    /// </summary>
    [Fact]
    public async Task An_administrator_erasure_creation_replays_exactly_and_another_membership_under_the_key_is_a_key_reuse()
    {
        await using var proof = await CreatePreparedAsync();
        using var client = proof.Client();
        var tenant = English.TenantId.Value;
        var admin = proof.Token(EnglishAdmin, English);
        await ChangeStatusAsync(client, proof, LearnerMembership, "Revoked");
        var body = JsonSerializer.Serialize(new { membershipId = LearnerMembership });
        var key = Key();

        Guid id;
        string location;
        using (var first = await SendAsync(client, admin, AdminPath + "/erasures", "POST", body, key: key))
        {
            id = Guid.Parse((await JsonAsync(first, HttpStatusCode.Accepted)).GetProperty("id").GetString()!);
            location = first.Headers.Location!.OriginalString;
        }

        using (var replay = await SendAsync(client, admin, AdminPath + "/erasures", "POST", body, key: key))
        {
            (await JsonAsync(replay, HttpStatusCode.Accepted)).GetProperty("id").GetString().Should().Be(id.ToString("D"));
            replay.Headers.GetValues("Idempotency-Replayed").Should().Equal("true");
            replay.Headers.Location!.OriginalString.Should().Be(location);
        }

        using (var changed = await SendAsync(client, admin, AdminPath + "/erasures", "POST",
            JsonSerializer.Serialize(new { membershipId = SeedData.IdentityMemberships[0].MembershipId }), key: key))
            await ProblemAsync(changed, HttpStatusCode.Conflict, "idempotency_key_reuse");
        (await CountAsync(proof, tenant, null, "SELECT count(*) FROM public.data_subject_requests WHERE kind='erasure'")).Should().Be(1);
        (await AuditCountAsync(proof, tenant, null, "identity.data_request.create_erasure")).Should().Be(1);
    }

    /// <summary>
    /// The retained entry's erasure creation replays exactly with no <c>Location</c>, and its artifact revocation replays with
    /// the same key and <c>If-Match</c>: the stored answer again, one audit row, the artifact revoked once.
    /// </summary>
    [Fact]
    public async Task The_retained_entry_replays_an_erasure_creation_and_an_artifact_revocation_exactly()
    {
        await using var proof = await CreatePreparedAsync();
        using var client = RetainedClient(proof);
        var tenant = English.TenantId.Value;
        var native = NativeToken(proof, EnglishLearner);
        var (export, _) = await FileExportAsync(client, proof.Token(EnglishLearner, English));
        await RunExportAsync(proof, tenant, null, export);
        await ChangeStatusAsync(client, proof, LearnerMembershipId, "Suspended");

        var key = Key();
        string id;
        using (var first = await EntrySendAsync(client, native, "/erasures?group=tenant", "{}", key))
        {
            id = (await JsonAsync(first, HttpStatusCode.Accepted)).GetProperty("id").GetString()!;
            first.Headers.Location.Should().BeNull();
        }

        using (var replay = await EntrySendAsync(client, native, "/erasures?group=tenant", "{}", key))
        {
            var replayed = await JsonAsync(replay, HttpStatusCode.Accepted);
            replayed.GetProperty("id").GetString().Should().Be(id);
            replayed.GetProperty("kind").GetString().Should().Be("erasure");
            replay.Headers.GetValues("Idempotency-Replayed").Should().Equal("true");
            replay.Headers.Location.Should().BeNull("the retained entry has no per-request route");
        }

        (await AuditCountAsync(proof, tenant, null, "identity.data_request.create_erasure", id)).Should().Be(1);
        (await CountAsync(proof, tenant, null, "SELECT count(*) FROM public.data_subject_requests WHERE kind='erasure'")).Should().Be(1);

        JsonElement artifact;
        using (var page = await EntrySendAsync(client, native, "/requests?group=tenant"))
            artifact = (await JsonAsync(page)).GetProperty("items").EnumerateArray()
                .Single(item => item.GetProperty("id").GetString() == export.ToString("D")).GetProperty("artifact");
        var body = JsonSerializer.Serialize(new { requestId = export });
        var revokeKey = Key();
        var ifMatch = "\"" + Version(artifact) + "\"";
        string revokedVersion;
        using (var revoked = await EntrySendAsync(client, native, "/revocations?group=tenant", body, revokeKey, ifMatch))
            revokedVersion = Version(await JsonAsync(revoked));
        using (var replay = await EntrySendAsync(client, native, "/revocations?group=tenant", body, revokeKey, ifMatch))
        {
            Version(await JsonAsync(replay)).Should().Be(revokedVersion, "the stored answer, not a second revocation of a revoked artifact");
            replay.Headers.GetValues("Idempotency-Replayed").Should().Equal("true");
            replay.Headers.Location.Should().BeNull();
        }

        (await AuditCountAsync(proof, tenant, null, "identity.data_export.revoke")).Should().Be(1);
    }

    /// <summary>The request's stored erasure job: its definition, group, identifier-only parameters and due time.</summary>
    private static async Task AssertErasureJobAsync(IdentityAuthorizationHttpProof proof, Guid tenant, Guid? organization, Guid request,
        JsonElement receipt)
    {
        var definition = organization is null ? "identity.data_erasure_tenant.v1" : "identity.data_erasure_organization.v1";
        var jobs = new List<Guid>();
        await using (var app = await OpenAppAsync(proof))
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await IdentityMembershipFixture.ScopeAsync(app, transaction, tenant, organization);
            await using var command = new Npgsql.NpgsqlCommand(
                "SELECT id FROM public.background_jobs WHERE definition_key LIKE 'identity.data_erasure%' AND parameters::text LIKE '%' || @request || '%'",
                app, transaction);
            command.Parameters.AddWithValue("request", request.ToString("D"));
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) jobs.Add(reader.GetGuid(0));
        }

        jobs.Should().ContainSingle("filing schedules exactly one job for the request");
        var row = (await BackgroundJobRuntimeRows.ReadAsync(proof.Database.AppConnectionString, BackgroundJobId.From(jobs[0]), organization, tenant))!;
        (row.Definition, row.TenantId, row.OrganizationId, row.Profile).Should().Be((definition, tenant, organization, "transactional"));
        using var parameters = JsonDocument.Parse(row.Parameters);
        parameters.RootElement.EnumerateObject().Select(property => (property.Name, property.Value.GetString())).Should()
            .Equal([("RequestId", (string?)request.ToString("D"))], "identifiers only: no subject, no Membership");
        var executeAt = DateTimeOffset.Parse(receipt.GetProperty("executeAt").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        row.OriginalDue.Should().BeCloseTo(executeAt, TimeSpan.FromMilliseconds(1), "the job waits for the window to end");
    }

    /// <summary>Runs the request's erasure job through the actual job runtime of a host on the same database.</summary>
    private static async Task RunErasureAsync(IdentityAuthorizationHttpProof proof, Guid tenant, Guid? organization, Guid request)
    {
        // The runtime dispatches a job only once it is due, and the job the API stored waits for the window; the request's
        // window is moved back by the caller, so the due job is the one a clock that had passed it would have claimed.
        var job = await BackgroundJobRuntimeRows.InsertAsync(proof.Database.AppConnectionString,
            organization is null ? "identity.data_erasure_tenant.v1" : "identity.data_erasure_organization.v1",
            organization: organization, tenant: tenant, parameters: DataErasureTestKit.Parameters(request));
        await using var host = new ConstructionOnlyApiFactory().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(Environments.Development);
            builder.UseSetting("ConnectionStrings:Default", proof.Database.AppConnectionString);
            builder.UseSetting("ConnectionStrings:OutboxDispatcher", proof.Database.OutboxConnectionString);
        });
        var executor = host.Services.GetRequiredService<JobExecutor>();
        var start = await executor.ExecuteAsync(await DataExportTestKit.RoutingClaim.ClaimAsync(proof.Database.OutboxConnectionString,
            job), BackgroundJobTestRun.Create());
        start.Outcome.Should().Be(JobStartExecutionOutcome.Started);
        var result = await executor.ExecuteBusinessAsync(start.Receipt!, start.Receipt!.Run.OpenBoundary(BackgroundJobCheckpointKind.Transactional));
        result.CommitAcknowledged.Should().BeTrue("disposition {0}, code {1}", result.Decision.Disposition, result.Decision.ErrorCode);
    }

    private sealed class AlwaysHold : IDataRequestHoldSource
    {
        public Task<bool> HasHoldAsync(DataRequestHoldQuery query, CancellationToken cancellationToken) => Task.FromResult(true);
    }
}
