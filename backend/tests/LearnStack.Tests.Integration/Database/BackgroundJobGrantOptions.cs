using System.Data.Common;

namespace LearnStack.Tests.Integration.Database;

/// <summary>Grant-option inspection includes PUBLIC (OID zero) and every non-owner grantee.</summary>
internal static class BackgroundJobGrantOptions
{
    private static readonly string Names = string.Join(",", BackgroundJobSchemaInventory.Tables
        .Concat(SchemaTableClasses.Vendor).Select(name => $"'{name}'"));

    public sealed record Probe(string Grant, string Restore, string Finding);
    public static readonly Probe[] Probes =
    [
        new("GRANT SELECT ON background_jobs TO learnstack_app WITH GRANT OPTION",
            "REVOKE GRANT OPTION FOR SELECT ON background_jobs FROM learnstack_app",
            "table:background_jobs:learnstack_app:SELECT"),
        new("GRANT SELECT (parameters) ON background_jobs TO learnstack_app WITH GRANT OPTION",
            "REVOKE SELECT (parameters) ON background_jobs FROM learnstack_app",
            "column:background_jobs.parameters:learnstack_app:SELECT"),
        new("GRANT USAGE ON SEQUENCE job_id_seq TO learnstack_outbox_admin WITH GRANT OPTION",
            "REVOKE GRANT OPTION FOR USAGE ON SEQUENCE job_id_seq FROM learnstack_outbox_admin",
            "sequence:job_id_seq:learnstack_outbox_admin:USAGE"),
        new("GRANT EXECUTE ON FUNCTION fn_hangfire_queue(text) TO learnstack_outbox_admin WITH GRANT OPTION",
            "REVOKE GRANT OPTION FOR EXECUTE ON FUNCTION fn_hangfire_queue(text) FROM learnstack_outbox_admin",
            "function:fn_hangfire_queue:learnstack_outbox_admin:EXECUTE"),
    ];

    public static Task<List<string>> ReadAsync(DbConnection owner) => SchemaQueries.ReadStringsAsync(owner, $"""
        SELECT finding FROM (
            SELECT CASE WHEN c.relkind = 'S' THEN 'sequence:' ELSE 'table:' END || c.relname || ':' ||
                CASE WHEN a.grantee = 0 THEN 'PUBLIC' ELSE a.grantee::regrole::text END || ':' || a.privilege_type AS finding
            FROM pg_class c CROSS JOIN LATERAL aclexplode(c.relacl) a
            WHERE c.relnamespace = 'public'::regnamespace AND a.is_grantable AND a.grantee <> c.relowner
              AND (c.relkind = 'r' AND c.relname IN ({Names}) OR c.relkind = 'S')
            UNION ALL
            SELECT 'column:' || c.relname || '.' || column_acl.attname || ':' ||
                CASE WHEN a.grantee = 0 THEN 'PUBLIC' ELSE a.grantee::regrole::text END || ':' || a.privilege_type
            FROM pg_class c JOIN pg_attribute column_acl ON column_acl.attrelid = c.oid
                CROSS JOIN LATERAL aclexplode(column_acl.attacl) a
            WHERE c.relnamespace = 'public'::regnamespace AND c.relname IN ({Names})
              AND a.is_grantable AND a.grantee <> c.relowner
            UNION ALL
            SELECT 'function:' || p.proname || ':' ||
                CASE WHEN a.grantee = 0 THEN 'PUBLIC' ELSE a.grantee::regrole::text END || ':' || a.privilege_type
            FROM pg_proc p CROSS JOIN LATERAL aclexplode(p.proacl) a
            WHERE p.pronamespace = 'public'::regnamespace AND a.is_grantable AND a.grantee <> p.proowner
              AND (p.proname LIKE 'fn_background_job%' OR p.proname LIKE 'fn_hangfire%')
        ) granted ORDER BY finding
        """);
}
