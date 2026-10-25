using FluentAssertions;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Audit.Infrastructure.Persistence;
using LearnStack.Modules.Customization.Infrastructure.Persistence;
using LearnStack.Modules.Education.Infrastructure.Persistence;
using LearnStack.Modules.Tenancy.Infrastructure.Persistence;
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
        Check(subject.Document, subject.Platform, subject.Audit, subject.Modules).Should().BeEmpty(
            "Architecture 23 inventories actual compiled Up operations, including all vendor installer tables; "
            + "Fix: reconcile the inventory and its explicit exclusions against the storage definition");
    }

    [Fact]
    public void Inventory_coverage_rejects_added_removed_and_reclassified_storage()
    {
        var subject = Subject();
        Check(subject.Document, subject.Platform, subject.Audit, subject.Modules).Should().BeEmpty("the unchanged control must pass");

        // Both operation carriers matter: raw SQL is current Platform storage, typed table
        // operations are used by Audit and must not silently bypass the census in a new migration.
        MigrationOperation[] additions =
        [
            new SqlOperation { Sql = "CREATE TABLE public.inventory_probe (id uuid);" },
            new CreateTableOperation { Name = "inventory_probe" },
        ];
        foreach (var addition in additions)
        {
            Check(subject.Document, [.. subject.Platform, addition], subject.Audit, subject.Modules)
                .Should().Contain(failure => failure.Contains("unexpected [inventory_probe]", StringComparison.Ordinal));
            Check(subject.Document, subject.Platform, [.. subject.Audit, addition], subject.Modules)
                .Should().Contain(failure => failure.Contains("unexpected [inventory_probe]", StringComparison.Ordinal));
            foreach (var module in subject.Modules.Keys)
            {
                var dirtyModules = new Dictionary<string, MigrationOperation[]>(subject.Modules)
                {
                    [module] = [.. subject.Modules[module], addition],
                };
                Check(subject.Document, subject.Platform, subject.Audit, dirtyModules)
                    .Should().Contain(failure => failure.Contains("unexpected [inventory_probe]", StringComparison.Ordinal),
                        $"the {module} chain must independently expose newly added storage");
            }
        }

        Check(subject.Document, [.. subject.Platform, new DropTableOperation { Name = "background_jobs" }], subject.Audit, subject.Modules)
            .Should().Contain(failure => failure.Contains("missing [background_jobs]", StringComparison.Ordinal));
        Check(subject.Document, [.. subject.Platform, new RenameTableOperation { Name = "background_jobs", NewName = "renamed_jobs" }], subject.Audit, subject.Modules)
            .Should().Contain(failure => failure.Contains("unexpected [renamed_jobs]; missing [background_jobs]", StringComparison.Ordinal));
        Check(subject.Document, subject.Platform, [.. subject.Audit, new DropTableOperation { Name = "audit_log" }], subject.Modules)
            .Should().Contain(failure => failure.Contains("missing [audit_log]", StringComparison.Ordinal));
        AsyncStoreInventoryDocument.Check(subject.Document, subject.Platform, subject.Audit,
            HangfireStorageContract.Columns.Keys.Append("new_vendor_table"), subject.Modules).Should().Contain(failure => failure.Contains(
                "vendor contract classifications: unexpected []; missing [new_vendor_table]", StringComparison.Ordinal));
    }

    [Fact]
    public void Inventory_coverage_rejects_every_removed_row_and_blank_vendor_classification()
    {
        var subject = Subject();
        Check(subject.Document, subject.Platform, subject.Audit, subject.Modules).Should().BeEmpty("the unchanged control must pass");
        foreach (var section in AsyncStoreInventoryDocument.Sections)
        {
            foreach (var row in DataLines(subject.Document, section))
            {
                var removed = subject.Document.Replace(row + "\n", "", StringComparison.Ordinal);
                removed.Should().NotBe(subject.Document, "the planted control must actually remove its row");
                var reason = section == "custody" ? "audit/provider/external/volatile custody" : "complete migration census";
                Check(removed, subject.Platform, subject.Audit, subject.Modules)
                    .Should().Contain(failure => failure.Contains(reason, StringComparison.Ordinal));

                var duplicated = subject.Document.Replace(row, row + "\n" + row, StringComparison.Ordinal);
                Check(duplicated, subject.Platform, subject.Audit, subject.Modules)
                    .Should().Contain($"Duplicate {section} inventory row.");
                if (section != "vendor") continue;
                var withoutClassification = row[..(row.IndexOf('|', 1) + 1)] + " |";
                var dirty = subject.Document.Replace(row, withoutClassification, StringComparison.Ordinal);
                Check(dirty, subject.Platform, subject.Audit, subject.Modules)
                    .Should().Contain("Missing vendor classification or malformed row.");
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
            (subject.Document.Replace("<!-- async-store-vendor:start -->", "", StringComparison.Ordinal),
                "Missing, repeated or reversed vendor census markers."),
            (subject.Document.Replace("<!-- async-store-vendor:end -->", "<!-- async-store-vendor:end -->\n<!-- async-store-vendor:end -->", StringComparison.Ordinal),
                "Missing, repeated or reversed vendor census markers."),
            (subject.Document.Replace("| Vendor table |", "| Unrecognised header |", StringComparison.Ordinal),
                "Malformed vendor table header."),
            (subject.Document.Replace(row, row.TrimEnd('|'), StringComparison.Ordinal), "Malformed first-party table row."),
            // Keeping a removed row in nearby prose must not satisfy the canonical table.
            (subject.Document.Replace(row + "\n", "", StringComparison.Ordinal) + "\n" + row,
                "Expected 10 first-party rows; found 9."),
            (subject.Document.Replace("| `idempotency_keys` |", "| `unreviewed_exclusion` |", StringComparison.Ordinal),
                "explicit scope exclusion: unexpected [unreviewed_exclusion]; missing [idempotency_keys]."),
        };
        foreach (var (document, reason) in dirtyDocuments)
            Check(document, subject.Platform, subject.Audit, subject.Modules).Should().Contain(reason);
    }

    [Fact]
    public void Migration_census_tracks_ordered_literal_and_typed_table_lifecycle()
    {
        MigrationOperation[] operations =
        [
            new SqlOperation { Sql = "-- CREATE TABLE commented_out (id int);\nCREATE TABLE IF NOT EXISTS public.\"first\" (id int);" },
            new SqlOperation { Sql = "CREATE TABLE IF NOT EXISTS public.\"first\" (id int); ALTER TABLE public.\"first\" RENAME TO second;" },
            new CreateTableOperation { Name = "third" },
            new RenameTableOperation { Name = "third", NewName = "fourth" },
            new SqlOperation { Sql = "DROP TABLE IF EXISTS public.second; DO $body$ BEGIN CREATE TABLE final_table (id int); END $body$;" },
            new DropTableOperation { Name = "fourth" },
            new SqlOperation { Sql = "DO $$ DECLARE selected_id int; BEGIN SELECT id INTO selected_id FROM final_table; "
                + "EXECUTE 'CREATE SCHEMA \"public\";'; "
                + "EXECUTE ('ALTER SEQUENCE \"' || 'public' || '\".job_id_seq AS bigint MAXVALUE 9223372036854775807'); END $$;" },
            new SqlOperation { Sql = "SET LOCAL \"search_path\" TO 'public'; SET SESSION search_path = public; SET SCHEMA 'public'; "
                + "ALTER TABLE public.final_table RENAME TO renamed_table; ALTER TABLE public.renamed_table RENAME TO final_table; "
                + "CREATE TRIGGER census_trigger BEFORE INSERT ON final_table FOR EACH ROW EXECUTE FUNCTION public.census_guard(); "
                + "GRANT EXECUTE ON FUNCTION public.census_guard() TO PUBLIC; "
                + "INSERT INTO final_table(id) VALUES(1) ON CONFLICT DO NOTHING; "
                + "SELECT set_config('app.tenant_id', 'probe', true);" },
            new SqlOperation { Sql = "CREATE FUNCTION public.census_function(probe text DEFAULT $arg$)AS$arg$) RETURNS void LANGUAGE plpgsql SET search_path = pg_catalog, public AS $$ BEGIN NULL; END; $$; "
                + "CREATE OR REPLACE PROCEDURE public.census_procedure() AS $body$ BEGIN NULL; END; $body$ LANGUAGE plpgsql SET search_path TO 'public';" },
        ];
        AsyncStoreInventoryCensus.Tables(operations).Should().BeEquivalentTo(["final_table"]);
    }

    [Theory]
    [InlineData("SELECT E'\\''; SELECT 1 INTO hidden; SELECT 'tail';")]
    [InlineData("SELECT e'\\''; SELECT 1 INTO hidden; SELECT 'tail';")]
    [InlineData("SELECT E'doubled''quote\\''; SELECT 1 INTO hidden; SELECT 'tail';")]
    public void Migration_census_detects_select_into_after_escape_literals(string sql)
    {
        var read = () => AsyncStoreInventoryCensus.Tables([new SqlOperation { Sql = sql }]);
        read.Should().Throw<FormatException>().WithMessage(
            "Planted migration (SqlOperation): SELECT INTO table creation requires an explicit census reader decision.");
    }

    [Theory]
    [InlineData("SELECT E'\\'';")]
    [InlineData("SELECT e'\\'';")]
    [InlineData("SELECT E'backslash\\\\';")]
    [InlineData("SELECT E'doubled''quote';")]
    [InlineData("SELECT E'escaped\\q';")]
    [InlineData("SELECT E'; SELECT 1 INTO hidden;';")]
    [InlineData("SELECT E'\\'' || 'ordinary';")]
    [InlineData("SELECT E'\\''; SELECT 'ordinary';")]
    [InlineData("SELECT '\\';")]
    [InlineData("SELECT 'doubled''quote';")]
    public void Migration_census_preserves_literal_boundaries_before_table_declarations(string prefix)
    {
        AsyncStoreInventoryCensus.Tables([new SqlOperation
        {
            Sql = prefix + " CREATE TABLE literal_probe(id int); SELECT 'tail';",
        }]).Should().Equal("literal_probe");
    }

    [Theory]
    [InlineData("SELECT E'prefix'\n'\\''; SELECT 1 INTO hidden; SELECT 'tail';",
        "Escape literal continuation requires an explicit census reader decision.")]
    [InlineData("SELECT e'prefix'\n/* continuation */ '\\''; SELECT 1 INTO hidden; SELECT 'tail';",
        "Escape literal continuation requires an explicit census reader decision.")]
    [InlineData("SELECT E'prefix' -- continuation\n'\\''; SELECT 1 INTO hidden; SELECT 'tail';",
        "Escape literal continuation requires an explicit census reader decision.")]
    [InlineData("SELECT set_config(E'app.tenant_id', 'probe', true);",
        "set_config search-path resolution requires an explicit census reader decision.")]
    [InlineData("SELECT set_config(e'search_\\x70ath', 'other', true);",
        "set_config search-path resolution requires an explicit census reader decision.")]
    [InlineData("DO $$ BEGIN EXECUTE E'CREATE SCHEMA \"public\";'; END $$;",
        "Unresolved dynamic EXECUTE requires an explicit census reader decision.")]
    [InlineData("DO $$ BEGIN EXECUTE e'CREATE SCHEMA \"public\";'; END $$;",
        "Unresolved dynamic EXECUTE requires an explicit census reader decision.")]
    [InlineData("SET search_path = E'public';",
        "The table census does not assume a non-public search path.")]
    [InlineData("SET SCHEMA e'public';",
        "The table census does not assume a non-public search path.")]
    [InlineData("CREATE FUNCTION public.inventory_probe() RETURNS void SET search_path = E'public' AS $$ BEGIN NULL; END; $$ LANGUAGE plpgsql;",
        "Non-pinned routine search path requires an explicit census reader decision.")]
    public void Migration_census_does_not_resolve_escape_literal_values(string sql, string reason)
    {
        var read = () => AsyncStoreInventoryCensus.Tables([new SqlOperation { Sql = sql }]);
        read.Should().Throw<FormatException>().WithMessage($"Planted migration (SqlOperation): {reason}");
    }

    [Theory]
    [InlineData("CREATE TEMP TABLE hidden_table (id int);", "Unsupported table persistence declaration.")]
    [InlineData("CREATE UNLOGGED TABLE hidden_table (id int);", "Unsupported table persistence declaration.")]
    [InlineData("CREATE GLOBAL TEMP TABLE hidden_table (id int);", "Unsupported table persistence declaration.")]
    [InlineData("CREATE MATERIALIZED VIEW hidden_table AS SELECT 1 AS id;", "Materialized view storage")]
    [InlineData("DO $$ BEGIN EXECUTE 'CREATE TABLE hidden_table (id int)'; END $$;", "Dynamic EXECUTE is outside")]
    [InlineData("DO $$ BEGIN EXECUTE 'CREATE ' || 'TABLE hidden_table (id int)'; END $$;", "Dynamic EXECUTE is outside")]
    [InlineData("DO $$ BEGIN EXECUTE format('%s %s', 'CREATE', 'TABLE hidden_table (id int)'); END $$;", "Unresolved dynamic EXECUTE")]
    [InlineData("DO $$ BEGIN EXECUTE dynamic_sql; END $$;", "Unresolved dynamic EXECUTE")]
    [InlineData("DO $$ DECLARE function text := 'CRE' || 'ATE TABLE hidden_table(id int)'; BEGIN EXECUTE function; END $$;", "Unresolved dynamic EXECUTE")]
    [InlineData("DO $$ DECLARE procedure text := 'CRE' || 'ATE TABLE hidden_table(id int)'; BEGIN EXECUTE procedure; END $$;", "Unresolved dynamic EXECUTE")]
    [InlineData("SELECT * INTO public.hidden_table FROM public.background_jobs;", "SELECT INTO table creation")]
    [InlineData("DO $$BEGIN NULL; END$$; SELECT * INTO public.hidden_table FROM public.background_jobs;", "SELECT INTO table creation")]
    [InlineData("DO $body$BEGIN NULL; END$body$; SELECT 1 INTO public.hidden_table;", "SELECT INTO table creation")]
    [InlineData("DO 'BEGIN EXECUTE ''CRE'' || ''ATE TABLE hidden_table(id int)''; END';", "Non-dollar procedural bodies")]
    [InlineData("DO E'BEGIN EXECUTE ''CRE'' || ''ATE TABLE hidden_table(id int)''; END';", "Non-dollar procedural bodies")]
    [InlineData("DO LANGUAGE plpgsql 'BEGIN EXECUTE ''CRE'' || ''ATE TABLE hidden_table(id int)''; END';", "Non-dollar procedural bodies")]
    [InlineData("CREATE FUNCTION public.inventory_probe() RETURNS void LANGUAGE plpgsql AS 'BEGIN EXECUTE ''CRE'' || ''ATE TABLE public.hidden_table(id int)''; END;'; SELECT public.inventory_probe();", "Non-dollar procedural bodies")]
    [InlineData("CREATE OR REPLACE FUNCTION public.inventory_probe() RETURNS void AS E'BEGIN EXECUTE ''CRE'' || ''ATE TABLE public.hidden_table(id int)''; END;' LANGUAGE plpgsql; SELECT public.inventory_probe();", "Non-dollar procedural bodies")]
    [InlineData("CREATE PROCEDURE public.inventory_probe() LANGUAGE plpgsql AS 'BEGIN EXECUTE ''CRE'' || ''ATE TABLE public.hidden_table(id int)''; END;'; CALL public.inventory_probe();", "Non-dollar procedural bodies")]
    [InlineData("CREATE OR REPLACE PROCEDURE public.inventory_probe() AS E'BEGIN EXECUTE ''CRE'' || ''ATE TABLE public.hidden_table(id int)''; END;' LANGUAGE plpgsql; CALL public.inventory_probe();", "Non-dollar procedural bodies")]
    [InlineData("CREATE FUNCTION public.inventory_probe(probe text DEFAULT $$AS$$) RETURNS void LANGUAGE plpgsql AS 'BEGIN EXECUTE ''CRE'' || ''ATE TABLE public.hidden_table(id int)''; END;'; SELECT public.inventory_probe();", "Non-dollar procedural bodies")]
    [InlineData("CREATE FUNCTION public.inventory_probe(probe text DEFAULT $arg$)AS$arg$) RETURNS void LANGUAGE plpgsql AS 'BEGIN EXECUTE ''CRE'' || ''ATE TABLE public.hidden_table(id int)''; END;'; SELECT public.inventory_probe();", "Non-dollar procedural bodies")]
    [InlineData("CREATE FUNCTION public.inventory_probe() RETURNS void SET search_path = other AS $$ BEGIN CREATE TABLE IF NOT EXISTS background_jobs(id int); END; $$ LANGUAGE plpgsql; SELECT public.inventory_probe();", "Non-pinned routine search path")]
    [InlineData("CREATE FUNCTION public.inventory_probe() RETURNS void SET search_path = pg_catalog, other AS $$ BEGIN NULL; END; $$ LANGUAGE plpgsql;", "Non-pinned routine search path")]
    [InlineData("CREATE FUNCTION public.inventory_probe() RETURNS void SET search_path FROM CURRENT AS $$ BEGIN NULL; END; $$ LANGUAGE plpgsql;", "Unresolved routine search path")]
    [InlineData("CREATE FUNCTION public.inventory_probe() RETURNS void SET search_path = \"PUBLIC\" AS $$ BEGIN NULL; END; $$ LANGUAGE plpgsql;", "Non-pinned routine search path")]
    [InlineData("CREATE FUNCTION public.inventory_probe() RETURNS void SET \"SEARCH_PATH\" = other AS $$ BEGIN NULL; END; $$ LANGUAGE plpgsql;", "Non-pinned routine search path")]
    [InlineData("CREATE FUNCTION public.inventory_probe() RETURNS void AS $$ BEGIN CREATE TABLE IF NOT EXISTS background_jobs(id int); END; $$ LANGUAGE plpgsql SET search_path = other; SELECT public.inventory_probe();", "Non-pinned routine search path")]
    [InlineData("CREATE TABLE IF absent_table (id int);", "Malformed table declaration.")]
    [InlineData("CREATE TABLE (id int);", "Malformed table identifier.")]
    [InlineData("SET search_path = other; CREATE TABLE hidden_table (id int);", "non-public search path")]
    [InlineData("SET LOCAL search_path = other;", "non-public search path")]
    [InlineData("SET SESSION \"search_path\" TO other;", "non-public search path")]
    [InlineData("SET SESSION \"SEARCH_PATH\" TO other;", "non-public search path")]
    [InlineData("SET SCHEMA 'other';", "non-public search path")]
    [InlineData("DO $$ BEGIN SET LOCAL search_path = other; END $$;", "non-public search path")]
    [InlineData("DO $$ BEGIN IF true THEN SET LOCAL search_path = other; END IF; END $$;", "non-public search path")]
    [InlineData("DO $$ BEGIN IF false THEN NULL; ELSE SET SESSION search_path = other; END IF; END $$;", "non-public search path")]
    [InlineData("SELECT set_config('search_path', 'other', true); CREATE TABLE IF NOT EXISTS background_jobs(id int);", "set_config search-path resolution")]
    [InlineData("SELECT pg_catalog.\"set_config\"('search_path', 'other', true);", "set_config search-path resolution")]
    [InlineData("SELECT set_config('search_' || 'path', 'other', true);", "set_config search-path resolution")]
    [InlineData("SELECT set_config('SEARCH_PATH', 'other', true);", "set_config search-path resolution")]
    [InlineData("DROP SCHEMA public CASCADE;", "Schema mutation")]
    [InlineData("ALTER SCHEMA public RENAME TO previous;", "Schema mutation")]
    [InlineData("IMPORT FOREIGN SCHEMA remote FROM SERVER probe INTO public;", "Schema mutation")]
    [InlineData("CREATE TABLE previous (id int); ALTER TABLE previous SET SCHEMA other;", "Table schema moves")]
    [InlineData("CREATE TABLE other.before_name(id int); ALTER TABLE other.before_name RENAME TO after_name;", "Non-public table storage")]
    public void Migration_census_refuses_unsupported_ddl_for_the_expected_reason(string sql, string reason)
    {
        var operation = new SqlOperation { Sql = sql };
        operation["InventoryOrigin"] = "ProbeDbContext/20261025_unsupported";
        var read = () => AsyncStoreInventoryCensus.Tables([operation]);
        read.Should().Throw<FormatException>()
            .WithMessage($"ProbeDbContext/20261025_unsupported (SqlOperation): *{reason}*");
    }

    [Fact]
    public void Typed_migration_operations_cannot_hide_non_public_storage_or_schema_removal()
    {
        var nonPublic = () => AsyncStoreInventoryCensus.Tables(
            [new CreateTableOperation { Schema = "other", Name = "background_jobs" }]);
        nonPublic.Should().Throw<FormatException>().WithMessage("*Non-public table storage*");
        var dropSchema = () => AsyncStoreInventoryCensus.Tables([new DropSchemaOperation { Name = "public" }]);
        dropSchema.Should().Throw<FormatException>().WithMessage("*Schema mutation*");
    }

    private static string[] Check(string document, IReadOnlyList<MigrationOperation> platform,
        IReadOnlyList<MigrationOperation> audit, IReadOnlyDictionary<string, MigrationOperation[]> modules) =>
        AsyncStoreInventoryDocument.Check(document, platform, audit, HangfireStorageContract.Columns.Keys, modules);

    private static (string Document, MigrationOperation[] Platform, MigrationOperation[] Audit,
        Dictionary<string, MigrationOperation[]> Modules) Subject()
    {
        // Build migration services only. No design-time factory, environment credential or
        // database connection is consulted; UpOperations expands the pinned installer resources.
        using var platform = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>()
            .UseNpgsql("Host=unused").Options);
        using var audit = new AuditDbContext(new DbContextOptionsBuilder<AuditDbContext>()
            .UseNpgsql("Host=unused").Options, new StaticTenantContextAccessor(null));
        var modules = new Dictionary<string, MigrationOperation[]>(StringComparer.Ordinal);
        foreach (var module in Modules.Mapped.Where(module => module.Domain != typeof(LearnStack.Modules.Audit.Domain.AuditEntry).Assembly))
        {
            using var context = module.Context();
            modules.Add(module.Domain.GetName().Name!, AsyncStoreInventoryCensus.Operations(context));
        }
        modules.Values.Should().OnlyContain(operations => operations.Length > 0);
        var platformOperations = AsyncStoreInventoryCensus.Operations(platform);
        var auditOperations = AsyncStoreInventoryCensus.Operations(audit);
        platformOperations.Should().HaveCountGreaterThan(20, "the root must discover the real migration chain");
        auditOperations.Should().NotBeEmpty("audit discovery must independently reach its migration chain");
        return (File.ReadAllText(Path.Combine(RepositoryPaths.RepoRoot(), "docs", "architecture", "23-data-protection.md")),
            platformOperations, auditOperations, modules);
    }

    private static string[] DataLines(string document, string section)
    {
        var start = document.IndexOf($"<!-- async-store-{section}:start -->", StringComparison.Ordinal);
        var end = document.IndexOf($"<!-- async-store-{section}:end -->", start, StringComparison.Ordinal);
        return document[start..end].Split('\n').Where(line => line.StartsWith('|')).Skip(2).ToArray();
    }
}
