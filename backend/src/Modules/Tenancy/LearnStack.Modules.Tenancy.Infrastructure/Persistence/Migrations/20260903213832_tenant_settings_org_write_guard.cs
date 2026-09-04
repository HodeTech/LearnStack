using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LearnStack.Modules.Tenancy.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Narrows the two <c>AS RESTRICTIVE</c> write guards on <c>tenant_settings</c> so an
    /// organization-scoped session cannot write a tenant-wide row.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Policy-only: no table, column, index or data changes, which is why the model
    /// snapshot is unchanged and both methods are raw SQL.
    /// </para>
    /// <para>
    /// <b>What was wrong.</b> Each guard's first arm was a bare
    /// <c>organization_id IS NULL</c>. It exists so a tenant-scope session — one with no
    /// <c>app.organization_id</c> — can write the rows that belong to no organization. It
    /// also admitted an ORGANIZATION-scoped session to those same rows, so one
    /// organization could rewrite the tenant-wide fallback every other organization reads.
    /// Measured on the shipped schema: a session announcing tenant A and organization A1
    /// updated tenant A's <c>organization_id IS NULL</c> row without refusal.
    /// </para>
    /// <para>
    /// Intra-tenant rather than cross-tenant — the tenant term is untouched, and no row
    /// crosses a tenant boundary — so this is a write-scope correction, not an isolation
    /// fix. See ADR-0003 Amendment 4 and Database Standards § Tenant-Owned and
    /// Organization-Scoped Tables, which carries the corrected template.
    /// </para>
    /// <para>
    /// <b>Reversible.</b> <c>Down</c> restores the previous predicates exactly. Applying
    /// either direction is a policy replacement and rewrites no rows.
    /// </para>
    /// </remarks>
    public partial class tenant_settings_org_write_guard : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // DROP then CREATE rather than ALTER: PostgreSQL has no ALTER POLICY that
            // replaces a USING clause without restating it, and restating it under ALTER
            // reads as a smaller change than it is.
            migrationBuilder.Sql("""
                DROP POLICY tenant_settings_org_write_guard ON tenant_settings;
                DROP POLICY tenant_settings_org_delete_guard ON tenant_settings;

                CREATE POLICY tenant_settings_org_write_guard ON tenant_settings
                    AS RESTRICTIVE FOR UPDATE
                    USING (
                        (organization_id IS NULL
                         AND NULLIF(current_setting('app.organization_id', true), '') IS NULL)
                        OR organization_id = NULLIF(current_setting('app.organization_id', true), '')::uuid
                    );

                CREATE POLICY tenant_settings_org_delete_guard ON tenant_settings
                    AS RESTRICTIVE FOR DELETE
                    USING (
                        (organization_id IS NULL
                         AND NULLIF(current_setting('app.organization_id', true), '') IS NULL)
                        OR organization_id = NULLIF(current_setting('app.organization_id', true), '')::uuid
                    );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP POLICY tenant_settings_org_write_guard ON tenant_settings;
                DROP POLICY tenant_settings_org_delete_guard ON tenant_settings;

                CREATE POLICY tenant_settings_org_write_guard ON tenant_settings
                    AS RESTRICTIVE FOR UPDATE
                    USING (
                        organization_id IS NULL
                        OR organization_id = NULLIF(current_setting('app.organization_id', true), '')::uuid
                    );

                CREATE POLICY tenant_settings_org_delete_guard ON tenant_settings
                    AS RESTRICTIVE FOR DELETE
                    USING (
                        organization_id IS NULL
                        OR organization_id = NULLIF(current_setting('app.organization_id', true), '')::uuid
                    );
                """);
        }
    }
}
