using System.Text.Json;
using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Results;
using Npgsql;
using Xunit;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed class ModuleReceiptAuditFailureTests(TenancyAdministrationFixture fixture) : IClassFixture<TenancyAdministrationFixture>
{
    [Theory]
    [InlineData("membership", false, false)]
    [InlineData("membership", true, false)]
    [InlineData("profile", false, false)]
    [InlineData("profile", true, false)]
    [InlineData("own", false, false)]
    [InlineData("own", true, false)]
    [InlineData("role", false, false)]
    [InlineData("role", true, false)]
    [InlineData("organization", false, false)]
    [InlineData("organization", true, false)]
    [InlineData("default", false, false)]
    [InlineData("default", true, false)]
    [InlineData("membership", false, true)]
    [InlineData("membership", true, true)]
    [InlineData("profile", false, true)]
    [InlineData("profile", true, true)]
    public async Task Every_receipt_withholds_success_when_its_MUST_audit_insert_or_commit_fails(string kind, bool deferred, bool organizationClause)
    {
        var correlation = "receipt-must-refusal-" + kind + (deferred ? "-commit" : "-insert") + (organizationClause ? "-org" : "-tenant");
        var organization = organizationClause ? SchemaFixture.OrgA1 : (Guid?)null;
        var member = organizationClause ? BranchMembershipA1 : AdminMembershipA;
        var actor = organizationClause ? (await fixture.Membership.AddMemberAsync(SchemaFixture.OrgA1, BuiltInRoleKey.OrgAdmin)).Actor : AdminA;
        var query = ModuleReceiptDisclosureTests.Query(kind, member);
        var operation = ModuleReceiptDisclosureTests.Operation(kind);
        var before = await fixture.Membership.ReadAsync(member, organization);
        var tenantVersion = await fixture.TenantVersionAsync();
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.OwnerConnectionString);
        await ExecuteAsync(owner, null, """
            CREATE FUNCTION module_receipt_audit_proof() RETURNS trigger
                LANGUAGE plpgsql SECURITY INVOKER SET search_path=pg_catalog AS $$
            BEGIN
                IF NEW.correlation_id LIKE 'receipt-must-refusal-%' AND NEW.outcome='success'
                    AND NEW.operation IN ('identity.membership.read_receipt','identity.membership_profile.read_receipt',
                        'identity.role.read_receipt','tenancy.organization.read_receipt') THEN
                    RAISE EXCEPTION 'fixed module receipt audit refusal' USING ERRCODE='23514';
                END IF;
                RETURN NEW;
            END $$;
            """);
        await ExecuteAsync(owner, null, deferred ? """
            CREATE CONSTRAINT TRIGGER tg_module_receipt_audit_proof AFTER INSERT ON public.audit_log
                DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION module_receipt_audit_proof();
            """ : """
            CREATE TRIGGER tg_module_receipt_audit_proof BEFORE INSERT ON public.audit_log
                FOR EACH ROW EXECUTE FUNCTION module_receipt_audit_proof();
            """);
        try
        {
            await using var harness = await fixture.CreateAsync(correlation, actor, organization);
            Result<None>? published = null;
            var failure = await Assert.ThrowsAsync<AuditWriteFailedException>(async () => published = await harness.Sender.Send(query));
            Assert.Equal("audit_unavailable", failure.Error.Code);
            Assert.Null(published);
            Assert.False(harness.Unit.HasActiveTransaction);
            Assert.Equal(before.RootStamp, (await fixture.Membership.ReadAsync(member, organization)).RootStamp);
            Assert.Equal(tenantVersion, await fixture.TenantVersionAsync());
            var rows = await fixture.Membership.AuditAsync(correlation, operation, organization);
            ModuleReceiptDisclosureTests.AssertNoSuccess(rows);
            if (deferred)
            {
                using var audit = JsonDocument.Parse(Assert.Single(rows));
                Assert.Equal("indeterminate", audit.RootElement.GetProperty("outcome").GetString());
            }
        }
        finally
        {
            await ExecuteAsync(owner, null,
                "DROP TRIGGER tg_module_receipt_audit_proof ON public.audit_log; DROP FUNCTION module_receipt_audit_proof()");
        }
        var recoveredCorrelation = correlation.Replace("receipt-must-refusal-", "receipt-must-recovered-", StringComparison.Ordinal);
        Assert.True((await fixture.SendAsync(query, recoveredCorrelation, actor, organization)).IsSuccess);
        using var recovered = JsonDocument.Parse(Assert.Single(await fixture.Membership.AuditAsync(recoveredCorrelation, operation, organization)));
        Assert.Equal("success", recovered.RootElement.GetProperty("outcome").GetString());
        Assert.Equal("Must", recovered.RootElement.GetProperty("class").GetString());
        Assert.Equal("ReadSensitive", recovered.RootElement.GetProperty("type").GetString());
    }
}
