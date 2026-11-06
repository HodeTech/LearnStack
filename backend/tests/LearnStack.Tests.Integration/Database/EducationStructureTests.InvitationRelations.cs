using System.Data.Common;
using FluentAssertions;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class EducationStructureTests
{
    private sealed record ProviderRelation(string Child, string Parent, string Constraint,
        string ChildColumns, string ParentColumns);

    // ADR-0093's SQL-only provider metadata has no module EF relationship.
    // Each exact relation is still checked, including its independent immutable
    // guard and the closed owner-only receipt insertion functions/privileges.
    private static readonly ProviderRelation[] InvitationProviderRelations =
    [
        new("invitation_email_envelopes", "background_jobs", "fk_invitation_email_envelopes_job",
            "tenant_id,job_id", "tenant_id,id"),
        new("invitation_email_attempt_receipts", "invitation_email_envelopes", "fk_invitation_email_attempt_receipts_envelope",
            "tenant_id,envelope_id", "tenant_id,id"),
        new("invitation_email_attempt_receipts", "background_job_attempts", "fk_invitation_email_attempt_receipts_attempt",
            "tenant_id,job_id,attempt_id", "tenant_id,job_id,id"),
    ];

    private static bool IsDeclaredInvitationProviderRelation(ForeignKey key) => InvitationProviderRelations.Any(relation =>
        relation.Child == key.Child && relation.Parent == key.Parent && relation.Constraint == key.Name);

    private static async Task<List<string>> InvitationProviderGuardOffendersAsync(DbConnection connection,
        DbTransaction? transaction, List<ForeignKey> applied, List<string[]> triggers)
    {
        var offenders = new List<string>();
        foreach (var relation in InvitationProviderRelations)
        {
            var keys = applied.Where(key => key.Child == relation.Child && key.Parent == relation.Parent
                && key.Name == relation.Constraint).ToArray();
            if (keys.Length != 1)
                offenders.Add($"{relation.Child}: missing parent FK ({relation.Constraint})");
            else if (keys[0].ChildColumns != relation.ChildColumns || keys[0].ParentColumns != relation.ParentColumns
                || keys[0].DeleteAction != "a" || !keys[0].Validated || keys[0].Deferred)
                offenders.Add($"{relation.Child}: tenant-composite FK ({relation.Constraint})");
        }

        foreach (var (table, function, triggerType) in new[]
        {
            ("invitation_email_envelopes", InvitationSchemaGuardExpectations.EnvelopeGuard, "31"),
            ("invitation_email_attempt_receipts", InvitationSchemaGuardExpectations.AttemptGuard, "27"),
        })
            if (!triggers.Any(trigger => trigger[0] == table && trigger[1] == "public" && trigger[3] == "true"
                    && trigger[4] == function.Name && trigger[5] == triggerType && trigger[6] == "false"
                    && trigger[7] == "false" && trigger[8] == "v")
                || !await InvitationSchemaGuardExpectations.IsExactAsync(connection, transaction, function))
                offenders.Add($"{table}: provider binding guard");

        foreach (var function in new[] { InvitationSchemaGuardExpectations.ClaimFunction, InvitationSchemaGuardExpectations.CompleteFunction })
            if (!await InvitationSchemaGuardExpectations.IsExactAsync(connection, transaction, function))
                offenders.Add($"invitation_email_attempt_receipts: confined writer ({function.Name})");

        var custody = await SchemaQueries.ReadStringsAsync(connection, """
            SELECT 'invitation_email_attempt_receipts: owner-only custody'
            WHERE EXISTS (
                SELECT 1 FROM pg_policies WHERE schemaname='public'
                  AND tablename='invitation_email_attempt_receipts'
                  AND (policyname <> 'invitation_email_attempt_receipts_isolation'
                    OR permissive <> 'PERMISSIVE' OR cmd <> 'ALL'
                    OR qual <> '(CURRENT_USER = ''learnstack_migration''::name)'
                    OR with_check <> '(CURRENT_USER = ''learnstack_migration''::name)'))
              OR (SELECT count(*) FROM pg_policies WHERE schemaname='public'
                    AND tablename='invitation_email_attempt_receipts') <> 1
              OR EXISTS (SELECT 1 FROM unnest(ARRAY['learnstack_app','learnstack_platform','learnstack_outbox_admin']) role_name
                    WHERE has_any_column_privilege(role_name,'public.invitation_email_attempt_receipts','SELECT,INSERT,UPDATE')
                       OR has_table_privilege(role_name,'public.invitation_email_attempt_receipts','DELETE,TRUNCATE'))
            """, transaction);
        offenders.AddRange(custody);
        return offenders;
    }

    [Theory]
    [InlineData("invitation_email_envelopes")]
    [InlineData("invitation_email_attempt_receipts")]
    public async Task Provider_parent_census_rejects_each_missing_and_weakened_composite_relation(string table)
    {
        await using var owner = await PostgresFixture.OpenAsync(_schema.Postgres.MigrationConnectionString);
        await using var transaction = await owner.BeginTransactionAsync();
        (await ParentGuardOffendersAsync(owner, transaction)).Should().BeEmpty();
        foreach (var relation in InvitationProviderRelations.Where(relation => relation.Child == table))
        {
            var key = (await ReadForeignKeysAsync(owner, transaction)).Single(key => key.Name == relation.Constraint);
            await SchemaQueries.ExecuteAsync(owner, transaction, $"ALTER TABLE public.{table} DROP CONSTRAINT {key.Name}");
            (await ParentGuardOffendersAsync(owner, transaction)).Should().ContainSingle()
                .Which.Should().Be($"{table}: missing parent FK ({key.Name})");
            await SchemaQueries.ExecuteAsync(owner, transaction,
                $"ALTER TABLE public.{table} ADD CONSTRAINT {key.Name} {key.Definition} NOT VALID");
            (await ParentGuardOffendersAsync(owner, transaction)).Should().ContainSingle()
                .Which.Should().Be($"{table}: tenant-composite FK ({key.Name})");
            await SchemaQueries.ExecuteAsync(owner, transaction, $"ALTER TABLE public.{table} VALIDATE CONSTRAINT {key.Name}");
            (await ParentGuardOffendersAsync(owner, transaction)).Should().BeEmpty();
        }
    }

    [Theory]
    [InlineData("fn_invitation_email_envelope_guard", "no-lock")]
    [InlineData("fn_invitation_email_envelope_guard", "wrong-tenant")]
    [InlineData("fn_invitation_email_envelope_guard", "nullable-compare")]
    [InlineData("fn_invitation_email_envelope_guard", "early-return")]
    [InlineData("fn_invitation_email_attempt_guard", "mutable-scope")]
    [InlineData("fn_invitation_email_attempt_guard", "early-return")]
    public async Task Provider_parent_census_rejects_weakened_binding_function(string function, string defect)
    {
        await using var owner = await PostgresFixture.OpenAsync(_schema.Postgres.MigrationConnectionString);
        await using var transaction = await owner.BeginTransactionAsync();
        var body = await DefinitionAsync(owner, transaction,
            $"SELECT prosrc FROM pg_proc WHERE oid='public.{function}()'::regprocedure");
        var mutated = defect switch
        {
            "no-lock" => body.Replace(" FOR KEY SHARE", "", StringComparison.Ordinal),
            "wrong-tenant" => body.Replace("tenant_id=NEW.tenant_id", "tenant_id=tenant_id", StringComparison.Ordinal),
            "nullable-compare" => body.Replace("IS DISTINCT FROM NEW.organization_id", "<> NEW.organization_id", StringComparison.Ordinal),
            "mutable-scope" => body.Replace("NEW.tenant_id,NEW.organization_id,", "NEW.tenant_id,", StringComparison.Ordinal)
                .Replace("OLD.tenant_id,OLD.organization_id,", "OLD.tenant_id,", StringComparison.Ordinal),
            _ => body.Replace("BEGIN", "BEGIN RETURN NEW;", StringComparison.Ordinal),
        };
        mutated.Should().NotBe(body);
        await SchemaQueries.ExecuteAsync(owner, transaction,
            $"CREATE OR REPLACE FUNCTION public.{function}() RETURNS trigger LANGUAGE plpgsql "
            + $"SECURITY INVOKER SET search_path=pg_catalog AS $probe${mutated}$probe$");
        var table = function == "fn_invitation_email_envelope_guard" ? "invitation_email_envelopes" : "invitation_email_attempt_receipts";
        (await ParentGuardOffendersAsync(owner, transaction)).Should().ContainSingle()
            .Which.Should().Be($"{table}: provider binding guard");
    }

    [Theory]
    [InlineData("invitation_email_envelopes", "missing")]
    [InlineData("invitation_email_envelopes", "disabled")]
    [InlineData("invitation_email_envelopes", "after")]
    [InlineData("invitation_email_envelopes", "conditional")]
    [InlineData("invitation_email_attempt_receipts", "missing")]
    [InlineData("invitation_email_attempt_receipts", "disabled")]
    [InlineData("invitation_email_attempt_receipts", "after")]
    [InlineData("invitation_email_attempt_receipts", "conditional")]
    public async Task Provider_parent_census_rejects_inert_binding_triggers(string table, string defect)
    {
        await using var owner = await PostgresFixture.OpenAsync(_schema.Postgres.MigrationConnectionString);
        await using var transaction = await owner.BeginTransactionAsync();
        var envelope = table == "invitation_email_envelopes";
        var function = envelope ? "fn_invitation_email_envelope_guard" : "fn_invitation_email_attempt_guard";
        var trigger = $"tg_{table}_guard";
        await SchemaQueries.ExecuteAsync(owner, transaction, $"DROP TRIGGER {trigger} ON public.{table}");
        if (defect != "missing")
        {
            var timing = defect == "after" ? "AFTER" : "BEFORE";
            var events = envelope ? "INSERT OR UPDATE OR DELETE" : "UPDATE OR DELETE";
            var condition = defect == "conditional" ? " WHEN (true)" : "";
            await SchemaQueries.ExecuteAsync(owner, transaction,
                $"CREATE TRIGGER {trigger} {timing} {events} ON public.{table} FOR EACH ROW{condition} EXECUTE FUNCTION public.{function}()");
            if (defect == "disabled")
                await SchemaQueries.ExecuteAsync(owner, transaction, $"ALTER TABLE public.{table} DISABLE TRIGGER {trigger}");
        }
        (await ParentGuardOffendersAsync(owner, transaction)).Should().ContainSingle()
            .Which.Should().Be($"{table}: provider binding guard");
    }

    [Theory]
    [InlineData("grant")]
    [InlineData("policy")]
    [InlineData("claim")]
    [InlineData("complete")]
    public async Task Provider_parent_census_rejects_a_widened_receipt_writer(string defect)
    {
        await using var owner = await PostgresFixture.OpenAsync(_schema.Postgres.MigrationConnectionString);
        await using var transaction = await owner.BeginTransactionAsync();
        string expected;
        if (defect is "grant" or "policy")
        {
            await SchemaQueries.ExecuteAsync(owner, transaction, defect == "grant"
                ? "GRANT INSERT ON public.invitation_email_attempt_receipts TO learnstack_app"
                : "ALTER POLICY invitation_email_attempt_receipts_isolation ON public.invitation_email_attempt_receipts WITH CHECK (true)");
            expected = "invitation_email_attempt_receipts: owner-only custody";
        }
        else
        {
            var name = defect == "claim" ? "fn_invitation_smtp_claim_or_read" : "fn_invitation_smtp_complete_attempt";
            var definition = await DefinitionAsync(owner, transaction,
                $"SELECT pg_get_functiondef(oid) FROM pg_proc WHERE proname='{name}' AND pronamespace='public'::regnamespace");
            var mutated = definition.Replace("BEGIN", "BEGIN RETURN;", StringComparison.Ordinal);
            mutated.Should().NotBe(definition);
            await SchemaQueries.ExecuteAsync(owner, transaction, mutated);
            expected = $"invitation_email_attempt_receipts: confined writer ({name})";
        }
        (await ParentGuardOffendersAsync(owner, transaction)).Should().ContainSingle().Which.Should().Be(expected);
    }
}
