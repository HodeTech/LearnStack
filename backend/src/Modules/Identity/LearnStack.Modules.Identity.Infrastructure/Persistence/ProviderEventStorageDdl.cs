using Microsoft.EntityFrameworkCore.Migrations;

namespace LearnStack.Modules.Identity.Infrastructure.Persistence;

/// <summary>ADR-0097's confined machine storage; no provider payload or ordinary global directory.</summary>
internal static class ProviderEventStorageDdl
{
    internal static void Install(MigrationBuilder migration)
    {
        migration.Sql(Storage);
        migration.Sql(Accept);
        migration.Sql(Claim);
        migration.Sql(Retire);
        migration.Sql(TargetGuard);
    }

    private const string Storage = """
        CREATE TABLE public.identity_provider_event_scan (
            id integer PRIMARY KEY CHECK (id=1),
            authority_id uuid NOT NULL REFERENCES public.identity_authority(authority_id),
            realm_id varchar(255) NOT NULL CHECK (length(realm_id)>0),
            activation_lower timestamptz NOT NULL CHECK (isfinite(activation_lower)),
            retention_seconds integer NOT NULL CHECK (retention_seconds BETWEEN 604800 AND 2592000),
            cursor_from timestamptz, cursor_to timestamptz, cursor_offset integer NOT NULL DEFAULT 0 CHECK (cursor_offset>=0),
            last_attempted_at timestamptz, last_traversed_at timestamptz, last_completed_at timestamptz,
            historical_gap boolean NOT NULL DEFAULT false, earliest_gap_at timestamptz,
            CHECK ((cursor_from IS NULL)=(cursor_to IS NULL)),
            CHECK (cursor_from IS NULL OR (isfinite(cursor_from) AND isfinite(cursor_to) AND cursor_from<=cursor_to)),
            CHECK (historical_gap=(earliest_gap_at IS NOT NULL))
        );
        CREATE INDEX ix_identity_provider_event_scan_authority ON public.identity_provider_event_scan(authority_id);
        CREATE TABLE public.identity_provider_event_receipts (
            id uuid PRIMARY KEY CHECK ((uuid_extract_version(id)=7) IS TRUE),
            authority_id uuid NOT NULL, realm_id varchar(255) NOT NULL, provider_event_id uuid NOT NULL,
            source_occurred_at timestamptz NOT NULL CHECK (isfinite(source_occurred_at)),
            source_kind varchar(80) NOT NULL, subject_key bytea CHECK (subject_key IS NULL OR octet_length(subject_key)=32),
            fingerprint bytea NOT NULL CHECK (octet_length(fingerprint)=32), affected_user_id uuid,
            received_at timestamptz NOT NULL DEFAULT clock_timestamp(), accepted_at timestamptz,
            disposition text NOT NULL CHECK (disposition IN ('pending','fanout_exceeded','subject_absent','subject_unbound','subject_closed','no_recipient','routed')),
            target_count integer NOT NULL DEFAULT 0 CHECK (target_count BETWEEN 0 AND 256),
            UNIQUE(authority_id,realm_id,provider_event_id),
            CHECK ((accepted_at IS NULL)=(disposition IN ('pending','fanout_exceeded')))
        );
        CREATE INDEX ix_identity_provider_event_receipts_subject ON public.identity_provider_event_receipts(subject_key,id);
        CREATE INDEX ix_identity_provider_event_receipts_actor ON public.identity_provider_event_receipts(affected_user_id,id);
        CREATE INDEX ix_identity_provider_event_receipts_retire ON public.identity_provider_event_receipts(received_at,id);
        CREATE TABLE public.identity_provider_event_problems (
            problem_key varchar(512) PRIMARY KEY CHECK (length(problem_key)>0),
            authority_id uuid NOT NULL, realm_id varchar(255) NOT NULL,
            provider_event_id uuid, fingerprint bytea CHECK (fingerprint IS NULL OR octet_length(fingerprint)=32),
            range_from timestamptz NOT NULL, range_to timestamptz NOT NULL,
            source_offset integer CHECK (source_offset IS NULL OR source_offset>=0),
            reason text NOT NULL CHECK (reason IN ('fanout_exceeded','source_collision','malformed','oversized','out_of_window')),
            first_seen_at timestamptz NOT NULL DEFAULT clock_timestamp(),
            last_seen_at timestamptz NOT NULL DEFAULT clock_timestamp(),
            next_retry_at timestamptz NOT NULL DEFAULT clock_timestamp(),
            attempts integer NOT NULL DEFAULT 1 CHECK (attempts>0),
            resolved_at timestamptz, irrecoverable boolean NOT NULL DEFAULT false,
            CHECK (isfinite(range_from) AND isfinite(range_to) AND range_from<=range_to)
        );
        CREATE INDEX ix_identity_provider_event_problems_retry ON public.identity_provider_event_problems(next_retry_at,problem_key)
            WHERE resolved_at IS NULL AND NOT irrecoverable;
        CREATE INDEX ix_identity_provider_event_problems_event ON public.identity_provider_event_problems(authority_id,realm_id,provider_event_id);
        CREATE INDEX ix_identity_provider_event_problems_retire ON public.identity_provider_event_problems(first_seen_at,problem_key);
        CREATE INDEX ix_identity_provider_event_problems_gap ON public.identity_provider_event_problems(range_from) WHERE irrecoverable;

        ALTER TABLE public.identity_provider_event_receipts ENABLE ROW LEVEL SECURITY;
        ALTER TABLE public.identity_provider_event_receipts FORCE ROW LEVEL SECURITY;
        CREATE POLICY identity_provider_event_receipts_isolation ON public.identity_provider_event_receipts
            USING (current_user IN ('learnstack_migration','learnstack_identity_ingress'))
            WITH CHECK (current_user='learnstack_migration');
        ALTER TABLE public.identity_provider_event_scan ENABLE ROW LEVEL SECURITY;
        ALTER TABLE public.identity_provider_event_scan FORCE ROW LEVEL SECURITY;
        CREATE POLICY identity_provider_event_scan_isolation ON public.identity_provider_event_scan
            USING (current_user IN ('learnstack_migration','learnstack_identity_ingress'))
            WITH CHECK (current_user IN ('learnstack_migration','learnstack_identity_ingress'));
        ALTER TABLE public.identity_provider_event_problems ENABLE ROW LEVEL SECURITY;
        ALTER TABLE public.identity_provider_event_problems FORCE ROW LEVEL SECURITY;
        CREATE POLICY identity_provider_event_problems_isolation ON public.identity_provider_event_problems
            USING (current_user IN ('learnstack_migration','learnstack_identity_ingress'))
            WITH CHECK (current_user IN ('learnstack_migration','learnstack_identity_ingress'));
        REVOKE ALL ON public.identity_provider_event_receipts,public.identity_provider_event_scan,public.identity_provider_event_problems
            FROM PUBLIC,learnstack_app,learnstack_platform,learnstack_outbox_admin,learnstack_identity_ingress;
        GRANT SELECT ON public.identity_provider_event_receipts,public.identity_provider_event_scan,public.identity_provider_event_problems
            TO learnstack_identity_ingress;
        GRANT INSERT ON public.identity_provider_event_scan,public.identity_provider_event_problems TO learnstack_identity_ingress;
        GRANT UPDATE (retention_seconds,cursor_from,cursor_to,cursor_offset,last_attempted_at,last_traversed_at,last_completed_at,historical_gap,earliest_gap_at)
            ON public.identity_provider_event_scan TO learnstack_identity_ingress;
        GRANT UPDATE (last_seen_at,next_retry_at,attempts,resolved_at,irrecoverable)
            ON public.identity_provider_event_problems TO learnstack_identity_ingress;

        ALTER TABLE public.identity_provider_event_targets ENABLE ROW LEVEL SECURITY;
        ALTER TABLE public.identity_provider_event_targets FORCE ROW LEVEL SECURITY;
        CREATE POLICY identity_provider_event_targets_isolation ON public.identity_provider_event_targets
            USING (current_user='learnstack_migration'
                OR (current_user='learnstack_app' AND tenant_id=NULLIF(current_setting('app.tenant_id',true),'')::uuid))
            WITH CHECK (current_user='learnstack_migration'
                OR (current_user='learnstack_app' AND tenant_id=NULLIF(current_setting('app.tenant_id',true),'')::uuid));
        REVOKE ALL ON public.identity_provider_event_targets
            FROM PUBLIC,learnstack_app,learnstack_platform,learnstack_outbox_admin,learnstack_identity_ingress;
        GRANT SELECT ON public.identity_provider_event_targets TO learnstack_app;
        GRANT UPDATE (enqueued_at) ON public.identity_provider_event_targets TO learnstack_app;

        ALTER POLICY memberships_isolation ON public.memberships
            USING ((tenant_id=NULLIF(current_setting('app.tenant_id',true),'')::uuid
                AND (organization_id IS NULL OR organization_id=NULLIF(current_setting('app.organization_id',true),'')::uuid
                    OR NULLIF(current_setting('app.scope',true),'')='tenant'))
                OR (current_user='learnstack_migration' AND session_user='learnstack_identity_ingress'
                    AND status IN ('Active','Suspended') AND deleted_at IS NULL AND deleted_by IS NULL)
                OR (current_user='learnstack_migration' AND status='Active' AND deleted_at IS NULL
                    AND EXISTS (SELECT FROM public.users u WHERE u.id=public.memberships.user_id
                        AND u.authority_id::text=NULLIF(current_setting('app.identity_authority_id',true),'')
                        AND encode(u.subject_key,'hex')=NULLIF(current_setting('app.identity_subject_key',true),'')
                        AND u.status='Active')));
        """;

