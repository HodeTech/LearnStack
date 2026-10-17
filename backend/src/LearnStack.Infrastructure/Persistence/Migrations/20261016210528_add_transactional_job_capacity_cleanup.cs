using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LearnStack.Infrastructure.Persistence.Migrations;

/// <summary>Bind positive transactional cleanup and exact reservation release without changing existing owner authority.</summary>
public partial class add_transactional_job_capacity_cleanup : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        ALTER TABLE background_job_attempts ADD COLUMN transactional_cleanup_at timestamptz NULL;
        ALTER TABLE background_job_attempts ADD CONSTRAINT ck_background_job_attempts_transactional_cleanup CHECK (
            transactional_cleanup_at IS NULL OR (stage='normal' AND isfinite(transactional_cleanup_at)
                AND transactional_cleanup_at>=admitted_at AND plan_fingerprint IS NULL AND operation_id IS NULL
                AND execute_intent_at IS NULL AND outcome IS NULL AND outcome_kind IS NULL AND outcome_at IS NULL
                AND quiescence_id IS NULL AND quiescence_kind IS NULL AND quiescent_at IS NULL AND no_start_at IS NULL));
        ALTER TABLE background_job_reservations DROP CONSTRAINT ck_background_job_reservations_release;
        ALTER TABLE background_job_reservations ADD CONSTRAINT ck_background_job_reservations_release CHECK (
            (released_at IS NULL AND release_reason IS NULL)
            OR (released_at IS NOT NULL AND release_reason IS NOT NULL AND release_reason IN ('completed','never_started','quiescent','transactional_rollback')
                AND isfinite(released_at) AND released_at>=reserved_at));

        CREATE OR REPLACE FUNCTION public.fn_background_job_attempts_guard() RETURNS trigger
        LANGUAGE plpgsql SECURITY INVOKER SET search_path = pg_catalog AS $$
        DECLARE job record;
        BEGIN
            SELECT j.organization_id, j.phase, j.profile, j.normal_attempts, j.finalize_attempts,
                   j.ownership_token, j.ownership_kind, j.ownership_until, j.current_attempt_id,
                   j.final_outcome_attempt_id, j.plan_fingerprint, j.operation_id, j.capacity_group,
                   j.attempt_deadline, j.processed_at, j.dead_lettered_at, j.terminal_reason
              INTO job FROM public.background_jobs j
              WHERE j.tenant_id = NEW.tenant_id AND j.id = NEW.job_id FOR KEY SHARE;
            IF NOT FOUND OR job.organization_id IS DISTINCT FROM NEW.organization_id THEN
                RAISE EXCEPTION 'attempt parent scope is unavailable or mismatched' USING ERRCODE = '23514';
            END IF;
            IF TG_OP = 'INSERT' THEN
                IF job.ownership_kind IS DISTINCT FROM 'routing' OR job.ownership_until <= clock_timestamp()
                   OR NEW.admission_token <> job.ownership_token
                   OR (NEW.stage = 'normal' AND (job.phase <> 'pending' OR NEW.ordinal <> job.normal_attempts + 1))
                   OR (NEW.stage = 'finalize' AND (job.phase <> 'finalize_pending' OR NEW.ordinal <> job.finalize_attempts + 1
                       OR NEW.final_outcome_attempt_id IS DISTINCT FROM job.final_outcome_attempt_id))
                   OR NEW.plan_fingerprint IS NOT NULL OR NEW.operation_id IS NOT NULL
                   OR NEW.execute_intent_at IS NOT NULL OR NEW.outcome IS NOT NULL
                   OR NEW.quiescent_at IS NOT NULL OR NEW.no_start_at IS NOT NULL
                   OR NEW.transactional_cleanup_at IS NOT NULL THEN
                    RAISE EXCEPTION 'attempt insert requires a fresh routing receipt' USING ERRCODE = '23514';
                END IF;
                NEW.admitted_at := clock_timestamp();
                IF (job.profile = 'transactional' OR NEW.stage = 'finalize')
                   AND NEW.deadline > NEW.admitted_at + interval '5 minutes' THEN
                    RAISE EXCEPTION 'short stage deadline exceeds its bound' USING ERRCODE = '23514';
                END IF;
                RETURN NEW;
            END IF;
            IF ROW(NEW.id, NEW.tenant_id, NEW.organization_id, NEW.job_id, NEW.stage, NEW.ordinal,
                   NEW.admission_token, NEW.admitted_at, NEW.deadline, NEW.final_outcome_attempt_id)
               IS DISTINCT FROM ROW(OLD.id, OLD.tenant_id, OLD.organization_id, OLD.job_id, OLD.stage, OLD.ordinal,
                   OLD.admission_token, OLD.admitted_at, OLD.deadline, OLD.final_outcome_attempt_id)
               OR (OLD.plan_fingerprint IS NOT NULL AND ROW(NEW.plan_fingerprint, NEW.operation_id)
                    IS DISTINCT FROM ROW(OLD.plan_fingerprint, OLD.operation_id))
               OR (OLD.execute_intent_at IS NOT NULL AND NEW.execute_intent_at IS DISTINCT FROM OLD.execute_intent_at)
               OR (OLD.outcome IS NOT NULL AND ROW(NEW.outcome, NEW.outcome_kind, NEW.outcome_at)
                    IS DISTINCT FROM ROW(OLD.outcome, OLD.outcome_kind, OLD.outcome_at))
               OR (OLD.quiescent_at IS NOT NULL AND ROW(NEW.quiescence_id, NEW.quiescence_kind, NEW.quiescent_at)
                    IS DISTINCT FROM ROW(OLD.quiescence_id, OLD.quiescence_kind, OLD.quiescent_at))
               OR (OLD.no_start_at IS NOT NULL AND NEW.no_start_at IS DISTINCT FROM OLD.no_start_at)
               OR (OLD.transactional_cleanup_at IS NOT NULL AND NEW.transactional_cleanup_at IS DISTINCT FROM OLD.transactional_cleanup_at) THEN
                RAISE EXCEPTION 'attempt identity and recorded evidence are immutable' USING ERRCODE = '23514';
            END IF;
            IF job.current_attempt_id IS DISTINCT FROM NEW.id OR job.ownership_kind NOT IN ('execution', 'recovery')
               OR job.ownership_token IS NULL OR job.ownership_until <= clock_timestamp() THEN
                RAISE EXCEPTION 'attempt checkpoint requires current live ownership' USING ERRCODE = '23514';
            END IF;
            IF NEW.plan_fingerprint IS NOT NULL AND
                (NEW.plan_fingerprint IS DISTINCT FROM job.plan_fingerprint OR NEW.operation_id IS DISTINCT FROM job.operation_id) THEN
                RAISE EXCEPTION 'attempt plan does not match the immutable job plan' USING ERRCODE = '23514';
            END IF;
            IF NEW.execute_intent_at IS NOT NULL AND OLD.execute_intent_at IS NULL AND
               (job.phase <> 'prepared' OR job.ownership_kind <> 'execution' OR job.ownership_token <> NEW.admission_token
                OR NEW.execute_intent_at > clock_timestamp() OR NEW.execute_intent_at >= NEW.deadline) THEN
                RAISE EXCEPTION 'execute intent requires original prepared execution authority' USING ERRCODE = '23514';
            END IF;
            IF NEW.no_start_at IS NOT NULL AND OLD.no_start_at IS NULL AND
               (job.ownership_kind <> 'recovery' OR job.ownership_token = NEW.admission_token) THEN
                RAISE EXCEPTION 'no-start proof requires revoked original authority' USING ERRCODE = '23514';
            END IF;
            IF NEW.transactional_cleanup_at IS NOT NULL AND OLD.transactional_cleanup_at IS NULL THEN
                IF job.profile <> 'transactional' OR job.capacity_group IS NULL OR NEW.stage <> 'normal'
                   OR job.phase <> 'reconciling' OR job.ownership_kind <> 'recovery'
                   OR job.ownership_token = NEW.admission_token OR job.normal_attempts <> NEW.ordinal
                   OR job.attempt_deadline IS DISTINCT FROM NEW.deadline
                   OR job.processed_at IS NOT NULL OR job.dead_lettered_at IS NOT NULL OR job.terminal_reason IS NOT NULL
                   OR NOT isfinite(NEW.transactional_cleanup_at) OR NEW.transactional_cleanup_at > clock_timestamp() THEN
                    RAISE EXCEPTION 'transactional cleanup requires exact live revoked recovery authority' USING ERRCODE = '23514';
                END IF;
                NEW.transactional_cleanup_at := clock_timestamp();
            END IF;
            RETURN NEW;
        END;
        $$;
        CREATE OR REPLACE FUNCTION public.fn_background_job_reservations_guard() RETURNS trigger
        LANGUAGE plpgsql SECURITY INVOKER SET search_path = pg_catalog AS $$
        DECLARE attempt record;
        BEGIN
            SELECT a.organization_id, a.stage, a.no_start_at, a.quiescent_at, a.outcome_kind, a.transactional_cleanup_at, j.profile, j.capacity_group,
                   j.current_attempt_id, j.processed_at INTO attempt
              FROM public.background_job_attempts a JOIN public.background_jobs j
                ON j.tenant_id = a.tenant_id AND j.id = a.job_id
              WHERE a.tenant_id = NEW.tenant_id AND a.job_id = NEW.job_id AND a.id = NEW.attempt_id
              FOR KEY SHARE OF a, j;
            IF NOT FOUND OR attempt.organization_id IS DISTINCT FROM
                   NULLIF(current_setting('app.organization_id', true), '')::uuid
               OR attempt.capacity_group IS DISTINCT FROM NEW.capacity_group OR attempt.stage <> 'normal' THEN
                RAISE EXCEPTION 'reservation attempt scope is unavailable or mismatched' USING ERRCODE = '23514';
            END IF;
            IF TG_OP = 'INSERT' THEN
                IF NEW.released_at IS NOT NULL OR NEW.release_reason IS NOT NULL
                   OR attempt.current_attempt_id IS DISTINCT FROM NEW.attempt_id THEN
                    RAISE EXCEPTION 'reservation requires a current unreleased admission' USING ERRCODE = '23514';
                END IF;
                PERFORM 1 FROM public.background_job_capacity c
                  WHERE c.tenant_id = NEW.tenant_id AND c.capacity_group = NEW.capacity_group FOR UPDATE;
                IF NOT FOUND THEN RAISE EXCEPTION 'capacity mutex unavailable' USING ERRCODE = '23514'; END IF;
                NEW.reserved_at := clock_timestamp();
            ELSE
                IF ROW(NEW.tenant_id, NEW.capacity_group, NEW.job_id, NEW.attempt_id, NEW.reserved_at)
                    IS DISTINCT FROM ROW(OLD.tenant_id, OLD.capacity_group, OLD.job_id, OLD.attempt_id, OLD.reserved_at)
                   OR (OLD.released_at IS NOT NULL AND NEW IS DISTINCT FROM OLD) THEN
                    RAISE EXCEPTION 'reservation identity and release are immutable' USING ERRCODE = '23514';
                END IF;
                IF NEW.released_at IS NOT NULL AND OLD.released_at IS NULL AND NOT
                   ((NEW.release_reason = 'completed' AND attempt.processed_at IS NOT NULL)
                    OR (NEW.release_reason = 'never_started' AND attempt.no_start_at IS NOT NULL)
                    OR (NEW.release_reason = 'quiescent' AND attempt.quiescent_at IS NOT NULL AND attempt.outcome_kind IS NOT NULL)
                    OR (NEW.release_reason = 'transactional_rollback' AND attempt.profile = 'transactional'
                        AND attempt.transactional_cleanup_at IS NOT NULL)) THEN
                    RAISE EXCEPTION 'reservation release requires positive completion or quiescence' USING ERRCODE = '23514';
                END IF;
            END IF;
            RETURN NEW;
        END;
        $$;
        CREATE OR REPLACE FUNCTION public.fn_background_job_attempt_counts() RETURNS trigger
        LANGUAGE plpgsql SECURITY INVOKER SET search_path = pg_catalog AS $$
        DECLARE target_tenant uuid; target_job uuid; job record; normal_count bigint; finalize_count bigint;
        BEGIN
            target_tenant := NEW.tenant_id;
            IF TG_TABLE_NAME = 'background_jobs' THEN target_job := NEW.id; ELSE target_job := NEW.job_id; END IF;
            SELECT j.normal_attempts, j.finalize_attempts, j.current_attempt_id, j.capacity_group, j.profile, j.phase, j.processed_at INTO job
              FROM public.background_jobs j WHERE j.tenant_id = target_tenant AND j.id = target_job FOR NO KEY UPDATE;
            IF NOT FOUND THEN RAISE EXCEPTION 'attempt job unavailable' USING ERRCODE = '23514'; END IF;
            SELECT count(*) FILTER (WHERE a.stage = 'normal'), count(*) FILTER (WHERE a.stage = 'finalize')
              INTO normal_count, finalize_count FROM public.background_job_attempts a
              WHERE a.tenant_id = target_tenant AND a.job_id = target_job;
            IF job.normal_attempts <> normal_count OR job.finalize_attempts <> finalize_count
               OR ((normal_count + finalize_count = 0) <> (job.current_attempt_id IS NULL))
               OR EXISTS (SELECT 1 FROM public.background_job_attempts a
                   WHERE a.tenant_id = target_tenant AND a.job_id = target_job
                     AND a.ordinal > CASE WHEN a.stage = 'normal' THEN normal_count ELSE finalize_count END) THEN
                RAISE EXCEPTION 'job counts and durable attempt receipts must commit together' USING ERRCODE = '23514';
            END IF;
            IF job.capacity_group IS NOT NULL AND EXISTS (
                SELECT 1 FROM public.background_job_attempts a
                WHERE a.tenant_id = target_tenant AND a.job_id = target_job AND a.stage = 'normal'
                  AND NOT EXISTS (SELECT 1 FROM public.background_job_reservations r
                      WHERE r.tenant_id = a.tenant_id AND r.job_id = a.job_id AND r.attempt_id = a.id
                        AND r.capacity_group = job.capacity_group)) THEN
                RAISE EXCEPTION 'counted capacity admission requires its durable reservation' USING ERRCODE = '23514';
            END IF;
            IF job.capacity_group IS NOT NULL AND job.profile = 'transactional' THEN
                IF EXISTS (SELECT 1 FROM public.background_job_attempts a
                    WHERE a.tenant_id=target_tenant AND a.job_id=target_job AND a.transactional_cleanup_at IS NOT NULL
                      AND NOT EXISTS (SELECT 1 FROM public.background_job_reservations r
                          WHERE r.tenant_id=a.tenant_id AND r.job_id=a.job_id AND r.attempt_id=a.id
                            AND r.capacity_group=job.capacity_group AND r.released_at IS NOT NULL
                            AND r.release_reason='transactional_rollback')) THEN
                    RAISE EXCEPTION 'transactional cleanup and reservation release must commit together' USING ERRCODE='23514';
                END IF;
                IF EXISTS (SELECT 1 FROM public.background_job_attempts a
                    WHERE a.tenant_id=target_tenant AND a.job_id=target_job AND a.stage='normal'
                      AND (a.id IS DISTINCT FROM job.current_attempt_id OR job.phase IN ('pending','terminal_pending','completed'))
                      AND NOT EXISTS (SELECT 1 FROM public.background_job_reservations r
                          WHERE r.tenant_id=a.tenant_id AND r.job_id=a.job_id AND r.attempt_id=a.id
                            AND r.capacity_group=job.capacity_group AND r.released_at IS NOT NULL
                            AND ((r.release_reason='transactional_rollback' AND a.transactional_cleanup_at IS NOT NULL
                                    AND NOT (job.phase='completed' AND a.id=job.current_attempt_id))
                                OR (r.release_reason='never_started' AND a.no_start_at IS NOT NULL
                                    AND NOT (job.phase='completed' AND a.id=job.current_attempt_id))
                                OR (r.release_reason='completed' AND job.processed_at IS NOT NULL AND a.id=job.current_attempt_id)))) THEN
                    RAISE EXCEPTION 'grouped transactional eligibility requires positive released history' USING ERRCODE='23514';
                END IF;
            END IF;
            RETURN NULL;
        END;
        $$;
        GRANT UPDATE (transactional_cleanup_at) ON background_job_attempts TO learnstack_app;
        GRANT SELECT (transactional_cleanup_at) ON background_job_attempts TO learnstack_outbox_admin;
        """);

    // Reversal refuses recorded cleanup history. EF keeps the temporary owner-only
    // NO FORCE inspection and its restoration in this migration transaction.
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        ALTER TABLE background_job_attempts NO FORCE ROW LEVEL SECURITY;
        ALTER TABLE background_job_reservations NO FORCE ROW LEVEL SECURITY;
        DO $$ BEGIN
            IF EXISTS (SELECT 1 FROM public.background_job_attempts WHERE transactional_cleanup_at IS NOT NULL)
               OR EXISTS (SELECT 1 FROM public.background_job_reservations WHERE release_reason='transactional_rollback') THEN
                RAISE EXCEPTION 'transactional cleanup history cannot be discarded by migration reversal' USING ERRCODE='23514';
            END IF;
        END $$;
        ALTER TABLE background_job_attempts FORCE ROW LEVEL SECURITY;
        ALTER TABLE background_job_reservations FORCE ROW LEVEL SECURITY;
        REVOKE UPDATE (transactional_cleanup_at) ON background_job_attempts FROM learnstack_app;
        REVOKE SELECT (transactional_cleanup_at) ON background_job_attempts FROM learnstack_outbox_admin;
        CREATE OR REPLACE FUNCTION public.fn_background_job_attempts_guard() RETURNS trigger
            LANGUAGE plpgsql SECURITY INVOKER SET search_path = pg_catalog AS $$
            DECLARE job record;
            BEGIN
                SELECT j.organization_id, j.phase, j.profile, j.normal_attempts, j.finalize_attempts,
                       j.ownership_token, j.ownership_kind, j.ownership_until, j.current_attempt_id,
                       j.final_outcome_attempt_id, j.plan_fingerprint, j.operation_id
                  INTO job FROM public.background_jobs j
                  WHERE j.tenant_id = NEW.tenant_id AND j.id = NEW.job_id FOR KEY SHARE;
                IF NOT FOUND OR job.organization_id IS DISTINCT FROM NEW.organization_id THEN
                    RAISE EXCEPTION 'attempt parent scope is unavailable or mismatched' USING ERRCODE = '23514';
                END IF;
                IF TG_OP = 'INSERT' THEN
                    IF job.ownership_kind IS DISTINCT FROM 'routing' OR job.ownership_until <= clock_timestamp()
                       OR NEW.admission_token <> job.ownership_token
                       OR (NEW.stage = 'normal' AND (job.phase <> 'pending' OR NEW.ordinal <> job.normal_attempts + 1))
                       OR (NEW.stage = 'finalize' AND (job.phase <> 'finalize_pending' OR NEW.ordinal <> job.finalize_attempts + 1
                           OR NEW.final_outcome_attempt_id IS DISTINCT FROM job.final_outcome_attempt_id))
                       OR NEW.plan_fingerprint IS NOT NULL OR NEW.operation_id IS NOT NULL
                       OR NEW.execute_intent_at IS NOT NULL OR NEW.outcome IS NOT NULL
                       OR NEW.quiescent_at IS NOT NULL OR NEW.no_start_at IS NOT NULL THEN
                        RAISE EXCEPTION 'attempt insert requires a fresh routing receipt' USING ERRCODE = '23514';
                    END IF;
                    NEW.admitted_at := clock_timestamp();
                    IF (job.profile = 'transactional' OR NEW.stage = 'finalize')
                       AND NEW.deadline > NEW.admitted_at + interval '5 minutes' THEN
                        RAISE EXCEPTION 'short stage deadline exceeds its bound' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END IF;
                IF ROW(NEW.id, NEW.tenant_id, NEW.organization_id, NEW.job_id, NEW.stage, NEW.ordinal,
                       NEW.admission_token, NEW.admitted_at, NEW.deadline, NEW.final_outcome_attempt_id)
                   IS DISTINCT FROM ROW(OLD.id, OLD.tenant_id, OLD.organization_id, OLD.job_id, OLD.stage, OLD.ordinal,
                       OLD.admission_token, OLD.admitted_at, OLD.deadline, OLD.final_outcome_attempt_id)
                   OR (OLD.plan_fingerprint IS NOT NULL AND ROW(NEW.plan_fingerprint, NEW.operation_id)
                        IS DISTINCT FROM ROW(OLD.plan_fingerprint, OLD.operation_id))
                   OR (OLD.execute_intent_at IS NOT NULL AND NEW.execute_intent_at IS DISTINCT FROM OLD.execute_intent_at)
                   OR (OLD.outcome IS NOT NULL AND ROW(NEW.outcome, NEW.outcome_kind, NEW.outcome_at)
                        IS DISTINCT FROM ROW(OLD.outcome, OLD.outcome_kind, OLD.outcome_at))
                   OR (OLD.quiescent_at IS NOT NULL AND ROW(NEW.quiescence_id, NEW.quiescence_kind, NEW.quiescent_at)
                        IS DISTINCT FROM ROW(OLD.quiescence_id, OLD.quiescence_kind, OLD.quiescent_at))
                   OR (OLD.no_start_at IS NOT NULL AND NEW.no_start_at IS DISTINCT FROM OLD.no_start_at) THEN
                    RAISE EXCEPTION 'attempt identity and recorded evidence are immutable' USING ERRCODE = '23514';
                END IF;
                IF job.current_attempt_id IS DISTINCT FROM NEW.id OR job.ownership_kind NOT IN ('execution', 'recovery')
                   OR job.ownership_token IS NULL OR job.ownership_until <= clock_timestamp() THEN
                    RAISE EXCEPTION 'attempt checkpoint requires current live ownership' USING ERRCODE = '23514';
                END IF;
                IF NEW.plan_fingerprint IS NOT NULL AND
                    (NEW.plan_fingerprint IS DISTINCT FROM job.plan_fingerprint OR NEW.operation_id IS DISTINCT FROM job.operation_id) THEN
                    RAISE EXCEPTION 'attempt plan does not match the immutable job plan' USING ERRCODE = '23514';
                END IF;
                IF NEW.execute_intent_at IS NOT NULL AND OLD.execute_intent_at IS NULL AND
                   (job.phase <> 'prepared' OR job.ownership_kind <> 'execution' OR job.ownership_token <> NEW.admission_token
                    OR NEW.execute_intent_at > clock_timestamp() OR NEW.execute_intent_at >= NEW.deadline) THEN
                    RAISE EXCEPTION 'execute intent requires original prepared execution authority' USING ERRCODE = '23514';
                END IF;
                IF NEW.no_start_at IS NOT NULL AND OLD.no_start_at IS NULL AND
                   (job.ownership_kind <> 'recovery' OR job.ownership_token = NEW.admission_token) THEN
                    RAISE EXCEPTION 'no-start proof requires revoked original authority' USING ERRCODE = '23514';
                END IF;
                RETURN NEW;
            END;
            $$;
        CREATE OR REPLACE FUNCTION public.fn_background_job_reservations_guard() RETURNS trigger
            LANGUAGE plpgsql SECURITY INVOKER SET search_path = pg_catalog AS $$
            DECLARE attempt record;
            BEGIN
                SELECT a.organization_id, a.stage, a.no_start_at, a.quiescent_at, a.outcome_kind, j.capacity_group,
                       j.current_attempt_id, j.processed_at INTO attempt
                  FROM public.background_job_attempts a JOIN public.background_jobs j
                    ON j.tenant_id = a.tenant_id AND j.id = a.job_id
                  WHERE a.tenant_id = NEW.tenant_id AND a.job_id = NEW.job_id AND a.id = NEW.attempt_id
                  FOR KEY SHARE OF a, j;
                IF NOT FOUND OR attempt.organization_id IS DISTINCT FROM
                       NULLIF(current_setting('app.organization_id', true), '')::uuid
                   OR attempt.capacity_group IS DISTINCT FROM NEW.capacity_group OR attempt.stage <> 'normal' THEN
                    RAISE EXCEPTION 'reservation attempt scope is unavailable or mismatched' USING ERRCODE = '23514';
                END IF;
                IF TG_OP = 'INSERT' THEN
                    IF NEW.released_at IS NOT NULL OR NEW.release_reason IS NOT NULL
                       OR attempt.current_attempt_id IS DISTINCT FROM NEW.attempt_id THEN
                        RAISE EXCEPTION 'reservation requires a current unreleased admission' USING ERRCODE = '23514';
                    END IF;
                    PERFORM 1 FROM public.background_job_capacity c
                      WHERE c.tenant_id = NEW.tenant_id AND c.capacity_group = NEW.capacity_group FOR UPDATE;
                    IF NOT FOUND THEN RAISE EXCEPTION 'capacity mutex unavailable' USING ERRCODE = '23514'; END IF;
                    NEW.reserved_at := clock_timestamp();
                ELSE
                    IF ROW(NEW.tenant_id, NEW.capacity_group, NEW.job_id, NEW.attempt_id, NEW.reserved_at)
                        IS DISTINCT FROM ROW(OLD.tenant_id, OLD.capacity_group, OLD.job_id, OLD.attempt_id, OLD.reserved_at)
                       OR (OLD.released_at IS NOT NULL AND NEW IS DISTINCT FROM OLD) THEN
                        RAISE EXCEPTION 'reservation identity and release are immutable' USING ERRCODE = '23514';
                    END IF;
                    IF NEW.released_at IS NOT NULL AND OLD.released_at IS NULL AND NOT
                       ((NEW.release_reason = 'completed' AND attempt.processed_at IS NOT NULL)
                        OR (NEW.release_reason = 'never_started' AND attempt.no_start_at IS NOT NULL)
                        OR (NEW.release_reason = 'quiescent' AND attempt.quiescent_at IS NOT NULL AND attempt.outcome_kind IS NOT NULL)) THEN
                        RAISE EXCEPTION 'reservation release requires positive completion or quiescence' USING ERRCODE = '23514';
                    END IF;
                END IF;
                RETURN NEW;
            END;
            $$;
        CREATE OR REPLACE FUNCTION public.fn_background_job_attempt_counts() RETURNS trigger
        LANGUAGE plpgsql SECURITY INVOKER SET search_path = pg_catalog AS $$
        DECLARE target_tenant uuid; target_job uuid; job record; normal_count bigint; finalize_count bigint;
        BEGIN
            target_tenant := NEW.tenant_id;
            IF TG_TABLE_NAME = 'background_jobs' THEN target_job := NEW.id; ELSE target_job := NEW.job_id; END IF;
            SELECT j.normal_attempts, j.finalize_attempts, j.current_attempt_id, j.capacity_group INTO job
              FROM public.background_jobs j WHERE j.tenant_id = target_tenant AND j.id = target_job FOR NO KEY UPDATE;
            IF NOT FOUND THEN RAISE EXCEPTION 'attempt job unavailable' USING ERRCODE = '23514'; END IF;
            SELECT count(*) FILTER (WHERE a.stage = 'normal'), count(*) FILTER (WHERE a.stage = 'finalize')
              INTO normal_count, finalize_count FROM public.background_job_attempts a
              WHERE a.tenant_id = target_tenant AND a.job_id = target_job;
            IF job.normal_attempts <> normal_count OR job.finalize_attempts <> finalize_count
               OR ((normal_count + finalize_count = 0) <> (job.current_attempt_id IS NULL))
               OR EXISTS (SELECT 1 FROM public.background_job_attempts a
                   WHERE a.tenant_id = target_tenant AND a.job_id = target_job
                     AND a.ordinal > CASE WHEN a.stage = 'normal' THEN normal_count ELSE finalize_count END) THEN
                RAISE EXCEPTION 'job counts and durable attempt receipts must commit together' USING ERRCODE = '23514';
            END IF;
            IF job.capacity_group IS NOT NULL AND EXISTS (
                SELECT 1 FROM public.background_job_attempts a
                WHERE a.tenant_id = target_tenant AND a.job_id = target_job AND a.stage = 'normal'
                  AND NOT EXISTS (SELECT 1 FROM public.background_job_reservations r
                      WHERE r.tenant_id = a.tenant_id AND r.job_id = a.job_id AND r.attempt_id = a.id
                        AND r.capacity_group = job.capacity_group)) THEN
                RAISE EXCEPTION 'counted capacity admission requires its durable reservation' USING ERRCODE = '23514';
            END IF;
            RETURN NULL;
        END;
        $$;
        ALTER TABLE background_job_attempts DROP CONSTRAINT ck_background_job_attempts_transactional_cleanup;
        ALTER TABLE background_job_attempts DROP COLUMN transactional_cleanup_at;
        ALTER TABLE background_job_reservations DROP CONSTRAINT ck_background_job_reservations_release;
        ALTER TABLE background_job_reservations ADD CONSTRAINT ck_background_job_reservations_release CHECK (
            (released_at IS NULL AND release_reason IS NULL)
            OR (released_at IS NOT NULL AND release_reason IS NOT NULL AND release_reason IN ('completed','never_started','quiescent')
                AND isfinite(released_at) AND released_at>=reserved_at));
        """);
}
