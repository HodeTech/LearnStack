using System.Net;
using System.Text.Json;
using FluentAssertions;
using LearnStack.Tools.Seeder;
using Xunit;
using static LearnStack.Tests.Integration.Database.IdentityAuthorizationHttpProof;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// ADR-0106 § 3, § 7, § 10, § 11 and ADR-0107 § 4, § 5 through the actual Program: an erasure is filed on its own routes,
/// answered 202 <c>scheduled</c> with its execution time, cancellable until then and not after, refused for the sole
/// administrator, filed by an administrator only for a Revoked Membership, and filed, cancelled and an artifact revoked
/// through the retained entry by a person whose Membership is Suspended.
/// </summary>
public sealed partial class DataRequestHttpTests
{
    [Fact]
    public async Task A_person_files_an_erasure_scheduled_for_its_window_and_may_cancel_it_only_before_its_execution_time()
    {
        await using var proof = await CreatePreparedAsync();
        using var client = proof.Client();
        var learner = proof.Token(EnglishLearner, English);
        var tenant = English.TenantId.Value;

        var (id, version) = await FileErasureAsync(client, learner, OwnPath + "/erasures", "{}");
        var receipt = await ReceiptAsync(client, learner, id);
        receipt.GetProperty("kind").GetString().Should().Be("erasure");
        receipt.GetProperty("status").GetString().Should().Be("scheduled");
        DateTimeOffset.Parse(receipt.GetProperty("executeAt").GetString()!, System.Globalization.CultureInfo.InvariantCulture)
            .Should().Be(DateTimeOffset.Parse(receipt.GetProperty("createdAt").GetString()!, System.Globalization.CultureInfo.InvariantCulture)
                + TimeSpan.FromHours(24), "the cancellable window is 24 hours");
        (await AuditCountAsync(proof, tenant, null, "identity.data_request.create_erasure", id.ToString("D"))).Should().Be(1);
        using (var second = await SendAsync(client, learner, OwnPath + "/erasures", "POST", "{}", key: Key()))
            (await ProblemAsync(second, HttpStatusCode.Conflict, "business_rule_violation")).GetProperty("errors").GetProperty("$")[0]
                .GetProperty("key").GetString().Should().Be("lockey_request_already_active");

        using (var cancelled = await SendAsync(client, learner, OwnPath + "/" + id.ToString("D") + "/cancellations", "POST", "{}",
            version: version, key: Key()))
            await JsonAsync(cancelled);
        (await ReceiptAsync(client, learner, id)).GetProperty("status").GetString().Should().Be("cancelled");
        (await AuditCountAsync(proof, tenant, null, "identity.data_request.cancel", id.ToString("D"))).Should().Be(1);

        // Once the window has ended the erasure is no longer the person's to withdraw (ADR-0106 § 11).
        var (due, dueVersion) = await FileErasureAsync(client, learner, OwnPath + "/erasures", "{}");
        await DataErasureTestKit.BackdateAsync(proof.Database.MigrationConnectionString, tenant, null, due);
        using (var late = await SendAsync(client, learner, OwnPath + "/" + due.ToString("D") + "/cancellations", "POST", "{}",
            version: dueVersion, key: Key()))
            await ProblemAsync(late, HttpStatusCode.Conflict, "business_rule_violation");
        (await ReceiptAsync(client, learner, due)).GetProperty("status").GetString().Should().Be("scheduled");
    }

    /// <summary>
    /// ADR-0106 § 3, § 7: the tenant's only eligible administrator is refused with its closed code; an administrator files
    /// for a working member only after revoking the Membership, which is the separately audited act; a person who is not
    /// an administrator of the group is refused the administrator's route.
    /// </summary>
    [Fact]
    public async Task The_sole_administrator_is_refused_and_an_administrator_erases_only_a_member_whose_Membership_is_revoked()
    {
        await using var proof = await CreatePreparedAsync();
        using var client = proof.Client();
        var admin = proof.Token(EnglishAdmin, English);
        var learner = proof.Token(EnglishLearner, English);
        var body = JsonSerializer.Serialize(new { membershipId = LearnerMembership });

        using (var sole = await SendAsync(client, admin, OwnPath + "/erasures", "POST", "{}", key: Key()))
            (await ProblemAsync(sole, HttpStatusCode.Conflict, "business_rule_violation")).GetProperty("errors").GetProperty("$")[0]
                .GetProperty("key").GetString().Should().Be("lockey_blocked_last_administrator");
        using (var denied = await SendAsync(client, learner, AdminPath + "/erasures", "POST", body, key: Key()))
            await ProblemAsync(denied, HttpStatusCode.Forbidden, "forbidden");
        using (var working = await SendAsync(client, admin, AdminPath + "/erasures", "POST", body, key: Key()))
            (await ProblemAsync(working, HttpStatusCode.Conflict, "business_rule_violation")).GetProperty("errors").GetProperty("$")[0]
                .GetProperty("key").GetString().Should().Be("lockey_subject_membership_current");

        await ChangeStatusAsync(client, proof, LearnerMembership, "Revoked");
        var (id, version) = await FileErasureAsync(client, admin, AdminPath + "/erasures", body);
        var detail = await ReceiptAsync(client, admin, id, AdminPath);
        detail.GetProperty("origin").GetString().Should().Be("administrator");
        detail.GetProperty("kind").GetString().Should().Be("erasure");
        detail.GetProperty("membershipIds").EnumerateArray().Select(item => item.GetString()).Should().Equal(LearnerMembership.ToString("D"));
        using (var cancelled = await SendAsync(client, admin, AdminPath + "/" + id.ToString("D") + "/cancellations", "POST", "{}",
            version: version, key: Key()))
            await JsonAsync(cancelled);
        (await ReceiptAsync(client, admin, id, AdminPath)).GetProperty("status").GetString().Should().Be("cancelled");
    }

