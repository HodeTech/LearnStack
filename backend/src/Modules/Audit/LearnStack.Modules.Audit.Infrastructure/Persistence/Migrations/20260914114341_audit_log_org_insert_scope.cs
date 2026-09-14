using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LearnStack.Modules.Audit.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Requires an audit INSERT to carry exactly the session's organization scope
    /// (ADR-0003 Amendment 6). Existing writers announce that scope already.
    /// </summary>
    public partial class audit_log_org_insert_scope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER POLICY audit_log_isolation ON public.audit_log
                    WITH CHECK (
                        tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
                        AND (
                            (organization_id IS NULL
                             AND NULLIF(current_setting('app.organization_id', true), '') IS NULL)
                            OR organization_id = NULLIF(current_setting('app.organization_id', true), '')::uuid
                        )
                    );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER POLICY audit_log_isolation ON public.audit_log
                    WITH CHECK (
                        tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
                        AND (
                            organization_id IS NULL
                            OR organization_id = NULLIF(current_setting('app.organization_id', true), '')::uuid
                        )
                    );
                """);
        }
    }
}
