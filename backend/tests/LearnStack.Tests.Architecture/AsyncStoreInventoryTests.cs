using FluentAssertions;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Audit.Infrastructure.Persistence;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Xunit;

namespace LearnStack.Tests.Architecture;

/// <summary>
/// ADR-0046/0047/0057 structural inventory coverage. Locators, grants and mutability
/// require independent review; this proves neither erasure correctness nor legal compliance.
/// </summary>
public sealed class AsyncStoreInventoryTests
{
    [Fact]
    public void Durable_Async_Store_Inventory_Covers_The_Storage_Census()
    {
        var subject = Subject();
        Check(subject.Document, subject.Platform, subject.Audit).Should().BeEmpty(
            "Architecture 23 inventories actual compiled Up operations, including all vendor installer tables; "
            + "Fix: reconcile the inventory and its explicit exclusions against the storage definition");
    }

    [Fact]
    public void Inventory_coverage_rejects_added_removed_and_reclassified_storage()
    {
        var subject = Subject();
        Check(subject.Document, subject.Platform, subject.Audit).Should().BeEmpty("the unchanged control must pass");

        // Both operation carriers matter: raw SQL is current Platform storage, typed table
        // operations are used by Audit and must not silently bypass the census in a new migration.
        MigrationOperation[] additions =
        [
            new SqlOperation { Sql = "CREATE TABLE public.inventory_probe (id uuid);" },
            new CreateTableOperation { Name = "inventory_probe" },
            new CreateTableOperation { Schema = "other", Name = "background_jobs" },
        ];
        foreach (var addition in additions)
            Check(subject.Document, [.. subject.Platform, addition], subject.Audit).Should().NotBeEmpty(
                "an added table is discovered without a name-prefix or inventory whitelist filter");

        Check(subject.Document, [.. subject.Platform, new DropTableOperation { Name = "background_jobs" }], subject.Audit)
            .Should().NotBeEmpty("a removed actual table leaves a stale inventory row");
        Check(subject.Document, [.. subject.Platform, new RenameTableOperation { Name = "background_jobs", NewName = "renamed_jobs" }], subject.Audit)
            .Should().NotBeEmpty("a renamed actual table cannot hide behind the old documented name");
        Check(subject.Document, subject.Platform, [.. subject.Audit, new DropTableOperation { Name = "audit_log" }])
            .Should().NotBeEmpty("existing audit storage is checked independently of the Platform census");
        AsyncStoreInventoryDocument.Check(subject.Document, subject.Platform, subject.Audit,
            HangfireStorageContract.Columns.Keys.Append("new_vendor_table")).Should().NotBeEmpty(
                "a changed vendor contract is independently reconciled, rather than defining actual storage");
    }

    [Fact]
    public void Inventory_coverage_rejects_every_removed_row_and_blank_vendor_classification()
    {
        var subject = Subject();
        Check(subject.Document, subject.Platform, subject.Audit).Should().BeEmpty("the unchanged control must pass");
        foreach (var section in AsyncStoreInventoryDocument.Sections)
        {
            foreach (var row in DataLines(subject.Document, section))
            {
                var removed = subject.Document.Replace(row + "\n", "", StringComparison.Ordinal);
                removed.Should().NotBe(subject.Document, "the planted control must actually remove its row");
                Check(removed, subject.Platform, subject.Audit).Should().NotBeEmpty(
                    $"every {section} row is required, including schema/counters and non-DB custody boundaries");

                var duplicated = subject.Document.Replace(row, row + "\n" + row, StringComparison.Ordinal);
                Check(duplicated, subject.Platform, subject.Audit).Should().NotBeEmpty("duplicate rows must not collapse into a set");
                if (section != "vendor") continue;
                var withoutClassification = row[..(row.IndexOf('|', 1) + 1)] + " |";
                var dirty = subject.Document.Replace(row, withoutClassification, StringComparison.Ordinal);
                Check(dirty, subject.Platform, subject.Audit).Should().NotBeEmpty("a table name alone is not a classification");
            }
        }
    }

