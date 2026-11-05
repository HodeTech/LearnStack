using System.Data.Common;
using System.Text.RegularExpressions;
using Npgsql;

namespace LearnStack.Tests.Integration.Database;

/// <summary>Independent, frozen whole-body expectations for ADR-0093 guards; never read from the installer at runtime.</summary>
internal static class InvitationSchemaGuardExpectations
{
    internal sealed record Function(string Name, string Body, bool Definer, int Arguments);
    internal static readonly Function InvitationGuard = new("fn_identity_invitation_guard", Invitation, false, 0);
    internal static readonly Function EnvelopeGuard = new("fn_invitation_email_envelope_guard", Envelope, false, 0);
    internal static readonly Function AttemptGuard = new("fn_invitation_email_attempt_guard", Attempt, false, 0);
    internal static readonly Function ClaimFunction = new("fn_invitation_smtp_claim_or_read", Claim, true, 18);
    internal static readonly Function CompleteFunction = new("fn_invitation_smtp_complete_attempt", Complete, true, 16);

    internal static async Task<bool> IsExactAsync(DbConnection connection, DbTransaction? transaction, Function expected)
    {
        await using var command = new NpgsqlCommand("""
            SELECT p.prosrc, p.prosecdef, p.pronargs,
                (p.proconfig=ARRAY['search_path=pg_catalog'] AND p.provolatile='v'
                 AND l.lanname='plpgsql' AND owner.rolname='learnstack_migration'
                 AND NOT EXISTS(SELECT 1 FROM aclexplode(COALESCE(p.proacl,acldefault('f',p.proowner))) acl
                     WHERE acl.grantee=0 AND acl.privilege_type='EXECUTE')
                 AND NOT has_function_privilege('learnstack_platform',p.oid,'EXECUTE')
                 AND NOT has_function_privilege('learnstack_outbox_admin',p.oid,'EXECUTE')) AS confined,
                has_function_privilege('learnstack_app',p.oid,'EXECUTE') AS application_execute
            FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
            JOIN pg_language l ON l.oid=p.prolang JOIN pg_roles owner ON owner.oid=p.proowner
            WHERE n.nspname='public' AND p.proname=@name
            """, (NpgsqlConnection)connection, (NpgsqlTransaction?)transaction);
        command.Parameters.AddWithValue("name", expected.Name);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return false;
        var valid = Matches(reader.GetString(0), expected.Body) && reader.GetBoolean(1) == expected.Definer
            && reader.GetInt16(2) == expected.Arguments && !reader.IsDBNull(3) && reader.GetBoolean(3)
            && reader.GetBoolean(4) == expected.Definer;
        return valid && !await reader.ReadAsync();
    }

    internal static bool Matches(string actual, string expected) => Normalize(actual) == Normalize(expected);
    private static string Normalize(string value) => Regex.Replace(value.Trim(), @"\s+", " ");

    internal const string Invitation = """
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
                            WHERE tenant_id=NEW.tenant_id AND id=NEW.result_membership_id FOR KEY SHARE;
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
        END
        """;

    internal const string Envelope = """
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
        END;
        """;

    internal const string Attempt = """
        BEGIN
          IF TG_OP='DELETE' OR (TG_OP='UPDATE' AND (OLD.state<>'sending'
            OR ROW(NEW.tenant_id,NEW.organization_id,NEW.envelope_id,NEW.job_id,NEW.attempt_id,NEW.operation_id,NEW.plan_fingerprint,
              NEW.claim_id,NEW.admission_token,NEW.original_deadline,NEW.created_at)
              IS DISTINCT FROM ROW(OLD.tenant_id,OLD.organization_id,OLD.envelope_id,OLD.job_id,OLD.attempt_id,OLD.operation_id,OLD.plan_fingerprint,
              OLD.claim_id,OLD.admission_token,OLD.original_deadline,OLD.created_at)))
          THEN RAISE EXCEPTION 'Provider observation mutation refused.' USING ERRCODE='23514'; END IF;
          RETURN NEW;
        END;
        """;

