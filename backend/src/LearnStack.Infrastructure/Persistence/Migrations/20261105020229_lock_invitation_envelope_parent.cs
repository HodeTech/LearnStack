using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LearnStack.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class lock_invitation_envelope_parent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Preserve the existing body and ACL; lock the exact immutable parent before its FK runs.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION public.fn_invitation_email_envelope_guard() RETURNS trigger
                  LANGUAGE plpgsql SET search_path=pg_catalog AS $fn$
                DECLARE j record;
                BEGIN
                  IF TG_OP<>'INSERT' THEN RAISE EXCEPTION 'Provider envelope mutation refused.' USING ERRCODE='23514'; END IF;
                  SELECT * INTO j FROM public.background_jobs WHERE tenant_id=NEW.tenant_id AND id=NEW.job_id FOR KEY SHARE;
                  IF NOT FOUND OR j.organization_id IS DISTINCT FROM NEW.organization_id OR j.profile<>'external'
                     OR j.queue<>'external' OR j.capacity_group<>'identity.invitation_email'
                     OR j.definition_key IS DISTINCT FROM (CASE WHEN NEW.organization_id IS NULL THEN 'identity.invitation_email_tenant.v1' ELSE 'identity.invitation_email_organization.v1' END)
                     OR j.parameters::jsonb IS DISTINCT FROM jsonb_build_object('InvitationId',NEW.invitation_id,'GenerationId',NEW.generation_id,'SpoolId',NEW.id)
                  THEN RAISE EXCEPTION 'Provider envelope binding refused.' USING ERRCODE='23514'; END IF;
                  RETURN NEW;
                END; $fn$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            throw new NotSupportedException("Parent scope locking requires a reviewed forward migration.");
    }
}