    [Fact]
    public void Inventory_coverage_requires_bounded_well_formed_tables()
    {
        var subject = Subject();
        var row = DataLines(subject.Document, "first-party")[0];
        var dirtyDocuments = new[]
        {
            subject.Document.Replace("<!-- async-store-vendor:start -->", "", StringComparison.Ordinal),
            subject.Document.Replace("<!-- async-store-vendor:end -->", "<!-- async-store-vendor:end -->\n<!-- async-store-vendor:end -->", StringComparison.Ordinal),
            subject.Document.Replace("| Vendor table |", "| Unrecognised header |", StringComparison.Ordinal),
            subject.Document.Replace(row, row.TrimEnd('|'), StringComparison.Ordinal),
            // Keeping a removed row in nearby prose must not satisfy the canonical table.
            subject.Document.Replace(row + "\n", "", StringComparison.Ordinal) + "\n" + row,
            subject.Document.Replace("| `idempotency_keys` |", "| `unreviewed_exclusion` |", StringComparison.Ordinal),
        };
        foreach (var document in dirtyDocuments)
            Check(document, subject.Platform, subject.Audit).Should().NotBeEmpty("malformed or unbounded coverage cannot pass");
    }

    [Fact]
    public void Migration_census_tracks_ordered_literal_and_typed_table_lifecycle_and_refuses_unsupported_ddl()
    {
        MigrationOperation[] operations =
        [
            new SqlOperation { Sql = "-- CREATE TABLE commented_out (id int);\nCREATE TABLE IF NOT EXISTS public.\"first\" (id int);" },
            new SqlOperation { Sql = "CREATE TABLE IF NOT EXISTS public.\"first\" (id int); ALTER TABLE public.\"first\" RENAME TO second;" },
            new CreateTableOperation { Name = "third" },
            new RenameTableOperation { Name = "third", NewName = "fourth" },
            new SqlOperation { Sql = "DROP TABLE IF EXISTS public.second; DO $body$ BEGIN CREATE TABLE final_table (id int); END $body$;" },
            new DropTableOperation { Name = "fourth" },
        ];
        AsyncStoreInventoryCensus.Tables(operations).Should().BeEquivalentTo(["final_table"]);
        var unsupported = new[]
        {
            "CREATE TEMP TABLE hidden_table (id int);", "CREATE UNLOGGED TABLE hidden_table (id int);",
            "CREATE GLOBAL TEMP TABLE hidden_table (id int);",
            "DO $$ BEGIN EXECUTE 'CREATE TABLE hidden_table (id int)'; END $$;",
            "CREATE TABLE IF absent_table (id int);", "CREATE TABLE (id int);",
            "SET search_path = other; CREATE TABLE hidden_table (id int);",
            "CREATE TABLE previous (id int); ALTER TABLE previous SET SCHEMA other;",
        };
        foreach (var sql in unsupported)
        {
            var read = () => AsyncStoreInventoryCensus.Tables([new SqlOperation { Sql = sql }]);
            read.Should().Throw<FormatException>("unsupported or ambiguous table declarations must fail closed");
        }
    }

    private static string[] Check(string document, IReadOnlyList<MigrationOperation> platform, IReadOnlyList<MigrationOperation> audit) =>
        AsyncStoreInventoryDocument.Check(document, platform, audit, HangfireStorageContract.Columns.Keys);

    private static (string Document, MigrationOperation[] Platform, MigrationOperation[] Audit) Subject()
    {
        // Build migration services only. No design-time factory, environment credential or
        // database connection is consulted; UpOperations expands the pinned installer resources.
        using var platform = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>()
            .UseNpgsql("Host=unused").Options);
        using var audit = new AuditDbContext(new DbContextOptionsBuilder<AuditDbContext>()
            .UseNpgsql("Host=unused").Options, new StaticTenantContextAccessor(null));
        var platformOperations = AsyncStoreInventoryCensus.Operations(platform);
        var auditOperations = AsyncStoreInventoryCensus.Operations(audit);
        platformOperations.Should().HaveCountGreaterThan(20, "the root must discover the real migration chain");
        auditOperations.Should().NotBeEmpty("audit discovery must independently reach its migration chain");
        return (File.ReadAllText(Path.Combine(RepositoryPaths.RepoRoot(), "docs", "architecture", "23-data-protection.md")),
            platformOperations, auditOperations);
    }

    private static string[] DataLines(string document, string section)
    {
        var start = document.IndexOf($"<!-- async-store-{section}:start -->", StringComparison.Ordinal);
        var end = document.IndexOf($"<!-- async-store-{section}:end -->", start, StringComparison.Ordinal);
        return document[start..end].Split('\n').Where(line => line.StartsWith('|')).Skip(2).ToArray();
    }
}