    internal const string Claim = """
        DECLARE j record; a record; e record; r record;
        BEGIN
          IF (p_verify IS NOT NULL AND p_tenant IS NOT NULL AND p_tenant NOT IN ('00000000-0000-0000-0000-000000000000'::uuid,'00000000-0000-7000-8000-000000000002'::uuid)
            AND (p_org IS NULL OR p_org<>'00000000-0000-0000-0000-000000000000'::uuid)
            AND uuid_extract_version(p_envelope)=7 AND uuid_extract_version(p_invitation)=7 AND uuid_extract_version(p_generation)=7
            AND uuid_extract_version(p_job)=7 AND uuid_extract_version(p_attempt)=7 AND uuid_extract_version(p_claim)=7
            AND uuid_extract_version(p_receipt)=7 AND uuid_extract_version(p_evidence)=7
            AND p_operation<>'00000000-0000-0000-0000-000000000000'::uuid AND p_admission<>'00000000-0000-0000-0000-000000000000'::uuid
            AND p_fingerprint COLLATE "C" ~ '^[0-9a-f]{64}$' AND isfinite(p_deadline)) IS NOT TRUE
          THEN RAISE EXCEPTION 'Provider arguments refused.' USING ERRCODE='P0393'; END IF;
          IF (p_wake>=1 AND isfinite(p_until) AND p_until>clock_timestamp()
            AND (p_recovery IS NULL OR (p_recovery<>'00000000-0000-0000-0000-000000000000'::uuid AND p_recovery<>p_admission))) IS NOT TRUE
          THEN RAISE EXCEPTION 'Provider purpose refused.' USING ERRCODE='P0393'; END IF;
          SELECT * INTO j FROM public.background_jobs WHERE tenant_id=p_tenant AND id=p_job FOR SHARE;
          IF NOT FOUND OR j.organization_id IS DISTINCT FROM p_org OR j.profile<>'external' OR j.queue<>'external'
            OR j.capacity_group<>'identity.invitation_email'
            OR j.definition_key IS DISTINCT FROM (CASE WHEN p_org IS NULL THEN 'identity.invitation_email_tenant.v1' ELSE 'identity.invitation_email_organization.v1' END)
            OR j.plan_fingerprint IS DISTINCT FROM p_fingerprint OR j.operation_id IS DISTINCT FROM p_operation
            OR j.parameters::jsonb IS DISTINCT FROM jsonb_build_object('InvitationId',p_invitation,'GenerationId',p_generation,'SpoolId',p_envelope)
            OR j.plan::jsonb IS DISTINCT FROM j.parameters::jsonb
          THEN RAISE EXCEPTION 'Provider invocation refused.' USING ERRCODE='P0393'; END IF;
          SELECT * INTO a FROM public.background_job_attempts WHERE tenant_id=p_tenant AND job_id=p_job AND id=p_attempt;
          IF NOT FOUND OR a.stage<>'normal' OR a.organization_id IS DISTINCT FROM p_org OR a.admission_token IS DISTINCT FROM p_admission
            OR a.deadline IS DISTINCT FROM p_deadline OR a.plan_fingerprint IS DISTINCT FROM p_fingerprint OR a.operation_id IS DISTINCT FROM p_operation
            OR a.execute_intent_at IS NULL OR a.no_start_at IS NOT NULL
          THEN RAISE EXCEPTION 'Provider attempt refused.' USING ERRCODE='P0393'; END IF;
          IF j.current_attempt_id IS DISTINCT FROM p_attempt OR j.wake_generation IS DISTINCT FROM p_wake
            OR (j.ownership_until>=p_until AND j.ownership_until>clock_timestamp()) IS NOT TRUE
            OR (p_recovery IS NULL AND (j.ownership_kind IS DISTINCT FROM 'execution' OR j.ownership_token IS DISTINCT FROM p_admission
              OR j.phase IS DISTINCT FROM 'executing' OR (p_deadline>clock_timestamp()) IS NOT TRUE))
            OR (p_recovery IS NOT NULL AND (j.ownership_kind IS DISTINCT FROM 'recovery' OR j.ownership_token IS DISTINCT FROM p_recovery
              OR p_recovery=p_admission OR j.phase IS DISTINCT FROM 'reconciling'))
          THEN RAISE EXCEPTION 'Provider ownership refused.' USING ERRCODE='P0393'; END IF;
          SELECT * INTO e FROM public.invitation_email_envelopes WHERE tenant_id=p_tenant AND id=p_envelope FOR SHARE;
          IF NOT FOUND OR e.organization_id IS DISTINCT FROM p_org OR e.job_id IS DISTINCT FROM p_job OR e.invitation_id IS DISTINCT FROM p_invitation OR e.generation_id IS DISTINCT FROM p_generation
          THEN RAISE EXCEPTION 'Provider envelope refused.' USING ERRCODE='P0393'; END IF;
          IF NOT p_verify THEN
          INSERT INTO public.invitation_email_attempt_receipts(tenant_id,organization_id,envelope_id,job_id,attempt_id,operation_id,plan_fingerprint,
            claim_id,admission_token,original_deadline,state,receipt_id,evidence_id,completed_at)
          VALUES(p_tenant,p_org,p_envelope,p_job,p_attempt,p_operation,p_fingerprint,p_claim,p_admission,p_deadline,
            CASE WHEN p_recovery IS NULL THEN 'sending' ELSE 'cancelled_before_send' END,
            CASE WHEN p_recovery IS NULL THEN NULL ELSE p_receipt END,CASE WHEN p_recovery IS NULL THEN NULL ELSE p_evidence END,
            CASE WHEN p_recovery IS NULL THEN NULL ELSE clock_timestamp() END)
          ON CONFLICT (tenant_id,envelope_id,attempt_id) DO NOTHING;
          END IF;
          SELECT * INTO r FROM public.invitation_email_attempt_receipts WHERE tenant_id=p_tenant AND envelope_id=p_envelope AND attempt_id=p_attempt FOR UPDATE;
          IF NOT FOUND OR r.organization_id IS DISTINCT FROM p_org OR r.job_id IS DISTINCT FROM p_job OR r.operation_id IS DISTINCT FROM p_operation
            OR r.plan_fingerprint IS DISTINCT FROM p_fingerprint OR r.admission_token IS DISTINCT FROM p_admission OR r.original_deadline IS DISTINCT FROM p_deadline
          THEN RAISE EXCEPTION 'Provider receipt binding refused.' USING ERRCODE='P0393'; END IF;
          IF p_recovery IS NULL AND r.state='sending' AND r.claim_id=p_claim THEN
            RETURN QUERY SELECT r.state,r.claim_id,r.receipt_id,r.evidence_id,e.key_version,e.nonce,e.ciphertext,e.tag;
          ELSE
            RETURN QUERY SELECT r.state,r.claim_id,r.receipt_id,r.evidence_id,NULL::text,NULL::bytea,NULL::bytea,NULL::bytea;
          END IF;
        END;
        """;

