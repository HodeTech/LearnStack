using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LearnStack.Modules.Tenancy.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Completes exact organization write scope for INSERT and lets natural-key
    /// satellites use the shared immutability guard (ADR-0003 Amendment 6).
    /// </summary>
    /// <remarks>
    /// Policy/function-only: existing rows and read predicates are unchanged.
    /// Education depends on this function and must reverse before this migration.
    /// </remarks>
    public partial class org_insert_scope_and_keyless_guard : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER POLICY tenant_settings_isolation ON public.tenant_settings
                    WITH CHECK (
                        tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
                        AND (
                            (organization_id IS NULL
                             AND NULLIF(current_setting('app.organization_id', true), '') IS NULL)
                            OR organization_id = NULLIF(current_setting('app.organization_id', true), '')::uuid
                        )
                    );

                CREATE OR REPLACE FUNCTION public.fn_organization_id_immutable() RETURNS trigger AS $$
                BEGIN
                    IF NEW.organization_id IS DISTINCT FROM OLD.organization_id THEN
                        RAISE EXCEPTION
                            'organization_id is immutable after insert (table %)',
                            TG_TABLE_NAME
                            USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Restore the historical definitions exactly, including the old function's
            // id assumption. Dependent Education satellites must already be removed.
            migrationBuilder.Sql("""
                ALTER POLICY tenant_settings_isolation ON public.tenant_settings
                    WITH CHECK (
                        tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
                        AND (
                            organization_id IS NULL
                            OR organization_id = NULLIF(current_setting('app.organization_id', true), '')::uuid
                        )
                    );

                CREATE OR REPLACE FUNCTION public.fn_organization_id_immutable() RETURNS trigger AS $$
                BEGIN
                    IF NEW.organization_id IS DISTINCT FROM OLD.organization_id THEN
                        RAISE EXCEPTION
                            'organization_id is immutable after insert (table %, row %)',
                            TG_TABLE_NAME, OLD.id
                            USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;
                """);
        }
    }
}
