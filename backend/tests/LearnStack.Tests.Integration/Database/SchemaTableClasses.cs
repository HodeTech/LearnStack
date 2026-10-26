using System.Data.Common;
using FluentAssertions;

namespace LearnStack.Tests.Integration.Database;

/// <summary>ADR-0047's closed applied-table classes; discovery always comes from pg_catalog.</summary>
internal static class SchemaTableClasses
{
    public const long SelectDenied = -1;

    public static readonly string[] Vendor =
    [
        "aggregatedcounter", "counter", "hash", "job", "jobparameter", "jobqueue",
        "list", "lock", "schema", "server", "set", "state",
    ];

    public static readonly string[] Control = ["background_job_recurrences", "background_job_occurrences"];
    public static readonly string[] JobTenant =
    [
        "background_jobs", "background_job_attempts", "background_job_capacity",
        "background_job_reservations", "background_job_recipients",
    ];

    public static readonly string[] Existing =
    [
        "tenants", "organizations", "tenant_domains", "tenant_locales", "tenant_settings", "tenant_feature_flags",
        "platform_entitlement_cache", "platform_host_to_tenant", "outbox_messages", "idempotency_keys",
        "event_deliveries", "inbox_messages", "tenant_content_types", "tenant_level_taxonomies",
        "tenant_level_taxonomy_items", "customization_generations", "audit_log", "audit_config",
        "platform_killswitches", "courses", "lessons", "course_translations", "lesson_translations",
    ];

    public static readonly string[] GlobalIdentity = ["users"];
    public static readonly string[] IdentityPlatform = ["identity_authority"];
    public static readonly string[] IdentityContextual = ["memberships", "membership_profiles",
        "membership_role_assignments", "roles", "role_permission_grants"];
    public static readonly string[] Data = [.. Existing, .. JobTenant, .. Control, .. Vendor,
        .. GlobalIdentity, .. IdentityPlatform, .. IdentityContextual];
    public static bool AppSelectDenied(string table) => Control.Contains(table) || Vendor.Contains(table);

    internal static Task<List<string>> ReadAsync(DbConnection connection, DbTransaction? transaction = null) =>
        SchemaQueries.ReadStringsAsync(connection, """
            SELECT relname FROM pg_class
            WHERE relnamespace = 'public'::regnamespace AND relkind IN ('r', 'p', 'm') ORDER BY relname
            """, transaction);

    public static async Task AssertCompleteAsync(DbConnection connection, DbTransaction? transaction = null)
    {
        var discovered = await ReadAsync(connection, transaction);
        discovered.Should().BeEquivalentTo(Data.Concat(MigrationChains.HistoryTables),
            "every public table must have an exact reviewed class, including the six history tables; "
            + "Fix: classify a new table under Standards 05 rather than excluding it from discovery");
    }
}
