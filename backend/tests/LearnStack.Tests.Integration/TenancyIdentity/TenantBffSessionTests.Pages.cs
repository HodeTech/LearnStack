using System.Diagnostics;
using FluentAssertions;
using LearnStack.SharedKernel.Permissions;
using LearnStack.Tests.Integration.Database;
using LearnStack.Tools.Seeder;
using Npgsql;

namespace LearnStack.Tests.Integration.TenancyIdentity;

public sealed partial class TenantBffSessionTests
{
    private static async Task AssertSelectedNativePagesAsync(IdentityPublicReadFixture database,
        BffNativeDriver native, ApiHost api, SeedIdentity identity, BffWireCookies cookies)
    {
        var membership = SeedData.IdentityMemberships.Single(value => value.Identity == identity);
        (membership.IsFirstAdmin && membership.OrganizationId is null
            && membership.Scope == PermissionScope.Tenant
            && membership.Role.Scope == PermissionScope.Tenant
            && membership.Role.Tenant == identity.Tenant).Should().BeTrue();
        var memberId = membership.MembershipId.ToString("D");
        var roleId = membership.Role.RoleId.ToString("D");
        (string Target, (string Operation, string Entity, string? Id)[] Audits)[] pages =
        [
            ("/studio/members", [("identity.membership.list", "Membership", null)]),
            ("/studio/members/" + memberId,
                [("identity.membership.read", "Membership", memberId), ("identity.role.list", "Role", null)]),
            ("/studio/roles", [("identity.role.list", "Role", null)]),
            ("/studio/roles/" + roleId,
                [("identity.role.read", "Role", roleId), ("identity.role.list_permissions", "Role", null)]),
            ("/portal/profile", []),
        ];
        var traces = new HashSet<ActivityTraceId>();
        var beforeAudit = await AuditCountsAsync(database);
        foreach (var page in pages)
        {
            var beforeAccount = api.AccountResponses.Responses.Count;
            // Site bootstrap is an anonymous public read; this witness concerns
            // the separately authenticated AccountSite admission for the page.
            var beforeContext = api.Observed.Reads.Count(row => row.Actor is not null);
            var response = await native.RequestAsync(Host(identity.Tenant), page.Target, cookies.Header);
            response.Status.Should().Be(200);
            response.Rendered.Should().BeTrue();
            (response.PageReady == true).Should().BeTrue("the actual selected page must render its ready view, not an unavailable shell");
            response.PrivateFree.Should().BeTrue();
            (response.Cache?.Contains("no-store", StringComparison.Ordinal) == true).Should().BeTrue();
            RequireCookiesEmpty(response.Cookies, "ordinary selected-page reads do not replace the existing native session");
            var account = api.AccountResponses.Responses.Skip(beforeAccount).ToArray();
            account.Length.Should().Be(1, "each page obtains a fresh actual AccountSite witness");
            (account[0].Status == 200
                && account[0].Cache.Contains("no-store", StringComparison.Ordinal)).Should().BeTrue();
            ActivityContext.TryParse(account[0].Correlation, null, out var trace).Should().BeTrue();
            traces.Add(trace.TraceId).Should().BeTrue("each HTTP document owns a fresh request trace");
            var contexts = api.Observed.Reads.Where(row => row.Actor is not null).Skip(beforeContext).ToArray();
            contexts.Length.Should().Be(1);
            contexts.All(row => row.Actor == identity.UserId.Value
                && row.Tenant == identity.Tenant.TenantId.Value && row.Organization is null
                && row.HostTenant == identity.Tenant.TenantId.Value && row.HostOrganization is null)
                .Should().BeTrue("both management hosts preserve their selected NULL-org actor and tenant scope");

            // The public AccountSite receipt carries the same trace as the finite SDK reads.
            // Read the committed rows through the real app role after receiving the native body.
            var audits = await SelectedPageAuditsAsync(database, identity.Tenant, trace.TraceId);
            audits.Count.Should().Be(page.Audits.Length,
                "only the selected ordinary MUST reads commit; own profile/capabilities remain Off");
            audits.All(row => row.Tenant == identity.Tenant.TenantId.Value && row.Organization is null
                && row.Actor == identity.UserId.Value && row.Outcome == "success"
                && row.Class == "Must" && row.Type == "ReadSensitive"
                && ActivityContext.TryParse(row.Correlation, null, out var eventTrace)
                && eventTrace.TraceId == trace.TraceId).Should().BeTrue();
            foreach (var expected in page.Audits)
                audits.Count(row => row.Operation == expected.Operation && row.Entity == expected.Entity
                    && row.EntityId == expected.Id).Should().Be(1,
                    "the current trace must contain the exact declared operation and original resource target");
        }
        var afterAudit = await AuditCountsAsync(database);
        afterAudit.Where((count, index) => count != beforeAudit[index]
            + (SeedData.All[index] == identity.Tenant ? 6 : 0)).Any().Should().BeFalse();
    }

    private static async Task<List<SelectedPageAudit>> SelectedPageAuditsAsync(
        IdentityPublicReadFixture database, SeedTenant tenant, ActivityTraceId trace)
    {
        await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(connection);
        await using var transaction = await connection.BeginTransactionAsync();
        await IdentityMembershipFixture.ScopeAsync(connection, transaction, tenant.TenantId.Value, null);
        await using var command = new NpgsqlCommand("""
            SELECT tenant_id, organization_id, actor_user_id, operation, outcome,
                operation_class, operation_type, entity_type, entity_id, correlation_id
            FROM audit_log WHERE split_part(correlation_id, '-', 2)=@trace
            """, connection, transaction);
        command.Parameters.AddWithValue("trace", trace.ToString());
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<SelectedPageAudit>();
        while (await reader.ReadAsync())
            rows.Add(new(reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetGuid(1),
                reader.IsDBNull(2) ? null : reader.GetGuid(2), reader.GetString(3), reader.GetString(4),
                reader.GetString(5), reader.GetString(6), reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8), reader.GetString(9)));
        return rows;
    }

    private sealed record SelectedPageAudit(Guid Tenant, Guid? Organization, Guid? Actor,
        string Operation, string Outcome, string Class, string Type, string Entity,
        string? EntityId, string Correlation);
}
