using Microsoft.EntityFrameworkCore.Migrations;

namespace LearnStack.Modules.Identity.Infrastructure.Persistence;

/// <summary>Exact offer RLS, state backstops and subject-confined preliminary lookup.</summary>
internal static class InvitationStorageDdl
{
    internal static void Install(MigrationBuilder migration) => migration.Sql("""
        ALTER TABLE public.invitations ENABLE ROW LEVEL SECURITY;
        ALTER TABLE public.invitations FORCE ROW LEVEL SECURITY;
        CREATE POLICY invitations_isolation ON public.invitations
            USING ((tenant_id = NULLIF(current_setting('app.tenant_id',true),'')::uuid
                AND (organization_id IS NULL
                    OR organization_id = NULLIF(current_setting('app.organization_id',true),'')::uuid
                    OR NULLIF(current_setting('app.scope',true),'')='tenant'))
                OR current_user='learnstack_migration')
            WITH CHECK (current_user='learnstack_app'
                AND tenant_id = NULLIF(current_setting('app.tenant_id',true),'')::uuid
                AND ((organization_id IS NULL AND NULLIF(current_setting('app.organization_id',true),'') IS NULL)
                    OR organization_id = NULLIF(current_setting('app.organization_id',true),'')::uuid));
        CREATE POLICY invitations_org_write_guard ON public.invitations AS RESTRICTIVE FOR UPDATE
            USING ((organization_id IS NULL AND NULLIF(current_setting('app.organization_id',true),'') IS NULL)
                OR organization_id = NULLIF(current_setting('app.organization_id',true),'')::uuid);
        CREATE POLICY invitations_org_delete_guard ON public.invitations AS RESTRICTIVE FOR DELETE
            USING ((organization_id IS NULL AND NULLIF(current_setting('app.organization_id',true),'') IS NULL)
                OR organization_id = NULLIF(current_setting('app.organization_id',true),'')::uuid);
        REVOKE ALL ON public.invitations FROM PUBLIC,learnstack_app,learnstack_platform,learnstack_outbox_admin;
        GRANT SELECT,INSERT,UPDATE ON public.invitations TO learnstack_app;
        CREATE TRIGGER tg_invitations_organization_id_immutable BEFORE UPDATE ON public.invitations
            FOR EACH ROW EXECUTE FUNCTION public.fn_organization_id_immutable();

        CREATE FUNCTION public.fn_identity_invitation_guard() RETURNS trigger
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
                    IF OLD.submission_receipt_id IS NOT NULL AND ROW(NEW.submission_status,NEW.submission_receipt_id)
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
        REVOKE ALL ON FUNCTION public.fn_identity_invitation_guard() FROM PUBLIC;
        CREATE TRIGGER tg_invitations_mutation_guard BEFORE INSERT OR UPDATE ON public.invitations
            FOR EACH ROW EXECUTE FUNCTION public.fn_identity_invitation_guard();

        CREATE FUNCTION public.fn_identity_invitation_entry(invitation_id uuid,verifier_digest bytea,
            host_tenant uuid,authority_id uuid,subject_key bytea)
            RETURNS TABLE (offer_id uuid,tenant_id uuid,organization_id uuid,offer_version bigint,
                generation_id uuid,generation bigint,actor_user_id uuid,inviter_globally_admitted boolean,
                offer_status text,result_membership_id uuid,accepted_by uuid,acceptance_disposition text)
            LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $entry$
        DECLARE captured record;
        BEGIN
            IF session_user <> 'learnstack_app' OR invitation_id IS NULL OR host_tenant IS NULL
                OR host_tenant IN ('00000000-0000-0000-0000-000000000000'::uuid,'00000000-0000-7000-8000-000000000002'::uuid)
                OR verifier_digest IS NULL OR octet_length(verifier_digest) <> 32
                OR authority_id IS NULL OR subject_key IS NULL OR octet_length(subject_key) <> 32
                OR current_setting('transaction_isolation') <> 'read committed'
                OR (NULLIF(current_setting('app.tenant_id',true),'') IS NOT NULL
                    AND NULLIF(current_setting('app.tenant_id',true),'')::uuid IS DISTINCT FROM host_tenant)
            THEN RAISE EXCEPTION 'Invitation entry refused.' USING ERRCODE='23514'; END IF;
            SELECT i.id AS offer_id,i.tenant_id,i.organization_id,i.row_version AS offer_version,i.generation_id,i.generation,
                a.id AS actor_user_id,i.inviter_user_id,(v.status='Active' AND v.deleted_at IS NULL) AS inviter_globally_admitted,
                i.status AS offer_status,i.result_membership_id,i.accepted_by,i.acceptance_disposition INTO captured
                FROM public.invitations i
                JOIN public.users a ON a.authority_id=fn_identity_invitation_entry.authority_id
                    AND a.subject_key=fn_identity_invitation_entry.subject_key AND a.status='Active' AND a.deleted_at IS NULL
                JOIN public.users v ON v.id=i.inviter_user_id
                JOIN public.identity_authority installed ON installed.authority_id=a.authority_id
                WHERE i.id=invitation_id AND i.tenant_id=host_tenant AND i.deleted_at IS NULL
                    AND ((i.status='Pending' AND i.verifier_digest=fn_identity_invitation_entry.verifier_digest)
                        OR (i.status='Accepted' AND i.consumed_verifier_digest=fn_identity_invitation_entry.verifier_digest AND i.accepted_by=a.id));
            IF NOT FOUND THEN RETURN; END IF;
            IF current_setting('transaction_read_only')='off' THEN
                PERFORM public.fn_identity_assert_tenant_fence(host_tenant,false);
                IF captured.organization_id IS DISTINCT FROM NULLIF(current_setting('app.organization_id',true),'')::uuid
                    OR NOT public.fn_identity_lock_held('user',captured.actor_user_id,true)
                    OR NOT public.fn_identity_lock_held('user',captured.inviter_user_id,true)
                THEN RAISE EXCEPTION 'Invitation account observation fence refused.' USING ERRCODE='P0301'; END IF;
            END IF;
            RETURN QUERY SELECT captured.offer_id,captured.tenant_id,captured.organization_id,captured.offer_version,
                captured.generation_id,captured.generation,captured.actor_user_id,captured.inviter_globally_admitted,
                captured.offer_status,captured.result_membership_id,captured.accepted_by,captured.acceptance_disposition;
        END $entry$;
        REVOKE ALL ON FUNCTION public.fn_identity_invitation_entry(uuid,bytea,uuid,uuid,bytea) FROM PUBLIC;
        GRANT EXECUTE ON FUNCTION public.fn_identity_invitation_entry(uuid,bytea,uuid,uuid,bytea) TO learnstack_app;
        """);

    internal static void Remove(MigrationBuilder migration) => migration.Sql("""
        DROP FUNCTION public.fn_identity_invitation_entry(uuid,bytea,uuid,uuid,bytea);
        DROP TRIGGER tg_invitations_mutation_guard ON public.invitations;
        DROP FUNCTION public.fn_identity_invitation_guard();
        """);
}
