using System.Data.Common;
using System.Reflection;
using System.Text.RegularExpressions;
using LearnStack.Modules.Education.Domain;
using LearnStack.Modules.Education.Infrastructure.Persistence;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Npgsql;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class EducationStructureTests
{
    // Explicit classifications are joined to independently discovered EF relations
    // AND every applied org-to-org FK. Dropping a declaration, EF relationship or
    // database FK is therefore a failure, as is introducing an unclassified relation.
    private sealed record ParentRelation(string Child, string Parent, string ParentId, string DeleteAction);

    private static readonly ParentRelation[] ParentRelations =
    [
        new("lessons", "courses", "course_id", "r"),
        new("course_translations", "courses", "course_id", "c"),
        new("lesson_translations", "lessons", "lesson_id", "c"),
    ];

    private sealed record PatternRoot(Type Root, Type Satellite, string Table, string SatelliteTable, string ParentId, string[] Columns);

    private static readonly PatternRoot[] PatternRoots =
    [
        new(typeof(Course), typeof(CourseTranslation), "courses", "course_translations", "course_id",
            ["course_id", "locale", "tenant_id", "organization_id", "title", "summary", "slug"]),
        new(typeof(Lesson), typeof(LessonTranslation), "lessons", "lesson_translations", "lesson_id",
            ["lesson_id", "locale", "tenant_id", "organization_id", "title", "slug", "body"]),
    ];

    private static EducationDbContext ModelOnly() => new(
        new DbContextOptionsBuilder<EducationDbContext>()
            .UseNpgsql("Host=model-only;Database=model-only;Username=model-only").Options,
        StaticTenantContextAccessor.Unresolved);

    private static async Task<List<string>> ParentGuardOffendersAsync(
        DbConnection connection, DbTransaction? transaction = null, ParentRelation[]? declarations = null)
    {
        declarations ??= ParentRelations;
        var offenders = new List<string>();
        using var context = ModelOnly();
        var modelRelations = context.Model.GetEntityTypes()
            .Where(entity => typeof(IOrganizationScoped).IsAssignableFrom(entity.ClrType))
            .SelectMany(entity => entity.GetForeignKeys())
            .Where(key => typeof(IOrganizationScoped).IsAssignableFrom(key.PrincipalEntityType.ClrType))
            .ToList();

        if (declarations.Length == 0 || modelRelations.Count == 0)
        {
            offenders.Add("parent relation inventory is empty");
        }

        foreach (var key in modelRelations)
        {
            var child = key.DeclaringEntityType.GetTableName()!;
            var declaration = declarations.SingleOrDefault(item => item.Child == child
                && item.Parent == key.PrincipalEntityType.GetTableName());
            if (declaration is null)
            {
                offenders.Add($"{child}: undeclared model relation");
            }
            else if (!Columns(key.Properties).SequenceEqual(["tenant_id", declaration.ParentId])
                || !Columns(key.PrincipalKey.Properties).SequenceEqual(["tenant_id", "id"]))
            {
                offenders.Add($"{child}: model tenant-composite FK");
            }
        }

        var applied = await ReadForeignKeysAsync(connection, transaction);
        foreach (var key in applied.Where(key => !declarations.Any(item => item.Child == key.Child && item.Parent == key.Parent)))
        {
            offenders.Add($"{key.Child}: undeclared applied relation");
        }

        var triggers = await RowsAsync(connection, transaction,
            $"""
            SELECT c.relname, n.nspname, f.prosrc,
                   (NOT f.prosecdef AND f.proconfig = ARRAY['search_path=pg_catalog']
                    AND f.pronargs = 0 AND l.lanname = 'plpgsql'
                    AND t.tgenabled IN ('O', 'A') AND t.tgtype = 23
                    AND t.tgqual IS NULL AND t.tgnargs = 0
                    AND cardinality(t.tgattr::smallint[]) = 0)::text
            FROM pg_trigger t JOIN pg_class c ON c.oid = t.tgrelid
            JOIN pg_proc f ON f.oid = t.tgfoid
            JOIN pg_namespace n ON n.oid = f.pronamespace
            JOIN pg_language l ON l.oid = f.prolang
            WHERE c.oid IN ({SchemaQueries.TableOids}) AND NOT t.tgisinternal
            """);

        foreach (var relation in declarations)
        {
            if (!modelRelations.Any(key => key.DeclaringEntityType.GetTableName() == relation.Child
                && key.PrincipalEntityType.GetTableName() == relation.Parent))
            {
                offenders.Add($"{relation.Child}: missing model relation");
            }

            var keys = applied.Where(key => key.Child == relation.Child && key.Parent == relation.Parent).ToList();
            if (keys.Count != 1)
            {
                offenders.Add($"{relation.Child}: missing parent FK");
            }
            else if (!ValidForeignKey(keys[0], relation))
            {
                offenders.Add($"{relation.Child}: tenant-composite FK");
            }

            if (!triggers.Any(trigger => trigger[0] == relation.Child && trigger[1] == "public"
                && trigger[3] == "true" && IsParentGuardBody(trigger[2], relation)))
            {
                offenders.Add($"{relation.Child}: parent guard");
            }
        }

        return offenders.Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// A deliberately bounded grammar for Standards 05's canonical function, not
    /// a general PL/pgSQL verifier. Whole-body matching rejects early returns,
    /// comments containing required tokens, extra OR predicates and alternate
    /// error branches. Case/whitespace and the static error message may vary.
    /// Independent app-login tests prove execution and locking, which text cannot.
    /// </summary>
    private static bool IsParentGuardBody(string body, ParentRelation relation)
    {
        var normalized = Regex.Replace(body.Trim(), @"\s+", " ");
        var pattern = @"\ADECLARE parent_organization_id uuid; BEGIN "
            + @"SELECT parent\.organization_id INTO parent_organization_id "
            + $@"FROM public\.{Regex.Escape(relation.Parent)} AS parent "
            + @"WHERE parent\.tenant_id = NEW\.tenant_id AND "
            + $@"parent\.id = NEW\.{Regex.Escape(relation.ParentId)} FOR KEY SHARE; "
            + @"IF NOT FOUND OR parent_organization_id IS DISTINCT FROM NEW\.organization_id THEN "
            + @"RAISE EXCEPTION '[^']*', TG_TABLE_NAME USING ERRCODE = '23514'; "
            + @"END IF; RETURN NEW; END;\z";
        return Regex.IsMatch(normalized, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static async Task<List<string>> PatternOffendersAsync(
        DbConnection connection, DbTransaction? transaction = null)
    {
        var offenders = new List<string>();
        using var context = ModelOnly();
        var model = context.Model;
        var declared = PatternRoots.SelectMany(root => new[] { root.Root, root.Satellite }).ToHashSet();
        var actual = model.GetEntityTypes().Select(entity => entity.ClrType).ToHashSet();
        var domainScoped = typeof(Course).Assembly.GetTypes()
            .Where(type => !type.IsAbstract && typeof(IOrganizationScoped).IsAssignableFrom(type)).ToHashSet();
        if (PatternRoots.Length == 0 || !declared.SetEquals(actual) || !declared.SetEquals(domainScoped))
        {
            offenders.Add("Pattern A declaration/model/domain inventory differs");
        }

        var columnRows = await RowsAsync(connection, transaction,
            $"""
            SELECT c.relname, a.attname, format_type(a.atttypid, a.atttypmod), a.attnotnull::text
            FROM pg_class c JOIN pg_attribute a ON a.attrelid = c.oid
            WHERE c.oid IN ({SchemaQueries.TableOids}) AND a.attnum > 0 AND NOT a.attisdropped
            """);
        var indexes = await RowsAsync(connection, transaction,
            $"""
            SELECT c.relname, i.indisprimary::text,
                array_to_string(ARRAY(SELECT a.attname FROM unnest(i.indkey::smallint[])
                    WITH ORDINALITY k(attnum, position)
                    JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum = k.attnum
                    WHERE k.position <= i.indnkeyatts ORDER BY k.position), ','),
                (i.indisunique AND i.indisvalid AND i.indisready
                    AND i.indpred IS NULL AND i.indexprs IS NULL)::text
            FROM pg_index i JOIN pg_class c ON c.oid = i.indrelid
            WHERE c.oid IN ({SchemaQueries.TableOids})
            """);
        var foreignKeys = await ReadForeignKeysAsync(connection, transaction);

        foreach (var root in PatternRoots)
        {
            foreach (var table in new[] { root.Table, root.SatelliteTable })
            {
                if (!columnRows.Any(row => row[0] == table))
                {
                    offenders.Add($"{table}: missing table");
                }
            }

            if (columnRows.Any(row => row[0] == root.Table
                && Regex.IsMatch(row[1], @"^(title|summary|body|locale|slug)(_|$)|_(title|summary|body)$")
                && row[1] != "slug_key"))
            {
                offenders.Add($"{root.Table}: misplaced columns");
            }

            var satelliteColumns = columnRows.Where(row => row[0] == root.SatelliteTable).ToList();
            if (!satelliteColumns.Select(row => row[1]).ToHashSet().SetEquals(root.Columns))
            {
                offenders.Add($"{root.SatelliteTable}: misplaced columns");
            }

            if (!satelliteColumns.Any(row => row[1] == "locale" && row[2] == "character varying(35)" && row[3] == "true")
                || !satelliteColumns.Any(row => row[1] == "tenant_id" && row[2] == "uuid" && row[3] == "true")
                || !satelliteColumns.Any(row => row[1] == "organization_id" && row[2] == "uuid" && row[3] == "false"))
            {
                offenders.Add($"{root.SatelliteTable}: locale/scope columns");
            }

            if (!indexes.Any(index => index[0] == root.SatelliteTable && index[1] == "true"
                && index[2] == $"{root.ParentId},locale" && index[3] == "true"))
            {
                offenders.Add($"{root.SatelliteTable}: natural primary key");
            }

            if (!indexes.Any(index => index[0] == root.SatelliteTable
                && index[2] == "tenant_id,locale,slug" && index[3] == "true"))
            {
                offenders.Add($"{root.SatelliteTable}: slug namespace");
            }

            var entity = model.FindEntityType(root.Satellite);
            if (entity is null)
            {
                offenders.Add($"{root.SatelliteTable}: missing model satellite");
                continue;
            }

            if (root.Satellite.BaseType != typeof(object)
                || !Columns(entity.GetProperties()).ToHashSet().SetEquals(root.Columns)
                || entity.GetProperties().Any(property => property.IsConcurrencyToken)
                || !Columns(entity.FindPrimaryKey()?.Properties ?? []).SequenceEqual([root.ParentId, "locale"]))
            {
                offenders.Add($"{root.SatelliteTable}: plain natural-key model");
            }

            if (root.Satellite.GetCustomAttribute<TenantOwnedAttribute>() is null
                || root.Satellite.GetCustomAttribute<OrganizationScopedAttribute>() is null
                || !typeof(IOrganizationScoped).IsAssignableFrom(root.Satellite)
                || !entity.GetDeclaredQueryFilters().Any(filter => filter.Expression is not null))
            {
                offenders.Add($"{root.SatelliteTable}: model isolation");
            }

            var parentEntity = model.FindEntityType(root.Root);
            if (root.Root.GetProperties().Any(property => property.Name != "SlugKey"
                    && Regex.IsMatch(property.Name, @"^(Title|Summary|Body|Locale|Slug)($|[A-Z_])"))
                || (parentEntity is not null && Columns(parentEntity.GetProperties()).Any(column =>
                    column != "slug_key" && Regex.IsMatch(column, @"^(title|summary|body|locale|slug)(_|$)|_(title|summary|body)$"))))
            {
                offenders.Add($"{root.Table}: misplaced model fields");
            }

            if (parentEntity is null || !parentEntity.GetNavigations().Any(navigation =>
                navigation.IsCollection && navigation.TargetEntityType == entity))
            {
                offenders.Add($"{root.SatelliteTable}: contained navigation");
            }

            if (!entity.GetIndexes().Any(index => index.IsUnique && index.GetFilter() is null
                    && Columns(index.Properties).SequenceEqual(["tenant_id", "locale", "slug"]))
                && !entity.GetKeys().Any(key =>
                    Columns(key.Properties).SequenceEqual(["tenant_id", "locale", "slug"])))
            {
                offenders.Add($"{root.SatelliteTable}: model slug namespace");
            }
        }

        foreach (var relation in ParentRelations)
        {
            if (!foreignKeys.Any(key => key.Child == relation.Child && key.Parent == relation.Parent
                && ValidForeignKey(key, relation) && key.DeleteAction == relation.DeleteAction))
            {
                offenders.Add($"{relation.Child}: parent FK/delete action");
            }

            var entity = model.GetEntityTypes().SingleOrDefault(item => item.GetTableName() == relation.Child);
            if (entity is null || !entity.GetForeignKeys().Any(key =>
                key.PrincipalEntityType.GetTableName() == relation.Parent
                && Columns(key.Properties).SequenceEqual(["tenant_id", relation.ParentId])
                && Columns(key.PrincipalKey.Properties).SequenceEqual(["tenant_id", "id"])
                && key.DeleteBehavior == (relation.DeleteAction == "c" ? DeleteBehavior.Cascade : DeleteBehavior.Restrict)))
            {
                offenders.Add($"{relation.Child}: model FK/delete action");
            }
        }

        return offenders.Order(StringComparer.Ordinal).ToList();
    }

    private static IEnumerable<string> Columns(IEnumerable<IReadOnlyProperty> properties) =>
        properties.Select(property => property.GetColumnName());

    private sealed record ForeignKey(string Child, string Parent, string Name, string ChildColumns,
        string ParentColumns, string DeleteAction, bool Validated, string Definition);

    private static bool ValidForeignKey(ForeignKey key, ParentRelation relation) =>
        key.ChildColumns == $"tenant_id,{relation.ParentId}"
        && key.ParentColumns == "tenant_id,id" && key.Validated;

    private static async Task<List<ForeignKey>> ReadForeignKeysAsync(
        DbConnection connection, DbTransaction? transaction) =>
        (await RowsAsync(connection, transaction,
            $"""
            WITH scoped AS (
                SELECT c.oid, c.relname FROM pg_class c
                WHERE c.oid IN ({SchemaQueries.TableOids})
                    AND EXISTS (SELECT 1 FROM pg_attribute a WHERE a.attrelid = c.oid
                        AND a.attname = 'organization_id' AND NOT a.attisdropped)
                    AND EXISTS (SELECT 1 FROM pg_attribute a WHERE a.attrelid = c.oid
                        AND a.attname = 'tenant_id' AND NOT a.attisdropped)
            )
            SELECT child.relname, parent.relname, f.conname,
                array_to_string(ARRAY(SELECT a.attname FROM unnest(f.conkey) WITH ORDINALITY k(attnum, position)
                    JOIN pg_attribute a ON a.attrelid = f.conrelid AND a.attnum = k.attnum ORDER BY k.position), ','),
                array_to_string(ARRAY(SELECT a.attname FROM unnest(f.confkey) WITH ORDINALITY k(attnum, position)
                    JOIN pg_attribute a ON a.attrelid = f.confrelid AND a.attnum = k.attnum ORDER BY k.position), ','),
                f.confdeltype::text, (f.convalidated AND NOT f.condeferrable)::text, pg_get_constraintdef(f.oid)
            FROM pg_constraint f JOIN scoped child ON child.oid = f.conrelid
            JOIN scoped parent ON parent.oid = f.confrelid WHERE f.contype = 'f'
            """))
        .Select(row => new ForeignKey(row[0], row[1], row[2], row[3], row[4], row[5], row[6] == "true", row[7]))
        .ToList();

    private static async Task<List<string[]>> RowsAsync(
        DbConnection connection, DbTransaction? transaction, string sql)
    {
        await using var command = new NpgsqlCommand(sql, (NpgsqlConnection)connection, (NpgsqlTransaction?)transaction);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<string[]>();
        while (await reader.ReadAsync())
        {
            rows.Add(Enumerable.Range(0, reader.FieldCount)
                .Select(index => reader.IsDBNull(index) ? "" : reader.GetString(index)).ToArray());
        }

        return rows;
    }
}