    /// <summary>
    /// ADR-0106 § 9, ADR-0107 § 4: a Suspended person has no live route, and through the retained entry files and cancels an
    /// erasure and revokes their own export artifact; the revocation binds the artifact's version and another person's
    /// request is absent.
    /// </summary>
    [Fact]
    public async Task A_suspended_person_files_and_cancels_an_erasure_and_revokes_an_artifact_through_the_retained_entry()
    {
        await using var proof = await CreatePreparedAsync();
        using var client = RetainedClient(proof);
        var tenant = English.TenantId.Value;
        var native = NativeToken(proof, EnglishLearner);
        var (export, _) = await FileExportAsync(client, proof.Token(EnglishLearner, English));
        await RunExportAsync(proof, tenant, null, export);
        await ChangeStatusAsync(client, proof, LearnerMembershipId, "Suspended");
        using (var live = await SendAsync(client, proof.Token(EnglishLearner, English), OwnPath + "/erasures", "POST", "{}", key: Key()))
            await ProblemAsync(live, HttpStatusCode.NotFound, "not_found");

        using (var filed = await EntrySendAsync(client, native, "/erasures?group=tenant", "{}", Key()))
        {
            var accepted = await JsonAsync(filed, HttpStatusCode.Accepted);
            accepted.GetProperty("kind").GetString().Should().Be("erasure");
            accepted.GetProperty("status").GetString().Should().Be("scheduled");
            filed.Headers.Location.Should().BeNull("the retained entry has no per-request route");
            var erasure = accepted.GetProperty("id").GetString()!;
            (await AuditCountAsync(proof, tenant, null, "identity.data_request.create_erasure", erasure)).Should().Be(1);
            using var cancelled = await EntrySendAsync(client, native, "/cancellations?group=tenant",
                JsonSerializer.Serialize(new { requestId = erasure }), Key(), "\"" + Version(accepted) + "\"");
            await JsonAsync(cancelled);
        }

        JsonElement artifact;
        using (var page = await EntrySendAsync(client, native, "/requests?group=tenant"))
            artifact = (await JsonAsync(page)).GetProperty("items").EnumerateArray()
                .Single(item => item.GetProperty("id").GetString() == export.ToString("D")).GetProperty("artifact");
        var body = JsonSerializer.Serialize(new { requestId = export });
        using (var unconditional = await EntrySendAsync(client, native, "/revocations?group=tenant", body, Key()))
            unconditional.StatusCode.Should().Be(HttpStatusCode.BadRequest, "a revocation takes the artifact's version");
        using (var foreign = await EntrySendAsync(client, native, "/revocations?group=tenant",
            JsonSerializer.Serialize(new { requestId = Guid.CreateVersion7() }), Key(), "\"" + Version(artifact) + "\""))
            await ProblemAsync(foreign, HttpStatusCode.NotFound, "not_found");
        using (var revoked = await EntrySendAsync(client, native, "/revocations?group=tenant", body, Key(), "\"" + Version(artifact) + "\""))
            await JsonAsync(revoked);
        using (var page = await EntrySendAsync(client, native, "/requests?group=tenant"))
            (await JsonAsync(page)).GetProperty("items").EnumerateArray()
                .Single(item => item.GetProperty("id").GetString() == export.ToString("D")).GetProperty("artifact")
                .GetProperty("status").GetString().Should().Be("revoked");
        (await AuditCountAsync(proof, tenant, null, "identity.data_export.revoke")).Should().Be(1);
    }

    /// <summary>Files an erasure through a creation route and returns its id, after checking the 202 contract.</summary>
    private static async Task<(Guid Id, string Version)> FileErasureAsync(HttpClient client, string token, string path, string body)
    {
        using var response = await SendAsync(client, token, path, "POST", body, key: Key());
        var accepted = await JsonAsync(response, HttpStatusCode.Accepted);
        accepted.EnumerateObject().Select(property => property.Name).Should().Equal("id", "kind", "status", "version");
        accepted.GetProperty("kind").GetString().Should().Be("erasure");
        accepted.GetProperty("status").GetString().Should().Be("scheduled");
        var id = Guid.Parse(accepted.GetProperty("id").GetString()!);
        response.Headers.Location!.OriginalString.Should().EndWith("/" + id.ToString("D"));
        response.Headers.ETag!.Tag.Should().Be("\"" + Version(accepted) + "\"");
        return (id, Version(accepted));
    }
}
