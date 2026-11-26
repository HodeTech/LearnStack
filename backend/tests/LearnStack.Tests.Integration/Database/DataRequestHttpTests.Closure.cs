using System.Globalization;
using System.Net;
using System.Text.Json;
using FluentAssertions;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.Tools.Seeder;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using static LearnStack.Tests.Integration.Database.IdentityAuthorizationHttpProof;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// ADR-0106 § 3, § 7, § 10, § 11 and ADR-0107 § 7 through the actual Program: the person alone files an account closure
/// from their originating group, on its own routes, answered 202 <c>scheduled</c> with its execution time and no job;
/// it is cancellable until its Begin commits, even after its execution time; an administrator neither files, lists,
/// reads nor cancels one; and a Suspended person files, reads and cancels one through the retained entry.
/// </summary>
public sealed partial class DataRequestHttpTests
{
    [Fact]
    public async Task A_person_files_a_closure_scheduled_with_no_job_and_may_cancel_it_even_after_its_execution_time()
    {
        await using var proof = await CreatePreparedAsync();
        using var client = proof.Client();
        var learner = proof.Token(EnglishLearner, English);
        var tenant = English.TenantId.Value;

        const string Jobs = "SELECT count(*) FROM public.background_jobs";
        var jobsBefore = await CountAsync(proof, tenant, null, Jobs);
        var (id, version) = await FileClosureAsync(client, learner);
        var receipt = await ReceiptAsync(client, learner, id);
        receipt.GetProperty("kind").GetString().Should().Be("closure");
        receipt.GetProperty("origin").GetString().Should().Be("self");
        receipt.GetProperty("status").GetString().Should().Be("scheduled");
        receipt.GetProperty("lastRefusal").ValueKind.Should().Be(JsonValueKind.Null);
        Instant(receipt, "executeAt").Should().Be(Instant(receipt, "createdAt") + TimeSpan.FromHours(24), "a closure waits 24 hours");
        (await AuditCountAsync(proof, tenant, null, "identity.data_request.create_closure", id.ToString("D"))).Should().Be(1);
        (await CountAsync(proof, tenant, null, "SELECT count(*) FROM public.background_jobs WHERE parameters::text LIKE '%' || @request || '%'",
            ("request", id.ToString("D")))).Should().Be(0, "a closure is executed by the operator's Begin, never by a job");
        (await CountAsync(proof, tenant, null, Jobs)).Should().Be(jobsBefore, "filing queued nothing at all");

        using (var second = await SendAsync(client, learner, OwnPath + "/closures", "POST", "{}", key: Key()))
            (await ProblemAsync(second, HttpStatusCode.Conflict, "business_rule_violation")).GetProperty("errors").GetProperty("$")[0]
                .GetProperty("key").GetString().Should().Be("lockey_request_already_active", "one active closure per subject and group");
        var (erasure, _) = await FileErasureAsync(client, learner, OwnPath + "/erasures", "{}");
        erasure.Should().NotBe(id, "a closure and an erasure of the group are different kinds");

        // ADR-0106 § 11: the execution time ends no closure's cancellation; only its Begin does.
        await DataErasureTestKit.BackdateAsync(proof.Database.MigrationConnectionString, tenant, null, id);
        var due = await ReceiptAsync(client, learner, id);
        Instant(due, "executeAt").Should().BeBefore(DateTimeOffset.UtcNow, "the closure is past its execution time");
        due.GetProperty("status").GetString().Should().Be("scheduled", "no job moves a due closure");
        using (var cancelled = await SendAsync(client, learner, OwnPath + "/" + id.ToString("D") + "/cancellations", "POST", "{}",
            version: version, key: Key()))
            await JsonAsync(cancelled);
        (await ReceiptAsync(client, learner, id)).GetProperty("status").GetString().Should().Be("cancelled");
        (await AuditCountAsync(proof, tenant, null, "identity.data_request.cancel", id.ToString("D"))).Should().Be(1);
        var cancelledVersion = Version(await ReceiptAsync(client, learner, id));
        using (var again = await SendAsync(client, learner, OwnPath + "/" + id.ToString("D") + "/cancellations", "POST", "{}",
            version: cancelledVersion, key: Key()))
            (await ProblemAsync(again, HttpStatusCode.Conflict, "business_rule_violation")).GetProperty("errors").GetProperty("$")[0]
                .GetProperty("key").GetString().Should().Be("lockey_data_request_not_cancellable");

        var (refiled, _) = await FileClosureAsync(client, learner);
        refiled.Should().NotBe(id, "a cancelled closure frees its slot");
    }

