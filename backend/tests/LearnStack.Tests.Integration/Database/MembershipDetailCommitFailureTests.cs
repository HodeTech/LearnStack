using System.Text.Json;
using LearnStack.Api.Common;
using LearnStack.Modules.Identity.Application.Contracts.Memberships;
using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Results;
using Npgsql;
using Xunit;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed class MembershipDetailCommitFailureTests(MembershipAdministrationFixture fixture)
    : IClassFixture<MembershipAdministrationFixture>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Scoped_and_Tenant_detail_withhold_the_DTO_and_preserve_uncertainty_when_MUST_COMMIT_fails(bool tenantClause)
    {
        var target = await fixture.AddMemberAsync(SchemaFixture.OrgA1);
        var administrator = await fixture.AddMemberAsync(SchemaFixture.OrgA1, BuiltInRoleKey.OrgAdmin);
        var actor = tenantClause ? AdminA : administrator.Actor;
        var correlation = tenantClause ? "membership-detail-commit-tenant" : "membership-detail-commit-org";
        var before = await fixture.ReadAsync(target.Membership, SchemaFixture.OrgA1);
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.OwnerConnectionString);
        await ExecuteAsync(owner, null, """
            CREATE FUNCTION membership_detail_commit_proof() RETURNS trigger
                LANGUAGE plpgsql SECURITY INVOKER SET search_path=pg_catalog AS $$
            BEGIN
                IF NEW.correlation_id IN ('membership-detail-commit-tenant','membership-detail-commit-org')
                    AND NEW.operation='identity.membership.read'
                    AND NEW.outcome='success' THEN
                    RAISE EXCEPTION 'fixed membership detail commit refusal' USING ERRCODE='23514';
                END IF;
                RETURN NEW;
            END $$;
            CREATE CONSTRAINT TRIGGER tg_membership_detail_commit_proof AFTER INSERT ON public.audit_log
                DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION membership_detail_commit_proof();
            """);
        try
        {
            await using var harness = await fixture.CreateAsync(correlation, actor, SchemaFixture.OrgA1);
            Result<MembershipDetailDto>? published = null;
            var failure = await Assert.ThrowsAsync<AuditWriteFailedException>(async () => published = await harness.Sender.Send(
                new GetMembershipDetailQuery(target.Membership)));
            Assert.Equal("audit_unavailable", failure.Error.Code);
            Assert.Equal(503, HttpStatusMap.For(failure));
            Assert.Equal("23514", Assert.IsType<PostgresException>(failure.InnerException).SqlState);
            Assert.Null(published);
            Assert.False(harness.Unit.HasActiveTransaction);
            var after = await fixture.ReadAsync(target.Membership, SchemaFixture.OrgA1);
            Assert.Equal(before.Version, after.Version);
            Assert.Equal(before.Status, after.Status);
            Assert.Equal(before.DisplayName, after.DisplayName);
            Assert.Equal(before.PreferredLocale, after.PreferredLocale);
            Assert.Equal(before.RootStamp, after.RootStamp);
            Assert.Equal(before.Assignments.OrderBy(pair => pair.Key), after.Assignments.OrderBy(pair => pair.Key));
            var row = Assert.Single(await fixture.AuditAsync(correlation, "identity.membership.read", SchemaFixture.OrgA1));
            using var audit = JsonDocument.Parse(row);
            Assert.Equal("indeterminate", audit.RootElement.GetProperty("outcome").GetString());
        }
        finally
        {
            await ExecuteAsync(owner, null,
                "DROP TRIGGER tg_membership_detail_commit_proof ON public.audit_log; DROP FUNCTION membership_detail_commit_proof()");
        }
        var recoveredCorrelation = correlation + "-recovered";
        var recovered = await fixture.SendAsync(new GetMembershipDetailQuery(target.Membership),
            recoveredCorrelation, actor, SchemaFixture.OrgA1);
        Assert.True(recovered.IsSuccess);
        Assert.Equal(target.Membership, recovered.Value.MembershipId);
        var success = Assert.Single(await fixture.AuditAsync(recoveredCorrelation, "identity.membership.read", SchemaFixture.OrgA1));
        using var confirmed = JsonDocument.Parse(success);
        Assert.Equal("success", confirmed.RootElement.GetProperty("outcome").GetString());
    }
}