    internal const string Complete = """
        DECLARE r record;
        BEGIN
          IF (p_verify IS NOT NULL AND p_tenant IS NOT NULL AND p_tenant NOT IN ('00000000-0000-0000-0000-000000000000'::uuid,'00000000-0000-7000-8000-000000000002'::uuid)
            AND (p_org IS NULL OR p_org<>'00000000-0000-0000-0000-000000000000'::uuid)
            AND uuid_extract_version(p_envelope)=7 AND uuid_extract_version(p_invitation)=7 AND uuid_extract_version(p_generation)=7
            AND uuid_extract_version(p_job)=7 AND uuid_extract_version(p_attempt)=7 AND uuid_extract_version(p_claim)=7
            AND uuid_extract_version(p_receipt)=7 AND uuid_extract_version(p_evidence)=7
            AND p_operation<>'00000000-0000-0000-0000-000000000000'::uuid AND p_admission<>'00000000-0000-0000-0000-000000000000'::uuid
            AND p_fingerprint COLLATE "C" ~ '^[0-9a-f]{64}$' AND isfinite(p_deadline)) IS NOT TRUE
          THEN RAISE EXCEPTION 'Provider arguments refused.' USING ERRCODE='P0393'; END IF;
          IF p_state IS NULL OR p_state NOT IN ('accepted','temporary_rejected','permanent_rejected')
          THEN RAISE EXCEPTION 'Provider completion refused.' USING ERRCODE='P0393'; END IF;
          PERFORM 1 FROM public.invitation_email_envelopes e JOIN public.background_jobs j ON j.id=e.job_id AND j.tenant_id=e.tenant_id
            JOIN public.background_job_attempts a ON a.job_id=j.id AND a.tenant_id=j.tenant_id AND a.id=p_attempt
            WHERE e.tenant_id=p_tenant AND e.id=p_envelope AND e.organization_id IS NOT DISTINCT FROM p_org
              AND e.invitation_id=p_invitation AND e.generation_id=p_generation AND e.job_id=p_job
              AND j.definition_key=CASE WHEN p_org IS NULL THEN 'identity.invitation_email_tenant.v1' ELSE 'identity.invitation_email_organization.v1' END
              AND j.profile='external' AND j.queue='external' AND j.capacity_group='identity.invitation_email'
              AND j.plan_fingerprint=p_fingerprint AND j.operation_id=p_operation AND a.plan_fingerprint=p_fingerprint
              AND a.operation_id=p_operation AND a.admission_token=p_admission AND a.deadline=p_deadline
              AND a.stage='normal' AND a.execute_intent_at IS NOT NULL AND a.no_start_at IS NULL;
          IF NOT FOUND THEN RAISE EXCEPTION 'Provider completion binding refused.' USING ERRCODE='P0393'; END IF;
          SELECT * INTO r FROM public.invitation_email_attempt_receipts WHERE tenant_id=p_tenant AND envelope_id=p_envelope AND attempt_id=p_attempt FOR UPDATE;
          IF NOT FOUND OR r.organization_id IS DISTINCT FROM p_org OR r.job_id IS DISTINCT FROM p_job OR r.claim_id IS DISTINCT FROM p_claim OR r.operation_id IS DISTINCT FROM p_operation
            OR r.plan_fingerprint IS DISTINCT FROM p_fingerprint OR r.admission_token IS DISTINCT FROM p_admission OR r.original_deadline IS DISTINCT FROM p_deadline
          THEN RAISE EXCEPTION 'Provider claim refused.' USING ERRCODE='P0393'; END IF;
          IF p_verify THEN
            IF ROW(r.state,r.receipt_id,r.evidence_id) IS DISTINCT FROM ROW(p_state,p_receipt,p_evidence)
            THEN RAISE EXCEPTION 'Provider verification unresolved.' USING ERRCODE='P0393'; END IF;
          ELSIF r.state='sending' THEN
            UPDATE public.invitation_email_attempt_receipts SET state=p_state,receipt_id=p_receipt,evidence_id=p_evidence,completed_at=clock_timestamp()
              WHERE tenant_id=p_tenant AND envelope_id=p_envelope AND attempt_id=p_attempt;
          ELSIF ROW(r.state,r.receipt_id,r.evidence_id) IS DISTINCT FROM ROW(p_state,p_receipt,p_evidence) THEN
            RAISE EXCEPTION 'Provider completion conflict.' USING ERRCODE='P0393';
          END IF;
          RETURN QUERY SELECT p_state,p_claim,p_receipt,p_evidence;
        END;
        """;
}
