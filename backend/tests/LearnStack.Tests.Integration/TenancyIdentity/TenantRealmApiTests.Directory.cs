using System.Net;
using System.Text.Json;
using FluentAssertions;
using LearnStack.Tests.Integration.Database;
using LearnStack.Tools.Seeder;

namespace LearnStack.Tests.Integration.TenancyIdentity;

public sealed partial class TenantRealmApiTests
{
    private async Task DirectoryAsync(HttpClient client, SeedIdentity identity, string accessToken)
    {
        var membership = SeedData.IdentityMemberships.Single(member => member.Identity == identity);
        using var response = await Send(client, identity.Tenant, accessToken, IdentityAuthorizationHttpProof.DirectoryPath);
        TenantAuthenticationHttpTests.NoStore(response);
        if (membership.IsFirstAdmin)
        {
            response.StatusCode.Should().Be(HttpStatusCode.OK,
                "the actual provider administrator resolves its stored membership and permission grant");
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var page = document.RootElement;
            page.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo(["items", "pageInfo"]);
            var rows = page.GetProperty("items").EnumerateArray().ToArray();
            var expected = SeedData.IdentityMemberships.Where(member => member.Identity.Tenant == identity.Tenant).ToArray();
            rows.Select(row => row.GetProperty("membershipId").GetGuid()).Should()
                .BeEquivalentTo(expected.Select(member => member.MembershipId));
            foreach (var row in rows)
            {
                row.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo(
                    ["membershipId", "userId", "organizationId", "status", "displayName", "preferredLocale"]);
                var declared = expected.Single(member => member.MembershipId == row.GetProperty("membershipId").GetGuid());
                row.GetProperty("userId").GetGuid().Should().Be(declared.Identity.UserId.Value);
                row.GetProperty("status").GetString().Should().Be("Active");
                row.GetProperty("displayName").GetString().Should().Be(declared.DisplayName);
                row.GetProperty("preferredLocale").GetString().Should().Be(declared.PreferredLocale);
                if (declared.OrganizationId is { } organization)
                    row.GetProperty("organizationId").GetGuid().Should().Be(organization.Value);
                else row.GetProperty("organizationId").ValueKind.Should().Be(JsonValueKind.Null);
            }
            page.GetProperty("pageInfo").GetProperty("nextCursor").ValueKind.Should().Be(JsonValueKind.Null);
        }
        else
        {
            await TenantAuthenticationHttpTests.Problem(response, HttpStatusCode.Forbidden, "forbidden");
            (await response.Content.ReadAsStringAsync()).Should().NotContain("membershipId").And.NotContain("displayName");
        }

        var correlation = TenantAuthenticationReconciliationTests.Correlation(response);
        var audit = (await AccountHttpAudit.Read(_database, identity.Tenant, correlation,
            identity.Tenant.MapHostToDefaultOrganization ? identity.Tenant.DefaultOrganization.OrganizationId : null))
            .Should().ContainSingle("the completed provider-backed request must have its committed MUST event").Subject;
        audit.Tenant.Should().Be(identity.Tenant.TenantId.Value);
        audit.Organization.Should().Be(Organization(identity.Tenant));
        audit.Actor.Should().Be(identity.UserId.Value);
        audit.Correlation.Should().Be(correlation);
        audit.Operation.Should().Be(membership.IsFirstAdmin ? "identity.membership.list" : "identity.authorization.deny");
        audit.Outcome.Should().Be(membership.IsFirstAdmin ? "success" : "denied");
        audit.Metadata.Should().Contain("ListMemberships");
    }
}
