using FluentAssertions;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class EducationStructureTests
{
    [Fact]
    public async Task Existing_invitation_schema_upgrades_both_parent_locks_without_changing_grants_or_relations()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(_schema.Postgres, applyMigrations: false);
        await MigrationChains.ApplyThroughInvitationPredecessorsAsync(database.MigrationConnectionString,
            "20261104181406_add_invitation_protected_receipts", "20261104180100_add_invitation_workflow_failure");
        await using var owner = await PostgresFixture.OpenAsync(database.MigrationConnectionString);
        var beforeRelations = await ReadForeignKeysAsync(owner, null);
        const string aclSql = """
            SELECT 'table:' || relname || ':' || relacl::text FROM pg_class
              WHERE oid IN ('public.invitations'::regclass,'public.invitation_email_envelopes'::regclass,
                'public.invitation_email_attempt_receipts'::regclass)
            UNION ALL
            SELECT 'function:' || proname || ':' || proacl::text FROM pg_proc
              WHERE oid IN ('public.fn_identity_invitation_guard()'::regprocedure,
                'public.fn_invitation_email_envelope_guard()'::regprocedure)
            ORDER BY 1
            """;
        var beforeAcl = await SchemaQueries.ReadStringsAsync(owner, aclSql);
        foreach (var function in new[] { InvitationSchemaGuardExpectations.InvitationGuard, InvitationSchemaGuardExpectations.EnvelopeGuard })
        {
            var old = (await SchemaQueries.ReadStringsAsync(owner,
                $"SELECT prosrc FROM pg_proc WHERE oid='public.{function.Name}()'::regprocedure")).Single();
            InvitationSchemaGuardExpectations.Matches(old, function.Body.Replace(" FOR KEY SHARE", "", StringComparison.Ordinal))
                .Should().BeTrue("the predecessor must carry the exact old body, not a fresh-schema approximation");
        }
        await MigrationChains.ApplyAllAsync(database.MigrationConnectionString);
        (await ParentGuardOffendersAsync(owner)).Should().BeEmpty();
        (await ReadForeignKeysAsync(owner, null)).Should().BeEquivalentTo(beforeRelations);
        (await SchemaQueries.ReadStringsAsync(owner, aclSql)).Should().Equal(beforeAcl);
    }

    [Theory]
    [InlineData("no-lock")]
    [InlineData("wrong-tenant")]
    [InlineData("wrong-parent")]
    [InlineData("nullable-compare")]
    [InlineData("missing-parent")]
    [InlineData("early-return")]
    public async Task Invitation_result_parent_guard_rejects_each_weakened_lookup(string defect)
    {
        await using var owner = await PostgresFixture.OpenAsync(_schema.Postgres.MigrationConnectionString);
        await using var transaction = await owner.BeginTransactionAsync();
        var original = await DefinitionAsync(owner, transaction,
            "SELECT pg_get_functiondef('public.fn_identity_invitation_guard()'::regprocedure)");
        var mutated = defect switch
        {
            "no-lock" => original.Replace(" FOR KEY SHARE", "", StringComparison.Ordinal),
            "wrong-tenant" => original.Replace("tenant_id=NEW.tenant_id AND id=NEW.result_membership_id",
                "tenant_id=tenant_id AND id=NEW.result_membership_id", StringComparison.Ordinal),
            "wrong-parent" => original.Replace("id=NEW.result_membership_id", "id=NEW.id", StringComparison.Ordinal),
            "nullable-compare" => original.Replace("selected_member.organization_id IS DISTINCT FROM",
                "selected_member.organization_id <>", StringComparison.Ordinal),
            "missing-parent" => original.Replace("NOT FOUND OR selected_member.organization_id",
                "selected_member.organization_id", StringComparison.Ordinal),
            _ => original.Replace("BEGIN", "BEGIN RETURN NEW;", StringComparison.Ordinal),
        };
        mutated.Should().NotBe(original);
        await SchemaQueries.ExecuteAsync(owner, transaction, mutated);
        (await ParentGuardOffendersAsync(owner, transaction)).Should().ContainSingle().Which.Should().Be("invitations: parent guard");
    }
}