    private const string Accept = """
        CREATE FUNCTION public.fn_identity_provider_event_accept(
            p_authority uuid,p_realm text,p_event uuid,p_occurred timestamptz,p_kind text,p_subject bytea)
        RETURNS TABLE (receipt_id uuid,disposition text,accepted boolean,target_count integer)
        LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $accept$
        DECLARE cfg public.identity_provider_event_scan%ROWTYPE;
            prior public.identity_provider_event_receipts%ROWTYPE;
            selected_user uuid; selected_status text; recipients uuid[]; fact_hash bytea;
            result_state text; recipient_count integer; issue_key text;
        BEGIN
            IF session_user <> 'learnstack_identity_ingress' OR current_setting('transaction_isolation') <> 'read committed'
                OR p_authority IS NULL OR p_event IS NULL OR p_event='00000000-0000-0000-0000-000000000000'::uuid
                OR p_realm IS NULL OR length(p_realm) NOT BETWEEN 1 AND 255
                OR p_occurred IS NULL OR NOT isfinite(p_occurred)
                OR p_subject IS NOT NULL AND octet_length(p_subject)<>32
                OR p_kind IS NULL OR p_kind NOT IN ('LOGIN','LOGIN_ERROR','LOGOUT','LOGOUT_ERROR',
                    'SEND_RESET_PASSWORD','SEND_RESET_PASSWORD_ERROR','RESET_PASSWORD','RESET_PASSWORD_ERROR',
                    'UPDATE_CREDENTIAL','UPDATE_CREDENTIAL_ERROR','REMOVE_CREDENTIAL','REMOVE_CREDENTIAL_ERROR',
                    'UPDATE_PASSWORD','UPDATE_PASSWORD_ERROR','UPDATE_TOTP','UPDATE_TOTP_ERROR','REMOVE_TOTP','REMOVE_TOTP_ERROR',
                    'USER_DISABLED_BY_TEMPORARY_LOCKOUT','USER_DISABLED_BY_PERMANENT_LOCKOUT')
            THEN RAISE EXCEPTION 'Provider fact refused.' USING ERRCODE='23514'; END IF;
            SELECT * INTO cfg FROM public.identity_provider_event_scan WHERE id=1;
            IF NOT FOUND OR cfg.authority_id IS DISTINCT FROM p_authority OR cfg.realm_id IS DISTINCT FROM p_realm
                OR NOT EXISTS (SELECT FROM public.identity_authority a WHERE a.id=1 AND a.authority_id=p_authority)
                OR p_occurred<cfg.activation_lower OR p_occurred<clock_timestamp()-make_interval(secs=>cfg.retention_seconds)
                OR p_occurred>clock_timestamp()
            THEN RAISE EXCEPTION 'Provider source refused.' USING ERRCODE='23514'; END IF;
            fact_hash := sha256(uuid_send(p_authority)||int4send(octet_length(convert_to(p_realm,'UTF8')))
                ||convert_to(p_realm,'UTF8')||uuid_send(p_event)||timestamptz_send(p_occurred)
                ||int4send(octet_length(convert_to(p_kind,'UTF8')))||convert_to(p_kind,'UTF8')
                ||COALESCE(p_subject,decode('','hex')));
            INSERT INTO public.identity_provider_event_receipts(id,authority_id,realm_id,provider_event_id,
                source_occurred_at,source_kind,subject_key,fingerprint,disposition)
                VALUES(uuidv7(),p_authority,p_realm,p_event,p_occurred,p_kind,p_subject,fact_hash,'pending')
                ON CONFLICT(authority_id,realm_id,provider_event_id) DO NOTHING;
            SELECT * INTO prior FROM public.identity_provider_event_receipts r
                WHERE r.authority_id=p_authority AND r.realm_id=p_realm AND r.provider_event_id=p_event FOR UPDATE;
            IF NOT FOUND THEN RAISE EXCEPTION 'Provider acceptance requires reconciliation.' USING ERRCODE='40001'; END IF;
            issue_key := 'event:'||p_authority::text||':'||p_realm||':'||p_event::text;
            IF prior.fingerprint IS DISTINCT FROM fact_hash THEN
                INSERT INTO public.identity_provider_event_problems(problem_key,authority_id,realm_id,provider_event_id,
                    fingerprint,range_from,range_to,reason)
                    VALUES('collision:'||issue_key,p_authority,p_realm,p_event,fact_hash,p_occurred,p_occurred,'source_collision')
                    ON CONFLICT(problem_key) DO UPDATE SET last_seen_at=clock_timestamp(),attempts=identity_provider_event_problems.attempts+1;
                RETURN QUERY SELECT prior.id,'source_collision'::text,false,0; RETURN;
            END IF;
            IF prior.accepted_at IS NOT NULL THEN
                RETURN QUERY SELECT prior.id,prior.disposition,true,prior.target_count; RETURN;
            END IF;
            IF p_subject IS NULL THEN result_state:='subject_absent';
            ELSE
                SELECT u.id INTO selected_user FROM public.users u WHERE u.authority_id=p_authority AND u.subject_key=p_subject;
                IF selected_user IS NULL THEN result_state:='subject_unbound';
                ELSE
                    PERFORM pg_advisory_xact_lock_shared(public.fn_identity_contextual_lock_key('user',selected_user));
                    SELECT u.status INTO selected_status FROM public.users u WHERE u.id=selected_user AND u.authority_id=p_authority AND u.subject_key=p_subject;
                    IF selected_status IS DISTINCT FROM 'Active' THEN result_state:='subject_closed';
                    ELSE
                        SELECT array_agg(t.tenant_id ORDER BY t.tenant_id) INTO recipients FROM
                            (SELECT DISTINCT m.tenant_id FROM public.memberships m WHERE m.user_id=selected_user
                                AND m.status IN ('Active','Suspended') AND m.deleted_at IS NULL AND m.deleted_by IS NULL
                                ORDER BY m.tenant_id LIMIT 257) t;
                        recipient_count:=COALESCE(cardinality(recipients),0);
                        result_state:=CASE WHEN recipient_count>256 THEN 'fanout_exceeded'
                            WHEN recipient_count=0 THEN 'no_recipient' ELSE 'routed' END;
                    END IF;
                END IF;
            END IF;
            IF result_state='fanout_exceeded' THEN
                UPDATE public.identity_provider_event_receipts r SET disposition=result_state,affected_user_id=selected_user WHERE r.id=prior.id;
                INSERT INTO public.identity_provider_event_problems(problem_key,authority_id,realm_id,provider_event_id,
                    fingerprint,range_from,range_to,reason)
                    VALUES(issue_key,p_authority,p_realm,p_event,fact_hash,p_occurred,p_occurred,'fanout_exceeded')
                    ON CONFLICT(problem_key) DO UPDATE SET last_seen_at=clock_timestamp(),attempts=identity_provider_event_problems.attempts+1,
                        next_retry_at=clock_timestamp()+interval '5 minutes';
                RETURN QUERY SELECT prior.id,result_state,false,0; RETURN;
            END IF;
            IF result_state='routed' THEN
                INSERT INTO public.identity_provider_event_targets(id,tenant_id,receipt_id,event_id,authority_id,realm_id,
                    provider_event_id,affected_user_id,subject_key,source_occurred_at,source_kind,received_at,attempts)
                    SELECT uuidv7(),tenant,prior.id,uuidv7(),p_authority,p_realm,p_event,selected_user,p_subject,
                        p_occurred,p_kind,prior.received_at,0 FROM unnest(recipients) tenant;
            END IF;
            UPDATE public.identity_provider_event_receipts r SET disposition=result_state,affected_user_id=selected_user,
                accepted_at=clock_timestamp(),target_count=COALESCE(cardinality(recipients),0) WHERE r.id=prior.id;
            UPDATE public.identity_provider_event_problems SET resolved_at=clock_timestamp() WHERE problem_key=issue_key;
            RETURN QUERY SELECT prior.id,result_state,true,COALESCE(cardinality(recipients),0);
        END $accept$;
        REVOKE ALL ON FUNCTION public.fn_identity_provider_event_accept(uuid,text,uuid,timestamptz,text,bytea) FROM PUBLIC;
        GRANT EXECUTE ON FUNCTION public.fn_identity_provider_event_accept(uuid,text,uuid,timestamptz,text,bytea) TO learnstack_identity_ingress;
        """;

