using System.Net;
using System.Text.Json;
using FluentAssertions;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Permissions;
using LearnStack.Tools.Seeder;
using Xunit;
using static LearnStack.Tests.Integration.Database.IdentityAuthorizationHttpProof;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class DataRequestHttpTests
{
    [Fact]
    public async Task A_tenant_administrator_files_an_export_for_a_member_sees_status_and_counts_only_and_never_a_ticket()
    {
        await using var proof = await CreatePreparedAsync();
        using var client = proof.Client();
        var admin = proof.Token(EnglishAdmin, English);
        var learner = proof.Token(EnglishLearner, English);
        var tenant = English.TenantId.Value;
        var body = JsonSerializer.Serialize(new { membershipId = LearnerMembership });

        // Never for oneself through this path.
        using (var self = await SendAsync(client, admin, AdminPath + "/exports", "POST",
            JsonSerializer.Serialize(new { membershipId = SeedData.IdentityMemberships[0].MembershipId }), key: Key()))
            await ProblemAsync(self, HttpStatusCode.Conflict, "business_rule_violation");
        // A Membership of another tenant is absent.
        using (var foreign = await SendAsync(client, admin, AdminPath + "/exports", "POST",
            JsonSerializer.Serialize(new { membershipId = SeedData.IdentityMemberships[2].MembershipId }), key: Key()))
            await ProblemAsync(foreign, HttpStatusCode.NotFound, "not_found");

        var (id, _) = await FileExportAsync(client, admin, path: AdminPath + "/exports", body: body);
        var detail = await ReceiptAsync(client, admin, id, AdminPath);
        detail.GetProperty("origin").GetString().Should().Be("administrator");
        detail.GetProperty("requestedByYou").GetBoolean().Should().BeTrue();
        detail.GetProperty("membershipIds").EnumerateArray().Select(item => item.GetString()).Should().Equal(LearnerMembership.ToString("D"));
        (await AuditCountAsync(proof, tenant, null, "identity.data_request.read", id.ToString("D"))).Should().Be(1,
            "an administrator's read of another person's request is a MUST read-sensitive row");
        using (var page = await SendAsync(client, admin, AdminPath))
            (await JsonAsync(page)).GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetString())
                .Should().Equal(id.ToString("D"));
        (await AuditCountAsync(proof, tenant, null, "identity.data_request.list")).Should().Be(1);

        // The subject sees the request on their own page and may cancel it; the one-active rule spans requesters.
        var own = await ReceiptAsync(client, learner, id);
        own.GetProperty("origin").GetString().Should().Be("administrator");
        using (var conflict = await SendAsync(client, learner, OwnPath + "/exports", "POST", "{}", key: Key()))
            await ProblemAsync(conflict, HttpStatusCode.Conflict, "business_rule_violation");

        await RunExportAsync(proof, tenant, null, id);
        var completed = await ReceiptAsync(client, admin, id, AdminPath);
        completed.GetProperty("status").GetString().Should().Be("completed");
        completed.GetProperty("artifact").GetProperty("chunkCount").GetInt32().Should().BeGreaterThan(0);

        // The administrator never gets content: the self path reaches no request about another person.
        using (var ticket = await SendAsync(client, admin, OwnPath + "/" + id.ToString("D") + "/artifact/tickets", "POST", "{}"))
            await ProblemAsync(ticket, HttpStatusCode.NotFound, "not_found");
        // The subject retrieves it.
        var (_, bundle) = await DownloadAsync(client, learner, id);
        bundle.Should().NotBeEmpty();

        // While the subject holds a readable artifact, a new administrator export would withdraw it: refused.
        using (var refused = await SendAsync(client, admin, AdminPath + "/exports", "POST", body, key: Key()))
            (await ProblemAsync(refused, HttpStatusCode.Conflict, "business_rule_violation")).GetProperty("errors").GetProperty("$")[0]
                .GetProperty("key").GetString().Should().Be("lockey_subject_export_available");

        // The requesting administrator may revoke what their export produced.
        var artifact = completed.GetProperty("artifact");
        using (var revoked = await SendAsync(client, admin, AdminPath + "/" + id.ToString("D") + "/artifact/revocations", "POST", "{}",
            version: Version(artifact), key: Key()))
            await JsonAsync(revoked);
        (await ReceiptAsync(client, learner, id)).GetProperty("artifact").GetProperty("status").GetString().Should().Be("revoked");

        // With no readable artifact left, the administrator may file again.
        var (again, _) = await FileExportAsync(client, admin, path: AdminPath + "/exports", body: body);
        again.Should().NotBe(id);
    }

    [Fact]
    public async Task An_organization_administrator_reaches_only_their_organization_and_a_Revoked_Membership_is_still_a_subject()
    {
        await using var proof = await CreatePreparedAsync();
        var branch = English.DefaultOrganization.OrganizationId;
        var other = English.SecondOrganization.OrganizationId;
        // A person who administers the English branch through an Organization-scoped role.
        await proof.Database.AddMembershipAsync(English, YogaAdmin, branch, BuiltInRoleKey.OrgAdmin);
        var branchMember = await proof.Database.AddMembershipAsync(English, EnglishLearner, branch);
        var otherMember = await proof.Database.AddMembershipAsync(English, EnglishLearner, other);
        using var client = proof.Client();
        var tenant = English.TenantId.Value;
        var branchAdmin = proof.Token(YogaAdmin, English, organization: true, claimedOrganization: branch);
        var tenantAdminInBranch = proof.Token(EnglishAdmin, English, organization: true, claimedOrganization: branch);
        var tenantAdmin = proof.Token(EnglishAdmin, English);

        // The tenant-wide group's request is invisible to the branch, and the reverse.
        var (tenantWide, _) = await FileExportAsync(client, tenantAdmin, path: AdminPath + "/exports",
            body: JsonSerializer.Serialize(new { membershipId = LearnerMembership }));
        using (var list = await SendAsync(client, branchAdmin, AdminPath))
            (await JsonAsync(list)).GetProperty("items").GetArrayLength().Should().Be(0);
        using (var read = await SendAsync(client, branchAdmin, AdminPath + "/" + tenantWide.ToString("D")))
            await ProblemAsync(read, HttpStatusCode.NotFound, "not_found");
        using (var read = await SendAsync(client, tenantAdminInBranch, AdminPath + "/" + tenantWide.ToString("D")))
            await ProblemAsync(read, HttpStatusCode.NotFound, "not_found");
        // A Membership of the tenant-wide group or of another organization is absent to the branch.
        foreach (var membership in new[] { LearnerMembership, otherMember })
        {
            using var absent = await SendAsync(client, branchAdmin, AdminPath + "/exports", "POST",
                JsonSerializer.Serialize(new { membershipId = membership }), key: Key());
            await ProblemAsync(absent, HttpStatusCode.NotFound, "not_found");
        }

        // The branch administrator files in their own group; a tenant administrator in the branch context files there too.
        var (branchRequest, _) = await FileExportAsync(client, branchAdmin, path: AdminPath + "/exports",
            body: JsonSerializer.Serialize(new { membershipId = branchMember }));
        using (var list = await SendAsync(client, branchAdmin, AdminPath))
            (await JsonAsync(list)).GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetString())
                .Should().Equal(branchRequest.ToString("D"));
        using (var list = await SendAsync(client, tenantAdmin, AdminPath))
            (await JsonAsync(list)).GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetString())
                .Should().Equal(new[] { tenantWide.ToString("D") }, "a tenant-wide context lists only the tenant-wide group");
        // Another administrator did not file it: they read it but cannot cancel it.
        var branchReceipt = await ReceiptAsync(client, tenantAdminInBranch, branchRequest, AdminPath);
        branchReceipt.GetProperty("requestedByYou").GetBoolean().Should().BeFalse();
        using (var cancel = await SendAsync(client, tenantAdminInBranch, AdminPath + "/" + branchRequest.ToString("D") + "/cancellations", "POST", "{}",
            version: Version(branchReceipt), key: Key()))
            await ProblemAsync(cancel, HttpStatusCode.NotFound, "not_found");

        // ADR-0106 § 3: a Membership of any status counts, Revoked included.
        await proof.SetMembershipStatusAsync(English, EnglishAdmin, otherMember, "Revoked", other);
        var tenantAdminInOther = proof.Token(EnglishAdmin, English, organization: true, claimedOrganization: other);
        var (revokedSubject, _) = await FileExportAsync(client, tenantAdminInOther, path: AdminPath + "/exports",
            body: JsonSerializer.Serialize(new { membershipId = otherMember }));
        await RunExportAsync(proof, tenant, other.Value, revokedSubject);
        (await ReceiptAsync(client, tenantAdminInOther, revokedSubject, AdminPath)).GetProperty("status").GetString().Should().Be("completed");

        // A learner without administration authority reaches no administrator operation.
        using (var refused = await SendAsync(client, proof.Token(EnglishLearner, English), AdminPath))
            refused.StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Requests_are_isolated_by_group_by_person_and_by_tenant()
    {
        await using var proof = await CreatePreparedAsync();
        var branch = English.DefaultOrganization.OrganizationId;
        await proof.Database.AddMembershipAsync(English, EnglishLearner, branch);
        using var client = proof.Client();
        var tenantWide = proof.Token(EnglishLearner, English);
        var inBranch = proof.Token(EnglishLearner, English, organization: true, claimedOrganization: branch);

        var (wide, _) = await FileExportAsync(client, tenantWide);
        var (local, _) = await FileExportAsync(client, inBranch);
        using (var page = await SendAsync(client, inBranch, OwnPath))
            (await JsonAsync(page)).GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetString())
                .Should().Equal(new[] { local.ToString("D") }, "an organization context never lists the tenant-wide group's requests");
        using (var page = await SendAsync(client, tenantWide, OwnPath))
            (await JsonAsync(page)).GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetString())
                .Should().Equal(new[] { wide.ToString("D") }, "and the tenant-wide context never lists an organization's");
        using (var crossed = await SendAsync(client, inBranch, OwnPath + "/" + wide.ToString("D")))
            await ProblemAsync(crossed, HttpStatusCode.NotFound, "not_found");

        // Another person of the same tenant reaches neither request.
        var admin = proof.Token(EnglishAdmin, English);
        using (var third = await SendAsync(client, admin, OwnPath + "/" + wide.ToString("D")))
            await ProblemAsync(third, HttpStatusCode.NotFound, "not_found");
        using (var page = await SendAsync(client, admin, OwnPath))
            (await JsonAsync(page)).GetProperty("items").GetArrayLength().Should().Be(0);
        using (var cancel = await SendAsync(client, admin, OwnPath + "/" + wide.ToString("D") + "/cancellations", "POST", "{}",
            version: "0", key: Key()))
            await ProblemAsync(cancel, HttpStatusCode.NotFound, "not_found");

        // Another tenant reaches nothing: its administrator, on its own host, finds no such request.
        using (var foreign = await SendAsync(client, proof.Token(YogaAdmin, SeedData.Yoga), AdminPath + "/" + wide.ToString("D"),
            host: SeedData.Yoga.Host))
            await ProblemAsync(foreign, HttpStatusCode.NotFound, "not_found");

        // A cursor is bound to its operation, actor and group.
        using (var paged = await SendAsync(client, tenantWide, OwnPath + "?limit=1"))
        {
            var info = (await JsonAsync(paged)).GetProperty("pageInfo");
            info.GetProperty("hasNext").GetBoolean().Should().BeFalse();
        }
        using (var forged = await SendAsync(client, tenantWide, OwnPath + "?cursor=eyJ2IjoxfQ"))
            await ProblemAsync(forged, HttpStatusCode.BadRequest, "validation_failed");
    }

    [Fact]
    public async Task Concurrent_creations_for_one_subject_and_group_file_exactly_one_request()
    {
        await using var proof = await CreatePreparedAsync();
        using var client = proof.Client();
        var learner = proof.Token(EnglishLearner, English);
        var admin = proof.Token(EnglishAdmin, English);
        var body = JsonSerializer.Serialize(new { membershipId = LearnerMembership });
        var attempts = Enumerable.Range(0, 6).Select(index => index % 2 == 0
            ? SendAsync(client, learner, OwnPath + "/exports", "POST", "{}", key: Key())
            : SendAsync(client, admin, AdminPath + "/exports", "POST", body, key: Key())).ToArray();
        var responses = await Task.WhenAll(attempts);
        try
        {
            responses.Count(response => response.StatusCode == HttpStatusCode.Accepted).Should().Be(1);
            // The subject's exclusive User fence serializes every creation; a loser is told the slot is taken, or, when it
            // waited past the fence's lock timeout, that the dependency is unavailable. Never a second request.
            var losers = responses.Where(response => response.StatusCode != HttpStatusCode.Accepted).ToArray();
            foreach (var refused in losers)
            {
                if (refused.StatusCode == HttpStatusCode.ServiceUnavailable)
                {
                    await ProblemAsync(refused, HttpStatusCode.ServiceUnavailable, "dependency_unavailable");
                    continue;
                }

                var problem = await ProblemAsync(refused, HttpStatusCode.Conflict, "business_rule_violation");
                problem.GetProperty("errors").GetProperty("$")[0].GetProperty("key").GetString().Should().Be("lockey_request_already_active");
            }

            losers.Should().Contain(response => response.StatusCode == HttpStatusCode.Conflict);
        }
        finally
        {
            foreach (var response in responses) response.Dispose();
        }

        (await CountAsync(proof, English.TenantId.Value, null,
            "SELECT count(*) FROM public.data_subject_requests WHERE status='requested' AND organization_id IS NULL")).Should().Be(1);
    }
}
