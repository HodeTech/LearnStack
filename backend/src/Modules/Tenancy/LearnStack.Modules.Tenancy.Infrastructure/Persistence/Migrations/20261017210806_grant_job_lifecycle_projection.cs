using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LearnStack.Modules.Tenancy.Infrastructure.Persistence.Migrations
{
    /// <summary>ADR-0047: expose only the named lifecycle projection to the confined job-control role.</summary>
    public partial class grant_job_lifecycle_projection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                GRANT SELECT (id,status,deleted_at,default_organization_id) ON public.tenants TO learnstack_outbox_admin;
                GRANT SELECT (tenant_id,id,status,deleted_at) ON public.organizations TO learnstack_outbox_admin;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                REVOKE SELECT (id,status,deleted_at,default_organization_id) ON public.tenants FROM learnstack_outbox_admin;
                REVOKE SELECT (tenant_id,id,status,deleted_at) ON public.organizations FROM learnstack_outbox_admin;
                """);
        }
    }
}