    /// <summary>
    /// ADR-0106 § 10: a repeated closure filing with the same key answers the stored receipt and files nothing again; the
    /// same key on the erasure route is a key reuse, not a replay, and files no erasure.
    /// </summary>
    [Fact]
    public async Task A_closure_filing_replays_exactly_and_the_same_key_on_an_erasure_is_a_key_reuse()
    {
        await using var proof = await CreatePreparedAsync();
        using var client = proof.Client();
        var tenant = English.TenantId.Value;
        var learner = proof.Token(EnglishLearner, English);
        var key = Key();

        Guid id;
        string location;
        string etag;
        using (var first = await SendAsync(client, learner, OwnPath + "/closures", "POST", "{}", key: key))
        {
            id = Guid.Parse((await JsonAsync(first, HttpStatusCode.Accepted)).GetProperty("id").GetString()!);
            location = first.Headers.Location!.OriginalString;
            etag = first.Headers.ETag!.Tag;
            first.Headers.Contains("Idempotency-Replayed").Should().BeFalse();
        }

        using (var replay = await SendAsync(client, learner, OwnPath + "/closures", "POST", "{}", key: key))
        {
            var replayed = await JsonAsync(replay, HttpStatusCode.Accepted);
            replayed.EnumerateObject().Select(property => (property.Name, property.Value.GetString())).Should()
                .Equal(("id", id.ToString("D")), ("kind", "closure"), ("status", "scheduled"), ("version", "0"));
            replay.Headers.GetValues("Idempotency-Replayed").Should().Equal("true");
            replay.Headers.Location!.OriginalString.Should().Be(location);
            replay.Headers.ETag!.Tag.Should().Be(etag);
        }

        using (var reused = await SendAsync(client, learner, OwnPath + "/erasures", "POST", "{}", key: key))
            await ProblemAsync(reused, HttpStatusCode.Conflict, "idempotency_key_reuse");
        using (var changed = await SendAsync(client, learner, OwnPath + "/closures", "POST", "{\"reason\":\"x\"}", key: Key()))
            await ProblemAsync(changed, HttpStatusCode.BadRequest, "validation_failed");
        (await CountAsync(proof, tenant, null, "SELECT count(*) FROM public.data_subject_requests WHERE kind='closure'")).Should().Be(1);
        (await CountAsync(proof, tenant, null, "SELECT count(*) FROM public.data_subject_requests WHERE kind='erasure'")).Should().Be(0);
        (await AuditCountAsync(proof, tenant, null, "identity.data_request.create_closure")).Should().Be(1, "a replay audits nothing");
    }

    /// <summary>
    /// ADR-0106 § 7, ADR-0107 § 7: a hold refuses a closure with its closed code and files nothing; the tenant's only
    /// eligible administrator still files one (that check is the Begin's); and a person with no Membership in a group has
    /// no closure route there, live or retained.
    /// </summary>
    [Fact]
    public async Task A_hold_refuses_a_closure_the_sole_administrator_files_one_and_a_group_without_a_membership_is_absent()
    {
        await using var proof = await CreatePreparedAsync();
        var tenant = English.TenantId.Value;
        var branch = English.DefaultOrganization.OrganizationId;
        using (var held = proof.Client(services => services.AddSingleton<IDataRequestHoldSource>(new AlwaysHold())))
        using (var own = await SendAsync(held, proof.Token(EnglishLearner, English), OwnPath + "/closures", "POST", "{}", key: Key()))
            (await ProblemAsync(own, HttpStatusCode.Conflict, "business_rule_violation")).GetProperty("errors").GetProperty("$")[0]
                .GetProperty("key").GetString().Should().Be("lockey_blocked_hold");
        (await CountAsync(proof, tenant, null, "SELECT count(*) FROM public.data_subject_requests")).Should().Be(0);
        (await AuditCountAsync(proof, tenant, null, "identity.data_request.create_closure")).Should().Be(0);

        using var client = RetainedClient(proof);
        var (sole, _) = await FileClosureAsync(client, proof.Token(EnglishAdmin, English));
        (await AuditCountAsync(proof, tenant, null, "identity.data_request.create_closure", sole.ToString("D"))).Should().Be(1,
            "the last-administrator check is the Begin's, across every participant tenant");

        using (var live = await SendAsync(client, proof.Token(EnglishLearner, English, organization: true, claimedOrganization: branch),
            OwnPath + "/closures", "POST", "{}", key: Key()))
            await ProblemAsync(live, HttpStatusCode.NotFound, "not_found");
        using (var retained = await EntrySendAsync(client, NativeToken(proof, EnglishLearner), "/closures?group=" + branch.Value.ToString("D"),
            "{}", Key()))
            await ProblemAsync(retained, HttpStatusCode.NotFound, "not_found");
        // An organization context also sees the tenant-wide group's rows (the canonical policy), so the branch is named.
        (await CountAsync(proof, tenant, branch.Value, "SELECT count(*) FROM public.data_subject_requests WHERE organization_id=@branch",
            ("branch", branch.Value))).Should().Be(0, "the refused filings wrote no request in the branch");
    }

