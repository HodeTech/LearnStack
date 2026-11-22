using FluentAssertions;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class EducationStructureTests
{
    // ADR-0107 § 4's ciphertext chunks are SQL-only, so the relation has no EF counterpart; the applied foreign key
    // and the canonical parent-scope function are still checked exactly.
    private static readonly ParentRelation DataExportChunkRelation =
        new("data_export_chunks", "data_export_artifacts", "artifact_id", "r");

    private static bool IsDeclaredDataExportChunkRelation(ForeignKey key) =>
        key.Child == DataExportChunkRelation.Child && key.Parent == DataExportChunkRelation.Parent;

    private static List<string> DataExportChunkGuardOffenders(List<ForeignKey> applied, List<string[]> triggers)
    {
        var offenders = new List<string>();
        var relation = DataExportChunkRelation;
        var keys = applied.Where(IsDeclaredDataExportChunkRelation).ToArray();
        if (keys.Length != 1)
            offenders.Add($"{relation.Child}: missing parent FK");
        else if (!ValidForeignKey(keys[0], relation) || keys[0].DeleteAction != relation.DeleteAction)
            offenders.Add($"{relation.Child}: tenant-composite FK");
        if (!triggers.Any(trigger => trigger[0] == relation.Child && trigger[1] == "public" && trigger[3] == "true"
                && trigger[5] == "23" && trigger[6] == "false" && trigger[7] == "false" && IsParentGuardBody(trigger[2], relation)))
            offenders.Add($"{relation.Child}: parent guard");
        return offenders;
    }

    [Theory]
    [InlineData("data_export_artifacts")]
    [InlineData("data_export_tickets")]
    [InlineData("data_export_chunks")]
    public async Task The_data_request_parent_guard_rejects_a_missing_weakened_or_inert_relation(string table)
    {
        await using var owner = await PostgresFixture.OpenAsync(_schema.Postgres.MigrationConnectionString);
        await using var transaction = await owner.BeginTransactionAsync();
        (await ParentGuardOffendersAsync(owner, transaction)).Should().BeEmpty();

        var key = (await ReadForeignKeysAsync(owner, transaction)).Single(item => item.Child == table);
        await SchemaQueries.ExecuteAsync(owner, transaction, $"SAVEPOINT relation; ALTER TABLE public.{table} DROP CONSTRAINT {key.Name}");
        (await ParentGuardOffendersAsync(owner, transaction)).Should().ContainSingle().Which.Should().Be($"{table}: missing parent FK");
        await SchemaQueries.ExecuteAsync(owner, transaction,
            $"ROLLBACK TO SAVEPOINT relation; ALTER TABLE public.{table} DROP CONSTRAINT {key.Name}; "
            + $"ALTER TABLE public.{table} ADD CONSTRAINT {key.Name} "
            + key.Definition.Replace("ON DELETE RESTRICT", "ON DELETE CASCADE", StringComparison.Ordinal));
        (await ParentGuardOffendersAsync(owner, transaction)).Should().ContainSingle().Which.Should().Be($"{table}: tenant-composite FK");
        await SchemaQueries.ExecuteAsync(owner, transaction, "ROLLBACK TO SAVEPOINT relation");
        (await ParentGuardOffendersAsync(owner, transaction)).Should().BeEmpty();

        await SchemaQueries.ExecuteAsync(owner, transaction, $"DROP TRIGGER tg_{table}_parent_scope ON public.{table}");
        (await ParentGuardOffendersAsync(owner, transaction)).Should().ContainSingle().Which.Should().Be($"{table}: parent guard");
        await SchemaQueries.ExecuteAsync(owner, transaction, "ROLLBACK TO SAVEPOINT relation");
        await SchemaQueries.ExecuteAsync(owner, transaction,
            $"CREATE OR REPLACE FUNCTION public.fn_{table}_parent_scope() RETURNS trigger LANGUAGE plpgsql SECURITY INVOKER "
            + "SET search_path = pg_catalog AS $probe$ BEGIN RETURN NEW; END; $probe$");
        (await ParentGuardOffendersAsync(owner, transaction)).Should().ContainSingle().Which.Should().Be($"{table}: parent guard",
            "an early-returning function is not the canonical parent check");
    }
}
