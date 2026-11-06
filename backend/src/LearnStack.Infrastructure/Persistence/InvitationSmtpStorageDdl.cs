namespace LearnStack.Infrastructure.Persistence;

/// <summary>Immutable SQL emitted by the initial SMTP custody migration.</summary>
internal static class InvitationSmtpStorageDdl
{
    internal const string Sql = """
        CREATE TABLE public.invitation_email_envelopes (
            id uuid PRIMARY KEY, tenant_id uuid NOT NULL, organization_id uuid NULL,
            invitation_id uuid NOT NULL, generation_id uuid NOT NULL, job_id uuid NOT NULL,
            format_version integer NOT NULL, key_version text NOT NULL,
            nonce bytea NOT NULL, ciphertext bytea NOT NULL, tag bytea NOT NULL,
            created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
            CONSTRAINT ux_invitation_email_envelopes_job UNIQUE (tenant_id,job_id),
            CONSTRAINT ux_invitation_email_envelopes_identity UNIQUE (tenant_id,id),
            CONSTRAINT ux_invitation_email_envelopes_generation UNIQUE (tenant_id,invitation_id,generation_id),
            CONSTRAINT fk_invitation_email_envelopes_job FOREIGN KEY (tenant_id,job_id) REFERENCES public.background_jobs(tenant_id,id),
            CONSTRAINT ck_invitation_email_envelopes_shape CHECK ((
                uuid_extract_version(id)=7 AND uuid_extract_version(invitation_id)=7 AND uuid_extract_version(generation_id)=7
                AND tenant_id NOT IN ('00000000-0000-0000-0000-000000000000'::uuid,'00000000-0000-7000-8000-000000000002'::uuid)
                AND (organization_id IS NULL OR organization_id <> '00000000-0000-0000-0000-000000000000'::uuid)
                AND format_version=1 AND key_version COLLATE "C" ~ '^[A-Za-z0-9_-]{1,32}$'
                AND octet_length(nonce)=12 AND octet_length(tag)=16 AND octet_length(ciphertext) BETWEEN 1 AND 32768
                AND isfinite(created_at)) IS TRUE)
        );
        CREATE INDEX ix_invitation_email_envelopes_scope ON public.invitation_email_envelopes(tenant_id,organization_id);
        CREATE TABLE public.invitation_email_attempt_receipts (
            tenant_id uuid NOT NULL, organization_id uuid NULL, envelope_id uuid NOT NULL, job_id uuid NOT NULL,
            attempt_id uuid NOT NULL, operation_id uuid NOT NULL, plan_fingerprint text NOT NULL,
            claim_id uuid NOT NULL, admission_token uuid NOT NULL, original_deadline timestamptz NOT NULL,
            state text NOT NULL, receipt_id uuid NULL, evidence_id uuid NULL,
            created_at timestamptz NOT NULL DEFAULT clock_timestamp(), completed_at timestamptz NULL,
            CONSTRAINT pk_invitation_email_attempt_receipts PRIMARY KEY (tenant_id,envelope_id,attempt_id),
            CONSTRAINT ux_invitation_email_attempt_receipts_claim UNIQUE (claim_id),
            CONSTRAINT ux_invitation_email_attempt_receipts_receipt UNIQUE (receipt_id),
            CONSTRAINT fk_invitation_email_attempt_receipts_envelope FOREIGN KEY (tenant_id,envelope_id) REFERENCES public.invitation_email_envelopes(tenant_id,id),
            CONSTRAINT fk_invitation_email_attempt_receipts_attempt FOREIGN KEY (tenant_id,job_id,attempt_id) REFERENCES public.background_job_attempts(tenant_id,job_id,id),
            CONSTRAINT ck_invitation_email_attempt_receipts_shape CHECK ((
                operation_id <> '00000000-0000-0000-0000-000000000000'::uuid AND uuid_extract_version(claim_id)=7
                AND admission_token <> '00000000-0000-0000-0000-000000000000'::uuid
                AND plan_fingerprint COLLATE "C" ~ '^[0-9a-f]{64}$' AND isfinite(original_deadline) AND isfinite(created_at)
                AND ((state='sending' AND receipt_id IS NULL AND evidence_id IS NULL AND completed_at IS NULL)
                  OR (state IN ('accepted','temporary_rejected','permanent_rejected','cancelled_before_send')
                    AND uuid_extract_version(receipt_id)=7 AND uuid_extract_version(evidence_id)=7
                    AND completed_at IS NOT NULL AND isfinite(completed_at) AND completed_at>=created_at))) IS TRUE)
        );
        CREATE INDEX ix_invitation_email_attempt_receipts_job ON public.invitation_email_attempt_receipts(tenant_id,job_id,attempt_id);
        CREATE INDEX ix_invitation_email_attempt_receipts_scope ON public.invitation_email_attempt_receipts(tenant_id,organization_id);
        ALTER TABLE public.invitation_email_envelopes ENABLE ROW LEVEL SECURITY;
        ALTER TABLE public.invitation_email_envelopes FORCE ROW LEVEL SECURITY;
        ALTER TABLE public.invitation_email_attempt_receipts ENABLE ROW LEVEL SECURITY;
        ALTER TABLE public.invitation_email_attempt_receipts FORCE ROW LEVEL SECURITY;
        CREATE POLICY invitation_email_envelopes_isolation ON public.invitation_email_envelopes
          USING (current_user='learnstack_migration' OR (current_user='learnstack_app'
            AND tenant_id=NULLIF(current_setting('app.tenant_id',true),'')::uuid
            AND organization_id IS NOT DISTINCT FROM NULLIF(current_setting('app.organization_id',true),'')::uuid))
          WITH CHECK (current_user='learnstack_app' AND tenant_id=NULLIF(current_setting('app.tenant_id',true),'')::uuid
            AND organization_id IS NOT DISTINCT FROM NULLIF(current_setting('app.organization_id',true),'')::uuid);
        CREATE POLICY invitation_email_attempt_receipts_isolation ON public.invitation_email_attempt_receipts
          USING (current_user='learnstack_migration') WITH CHECK (current_user='learnstack_migration');
        REVOKE ALL ON public.invitation_email_envelopes,public.invitation_email_attempt_receipts FROM PUBLIC,learnstack_app,learnstack_platform,learnstack_outbox_admin;
        GRANT INSERT(id,tenant_id,organization_id,invitation_id,generation_id,job_id,format_version,key_version,nonce,ciphertext,tag)
          ON public.invitation_email_envelopes TO learnstack_app;
        ALTER POLICY background_jobs_isolation ON public.background_jobs USING (
          (current_user='learnstack_migration' AND definition_key IN ('identity.invitation_email_tenant.v1','identity.invitation_email_organization.v1')
            AND profile='external' AND capacity_group='identity.invitation_email' AND queue='external')
          OR (tenant_id=NULLIF(current_setting('app.tenant_id',true),'')::uuid AND (organization_id IS NULL
            OR organization_id=NULLIF(current_setting('app.organization_id',true),'')::uuid OR current_setting('app.scope',true)='tenant')));
        -- Row-locking SELECT also consumes UPDATE USING. Preserve the original WITH CHECK:
        -- the owner can lock this exact metadata, but still cannot UPDATE its job row.
        ALTER POLICY background_jobs_org_write_guard ON public.background_jobs USING (
          (current_user='learnstack_migration' AND definition_key IN ('identity.invitation_email_tenant.v1','identity.invitation_email_organization.v1')
            AND profile='external' AND capacity_group='identity.invitation_email' AND queue='external')
          OR ((organization_id IS NULL AND NULLIF(current_setting('app.organization_id',true),'') IS NULL)
            OR organization_id=NULLIF(current_setting('app.organization_id',true),'')::uuid));
        ALTER POLICY background_job_attempts_isolation ON public.background_job_attempts USING (
          (current_user='learnstack_migration' AND EXISTS (SELECT 1 FROM public.background_jobs j
            WHERE j.id=job_id AND j.tenant_id=background_job_attempts.tenant_id
              AND j.definition_key IN ('identity.invitation_email_tenant.v1','identity.invitation_email_organization.v1')
              AND j.profile='external' AND j.capacity_group='identity.invitation_email' AND j.queue='external'))
          OR (tenant_id=NULLIF(current_setting('app.tenant_id',true),'')::uuid AND (organization_id IS NULL
            OR organization_id=NULLIF(current_setting('app.organization_id',true),'')::uuid OR current_setting('app.scope',true)='tenant')));
        CREATE FUNCTION public.fn_invitation_email_envelope_guard() RETURNS trigger
          LANGUAGE plpgsql SET search_path=pg_catalog AS $fn$
        DECLARE j record;
        BEGIN
          IF TG_OP<>'INSERT' THEN RAISE EXCEPTION 'Provider envelope mutation refused.' USING ERRCODE='23514'; END IF;
          SELECT * INTO j FROM public.background_jobs WHERE tenant_id=NEW.tenant_id AND id=NEW.job_id;
          IF NOT FOUND OR j.organization_id IS DISTINCT FROM NEW.organization_id OR j.profile<>'external'
             OR j.queue<>'external' OR j.capacity_group<>'identity.invitation_email'
             OR j.definition_key IS DISTINCT FROM (CASE WHEN NEW.organization_id IS NULL THEN 'identity.invitation_email_tenant.v1' ELSE 'identity.invitation_email_organization.v1' END)
             OR j.parameters::jsonb IS DISTINCT FROM jsonb_build_object('InvitationId',NEW.invitation_id,'GenerationId',NEW.generation_id,'SpoolId',NEW.id)
          THEN RAISE EXCEPTION 'Provider envelope binding refused.' USING ERRCODE='23514'; END IF;
          RETURN NEW;
        END; $fn$;
        REVOKE ALL ON FUNCTION public.fn_invitation_email_envelope_guard() FROM PUBLIC;
        CREATE TRIGGER tg_invitation_email_envelopes_guard BEFORE INSERT OR UPDATE OR DELETE ON public.invitation_email_envelopes
          FOR EACH ROW EXECUTE FUNCTION public.fn_invitation_email_envelope_guard();
        CREATE FUNCTION public.fn_invitation_email_attempt_guard() RETURNS trigger
          LANGUAGE plpgsql SET search_path=pg_catalog AS $fn$
        BEGIN
          IF TG_OP='DELETE' OR (TG_OP='UPDATE' AND (OLD.state<>'sending'
            OR ROW(NEW.tenant_id,NEW.organization_id,NEW.envelope_id,NEW.job_id,NEW.attempt_id,NEW.operation_id,NEW.plan_fingerprint,
              NEW.claim_id,NEW.admission_token,NEW.original_deadline,NEW.created_at)
              IS DISTINCT FROM ROW(OLD.tenant_id,OLD.organization_id,OLD.envelope_id,OLD.job_id,OLD.attempt_id,OLD.operation_id,OLD.plan_fingerprint,
              OLD.claim_id,OLD.admission_token,OLD.original_deadline,OLD.created_at)))
          THEN RAISE EXCEPTION 'Provider observation mutation refused.' USING ERRCODE='23514'; END IF;
          RETURN NEW;
        END; $fn$;
        REVOKE ALL ON FUNCTION public.fn_invitation_email_attempt_guard() FROM PUBLIC;
        CREATE TRIGGER tg_invitation_email_attempt_receipts_guard BEFORE UPDATE OR DELETE ON public.invitation_email_attempt_receipts
          FOR EACH ROW EXECUTE FUNCTION public.fn_invitation_email_attempt_guard();
        CREATE FUNCTION public.fn_invitation_smtp_claim_or_read(
          p_tenant uuid,p_org uuid,p_envelope uuid,p_invitation uuid,p_generation uuid,p_job uuid,p_attempt uuid,
          p_operation uuid,p_fingerprint text,p_admission uuid,p_deadline timestamptz,p_recovery uuid,p_wake bigint,p_until timestamptz,
          p_claim uuid,p_receipt uuid,p_evidence uuid,p_verify boolean)
        RETURNS TABLE(provider_state text,claim_id uuid,receipt_id uuid,evidence_id uuid,key_version text,nonce bytea,ciphertext bytea,tag bytea)
        LANGUAGE plpgsql SECURITY DEFINER VOLATILE SET search_path=pg_catalog AS $fn$
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
        END; $fn$;
        REVOKE ALL ON FUNCTION public.fn_invitation_smtp_claim_or_read(uuid,uuid,uuid,uuid,uuid,uuid,uuid,uuid,text,uuid,timestamptz,uuid,bigint,timestamptz,uuid,uuid,uuid,boolean)
          FROM PUBLIC,learnstack_platform,learnstack_outbox_admin;
        GRANT EXECUTE ON FUNCTION public.fn_invitation_smtp_claim_or_read(uuid,uuid,uuid,uuid,uuid,uuid,uuid,uuid,text,uuid,timestamptz,uuid,bigint,timestamptz,uuid,uuid,uuid,boolean) TO learnstack_app;
        CREATE FUNCTION public.fn_invitation_smtp_complete_attempt(
          p_tenant uuid,p_org uuid,p_envelope uuid,p_invitation uuid,p_generation uuid,p_job uuid,p_attempt uuid,
          p_operation uuid,p_fingerprint text,p_admission uuid,p_deadline timestamptz,p_claim uuid,p_state text,p_receipt uuid,p_evidence uuid,p_verify boolean)
        RETURNS TABLE(provider_state text,claim_id uuid,receipt_id uuid,evidence_id uuid)
        LANGUAGE plpgsql SECURITY DEFINER VOLATILE SET search_path=pg_catalog AS $fn$
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
        END; $fn$;
        REVOKE ALL ON FUNCTION public.fn_invitation_smtp_complete_attempt(uuid,uuid,uuid,uuid,uuid,uuid,uuid,uuid,text,uuid,timestamptz,uuid,text,uuid,uuid,boolean)
          FROM PUBLIC,learnstack_platform,learnstack_outbox_admin;
        GRANT EXECUTE ON FUNCTION public.fn_invitation_smtp_complete_attempt(uuid,uuid,uuid,uuid,uuid,uuid,uuid,uuid,text,uuid,timestamptz,uuid,text,uuid,uuid,boolean) TO learnstack_app;
        """;
}
