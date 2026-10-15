using FluentAssertions;

namespace LearnStack.Tests.Integration.Database;

internal static class BackgroundJobSchemaInventory
{
    public static readonly string[] Tables = [.. SchemaTableClasses.JobTenant, .. SchemaTableClasses.Control];
    private static readonly string Names = string.Join(",", Tables.Select(name => $"'{name}'"));

    public static async Task<List<string>> ReadAsync(string connectionString)
    {
        await using var owner = await PostgresFixture.OpenAsync(connectionString);
        return await SchemaQueries.ReadStringsAsync(owner, $"""
            SELECT item FROM (
                SELECT 'column:' || table_name || ':' || ordinal_position || ':' || column_name || ':' ||
                       data_type || ':' || udt_name || ':' || is_nullable || ':' || coalesce(column_default, '') || ':' ||
                       coalesce(character_maximum_length::text, '') AS item
                FROM information_schema.columns
                WHERE table_schema = 'public' AND table_name IN ({Names})
                UNION ALL
                SELECT 'constraint:' || c.relname || ':' || conname || ':' || pg_get_constraintdef(k.oid)
                FROM pg_constraint k JOIN pg_class c ON c.oid = k.conrelid
                WHERE c.relnamespace = 'public'::regnamespace AND c.relname IN ({Names})
                UNION ALL
                SELECT 'index:' || tablename || ':' || indexname || ':' || indexdef FROM pg_indexes
                WHERE schemaname = 'public' AND tablename IN ({Names})
                UNION ALL
                SELECT 'policy:' || tablename || ':' || policyname || ':' || permissive || ':' || roles::text || ':' || cmd || ':' ||
                       coalesce(qual, '') || ':' || coalesce(with_check, '') FROM pg_policies
                WHERE schemaname = 'public' AND tablename IN ({Names})
                UNION ALL
                SELECT 'rls:' || relname || ':' || relrowsecurity || ':' || relforcerowsecurity || ':' || relowner::regrole::text
                FROM pg_class WHERE relnamespace = 'public'::regnamespace AND relname IN ({Names})
                UNION ALL
                SELECT 'column-grant:' || table_name || ':' || grantee || ':' || column_name || ':' || privilege_type || ':' || is_grantable
                FROM information_schema.column_privileges WHERE table_schema = 'public' AND table_name IN ({Names})
                UNION ALL
                SELECT 'table-grant:' || table_name || ':' || grantee || ':' || privilege_type || ':' || is_grantable
                FROM information_schema.table_privileges WHERE table_schema = 'public' AND table_name IN ({Names})
                UNION ALL
                SELECT 'trigger:' || c.relname || ':' || t.tgenabled::text || ':' || pg_get_triggerdef(t.oid)
                FROM pg_trigger t JOIN pg_class c ON c.oid = t.tgrelid
                WHERE c.relnamespace = 'public'::regnamespace AND c.relname IN ({Names}) AND NOT t.tgisinternal
                UNION ALL
                SELECT 'function:' || pg_get_functiondef(oid) || ':' || coalesce(proacl::text, '') FROM pg_proc
                WHERE pronamespace = 'public'::regnamespace AND proname LIKE 'fn_background_job%'
            ) inventory ORDER BY item
            """);
    }

    public static async Task<string> CanonicalSqlAsync()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(RepositoryPath.RolesScript())!, "../../.."));
        var document = await File.ReadAllTextAsync(Path.Combine(root, "docs/standards/05-database.md"));
        var heading = document.IndexOf("## Background jobs\n", StringComparison.Ordinal);
        heading.Should().BeGreaterThanOrEqualTo(0, "Standards 05 owns the reusable authoritative job DDL");
        var section = document[heading..];
        var start = section.IndexOf("```sql\n", StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0);
        start += "```sql\n".Length;
        var end = section.IndexOf("\n```", start, StringComparison.Ordinal);
        end.Should().BeGreaterThan(start);
        return section[start..end];
    }
}
