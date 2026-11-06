using Microsoft.EntityFrameworkCore.Migrations;

namespace LearnStack.Modules.Identity.Infrastructure.Persistence.Migrations;

public sealed partial class AddInvitationWorkflowFailure : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        ALTER TABLE public.invitations DROP CONSTRAINT ck_invitations_submission;
        ALTER TABLE public.invitations ADD CONSTRAINT ck_invitations_submission CHECK (((submission_status IN ('Queued','Failed') AND submission_receipt_id IS NULL) OR (submission_status IN ('SubmissionAccepted','Failed') AND submission_receipt_id IS NOT NULL AND submission_receipt_id <> '00000000-0000-0000-0000-000000000000'::uuid)) IS TRUE);
        CREATE OR REPLACE FUNCTION public.fn_identity_invitation_guard() RETURNS trigger
            LANGUAGE plpgsql VOLATILE SECURITY INVOKER SET search_path=pg_catalog AS $guard$
        DECLARE selected_role public.roles%ROWTYPE; selected_member public.memberships%ROWTYPE;
        BEGIN
            IF session_user <> 'learnstack_app' OR current_user <> 'learnstack_app'
                OR TG_TABLE_SCHEMA <> 'public' OR TG_TABLE_NAME <> 'invitations'
                OR TG_OP NOT IN ('INSERT','UPDATE')
            THEN RAISE EXCEPTION 'Invitation mutation refused.' USING ERRCODE='23514'; END IF;
            PERFORM public.fn_identity_assert_tenant_fence(NEW.tenant_id,false);
            IF NEW.entry_origin !~ '^https://[^/@?#[:space:]]+$'
                OR NEW.entry_locale !~ '^[A-Za-z]{2,8}(-[A-Za-z0-9]{1,8})*$'
                OR length(split_part(NEW.recipient_email,'@',1)) > 64
                OR NEW.recipient_email !~ $mail$^[a-z0-9!#$%&'*+/=?^_`{|}~-]+(\.[a-z0-9!#$%&'*+/=?^_`{|}~-]+)*@[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?(\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)*$$mail$
            THEN RAISE EXCEPTION 'Invitation shape refused.' USING ERRCODE='23514'; END IF;
            IF TG_OP='INSERT' THEN
                IF NEW.status <> 'Pending' OR NEW.generation <> 1 OR NEW.row_version <> 0
                    OR NEW.inviter_user_id IS DISTINCT FROM NEW.created_by
                    OR NEW.updated_at IS NOT NULL OR NEW.updated_by IS NOT NULL
                    OR NEW.generation_created_at IS DISTINCT FROM NEW.created_at
                    OR NEW.submission_status <> 'Queued'
                THEN RAISE EXCEPTION 'Invitation initial state refused.' USING ERRCODE='23514'; END IF;
            ELSE
                IF ROW(NEW.id,NEW.tenant_id,NEW.organization_id,NEW.recipient_email,NEW.inviter_user_id,
                    NEW.role_id,NEW.pinned_role_version,NEW.created_at,NEW.created_by)
                    IS DISTINCT FROM ROW(OLD.id,OLD.tenant_id,OLD.organization_id,OLD.recipient_email,OLD.inviter_user_id,
                    OLD.role_id,OLD.pinned_role_version,OLD.created_at,OLD.created_by)
                    OR NEW.row_version IS DISTINCT FROM OLD.row_version+1
                    OR NEW.updated_at IS NULL OR NEW.updated_by IS NULL OR NOT isfinite(NEW.updated_at)
                    OR NEW.updated_at < COALESCE(OLD.updated_at,OLD.created_at)
                THEN RAISE EXCEPTION 'Invitation immutable state refused.' USING ERRCODE='23514'; END IF;
                IF NEW.generation IS DISTINCT FROM OLD.generation THEN
                    IF OLD.status <> 'Pending' OR NEW.status <> 'Pending' OR NEW.generation <> OLD.generation+1
                        OR NEW.generation_id=OLD.generation_id OR NEW.current_delivery_job_id=OLD.current_delivery_job_id
                        OR NEW.verifier_digest IS NOT DISTINCT FROM OLD.verifier_digest
                        OR NEW.generation_created_at < OLD.generation_created_at
                        OR NEW.submission_status <> 'Queued' OR NEW.submission_receipt_id IS NOT NULL
                    THEN RAISE EXCEPTION 'Invitation replacement refused.' USING ERRCODE='23514'; END IF;
                ELSE
                    IF ROW(NEW.generation_id,NEW.generation_created_at,NEW.expires_at,NEW.entry_origin,NEW.entry_locale,NEW.current_delivery_job_id)
                        IS DISTINCT FROM ROW(OLD.generation_id,OLD.generation_created_at,OLD.expires_at,OLD.entry_origin,OLD.entry_locale,OLD.current_delivery_job_id)
                    THEN RAISE EXCEPTION 'Invitation generation refused.' USING ERRCODE='23514'; END IF;
                    IF OLD.status='Pending' AND NEW.status='Accepted' THEN
                        IF clock_timestamp() >= OLD.expires_at OR NEW.consumed_verifier_digest IS DISTINCT FROM OLD.verifier_digest
                        THEN RAISE EXCEPTION 'Invitation acceptance refused.' USING ERRCODE='23514'; END IF;
                        IF NOT public.fn_identity_lock_held('user',NEW.accepted_by,true)
                            OR NOT public.fn_identity_lock_held('user',NEW.inviter_user_id,true)
                        THEN RAISE EXCEPTION 'Invitation account fence refused.' USING ERRCODE='P0301'; END IF;
                        SELECT * INTO selected_member FROM public.memberships
                            WHERE tenant_id=NEW.tenant_id AND id=NEW.result_membership_id;
                        IF NOT FOUND OR selected_member.organization_id IS DISTINCT FROM NEW.organization_id
                            OR selected_member.user_id IS DISTINCT FROM NEW.accepted_by
                            OR selected_member.status <> 'Active' OR selected_member.deleted_at IS NOT NULL
                        THEN RAISE EXCEPTION 'Invitation result refused.' USING ERRCODE='23514'; END IF;
                    ELSIF OLD.status='Pending' AND NEW.status='Revoked' THEN
                        NULL;
                    ELSIF ROW(NEW.status,NEW.verifier_digest,NEW.consumed_verifier_digest,NEW.accepted_by,
                        NEW.result_membership_id,NEW.acceptance_disposition,NEW.accepted_at)
                        IS DISTINCT FROM ROW(OLD.status,OLD.verifier_digest,OLD.consumed_verifier_digest,OLD.accepted_by,
                        OLD.result_membership_id,OLD.acceptance_disposition,OLD.accepted_at)
                    THEN RAISE EXCEPTION 'Invitation terminal state refused.' USING ERRCODE='23514'; END IF;
                    IF OLD.submission_status <> 'Queued' AND ROW(NEW.submission_status,NEW.submission_receipt_id)
                        IS DISTINCT FROM ROW(OLD.submission_status,OLD.submission_receipt_id)
                    THEN RAISE EXCEPTION 'Invitation submission evidence refused.' USING ERRCODE='23514'; END IF;
                END IF;
            END IF;
            -- Later delivery bookkeeping may follow legitimate Role edits. Only
            -- initial/reissued admission pins current compatible Role content.
            IF TG_OP='INSERT' OR NEW.generation IS DISTINCT FROM OLD.generation
                OR (OLD.status='Pending' AND NEW.status='Accepted') THEN
                SELECT * INTO selected_role FROM public.roles WHERE tenant_id=NEW.tenant_id AND id=NEW.role_id;
                IF NOT FOUND OR selected_role.deleted_at IS NOT NULL OR selected_role.row_version <> NEW.pinned_role_version
                    OR selected_role.permission_scope IS DISTINCT FROM (CASE WHEN NEW.organization_id IS NULL THEN 'Tenant' ELSE 'Organization' END)
                THEN RAISE EXCEPTION 'Invitation Role pin refused.' USING ERRCODE='23514'; END IF;
            END IF;
            RETURN NEW;
        END $guard$;
        """);
    protected override void Down(MigrationBuilder migrationBuilder) => throw new NotSupportedException("Selected failure evidence requires a reviewed forward migration.");
}
