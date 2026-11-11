namespace LearnStack.Modules.Identity.Infrastructure.Persistence;

/// <summary>Preserve accepted receipt reconciliation without reopening expired intake.</summary>
internal static class ProviderEventReceiptReplayDdl
{
    internal const string ReplaceAcceptance = """
        CREATE OR REPLACE FUNCTION public.fn_identity_provider_event_accept(
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
            THEN RAISE EXCEPTION 'Provider source refused.' USING ERRCODE='23514'; END IF;
            fact_hash := sha256(uuid_send(p_authority)||int4send(octet_length(convert_to(p_realm,'UTF8')))
                ||convert_to(p_realm,'UTF8')||uuid_send(p_event)||timestamptz_send(p_occurred)
                ||int4send(octet_length(convert_to(p_kind,'UTF8')))||convert_to(p_kind,'UTF8')
                ||COALESCE(p_subject,decode('','hex')));
            -- Reconciliation is not new source admission. A retained committed
            -- fingerprint survives source expiry and a legitimate shorter policy.
            SELECT * INTO prior FROM public.identity_provider_event_receipts r
                WHERE r.authority_id=p_authority AND r.realm_id=p_realm AND r.provider_event_id=p_event FOR UPDATE;
            IF FOUND AND prior.accepted_at IS NOT NULL AND prior.fingerprint=fact_hash THEN
                RETURN QUERY SELECT prior.id,prior.disposition,true,prior.target_count; RETURN;
            END IF;
            IF p_occurred<cfg.activation_lower OR p_occurred<clock_timestamp()-make_interval(secs=>cfg.retention_seconds)
                OR p_occurred>clock_timestamp() THEN
                RAISE EXCEPTION 'Provider source refused.' USING ERRCODE='23514';
            END IF;
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
}