    private const string Claim = """
        CREATE FUNCTION public.fn_identity_provider_event_claim(p_token uuid)
        RETURNS TABLE (target_id uuid,event_id uuid,tenant_id uuid,claim_token uuid,claim_until timestamptz)
        LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $claim$
        BEGIN
            IF session_user<>'learnstack_identity_ingress' OR p_token IS NULL
                OR p_token='00000000-0000-0000-0000-000000000000'::uuid
                OR current_setting('transaction_isolation')<>'read committed'
            THEN RAISE EXCEPTION 'Provider claim refused.' USING ERRCODE='23514'; END IF;
            RETURN QUERY WITH candidate AS (
                SELECT t.id FROM public.identity_provider_event_targets t WHERE t.enqueued_at IS NULL
                    AND (t.claim_until IS NULL OR t.claim_until<=clock_timestamp())
                    ORDER BY t.received_at,t.id FOR UPDATE SKIP LOCKED LIMIT 1)
            UPDATE public.identity_provider_event_targets t SET claim_token=p_token,
                claim_until=clock_timestamp()+interval '30 seconds',attempts=t.attempts+1
                FROM candidate c WHERE t.id=c.id
                RETURNING t.id,t.event_id,t.tenant_id,t.claim_token,t.claim_until;
        END $claim$;
        REVOKE ALL ON FUNCTION public.fn_identity_provider_event_claim(uuid) FROM PUBLIC;
        GRANT EXECUTE ON FUNCTION public.fn_identity_provider_event_claim(uuid) TO learnstack_identity_ingress;
        """;