    /// <summary>
    /// ADR-0106 § 3, ADR-0107 § 7: no administrator creates or reads a closure. The person's closure is absent from the
    /// administrator's list, its read and its cancellation, its receipt is never disclosed to them, and the administrator's
    /// surface has no closure route.
    /// </summary>
    [Fact]
    public async Task The_administrator_neither_files_lists_reads_nor_cancels_a_closure()
    {
        await using var proof = await CreatePreparedAsync();
        using var client = proof.Client();
        var admin = proof.Token(EnglishAdmin, English);
        var learner = proof.Token(EnglishLearner, English);
        var (id, version) = await FileClosureAsync(client, learner);
        var (erasure, _) = await FileErasureAsync(client, learner, OwnPath + "/erasures", "{}");

        using (var list = await SendAsync(client, admin, AdminPath))
            (await JsonAsync(list)).GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetString())
                .Should().Equal([erasure.ToString("D")], "the administrator sees the person's erasure and never their closure");
        using (var read = await SendAsync(client, admin, AdminPath + "/" + id.ToString("D")))
            await ProblemAsync(read, HttpStatusCode.NotFound, "not_found");
        using (var cancel = await SendAsync(client, admin, AdminPath + "/" + id.ToString("D") + "/cancellations", "POST", "{}",
            version: version, key: Key()))
            await ProblemAsync(cancel, HttpStatusCode.NotFound, "not_found");
        using (var file = await SendAsync(client, admin, AdminPath + "/closures", "POST",
            JsonSerializer.Serialize(new { membershipId = LearnerMembership }), key: Key()))
            file.StatusCode.Should().BeOneOf([HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed], "no administrator closure route exists");
        using (var foreign = await SendAsync(client, admin, OwnPath + "/" + id.ToString("D")))
            await ProblemAsync(foreign, HttpStatusCode.NotFound, "not_found");
        (await ReceiptAsync(client, learner, id)).GetProperty("status").GetString().Should().Be("scheduled", "nothing changed it");
        (await CountAsync(proof, English.TenantId.Value, null, "SELECT count(*) FROM public.data_subject_requests WHERE kind='closure'"))
            .Should().Be(1, "the administrator filed none");
    }

    /// <summary>
    /// ADR-0106 § 2: a closure belongs to its originating group and tenant. A person with Memberships in two groups files one
    /// per group; neither is visible, cancellable or readable from the other group, nor from another tenant's host.
    /// </summary>
    [Fact]
    public async Task A_closure_is_confined_to_its_originating_group_and_tenant()
    {
        await using var proof = await CreatePreparedAsync();
        using var client = proof.Client();
        var tenant = English.TenantId.Value;
        var branch = English.DefaultOrganization.OrganizationId;
        await proof.Database.AddMembershipAsync(English, EnglishLearner, branch);
        var wide = proof.Token(EnglishLearner, English);
        var inBranch = proof.Token(EnglishLearner, English, organization: true, claimedOrganization: branch);

        var (tenantWide, version) = await FileClosureAsync(client, wide);
        var (branchClosure, _) = await FileClosureAsync(client, inBranch);
        (await AuditCountAsync(proof, tenant, branch.Value, "identity.data_request.create_closure", branchClosure.ToString("D"))).Should().Be(1);

        using (var list = await SendAsync(client, inBranch, OwnPath))
            (await JsonAsync(list)).GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetString())
                .Should().Equal(branchClosure.ToString("D"));
        using (var read = await SendAsync(client, inBranch, OwnPath + "/" + tenantWide.ToString("D")))
            await ProblemAsync(read, HttpStatusCode.NotFound, "not_found");
        using (var cancel = await SendAsync(client, inBranch, OwnPath + "/" + tenantWide.ToString("D") + "/cancellations", "POST", "{}",
            version: version, key: Key()))
            await ProblemAsync(cancel, HttpStatusCode.NotFound, "not_found");
        using (var yoga = await SendAsync(client, wide, OwnPath + "/" + tenantWide.ToString("D"), host: SeedData.Yoga.Host))
            yoga.StatusCode.Should().BeOneOf([HttpStatusCode.NotFound, HttpStatusCode.Unauthorized], "another tenant's host never reaches it");
        (await ReceiptAsync(client, wide, tenantWide)).GetProperty("status").GetString().Should().Be("scheduled");
        (await CountAsync(proof, tenant, null,
            "SELECT count(*) FROM public.data_subject_requests WHERE kind='closure' AND organization_id IS NULL")).Should().Be(1);
        (await CountAsync(proof, tenant, branch.Value,
            "SELECT count(*) FROM public.data_subject_requests WHERE kind='closure' AND organization_id=@branch", ("branch", branch.Value)))
            .Should().Be(1, "one closure per group");
    }

    /// <summary>
    /// ADR-0106 § 9, ADR-0107 § 7: a Suspended person has no live closure route and files, replays, reads and cancels a
    /// closure through the retained entry, with no <c>Location</c>.
    /// </summary>
    [Fact]
    public async Task A_suspended_person_files_reads_and_cancels_a_closure_through_the_retained_entry()
    {
        await using var proof = await CreatePreparedAsync();
        using var client = RetainedClient(proof);
        var tenant = English.TenantId.Value;
        var native = NativeToken(proof, EnglishLearner);
        await ChangeStatusAsync(client, proof, LearnerMembershipId, "Suspended");
        using (var live = await SendAsync(client, proof.Token(EnglishLearner, English), OwnPath + "/closures", "POST", "{}", key: Key()))
            await ProblemAsync(live, HttpStatusCode.NotFound, "not_found");

        var key = Key();
        string id;
        string version;
        using (var filed = await EntrySendAsync(client, native, "/closures?group=tenant", "{}", key))
        {
            var accepted = await JsonAsync(filed, HttpStatusCode.Accepted);
            accepted.EnumerateObject().Select(property => property.Name).Should().Equal("id", "kind", "status", "version");
            accepted.GetProperty("kind").GetString().Should().Be("closure");
            accepted.GetProperty("status").GetString().Should().Be("scheduled");
            filed.Headers.Location.Should().BeNull("the retained entry has no per-request route");
            filed.Headers.ETag!.Tag.Should().Be("\"" + Version(accepted) + "\"");
            id = accepted.GetProperty("id").GetString()!;
            version = Version(accepted);
        }

        using (var replay = await EntrySendAsync(client, native, "/closures?group=tenant", "{}", key))
        {
            (await JsonAsync(replay, HttpStatusCode.Accepted)).GetProperty("id").GetString().Should().Be(id);
            replay.Headers.GetValues("Idempotency-Replayed").Should().Equal("true");
            replay.Headers.Location.Should().BeNull();
        }

        (await AuditCountAsync(proof, tenant, null, "identity.data_request.create_closure", id)).Should().Be(1);
        using (var page = await EntrySendAsync(client, native, "/requests?group=tenant"))
        {
            var item = (await JsonAsync(page)).GetProperty("items").EnumerateArray().Single();
            item.GetProperty("id").GetString().Should().Be(id);
            item.GetProperty("kind").GetString().Should().Be("closure");
            item.GetProperty("status").GetString().Should().Be("scheduled");
            Instant(item, "executeAt").Should().Be(Instant(item, "createdAt") + TimeSpan.FromHours(24));
        }

        using (var cancelled = await EntrySendAsync(client, native, "/cancellations?group=tenant",
            JsonSerializer.Serialize(new { requestId = id }), Key(), "\"" + version + "\""))
            await JsonAsync(cancelled);
        using (var page = await EntrySendAsync(client, native, "/requests?group=tenant"))
            (await JsonAsync(page)).GetProperty("items").EnumerateArray().Single().GetProperty("status").GetString().Should().Be("cancelled");
        (await AuditCountAsync(proof, tenant, null, "identity.data_request.cancel", id)).Should().Be(1);
    }

    /// <summary>
    /// ADR-0106 § 7, § 8, ADR-0107 § 7: the receipt of a closure as an operator's acts leave it, written here by the owner with
    /// the request guard paused: a refused Begin's <c>lastRefusal</c> on a still scheduled and cancellable closure, a begun
    /// closure <c>fulfilling</c> and no longer cancellable, and a finalized one <c>completed</c> with its retained report,
    /// <c>unserved_group</c> included. None is ever shown failed.
    /// </summary>
    [Fact]
    public async Task The_receipts_of_a_refused_a_begun_and_a_finalized_closure_render_as_stored()
    {
        await using var proof = await CreatePreparedAsync();
        using var client = proof.Client();
        var learner = proof.Token(EnglishLearner, English);
        var tenant = English.TenantId.Value;
        var (id, _) = await FileClosureAsync(client, learner);

        await ClosureAsOwnerAsync(proof, tenant, id, "last_refusal='blocked_last_administrator'");
        var refused = await ReceiptAsync(client, learner, id);
        refused.GetProperty("status").GetString().Should().Be("scheduled");
        refused.GetProperty("lastRefusal").GetString().Should().Be("blocked_last_administrator");

        await ClosureAsOwnerAsync(proof, tenant, id, "status='fulfilling', last_refusal=NULL");
        var begun = await ReceiptAsync(client, learner, id);
        begun.GetProperty("status").GetString().Should().Be("fulfilling");
        begun.GetProperty("failureReason").ValueKind.Should().Be(JsonValueKind.Null);
        using (var late = await SendAsync(client, learner, OwnPath + "/" + id.ToString("D") + "/cancellations", "POST", "{}",
            version: Version(begun), key: Key()))
            (await ProblemAsync(late, HttpStatusCode.Conflict, "business_rule_violation")).GetProperty("errors").GetProperty("$")[0]
                .GetProperty("key").GetString().Should().Be("lockey_data_request_not_cancellable", "a begun closure is irreversible");

        await ClosureAsOwnerAsync(proof, tenant, id,
            "status='completed', completed_at=now(), result='{\"audit_entity_reference\":4,\"unserved_group\":1}'::jsonb");
        var completed = await ReceiptAsync(client, learner, id);
        completed.GetProperty("status").GetString().Should().Be("completed");
        completed.GetProperty("completedAt").ValueKind.Should().Be(JsonValueKind.String);
        completed.GetProperty("retained").EnumerateArray().Select(item => (item.GetProperty("kind").GetString(), item.GetProperty("count").GetInt64()))
            .Should().Equal(("audit_entity_reference", 4L), ("unserved_group", 1L));
        completed.TryGetProperty("membershipIds", out _).Should().BeFalse("the person's receipt names no internal identifier");
    }

    /// <summary>Files a closure on the live route and returns its id, after checking the 202 contract.</summary>
    private static async Task<(Guid Id, string Version)> FileClosureAsync(HttpClient client, string token)
    {
        using var response = await SendAsync(client, token, OwnPath + "/closures", "POST", "{}", key: Key());
        var accepted = await JsonAsync(response, HttpStatusCode.Accepted);
        accepted.EnumerateObject().Select(property => property.Name).Should().Equal("id", "kind", "status", "version");
        accepted.GetProperty("kind").GetString().Should().Be("closure");
        accepted.GetProperty("status").GetString().Should().Be("scheduled");
        var id = Guid.Parse(accepted.GetProperty("id").GetString()!);
        id.Version.Should().Be(7);
        response.Headers.Location!.OriginalString.Should().Be(OwnPath + "/" + id.ToString("D"));
        response.Headers.ETag!.Tag.Should().Be("\"" + Version(accepted) + "\"");
        return (id, Version(accepted));
    }

    /// <summary>
    /// Stands in for an operator's closure act (ADR-0107 § 7), which is not this test's subject: the owner applies
    /// <paramref name="assignments"/> to the tenant-wide closure with the request guard paused and restored, stepping its
    /// version as the guard would.
    /// </summary>
    private static async Task ClosureAsOwnerAsync(IdentityAuthorizationHttpProof proof, Guid tenant, Guid request, string assignments)
    {
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(proof.Database.MigrationConnectionString);
        await using var transaction = await owner.BeginTransactionAsync();
        await ExecuteAsync(owner, transaction, "ALTER TABLE public.data_subject_requests DISABLE TRIGGER tg_data_subject_requests_mutation_guard");
        await ScopeAsync(owner, transaction, tenant, null);
        (await ScalarAsync<long>(owner, transaction, $"""
            WITH moved AS (
                UPDATE public.data_subject_requests SET {assignments}, updated_at=now(), row_version=row_version+1
                WHERE id=@id AND kind='closure' RETURNING 1)
            SELECT count(*) FROM moved
            """, ("id", request))).Should().Be(1);
        await ExecuteAsync(owner, transaction, "ALTER TABLE public.data_subject_requests ENABLE TRIGGER tg_data_subject_requests_mutation_guard");
        await transaction.CommitAsync();
    }

    private static DateTimeOffset Instant(JsonElement element, string property) =>
        DateTimeOffset.Parse(element.GetProperty(property).GetString()!, CultureInfo.InvariantCulture);
}
