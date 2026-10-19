using System.Data.Common;
using FluentAssertions;
using LearnStack.SharedKernel.Identifiers;
using Npgsql;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobPositiveSdkTests
{
    private static async Task InstallTransactionObserverAsync(DisposableSchemaDatabase database,
        OrganizationId prepared, OrganizationId finalized)
    {
        await using var connection = await PostgresFixture.OpenAsync(database.MigrationConnectionString);
        // Test-only observer writes on the real caller transaction. pg_current_xact_id
        // identifies its top-level transaction even when EF uses a savepoint;
        // tuple xmin may instead name a child transaction and cannot prove this.
        await using var command = new NpgsqlCommand($"""
            CREATE TABLE sdk_transaction_observations(label text NOT NULL,root_id xid8 NOT NULL);
            GRANT INSERT,SELECT ON sdk_transaction_observations TO learnstack_app;
            CREATE FUNCTION sdk_observe_transaction() RETURNS trigger LANGUAGE plpgsql
                SET search_path=pg_catalog,public AS $$
            DECLARE label text;
            BEGIN
                IF TG_TABLE_NAME='organizations' THEN
                    IF NEW.id='{prepared.Value:D}'::uuid THEN label:='prepare-domain';
                    ELSIF NEW.id='{finalized.Value:D}'::uuid THEN label:='finalize-domain'; END IF;
                ELSIF TG_TABLE_NAME='audit_log' THEN
                    -- Reconciled failed attempts have their separate standalone
                    -- transaction; they are not successful business writes.
                    IF NEW.outcome<>'success' THEN RETURN NEW; END IF;
                    IF NEW.operation='tenancy.organization.create' AND NEW.entity_id='{prepared.Value:D}' THEN
                        label:='prepare-business-must';
                    ELSIF NEW.operation='tenancy.organization.create' AND NEW.entity_id='{finalized.Value:D}' THEN
                        label:='finalize-business-must';
                    ELSIF NEW.operation='platform.job.dead_letter' THEN label:='finalize-terminal-must'; END IF;
                ELSIF NEW.phase IN ('completed','terminal_pending') AND OLD.phase='finalizing' THEN
                    label:='finalize-job';
                END IF;
                IF label IS NOT NULL THEN
                    INSERT INTO public.sdk_transaction_observations VALUES(label,pg_current_xact_id());
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER sdk_observe_domain AFTER INSERT ON organizations
                FOR EACH ROW EXECUTE FUNCTION sdk_observe_transaction();
            CREATE TRIGGER sdk_observe_audit AFTER INSERT ON audit_log
                FOR EACH ROW EXECUTE FUNCTION sdk_observe_transaction();
            CREATE TRIGGER sdk_observe_job AFTER UPDATE ON background_jobs
                FOR EACH ROW EXECUTE FUNCTION sdk_observe_transaction();
            """, (NpgsqlConnection)connection) { CommandTimeout = 5 };
        await command.ExecuteNonQueryAsync();
    }

    private static async Task AssertObservedTransactionsAsync(DbConnection connection, DbTransaction transaction, bool failure)
    {
        await using var command = new NpgsqlCommand("SELECT label,root_id::text FROM sdk_transaction_observations",
            (NpgsqlConnection)connection, (NpgsqlTransaction)transaction)
        { CommandTimeout = 5 };
        await using var row = await command.ExecuteReaderAsync();
        var observed = new List<(string Label, string Transaction)>();
        while (await row.ReadAsync()) observed.Add((row.GetString(0), row.GetString(1)));
        var labels = new List<string> { "prepare-domain", "prepare-business-must", "finalize-domain", "finalize-business-must", "finalize-job" };
        if (failure) labels.Add("finalize-terminal-must");
        observed.Select(item => item.Label).Should().BeEquivalentTo(labels);
        observed.Where(item => item.Label.StartsWith("prepare-", StringComparison.Ordinal))
            .Select(item => item.Transaction).Distinct().Should().ContainSingle();
        observed.Where(item => item.Label.StartsWith("finalize-", StringComparison.Ordinal))
            .Select(item => item.Transaction).Distinct().Should().ContainSingle(
                "domain state, business MUST, terminal state and terminal MUST share the actual physical transaction");
        observed.Select(item => item.Transaction).Distinct().Should().HaveCount(2, "Prepare and Finalize are separate real transactions");
    }
}