    private const string Retire = """
        CREATE FUNCTION public.fn_identity_provider_event_retire() RETURNS integer
        LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $retire$
        DECLARE removed integer; previous_gap timestamptz;
        BEGIN
            IF session_user<>'learnstack_identity_ingress' THEN RAISE EXCEPTION 'Provider retirement refused.' USING ERRCODE='23514'; END IF;
            WITH expired AS (SELECT p.problem_key FROM public.identity_provider_event_problems p
                WHERE p.resolved_at IS NULL AND NOT p.irrecoverable AND p.range_to < clock_timestamp()-
                    make_interval(secs=>(SELECT s.retention_seconds FROM public.identity_provider_event_scan s WHERE s.id=1))
                ORDER BY p.first_seen_at,p.problem_key LIMIT 100)
            UPDATE public.identity_provider_event_problems p SET irrecoverable=true FROM expired e WHERE p.problem_key=e.problem_key;
            SELECT min(p.range_from) INTO previous_gap FROM public.identity_provider_event_problems p
                WHERE p.irrecoverable OR (p.resolved_at IS NULL AND p.first_seen_at<clock_timestamp()-interval '32 days');
            IF previous_gap IS NOT NULL THEN
                UPDATE public.identity_provider_event_scan SET historical_gap=true,
                    earliest_gap_at=LEAST(earliest_gap_at,previous_gap) WHERE id=1;
            END IF;
            WITH expired AS (SELECT r.id FROM public.identity_provider_event_receipts r
                WHERE r.received_at<clock_timestamp()-interval '32 days' ORDER BY r.received_at,r.id LIMIT 100)
            DELETE FROM public.identity_provider_event_receipts r USING expired e WHERE r.id=e.id;
            GET DIAGNOSTICS removed=ROW_COUNT;
            WITH expired AS (SELECT p.problem_key FROM public.identity_provider_event_problems p
                WHERE p.first_seen_at<clock_timestamp()-interval '32 days' ORDER BY p.first_seen_at,p.problem_key LIMIT 100)
            DELETE FROM public.identity_provider_event_problems p USING expired e WHERE p.problem_key=e.problem_key;
            RETURN removed;
        END $retire$;
        REVOKE ALL ON FUNCTION public.fn_identity_provider_event_retire() FROM PUBLIC;
        GRANT EXECUTE ON FUNCTION public.fn_identity_provider_event_retire() TO learnstack_identity_ingress;
        """;

