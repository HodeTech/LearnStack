using System.Net;
using FluentAssertions;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Permissions;
using LearnStack.Tools.Seeder;
using Xunit;
using static LearnStack.Tests.Integration.Database.IdentityAuthorizationHttpProof;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class IdentityAuthorizationHttpTests
{
    [Fact]
    public async Task Directory_exposes_only_contextual_fields_with_live_tenant_wide_pagination_and_scope_bound_cursors()
    {
        await using var proof = await CreateAsync(parent);
        var firstBranch = await proof.Database.AddMembershipAsync(SeedData.English, EnglishLearner,
            SeedData.English.DefaultOrganization.OrganizationId, displayName: "suspended-contextual-profile");
        await proof.SetMembershipStatusAsync(SeedData.English, EnglishLearner, firstBranch, "Suspended",
            SeedData.English.DefaultOrganization.OrganizationId);
        var secondBranch = await proof.Database.AddMembershipAsync(SeedData.English, EnglishLearner,
            SeedData.English.SecondOrganization.OrganizationId, displayName: "second-branch-contextual-profile");
        var otherAdmin = UserId.From(Guid.CreateVersion7());
        const string OtherSubject = "cursor-other-admin";
        await proof.Database.AdoptAsync(otherAdmin, OtherSubject);
        var otherMembership = await proof.Database.AddMembershipAsync(SeedData.English, otherAdmin,
            role: BuiltInRoleKey.TenantAdmin);
        var revoked = await proof.Database.AddMembershipAsync(SeedData.English, otherAdmin,
            SeedData.English.DefaultOrganization.OrganizationId, displayName: "revoked-profile-canary");
        await proof.SetMembershipStatusAsync(SeedData.English, otherAdmin, revoked, "Revoked",
            SeedData.English.DefaultOrganization.OrganizationId);
        using var client = proof.Client();
        var token = proof.Token(EnglishAdmin, SeedData.English, organization: true);
        using var full = await SendAsync(client, token);
        var page = await PageAsync(full);
        proof.Observation.LastTake.Should().Be(21, "the default page has a 20-row limit plus one lookahead");
        var rows = page.GetProperty("items").EnumerateArray().ToArray();
        rows.Select(row => row.GetProperty("membershipId").GetGuid()).Should().BeEquivalentTo(
            [SeedData.IdentityMemberships[0].MembershipId, SeedData.IdentityMemberships[1].MembershipId,
                firstBranch, secondBranch, otherMembership]);
        foreach (var row in rows)
            row.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo(
                ["membershipId", "userId", "organizationId", "status", "displayName", "preferredLocale"]);
        page.GetRawText().Should().NotContain("revoked-profile-canary")
            .And.NotContain(IdentityPublicReadFixture.FoundationSubject(EnglishAdmin));

        using var capped = await SendAsync(client, token, path: DirectoryPath + "?limit=1000");
        await PageAsync(capped);
        proof.Observation.LastTake.Should().Be(101, "a clamped 100-row page requests one lookahead");

        using var first = await SendAsync(client, token, path: DirectoryPath + "?limit=1");
        var firstPage = await PageAsync(first);
        var cursor = firstPage.GetProperty("pageInfo").GetProperty("nextCursor").GetString()!;
        var ids = new List<Guid> { firstPage.GetProperty("items")[0].GetProperty("membershipId").GetGuid() };
        var next = cursor;
        while (next is not null)
        {
            using var response = await SendAsync(client, token,
                path: DirectoryPath + "?limit=1&cursor=" + Uri.EscapeDataString(next));
            var current = await PageAsync(response);
            ids.AddRange(current.GetProperty("items").EnumerateArray().Select(row => row.GetProperty("membershipId").GetGuid()));
            next = current.GetProperty("pageInfo").GetProperty("nextCursor").GetString();
            ids.Count.Should().BeLessThanOrEqualTo(rows.Length, "seek pagination must make progress");
        }
        ids.Should().Equal(rows.Select(row => row.GetProperty("membershipId").GetGuid()));
        var replayPath = DirectoryPath + "?cursor=" + Uri.EscapeDataString(cursor);
        foreach (var differentScope in new[]
        {
            proof.Token(EnglishAdmin, SeedData.English),
            proof.Token(otherAdmin, SeedData.English, organization: true, subject: OtherSubject),
        })
        {
            using var refused = await SendAsync(client, differentScope, path: replayPath);
            await ProblemAsync(refused, HttpStatusCode.BadRequest, "validation_failed");
        }
        foreach (var suffix in new[] { "?limit=0", "?cursor=not-a-cursor", "?sort=displayName", "?limit=1&limit=2",
            "?cursor=" + new string('a', 1025) })
        {
            using var refused = await SendAsync(client, token, path: DirectoryPath + suffix);
            await ProblemAsync(refused, HttpStatusCode.BadRequest, "validation_failed");
        }

        var ownToken = proof.Token(otherAdmin, SeedData.English, organization: true, subject: OtherSubject);
        using var ownFirst = await SendAsync(client, ownToken, path: DirectoryPath + "?limit=1");
        var ownPage = await PageAsync(ownFirst);
        var ownCursor = ownPage.GetProperty("pageInfo").GetProperty("nextCursor").GetString()!;
        await proof.SetMembershipStatusAsync(SeedData.English, otherAdmin, otherMembership, "Revoked");
        var reads = proof.Observation.DirectoryReads;
        using var afterRevocation = await SendAsync(client, ownToken,
            path: DirectoryPath + "?cursor=" + Uri.EscapeDataString(ownCursor));
        await ProblemAsync(afterRevocation, HttpStatusCode.NotFound, "not_found");
        proof.Observation.DirectoryReads.Should().Be(reads, "a cursor supplies no reusable authorization");
    }
}