    private const string TargetGuard = """
        CREATE FUNCTION public.fn_identity_provider_target_guard() RETURNS trigger
        LANGUAGE plpgsql VOLATILE SECURITY INVOKER SET search_path=pg_catalog AS $guard$
        BEGIN
            IF TG_OP<>'UPDATE' OR TG_TABLE_SCHEMA<>'public' OR TG_TABLE_NAME<>'identity_provider_event_targets'
                THEN RAISE EXCEPTION 'Provider target transition refused.' USING ERRCODE='23514'; END IF;
            IF TG_WHEN='BEFORE' THEN
                IF ROW(NEW.id,NEW.tenant_id,NEW.receipt_id,NEW.event_id,NEW.authority_id,NEW.realm_id,NEW.provider_event_id,
                    NEW.affected_user_id,NEW.subject_key,NEW.source_occurred_at,NEW.source_kind,NEW.received_at)
                    IS DISTINCT FROM ROW(OLD.id,OLD.tenant_id,OLD.receipt_id,OLD.event_id,OLD.authority_id,OLD.realm_id,OLD.provider_event_id,
                    OLD.affected_user_id,OLD.subject_key,OLD.source_occurred_at,OLD.source_kind,OLD.received_at)
                    OR OLD.enqueued_at IS NOT NULL THEN
                    RAISE EXCEPTION 'Provider target is immutable.' USING ERRCODE='23514'; END IF;
                IF current_user='learnstack_migration' AND session_user='learnstack_identity_ingress' THEN
                    IF NEW.enqueued_at IS DISTINCT FROM OLD.enqueued_at OR NEW.claim_token IS NULL
                        OR NEW.claim_token IS NOT DISTINCT FROM OLD.claim_token OR NEW.claim_until<=clock_timestamp()
                        OR NEW.attempts<>OLD.attempts+1 THEN
                        RAISE EXCEPTION 'Provider target claim refused.' USING ERRCODE='23514'; END IF;
                ELSIF current_user='learnstack_app' AND session_user='learnstack_app' THEN
                    IF NEW.enqueued_at IS NULL OR NEW.claim_token IS DISTINCT FROM OLD.claim_token
                        OR NEW.claim_until IS DISTINCT FROM OLD.claim_until OR NEW.attempts<>OLD.attempts
                        OR OLD.claim_until IS NULL OR OLD.claim_until<=clock_timestamp() THEN
                        RAISE EXCEPTION 'Provider target completion refused.' USING ERRCODE='23514'; END IF;
                ELSE RAISE EXCEPTION 'Provider target owner refused.' USING ERRCODE='23514'; END IF;
                RETURN NEW;
            END IF;
            IF NEW.enqueued_at IS NOT NULL AND NOT EXISTS (SELECT FROM public.outbox_messages o
                WHERE o.id=NEW.event_id AND o.tenant_id=NEW.tenant_id AND o.organization_id IS NULL
                    AND o.topic='learnstack.identity.user' AND o.payload->>'TargetId'=NEW.id::text
                    AND o.payload->>'EventId'=NEW.event_id::text AND o.payload->>'TenantId'=NEW.tenant_id::text
                    AND split_part(o.type,',',1)='LearnStack.Modules.Identity.Application.Contracts.IntegrationEvents.UserProviderEventObservedIntegrationEventV1'
                    AND trim(split_part(o.type,',',2))='LearnStack.Modules.Identity.Application.Contracts') THEN
                RAISE EXCEPTION 'Provider target requires its outbox.' USING ERRCODE='23514'; END IF;
            RETURN NULL;
        END $guard$;
        REVOKE ALL ON FUNCTION public.fn_identity_provider_target_guard() FROM PUBLIC;
        CREATE TRIGGER tg_provider_target_transition BEFORE UPDATE ON public.identity_provider_event_targets
            FOR EACH ROW EXECUTE FUNCTION public.fn_identity_provider_target_guard();
        CREATE CONSTRAINT TRIGGER tg_provider_target_outbox AFTER UPDATE ON public.identity_provider_event_targets
            DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.fn_identity_provider_target_guard();
        """;
}
