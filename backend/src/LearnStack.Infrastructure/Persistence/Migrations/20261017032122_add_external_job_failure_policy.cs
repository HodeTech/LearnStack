using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LearnStack.Infrastructure.Persistence.Migrations;

/// <summary>Enforce the explicit immutable external failure policy without inventing legacy facts.</summary>
public partial class add_external_job_failure_policy : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        ALTER TABLE background_jobs NO FORCE ROW LEVEL SECURITY;
        ALTER TABLE background_job_attempts NO FORCE ROW LEVEL SECURITY;
        ALTER TABLE background_job_reservations NO FORCE ROW LEVEL SECURITY;
        DO $$ BEGIN
            IF EXISTS (SELECT 1 FROM public.background_job_attempts WHERE outcome_kind IN ('failed','cancelled')) THEN
                RAISE EXCEPTION 'external completed-failure history requires an explicit recovery design' USING ERRCODE='23514';
            END IF;
            IF EXISTS (
                SELECT 1 FROM public.background_jobs j
                LEFT JOIN public.background_job_attempts a
                  ON a.tenant_id=j.tenant_id AND a.job_id=j.id AND a.id=j.final_outcome_attempt_id
                WHERE j.finalization_status IS NOT NULL AND
                  (a.stage='normal' AND a.ordinal=j.normal_attempts
                   AND a.organization_id IS NOT DISTINCT FROM j.organization_id
                   AND a.plan_fingerprint IS NOT DISTINCT FROM j.plan_fingerprint
                   AND a.operation_id IS NOT DISTINCT FROM j.operation_id
                   AND ((a.outcome_kind='succeeded' AND a.quiescent_at IS NOT NULL
                         AND j.finalization_status='succeeded' AND j.finalization_reason='none')
                     OR (a.outcome_kind IS NULL AND a.no_start_at IS NOT NULL AND a.execute_intent_at IS NULL
                         AND j.finalization_status='failed' AND (j.finalization_reason='permanent_failure'
                           OR (j.finalization_reason='normal_attempts_exhausted' AND j.normal_attempts=6))))) IS NOT TRUE)
              OR EXISTS (
                SELECT 1 FROM public.background_job_attempts a
                JOIN public.background_jobs j ON j.tenant_id=a.tenant_id AND j.id=a.job_id
                WHERE j.profile='external' AND a.stage='normal' AND
                  ((a.outcome IS NOT NULL AND
                    (a.organization_id IS NOT DISTINCT FROM j.organization_id
                     AND a.plan_fingerprint IS NOT DISTINCT FROM j.plan_fingerprint
                     AND a.operation_id IS NOT DISTINCT FROM j.operation_id
                     AND isfinite(a.outcome_at) AND isfinite(a.quiescent_at)
                     AND a.outcome_at>=a.execute_intent_at AND a.quiescent_at>=a.execute_intent_at
                     AND a.outcome_at<=clock_timestamp() AND a.quiescent_at<=clock_timestamp()) IS NOT TRUE)
                   OR ((a.outcome_kind IS NOT NULL OR a.no_start_at IS NOT NULL) AND NOT EXISTS (
                     SELECT 1 FROM public.background_job_reservations r
                     WHERE r.tenant_id=a.tenant_id AND r.capacity_group=j.capacity_group AND r.job_id=a.job_id
                       AND r.attempt_id=a.id AND r.released_at IS NOT NULL
                       AND ((a.outcome_kind IS NOT NULL AND a.quiescent_at IS NOT NULL AND r.release_reason='quiescent')
                         OR (a.no_start_at IS NOT NULL AND a.execute_intent_at IS NULL AND r.release_reason='never_started')))))) THEN
                RAISE EXCEPTION 'external retained history requires an explicit recovery design' USING ERRCODE='23514';
            END IF;
        END $$;
        ALTER TABLE background_jobs FORCE ROW LEVEL SECURITY;
        ALTER TABLE background_job_attempts FORCE ROW LEVEL SECURITY;
        ALTER TABLE background_job_reservations FORCE ROW LEVEL SECURITY;
        ALTER TABLE background_job_attempts ADD COLUMN failure_policy text NULL;
        ALTER TABLE background_job_attempts ADD CONSTRAINT ck_background_job_attempts_failure_policy CHECK (((failure_policy IS NULL
            AND (outcome_kind IS NULL OR outcome_kind='succeeded'))
            OR (stage='normal' AND outcome IS NOT NULL AND outcome_at IS NOT NULL AND execute_intent_at IS NOT NULL
                AND quiescent_at IS NOT NULL AND no_start_at IS NULL AND transactional_cleanup_at IS NULL
                AND failure_policy IS NOT NULL AND ((outcome_kind='failed'
                    AND failure_policy IN ('permanent','transient_repeatable','transient_not_repeatable'))
                    OR (outcome_kind='cancelled' AND failure_policy IN ('transient_repeatable','transient_not_repeatable'))))) IS TRUE);
        ALTER TABLE background_jobs DROP CONSTRAINT ck_background_jobs_errors;
        ALTER TABLE background_jobs ADD CONSTRAINT ck_background_jobs_errors CHECK ((last_error IS NULL OR
                (octet_length(last_error) <= 256 AND last_error IN ('deployment_mismatch', 'capacity_unavailable', 'concurrency_conflict', 'rate_limited', 'dependency_unavailable', 'audit_unavailable', 'unexpected_fault', 'permanent_failure', 'lease_lost', 'deadline_exceeded', 'shutdown', 'unknown_outcome', 'invalid_control', 'finalize_refused', 'finalize_exhausted')))
                AND (quarantine_reason IS NULL OR quarantine_reason IN ('unknown_outcome', 'probe_exhausted', 'invalid_control'))
                AND (terminal_reason IS NULL OR terminal_reason IN ('retired_definition', 'invalid_envelope', 'permanent_failure', 'retry_exhausted', 'external_retry_forbidden', 'finalize_refused', 'finalize_exhausted')));

        CREATE OR REPLACE FUNCTION public.fn_background_job_attempts_guard() RETURNS trigger
        LANGUAGE plpgsql SECURITY INVOKER SET search_path = pg_catalog AS $$
        DECLARE job record;
        BEGIN
            SELECT j.organization_id, j.phase, j.profile, j.normal_attempts, j.finalize_attempts,
                   j.ownership_token, j.ownership_kind, j.ownership_until, j.current_attempt_id,
                   j.final_outcome_attempt_id, j.finalization_status, j.plan_fingerprint, j.operation_id, j.capacity_group,
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
                   OR NEW.transactional_cleanup_at IS NOT NULL OR NEW.failure_policy IS NOT NULL THEN
                    RAISE EXCEPTION 'attempt insert requires a fresh routing receipt' USING ERRCODE = '23514';
                END IF;
                IF job.profile='external' AND NEW.stage='normal' AND
                   (job.finalization_status IS NOT NULL OR EXISTS (
                       SELECT 1 FROM public.background_job_attempts a
                       WHERE a.tenant_id=NEW.tenant_id AND a.job_id=NEW.job_id AND a.stage='normal'
                         AND a.no_start_at IS NULL AND (a.quiescent_at IS NULL
                             OR a.outcome_kind NOT IN ('failed','cancelled')
                             OR a.failure_policy IS DISTINCT FROM 'transient_repeatable'))) THEN
                    RAISE EXCEPTION 'external admission requires resolved repeatable history' USING ERRCODE='23514';
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
               OR (OLD.outcome IS NOT NULL AND ROW(NEW.outcome, NEW.outcome_kind, NEW.outcome_at, NEW.failure_policy)
                    IS DISTINCT FROM ROW(OLD.outcome, OLD.outcome_kind, OLD.outcome_at, OLD.failure_policy))
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
            IF NEW.outcome IS NOT NULL AND OLD.outcome IS NULL THEN
                IF (job.profile='external' AND NEW.stage='normal' AND job.phase IN ('executing','reconciling')
                    AND OLD.execute_intent_at IS NOT NULL AND NEW.plan_fingerprint=job.plan_fingerprint
                    AND NEW.operation_id=job.operation_id AND isfinite(NEW.outcome_at) AND isfinite(NEW.quiescent_at)
                    AND NEW.outcome_at>=NEW.execute_intent_at AND NEW.quiescent_at>=NEW.execute_intent_at
                    AND NEW.outcome_at<=clock_timestamp() AND NEW.quiescent_at<=clock_timestamp()) IS NOT TRUE THEN
                    RAISE EXCEPTION 'outcome requires its armed external attempt and bounded evidence times' USING ERRCODE='23514';
                END IF;
            END IF;
            RETURN NEW;
        END;
        $$;
        CREATE OR REPLACE FUNCTION public.fn_background_jobs_transition() RETURNS trigger
        LANGUAGE plpgsql SECURITY INVOKER SET search_path = pg_catalog AS $$
        DECLARE attempt record; final_attempt record; admission boolean;
            expected_status text; expected_reason text; expected_terminal text;
        BEGIN
            SELECT NULL::text AS stage, NULL::timestamptz AS execute_intent_at, NULL::timestamptz AS quiescent_at,
                   NULL::timestamptz AS no_start_at, NULL::text AS outcome_kind, NULL::text AS failure_policy, NULL::text AS plan_fingerprint,
                   NULL::uuid AS operation_id INTO attempt;
            IF OLD.processed_at IS NOT NULL OR OLD.dead_lettered_at IS NOT NULL OR OLD.phase = 'quarantined' THEN
                IF NEW IS DISTINCT FROM OLD THEN
                    RAISE EXCEPTION 'completed or quarantined job is frozen' USING ERRCODE = '23514';
                END IF;
                RETURN NEW;
            END IF;
            IF (OLD.plan IS NOT NULL AND ROW(NEW.plan, NEW.plan_fingerprint, NEW.prepared_at, NEW.operation_id)
                    IS DISTINCT FROM ROW(OLD.plan, OLD.plan_fingerprint, OLD.prepared_at, OLD.operation_id))
               OR (OLD.finalization_status IS NOT NULL AND
                   ROW(NEW.final_outcome_attempt_id, NEW.finalization_status, NEW.finalization_reason)
                    IS DISTINCT FROM ROW(OLD.final_outcome_attempt_id, OLD.finalization_status, OLD.finalization_reason)) THEN
                RAISE EXCEPTION 'job checkpoints are write-once' USING ERRCODE = '23514';
            END IF;
            admission := NEW.normal_attempts <> OLD.normal_attempts OR NEW.finalize_attempts <> OLD.finalize_attempts;
            IF NOT admission AND (NEW.unsuccessful_probes < OLD.unsuccessful_probes
                OR NEW.unsuccessful_probes > OLD.unsuccessful_probes + 1) THEN
                RAISE EXCEPTION 'recovery probes advance one bounded receipt at a time' USING ERRCODE = '23514';
            END IF;
            IF admission THEN
                IF current_user = 'learnstack_outbox_admin' OR OLD.ownership_kind IS DISTINCT FROM 'routing'
                   OR OLD.ownership_until <= clock_timestamp() OR OLD.available_after > clock_timestamp()
                   OR NEW.current_attempt_id IS NULL OR NEW.current_attempt_id IS NOT DISTINCT FROM OLD.current_attempt_id
                   OR NOT ((OLD.phase = 'pending' AND NEW.normal_attempts = OLD.normal_attempts + 1
                            AND NEW.finalize_attempts = OLD.finalize_attempts
                            AND NEW.phase = (CASE WHEN NEW.profile = 'transactional' THEN 'executing'
                                WHEN NEW.plan IS NULL THEN 'preparing' ELSE 'prepared' END))
                        OR (OLD.phase = 'finalize_pending' AND NEW.finalize_attempts = OLD.finalize_attempts + 1
                            AND NEW.normal_attempts = OLD.normal_attempts AND NEW.phase = 'finalizing')) THEN
                    RAISE EXCEPTION 'invalid counted job admission' USING ERRCODE = '23514';
                END IF;
                SELECT a.stage, a.ordinal, a.admission_token, a.deadline INTO attempt
                  FROM public.background_job_attempts a
                  WHERE a.tenant_id = NEW.tenant_id AND a.job_id = NEW.id AND a.id = NEW.current_attempt_id;
                IF NOT FOUND OR attempt.admission_token <> OLD.ownership_token
                   OR attempt.stage <> (CASE WHEN NEW.phase = 'finalizing' THEN 'finalize' ELSE 'normal' END)
                   OR attempt.ordinal <> (CASE WHEN NEW.phase = 'finalizing' THEN NEW.finalize_attempts ELSE NEW.normal_attempts END)
                   OR NEW.attempt_deadline IS DISTINCT FROM attempt.deadline THEN
                    RAISE EXCEPTION 'admission requires the matching attempt receipt' USING ERRCODE = '23514';
                END IF;
                NEW.ownership_kind := 'execution';
                NEW.unsuccessful_probes := 0;
                NEW.ownership_until := LEAST(OLD.ownership_until, attempt.deadline);
            ELSIF NEW.current_attempt_id IS DISTINCT FROM OLD.current_attempt_id
                  OR NEW.attempt_deadline IS DISTINCT FROM OLD.attempt_deadline THEN
                RAISE EXCEPTION 'attempt identity changes only with admission' USING ERRCODE = '23514';
            END IF;
            IF NEW.current_attempt_id IS NOT NULL THEN
                SELECT a.stage, a.execute_intent_at, a.quiescent_at, a.no_start_at, a.outcome_kind, a.failure_policy,
                       a.plan_fingerprint, a.operation_id INTO attempt
                  FROM public.background_job_attempts a
                  WHERE a.tenant_id = NEW.tenant_id AND a.job_id = NEW.id AND a.id = NEW.current_attempt_id;
                IF NOT FOUND THEN RAISE EXCEPTION 'current attempt unavailable' USING ERRCODE = '23514'; END IF;
            END IF;
            IF current_user = 'learnstack_outbox_admin' AND
               (NEW.processed_at IS DISTINCT FROM OLD.processed_at OR NEW.phase = 'completed'
                OR ROW(NEW.plan, NEW.plan_fingerprint, NEW.prepared_at, NEW.operation_id,
                       NEW.final_outcome_attempt_id, NEW.finalization_status, NEW.finalization_reason)
                   IS DISTINCT FROM ROW(OLD.plan, OLD.plan_fingerprint, OLD.prepared_at, OLD.operation_id,
                       OLD.final_outcome_attempt_id, OLD.finalization_status, OLD.finalization_reason)) THEN
                RAISE EXCEPTION 'control role cannot complete business checkpoints' USING ERRCODE = '23514';
            END IF;
            IF current_user = 'learnstack_app' AND NOT admission THEN
                IF OLD.ownership_kind NOT IN ('execution', 'recovery') OR OLD.ownership_token IS NULL
                   OR OLD.ownership_until <= clock_timestamp() THEN
                    RAISE EXCEPTION 'checkpoint requires live execution or recovery ownership' USING ERRCODE = '23514';
                END IF;
                IF NEW.phase <> OLD.phase AND NOT
                   ((OLD.phase = 'preparing' AND NEW.phase = 'prepared')
                    OR (OLD.phase = 'prepared' AND NEW.phase = 'executing')
                    OR (OLD.phase IN ('prepared', 'executing', 'reconciling') AND NEW.phase = 'finalize_pending')
                    OR (OLD.phase = 'executing' AND OLD.profile = 'transactional' AND NEW.phase = 'completed')
                    OR (OLD.phase = 'finalizing' AND NEW.phase IN ('completed', 'terminal_pending'))) THEN
                    RAISE EXCEPTION 'invalid app checkpoint transition' USING ERRCODE = '23514';
                END IF;
            ELSIF current_user <> 'learnstack_app' AND NOT admission AND NEW.phase <> OLD.phase THEN
                IF NOT ((NEW.phase = 'reconciling' AND OLD.phase IN ('preparing', 'prepared', 'executing', 'finalizing')
                            AND OLD.ownership_until <= clock_timestamp())
                     OR (OLD.phase = 'reconciling' AND NEW.phase IN ('pending', 'finalize_pending'))
                     OR (OLD.phase IN ('pending', 'preparing', 'prepared', 'executing', 'reconciling', 'finalize_pending', 'finalizing')
                            AND NEW.phase IN ('terminal_pending', 'quarantined'))) THEN
                    RAISE EXCEPTION 'invalid control transition' USING ERRCODE = '23514';
                END IF;
            END IF;
            IF NEW.plan IS NOT NULL AND OLD.plan IS NULL AND
               (OLD.phase <> 'preparing' OR NEW.phase <> 'prepared' OR NEW.prepared_at > clock_timestamp()) THEN
                RAISE EXCEPTION 'plan requires the prepare checkpoint' USING ERRCODE = '23514';
            END IF;
            IF NEW.phase = 'executing' AND OLD.phase = 'prepared' AND
               (OLD.ownership_kind <> 'execution' OR attempt.execute_intent_at IS NULL
                OR attempt.plan_fingerprint IS DISTINCT FROM NEW.plan_fingerprint
                OR attempt.operation_id IS DISTINCT FROM NEW.operation_id) THEN
                RAISE EXCEPTION 'external execute requires durable matching intent' USING ERRCODE = '23514';
            END IF;
            IF NEW.phase IN ('pending', 'finalize_pending', 'terminal_pending') AND OLD.phase <> NEW.phase AND NEW.profile = 'external'
               AND EXISTS (SELECT 1 FROM public.background_job_attempts a WHERE a.tenant_id = NEW.tenant_id
                   AND a.job_id = NEW.id AND a.execute_intent_at IS NOT NULL
                   AND (a.quiescent_at IS NULL OR a.outcome_kind IS NULL)) THEN
                RAISE EXCEPTION 'unresolved external effects cannot retry or terminalize' USING ERRCODE = '23514';
            END IF;
            IF NEW.finalization_status IS NOT NULL AND OLD.finalization_status IS NULL THEN
                SELECT a.stage, a.ordinal, a.organization_id, a.outcome_kind, a.failure_policy,
                       a.quiescent_at, a.no_start_at, a.plan_fingerprint, a.operation_id INTO final_attempt
                  FROM public.background_job_attempts a
                  WHERE a.tenant_id = NEW.tenant_id AND a.job_id = NEW.id AND a.id = NEW.final_outcome_attempt_id;
                IF NOT FOUND OR NEW.final_outcome_attempt_id IS DISTINCT FROM NEW.current_attempt_id
                   OR final_attempt.stage <> 'normal' OR final_attempt.ordinal<>NEW.normal_attempts
                   OR final_attempt.organization_id IS DISTINCT FROM NEW.organization_id
                   OR final_attempt.plan_fingerprint IS DISTINCT FROM NEW.plan_fingerprint
                   OR final_attempt.operation_id IS DISTINCT FROM NEW.operation_id
                   OR (final_attempt.quiescent_at IS NULL AND final_attempt.no_start_at IS NULL)
                   OR (NEW.finalization_status = 'succeeded' AND final_attempt.outcome_kind IS DISTINCT FROM 'succeeded')
                   OR (NEW.finalization_reason = 'external_failed' AND final_attempt.outcome_kind IS DISTINCT FROM 'failed')
                   OR (NEW.finalization_reason = 'external_cancelled' AND final_attempt.outcome_kind IS DISTINCT FROM 'cancelled')
                   OR NEW.phase <> 'finalize_pending' THEN
                    RAISE EXCEPTION 'finalization requires positive selected-attempt evidence' USING ERRCODE = '23514';
                END IF;
                IF final_attempt.outcome_kind='succeeded' THEN
                    expected_status:='succeeded'; expected_reason:='none';
                ELSIF final_attempt.outcome_kind='failed' AND final_attempt.failure_policy='permanent' THEN
                    expected_status:='failed'; expected_reason:='external_failed';
                ELSIF final_attempt.outcome_kind IN ('failed','cancelled') AND final_attempt.failure_policy='transient_not_repeatable' THEN
                    expected_status:='failed';
                    expected_reason:=CASE WHEN final_attempt.outcome_kind='failed' THEN 'external_failed' ELSE 'external_cancelled' END;
                ELSIF final_attempt.outcome_kind IN ('failed','cancelled') AND final_attempt.failure_policy='transient_repeatable'
                      AND NEW.normal_attempts=6 THEN
                    expected_status:='failed'; expected_reason:='normal_attempts_exhausted';
                ELSIF final_attempt.outcome_kind IS NULL AND final_attempt.no_start_at IS NOT NULL
                      AND NEW.finalization_status='failed' AND
                      (NEW.finalization_reason='permanent_failure' OR (NEW.finalization_reason='normal_attempts_exhausted'
                          AND NEW.normal_attempts=6)) THEN
                    expected_status:='failed'; expected_reason:=NEW.finalization_reason;
                ELSE
                    RAISE EXCEPTION 'finalization does not match deterministic attempt policy' USING ERRCODE='23514';
                END IF;
                IF ROW(NEW.finalization_status,NEW.finalization_reason) IS DISTINCT FROM ROW(expected_status,expected_reason) THEN
                    RAISE EXCEPTION 'finalization status and reason contradict attempt policy' USING ERRCODE='23514';
                END IF;
            END IF;
            IF NEW.profile = 'external'
               AND (NEW.phase = 'pending' OR (admission AND NEW.normal_attempts <> OLD.normal_attempts))
               AND (NEW.finalization_status IS NOT NULL OR EXISTS (
                   SELECT 1 FROM public.background_job_attempts a WHERE a.tenant_id = NEW.tenant_id
                       AND a.job_id = NEW.id AND a.stage = 'normal' AND a.outcome_kind = 'succeeded')) THEN
                RAISE EXCEPTION 'known final outcome cannot reopen normal execution' USING ERRCODE = '23514';
            END IF;
            IF NEW.profile = 'external'
               AND (NEW.phase = 'pending' OR (admission AND NEW.normal_attempts <> OLD.normal_attempts))
               AND EXISTS (SELECT 1 FROM public.background_job_attempts a WHERE a.tenant_id = NEW.tenant_id
                   AND a.job_id = NEW.id AND a.stage = 'normal'
                   AND (NOT admission OR a.id IS DISTINCT FROM NEW.current_attempt_id)
                   AND a.no_start_at IS NULL AND (a.quiescent_at IS NULL OR a.outcome_kind NOT IN ('failed','cancelled')
                       OR a.failure_policy IS DISTINCT FROM 'transient_repeatable')) THEN
                RAISE EXCEPTION 'external retry requires positive prior-attempt evidence' USING ERRCODE = '23514';
            END IF;
            IF NEW.phase = 'completed' AND (NEW.processed_at IS NULL
               OR (NEW.profile = 'external' AND (OLD.phase <> 'finalizing' OR NEW.finalization_status <> 'succeeded'))) THEN
                RAISE EXCEPTION 'successful completion requires its business stage' USING ERRCODE = '23514';
            END IF;
            IF NEW.profile='external' AND NEW.phase='terminal_pending' THEN
                IF NEW.finalization_status IS NOT NULL THEN
                    SELECT a.stage,a.ordinal,a.organization_id,a.outcome_kind,a.failure_policy,a.quiescent_at,a.no_start_at,
                           a.plan_fingerprint,a.operation_id INTO final_attempt
                      FROM public.background_job_attempts a
                      WHERE a.tenant_id=NEW.tenant_id AND a.job_id=NEW.id AND a.id=NEW.final_outcome_attempt_id;
                    IF NOT FOUND OR final_attempt.stage<>'normal' OR final_attempt.ordinal<>NEW.normal_attempts
                       OR final_attempt.organization_id IS DISTINCT FROM NEW.organization_id
                       OR final_attempt.plan_fingerprint IS DISTINCT FROM NEW.plan_fingerprint
                       OR final_attempt.operation_id IS DISTINCT FROM NEW.operation_id THEN
                        RAISE EXCEPTION 'terminal policy requires the exact selected normal attempt' USING ERRCODE='23514';
                    END IF;
                    expected_terminal:=NULL;
                    IF final_attempt.outcome_kind='failed' AND final_attempt.failure_policy='permanent'
                       AND NEW.finalization_status='failed' AND NEW.finalization_reason='external_failed' THEN
                        expected_terminal:='permanent_failure';
                    ELSIF final_attempt.outcome_kind IN ('failed','cancelled')
                          AND final_attempt.failure_policy='transient_not_repeatable' AND NEW.finalization_status='failed'
                          AND NEW.finalization_reason=(CASE WHEN final_attempt.outcome_kind='failed'
                              THEN 'external_failed' ELSE 'external_cancelled' END) THEN
                        expected_terminal:='external_retry_forbidden';
                    ELSIF final_attempt.outcome_kind IN ('failed','cancelled') AND final_attempt.failure_policy='transient_repeatable'
                          AND NEW.normal_attempts=6 AND NEW.finalization_status='failed'
                          AND NEW.finalization_reason='normal_attempts_exhausted' THEN
                        expected_terminal:='retry_exhausted';
                    ELSIF final_attempt.outcome_kind IS NULL AND final_attempt.no_start_at IS NOT NULL
                          AND NEW.finalization_status='failed' THEN
                        IF NEW.finalization_reason='permanent_failure' THEN expected_terminal:='permanent_failure';
                        ELSIF NEW.finalization_reason='normal_attempts_exhausted' AND NEW.normal_attempts=6 THEN
                            expected_terminal:='retry_exhausted';
                        END IF;
                    ELSIF final_attempt.outcome_kind='succeeded' AND NEW.finalization_status='succeeded'
                          AND NEW.finalization_reason='none' THEN
                        expected_terminal:=NULL;
                    ELSE
                        RAISE EXCEPTION 'terminal selected facts contradict deterministic policy' USING ERRCODE='23514';
                    END IF;
                    IF NEW.terminal_reason IN ('finalize_refused','finalize_exhausted') THEN
                        IF current_user<>'learnstack_outbox_admin' OR NEW.finalize_attempts<1
                           OR (NEW.terminal_reason='finalize_exhausted' AND NEW.finalize_attempts<>6)
                           OR attempt.stage IS DISTINCT FROM 'finalize' THEN
                            RAISE EXCEPTION 'finalizer terminal route requires its separate counted finalization budget' USING ERRCODE='23514';
                        END IF;
                    ELSIF NEW.terminal_reason IN ('retired_definition','invalid_envelope') THEN
                        IF current_user<>'learnstack_outbox_admin' THEN
                            RAISE EXCEPTION 'uninvokable terminal route belongs only to control' USING ERRCODE='23514';
                        END IF;
                    ELSE
                        IF expected_terminal IS NULL OR NEW.terminal_reason IS DISTINCT FROM expected_terminal
                           OR current_user<>'learnstack_app' OR OLD.phase<>'finalizing' OR NEW.dead_lettered_at IS NULL
                           OR NEW.business_finalization_incomplete OR OLD.ownership_kind IS DISTINCT FROM 'execution'
                           OR OLD.ownership_until<=clock_timestamp() OR OLD.attempt_deadline<=clock_timestamp()
                           OR NOT EXISTS (SELECT 1 FROM public.background_job_attempts a
                               WHERE a.tenant_id=NEW.tenant_id AND a.job_id=NEW.id AND a.id=NEW.current_attempt_id
                                 AND a.organization_id IS NOT DISTINCT FROM NEW.organization_id AND a.stage='finalize'
                                 AND a.ordinal=NEW.finalize_attempts AND a.admission_token=OLD.ownership_token
                                 AND a.final_outcome_attempt_id=NEW.final_outcome_attempt_id AND a.deadline=OLD.attempt_deadline) THEN
                            RAISE EXCEPTION 'ordinary terminal policy requires live app failure finalization' USING ERRCODE='23514';
                        END IF;
                    END IF;
                ELSIF NEW.plan IS NOT NULL THEN
                    IF current_user<>'learnstack_outbox_admin' OR NEW.terminal_reason NOT IN ('retired_definition','invalid_envelope') THEN
                        RAISE EXCEPTION 'prepared work cannot bypass its domain finalizer' USING ERRCODE='23514';
                    END IF;
                ELSIF NEW.terminal_reason='external_retry_forbidden' OR NEW.terminal_reason IN ('finalize_refused','finalize_exhausted') THEN
                    RAISE EXCEPTION 'terminal policy is missing its selected outcome' USING ERRCODE='23514';
                END IF;
            END IF;
            IF NEW.dead_lettered_at IS NOT NULL AND OLD.dead_lettered_at IS NULL THEN
                IF current_user = 'learnstack_app' AND
                    (OLD.phase <> 'finalizing' OR NEW.finalization_status IS DISTINCT FROM 'failed' OR NEW.plan IS NULL) THEN
                    RAISE EXCEPTION 'app terminal completion requires failure finalization' USING ERRCODE = '23514';
                END IF;
                IF current_user = 'learnstack_outbox_admin' AND NEW.plan IS NOT NULL THEN
                    NEW.business_finalization_incomplete := true;
                END IF;
            END IF;
            IF NEW.ownership_until > clock_timestamp() + interval '10 minutes' THEN
                RAISE EXCEPTION 'job ownership exceeds its lease bound' USING ERRCODE = '23514';
            END IF;
            IF NEW.bridge_token IS NOT NULL THEN
                IF (NEW.bridge_token IS DISTINCT FROM OLD.bridge_token AND
                        (NEW.phase NOT IN ('pending', 'finalize_pending') OR NEW.ownership_token IS NOT NULL))
                   OR NEW.bridge_until > clock_timestamp() + interval '10 minutes'
                   OR (NEW.bridge_token = OLD.bridge_token AND NEW.bridge_until > OLD.bridge_until
                       AND OLD.bridge_until <= clock_timestamp())
                   OR (NEW.bridge_token IS DISTINCT FROM OLD.bridge_token AND OLD.bridge_token IS NOT NULL
                       AND OLD.bridge_until > clock_timestamp()) THEN
                    RAISE EXCEPTION 'bridge requires bounded available ownership' USING ERRCODE = '23514';
                END IF;
            END IF;
            IF NOT admission AND NEW.ownership_kind = 'execution' AND
               (OLD.ownership_kind IS DISTINCT FROM 'execution' OR NEW.ownership_token IS DISTINCT FROM OLD.ownership_token) THEN
                RAISE EXCEPTION 'execution authority is minted only by counted admission' USING ERRCODE = '23514';
            END IF;
            -- The checked app finalization selection below becomes a new
            -- eligibility cycle and clears all recovery ownership before RETURN.
            IF NOT admission AND NEW.ownership_kind = 'recovery' AND
               ((NEW.phase NOT IN ('reconciling', 'terminal_pending')
                 AND NOT (current_user='learnstack_app' AND OLD.phase='reconciling'
                     AND NEW.phase='finalize_pending' AND NEW.finalization_status IS NOT NULL))
                OR (OLD.ownership_kind IS DISTINCT FROM 'recovery' AND NEW.ownership_token IS NOT DISTINCT FROM OLD.ownership_token)) THEN
                RAISE EXCEPTION 'recovery requires a separate non-executing token' USING ERRCODE = '23514';
            END IF;
            IF NOT admission AND OLD.ownership_kind = 'execution' AND NEW.ownership_token IS NULL
               AND NEW.phase = OLD.phase THEN
                RAISE EXCEPTION 'execution authority cannot be silently discarded' USING ERRCODE = '23514';
            END IF;
            IF NEW.ownership_token IS NOT NULL AND NOT admission THEN
                IF NEW.ownership_kind = 'routing' AND (NEW.phase NOT IN ('pending', 'finalize_pending')
                    OR NEW.available_after > clock_timestamp() OR NEW.original_due_at > clock_timestamp()
                    OR OLD.ownership_kind IN ('execution', 'recovery')) THEN
                    RAISE EXCEPTION 'routing cannot replace execution authority' USING ERRCODE = '23514';
                END IF;
                IF NEW.ownership_token = OLD.ownership_token AND NEW.ownership_until > OLD.ownership_until
                   AND OLD.ownership_until <= clock_timestamp() THEN
                    RAISE EXCEPTION 'expired ownership cannot be renewed' USING ERRCODE = '23514';
                END IF;
                IF NEW.ownership_kind = 'execution' AND NEW.ownership_until > NEW.attempt_deadline THEN
                    RAISE EXCEPTION 'execution lease exceeds immutable deadline' USING ERRCODE = '23514';
                END IF;
                IF NEW.ownership_token IS DISTINCT FROM OLD.ownership_token AND OLD.ownership_token IS NOT NULL
                   AND OLD.ownership_until > clock_timestamp() THEN
                    RAISE EXCEPTION 'live ownership cannot be replaced' USING ERRCODE = '23514';
                END IF;
            END IF;
            IF NEW.phase IN ('pending', 'finalize_pending') AND NEW.phase <> OLD.phase THEN
                NEW.wake_generation := OLD.wake_generation + 1;
                IF current_user = 'learnstack_app' THEN NEW.available_after := clock_timestamp(); END IF;
                NEW.handoff_check_at := clock_timestamp();
                NEW.bridge_token := NULL; NEW.bridge_until := NULL; NEW.vendor_job_id := NULL;
                NEW.ownership_token := NULL; NEW.ownership_kind := NULL; NEW.ownership_until := NULL;
            ELSIF NEW.wake_generation <> OLD.wake_generation THEN
                RAISE EXCEPTION 'wake generation changes only with a new eligibility cycle' USING ERRCODE = '23514';
            END IF;
            IF NEW.phase IN ('completed', 'quarantined') OR NEW.dead_lettered_at IS NOT NULL
               OR (NEW.phase = 'terminal_pending' AND OLD.phase <> 'terminal_pending') THEN
                NEW.bridge_token := NULL; NEW.bridge_until := NULL; NEW.vendor_job_id := NULL;
                NEW.ownership_token := NULL; NEW.ownership_kind := NULL; NEW.ownership_until := NULL;
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
            IF job.capacity_group IS NOT NULL AND job.profile='external' AND EXISTS (
                SELECT 1 FROM public.background_job_attempts a
                WHERE a.tenant_id=target_tenant AND a.job_id=target_job AND a.stage='normal'
                  AND (a.outcome_kind IS NOT NULL OR a.no_start_at IS NOT NULL)
                  AND NOT EXISTS (SELECT 1 FROM public.background_job_reservations r
                      WHERE r.tenant_id=a.tenant_id AND r.capacity_group=job.capacity_group AND r.job_id=a.job_id
                        AND r.attempt_id=a.id AND r.released_at IS NOT NULL
                        AND ((a.outcome_kind IS NOT NULL AND a.quiescent_at IS NOT NULL AND r.release_reason='quiescent')
                            OR (a.no_start_at IS NOT NULL AND a.execute_intent_at IS NULL AND r.release_reason='never_started')))) THEN
                RAISE EXCEPTION 'external evidence and exact reservation release must commit together' USING ERRCODE='23514';
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
        GRANT UPDATE (failure_policy) ON background_job_attempts TO learnstack_app;
        GRANT SELECT (failure_policy) ON background_job_attempts TO learnstack_outbox_admin;
        """);

    // Newly governed failure facts are irreversible; empty/success-only history may restore its predecessor.
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        ALTER TABLE background_job_attempts NO FORCE ROW LEVEL SECURITY;
        ALTER TABLE background_jobs NO FORCE ROW LEVEL SECURITY;
        DO $$ BEGIN
            IF EXISTS (SELECT 1 FROM public.background_job_attempts WHERE failure_policy IS NOT NULL)
               OR EXISTS (SELECT 1 FROM public.background_jobs WHERE terminal_reason='external_retry_forbidden') THEN
                RAISE EXCEPTION 'external failure-policy history cannot be discarded by reversal' USING ERRCODE='23514';
            END IF;
        END $$;
        ALTER TABLE background_job_attempts FORCE ROW LEVEL SECURITY;
        ALTER TABLE background_jobs FORCE ROW LEVEL SECURITY;
        REVOKE UPDATE (failure_policy) ON background_job_attempts FROM learnstack_app;
        REVOKE SELECT (failure_policy) ON background_job_attempts FROM learnstack_outbox_admin;
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
        CREATE OR REPLACE FUNCTION public.fn_background_jobs_transition() RETURNS trigger
        LANGUAGE plpgsql SECURITY INVOKER SET search_path = pg_catalog AS $$
        DECLARE attempt record; final_attempt record; admission boolean;
        BEGIN
            SELECT NULL::text AS stage, NULL::timestamptz AS execute_intent_at, NULL::timestamptz AS quiescent_at,
                   NULL::timestamptz AS no_start_at, NULL::text AS outcome_kind, NULL::text AS plan_fingerprint,
                   NULL::uuid AS operation_id INTO attempt;
            IF OLD.processed_at IS NOT NULL OR OLD.dead_lettered_at IS NOT NULL OR OLD.phase = 'quarantined' THEN
                IF NEW IS DISTINCT FROM OLD THEN
                    RAISE EXCEPTION 'completed or quarantined job is frozen' USING ERRCODE = '23514';
                END IF;
                RETURN NEW;
            END IF;
            IF (OLD.plan IS NOT NULL AND ROW(NEW.plan, NEW.plan_fingerprint, NEW.prepared_at, NEW.operation_id)
                    IS DISTINCT FROM ROW(OLD.plan, OLD.plan_fingerprint, OLD.prepared_at, OLD.operation_id))
               OR (OLD.finalization_status IS NOT NULL AND
                   ROW(NEW.final_outcome_attempt_id, NEW.finalization_status, NEW.finalization_reason)
                    IS DISTINCT FROM ROW(OLD.final_outcome_attempt_id, OLD.finalization_status, OLD.finalization_reason)) THEN
                RAISE EXCEPTION 'job checkpoints are write-once' USING ERRCODE = '23514';
            END IF;
            admission := NEW.normal_attempts <> OLD.normal_attempts OR NEW.finalize_attempts <> OLD.finalize_attempts;
            IF NOT admission AND (NEW.unsuccessful_probes < OLD.unsuccessful_probes
                OR NEW.unsuccessful_probes > OLD.unsuccessful_probes + 1) THEN
                RAISE EXCEPTION 'recovery probes advance one bounded receipt at a time' USING ERRCODE = '23514';
            END IF;
            IF admission THEN
                IF current_user = 'learnstack_outbox_admin' OR OLD.ownership_kind IS DISTINCT FROM 'routing'
                   OR OLD.ownership_until <= clock_timestamp() OR OLD.available_after > clock_timestamp()
                   OR NEW.current_attempt_id IS NULL OR NEW.current_attempt_id IS NOT DISTINCT FROM OLD.current_attempt_id
                   OR NOT ((OLD.phase = 'pending' AND NEW.normal_attempts = OLD.normal_attempts + 1
                            AND NEW.finalize_attempts = OLD.finalize_attempts
                            AND NEW.phase = (CASE WHEN NEW.profile = 'transactional' THEN 'executing'
                                WHEN NEW.plan IS NULL THEN 'preparing' ELSE 'prepared' END))
                        OR (OLD.phase = 'finalize_pending' AND NEW.finalize_attempts = OLD.finalize_attempts + 1
                            AND NEW.normal_attempts = OLD.normal_attempts AND NEW.phase = 'finalizing')) THEN
                    RAISE EXCEPTION 'invalid counted job admission' USING ERRCODE = '23514';
                END IF;
                SELECT a.stage, a.ordinal, a.admission_token, a.deadline INTO attempt
                  FROM public.background_job_attempts a
                  WHERE a.tenant_id = NEW.tenant_id AND a.job_id = NEW.id AND a.id = NEW.current_attempt_id;
                IF NOT FOUND OR attempt.admission_token <> OLD.ownership_token
                   OR attempt.stage <> (CASE WHEN NEW.phase = 'finalizing' THEN 'finalize' ELSE 'normal' END)
                   OR attempt.ordinal <> (CASE WHEN NEW.phase = 'finalizing' THEN NEW.finalize_attempts ELSE NEW.normal_attempts END)
                   OR NEW.attempt_deadline IS DISTINCT FROM attempt.deadline THEN
                    RAISE EXCEPTION 'admission requires the matching attempt receipt' USING ERRCODE = '23514';
                END IF;
                NEW.ownership_kind := 'execution';
                NEW.unsuccessful_probes := 0;
                NEW.ownership_until := LEAST(OLD.ownership_until, attempt.deadline);
            ELSIF NEW.current_attempt_id IS DISTINCT FROM OLD.current_attempt_id
                  OR NEW.attempt_deadline IS DISTINCT FROM OLD.attempt_deadline THEN
                RAISE EXCEPTION 'attempt identity changes only with admission' USING ERRCODE = '23514';
            END IF;
            IF NEW.current_attempt_id IS NOT NULL THEN
                SELECT a.stage, a.execute_intent_at, a.quiescent_at, a.no_start_at, a.outcome_kind,
                       a.plan_fingerprint, a.operation_id INTO attempt
                  FROM public.background_job_attempts a
                  WHERE a.tenant_id = NEW.tenant_id AND a.job_id = NEW.id AND a.id = NEW.current_attempt_id;
                IF NOT FOUND THEN RAISE EXCEPTION 'current attempt unavailable' USING ERRCODE = '23514'; END IF;
            END IF;
            IF current_user = 'learnstack_outbox_admin' AND
               (NEW.processed_at IS DISTINCT FROM OLD.processed_at OR NEW.phase = 'completed'
                OR ROW(NEW.plan, NEW.plan_fingerprint, NEW.prepared_at, NEW.operation_id,
                       NEW.final_outcome_attempt_id, NEW.finalization_status, NEW.finalization_reason)
                   IS DISTINCT FROM ROW(OLD.plan, OLD.plan_fingerprint, OLD.prepared_at, OLD.operation_id,
                       OLD.final_outcome_attempt_id, OLD.finalization_status, OLD.finalization_reason)) THEN
                RAISE EXCEPTION 'control role cannot complete business checkpoints' USING ERRCODE = '23514';
            END IF;
            IF current_user = 'learnstack_app' AND NOT admission THEN
                IF OLD.ownership_kind NOT IN ('execution', 'recovery') OR OLD.ownership_token IS NULL
                   OR OLD.ownership_until <= clock_timestamp() THEN
                    RAISE EXCEPTION 'checkpoint requires live execution or recovery ownership' USING ERRCODE = '23514';
                END IF;
                IF NEW.phase <> OLD.phase AND NOT
                   ((OLD.phase = 'preparing' AND NEW.phase = 'prepared')
                    OR (OLD.phase = 'prepared' AND NEW.phase = 'executing')
                    OR (OLD.phase IN ('prepared', 'executing', 'reconciling') AND NEW.phase = 'finalize_pending')
                    OR (OLD.phase = 'executing' AND OLD.profile = 'transactional' AND NEW.phase = 'completed')
                    OR (OLD.phase = 'finalizing' AND NEW.phase IN ('completed', 'terminal_pending'))) THEN
                    RAISE EXCEPTION 'invalid app checkpoint transition' USING ERRCODE = '23514';
                END IF;
            ELSIF current_user <> 'learnstack_app' AND NOT admission AND NEW.phase <> OLD.phase THEN
                IF NOT ((NEW.phase = 'reconciling' AND OLD.phase IN ('preparing', 'prepared', 'executing', 'finalizing')
                            AND OLD.ownership_until <= clock_timestamp())
                     OR (OLD.phase = 'reconciling' AND NEW.phase IN ('pending', 'finalize_pending'))
                     OR (OLD.phase IN ('pending', 'preparing', 'prepared', 'executing', 'reconciling', 'finalize_pending', 'finalizing')
                            AND NEW.phase IN ('terminal_pending', 'quarantined'))) THEN
                    RAISE EXCEPTION 'invalid control transition' USING ERRCODE = '23514';
                END IF;
            END IF;
            IF NEW.plan IS NOT NULL AND OLD.plan IS NULL AND
               (OLD.phase <> 'preparing' OR NEW.phase <> 'prepared' OR NEW.prepared_at > clock_timestamp()) THEN
                RAISE EXCEPTION 'plan requires the prepare checkpoint' USING ERRCODE = '23514';
            END IF;
            IF NEW.phase = 'executing' AND OLD.phase = 'prepared' AND
               (OLD.ownership_kind <> 'execution' OR attempt.execute_intent_at IS NULL
                OR attempt.plan_fingerprint IS DISTINCT FROM NEW.plan_fingerprint
                OR attempt.operation_id IS DISTINCT FROM NEW.operation_id) THEN
                RAISE EXCEPTION 'external execute requires durable matching intent' USING ERRCODE = '23514';
            END IF;
            IF NEW.finalization_status IS NOT NULL AND OLD.finalization_status IS NULL THEN
                SELECT a.stage, a.outcome_kind, a.quiescent_at, a.no_start_at INTO final_attempt
                  FROM public.background_job_attempts a
                  WHERE a.tenant_id = NEW.tenant_id AND a.job_id = NEW.id AND a.id = NEW.final_outcome_attempt_id;
                IF NOT FOUND OR NEW.final_outcome_attempt_id IS DISTINCT FROM NEW.current_attempt_id
                   OR final_attempt.stage <> 'normal'
                   OR (final_attempt.quiescent_at IS NULL AND final_attempt.no_start_at IS NULL)
                   OR (NEW.finalization_status = 'succeeded' AND final_attempt.outcome_kind IS DISTINCT FROM 'succeeded')
                   OR (NEW.finalization_reason = 'external_failed' AND final_attempt.outcome_kind IS DISTINCT FROM 'failed')
                   OR (NEW.finalization_reason = 'external_cancelled' AND final_attempt.outcome_kind IS DISTINCT FROM 'cancelled')
                   OR NEW.phase <> 'finalize_pending' THEN
                    RAISE EXCEPTION 'finalization requires positive selected-attempt evidence' USING ERRCODE = '23514';
                END IF;
            END IF;
            IF NEW.profile = 'external'
               AND (NEW.phase = 'pending' OR (admission AND NEW.normal_attempts <> OLD.normal_attempts))
               AND (NEW.finalization_status IS NOT NULL OR EXISTS (
                   SELECT 1 FROM public.background_job_attempts a WHERE a.tenant_id = NEW.tenant_id
                       AND a.job_id = NEW.id AND a.stage = 'normal' AND a.outcome_kind = 'succeeded')) THEN
                RAISE EXCEPTION 'known final outcome cannot reopen normal execution' USING ERRCODE = '23514';
            END IF;
            IF NEW.profile = 'external'
               AND (NEW.phase = 'pending' OR (admission AND NEW.normal_attempts <> OLD.normal_attempts))
               AND EXISTS (SELECT 1 FROM public.background_job_attempts a WHERE a.tenant_id = NEW.tenant_id
                   AND a.job_id = NEW.id AND a.stage = 'normal'
                   AND (NOT admission OR a.id IS DISTINCT FROM NEW.current_attempt_id)
                   AND a.no_start_at IS NULL AND (a.quiescent_at IS NULL OR a.outcome_kind IS NULL)) THEN
                RAISE EXCEPTION 'external retry requires positive prior-attempt evidence' USING ERRCODE = '23514';
            END IF;
            IF NEW.phase IN ('pending', 'finalize_pending', 'terminal_pending') AND OLD.phase <> NEW.phase AND NEW.profile = 'external'
               AND EXISTS (SELECT 1 FROM public.background_job_attempts a WHERE a.tenant_id = NEW.tenant_id
                   AND a.job_id = NEW.id AND a.execute_intent_at IS NOT NULL
                   AND (a.quiescent_at IS NULL OR a.outcome_kind IS NULL)) THEN
                RAISE EXCEPTION 'unresolved external effects cannot retry or terminalize' USING ERRCODE = '23514';
            END IF;
            IF NEW.phase = 'completed' AND (NEW.processed_at IS NULL
               OR (NEW.profile = 'external' AND (OLD.phase <> 'finalizing' OR NEW.finalization_status <> 'succeeded'))) THEN
                RAISE EXCEPTION 'successful completion requires its business stage' USING ERRCODE = '23514';
            END IF;
            IF NEW.dead_lettered_at IS NOT NULL AND OLD.dead_lettered_at IS NULL THEN
                IF current_user = 'learnstack_app' AND
                    (OLD.phase <> 'finalizing' OR NEW.finalization_status IS DISTINCT FROM 'failed' OR NEW.plan IS NULL) THEN
                    RAISE EXCEPTION 'app terminal completion requires failure finalization' USING ERRCODE = '23514';
                END IF;
                IF current_user = 'learnstack_outbox_admin' AND NEW.plan IS NOT NULL THEN
                    NEW.business_finalization_incomplete := true;
                END IF;
            END IF;
            IF NEW.ownership_until > clock_timestamp() + interval '10 minutes' THEN
                RAISE EXCEPTION 'job ownership exceeds its lease bound' USING ERRCODE = '23514';
            END IF;
            IF NEW.bridge_token IS NOT NULL THEN
                IF (NEW.bridge_token IS DISTINCT FROM OLD.bridge_token AND
                        (NEW.phase NOT IN ('pending', 'finalize_pending') OR NEW.ownership_token IS NOT NULL))
                   OR NEW.bridge_until > clock_timestamp() + interval '10 minutes'
                   OR (NEW.bridge_token = OLD.bridge_token AND NEW.bridge_until > OLD.bridge_until
                       AND OLD.bridge_until <= clock_timestamp())
                   OR (NEW.bridge_token IS DISTINCT FROM OLD.bridge_token AND OLD.bridge_token IS NOT NULL
                       AND OLD.bridge_until > clock_timestamp()) THEN
                    RAISE EXCEPTION 'bridge requires bounded available ownership' USING ERRCODE = '23514';
                END IF;
            END IF;
            IF NOT admission AND NEW.ownership_kind = 'execution' AND
               (OLD.ownership_kind IS DISTINCT FROM 'execution' OR NEW.ownership_token IS DISTINCT FROM OLD.ownership_token) THEN
                RAISE EXCEPTION 'execution authority is minted only by counted admission' USING ERRCODE = '23514';
            END IF;
            IF NOT admission AND NEW.ownership_kind = 'recovery' AND
               (NEW.phase NOT IN ('reconciling', 'terminal_pending')
                OR (OLD.ownership_kind IS DISTINCT FROM 'recovery' AND NEW.ownership_token IS NOT DISTINCT FROM OLD.ownership_token)) THEN
                RAISE EXCEPTION 'recovery requires a separate non-executing token' USING ERRCODE = '23514';
            END IF;
            IF NOT admission AND OLD.ownership_kind = 'execution' AND NEW.ownership_token IS NULL
               AND NEW.phase = OLD.phase THEN
                RAISE EXCEPTION 'execution authority cannot be silently discarded' USING ERRCODE = '23514';
            END IF;
            IF NEW.ownership_token IS NOT NULL AND NOT admission THEN
                IF NEW.ownership_kind = 'routing' AND (NEW.phase NOT IN ('pending', 'finalize_pending')
                    OR NEW.available_after > clock_timestamp() OR NEW.original_due_at > clock_timestamp()
                    OR OLD.ownership_kind IN ('execution', 'recovery')) THEN
                    RAISE EXCEPTION 'routing cannot replace execution authority' USING ERRCODE = '23514';
                END IF;
                IF NEW.ownership_token = OLD.ownership_token AND NEW.ownership_until > OLD.ownership_until
                   AND OLD.ownership_until <= clock_timestamp() THEN
                    RAISE EXCEPTION 'expired ownership cannot be renewed' USING ERRCODE = '23514';
                END IF;
                IF NEW.ownership_kind = 'execution' AND NEW.ownership_until > NEW.attempt_deadline THEN
                    RAISE EXCEPTION 'execution lease exceeds immutable deadline' USING ERRCODE = '23514';
                END IF;
                IF NEW.ownership_token IS DISTINCT FROM OLD.ownership_token AND OLD.ownership_token IS NOT NULL
                   AND OLD.ownership_until > clock_timestamp() THEN
                    RAISE EXCEPTION 'live ownership cannot be replaced' USING ERRCODE = '23514';
                END IF;
            END IF;
            IF NEW.phase IN ('pending', 'finalize_pending') AND NEW.phase <> OLD.phase THEN
                NEW.wake_generation := OLD.wake_generation + 1;
                IF current_user = 'learnstack_app' THEN NEW.available_after := clock_timestamp(); END IF;
                NEW.handoff_check_at := clock_timestamp();
                NEW.bridge_token := NULL; NEW.bridge_until := NULL; NEW.vendor_job_id := NULL;
                NEW.ownership_token := NULL; NEW.ownership_kind := NULL; NEW.ownership_until := NULL;
            ELSIF NEW.wake_generation <> OLD.wake_generation THEN
                RAISE EXCEPTION 'wake generation changes only with a new eligibility cycle' USING ERRCODE = '23514';
            END IF;
            IF NEW.phase IN ('completed', 'quarantined') OR NEW.dead_lettered_at IS NOT NULL
               OR (NEW.phase = 'terminal_pending' AND OLD.phase <> 'terminal_pending') THEN
                NEW.bridge_token := NULL; NEW.bridge_until := NULL; NEW.vendor_job_id := NULL;
                NEW.ownership_token := NULL; NEW.ownership_kind := NULL; NEW.ownership_until := NULL;
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
        ALTER TABLE background_jobs DROP CONSTRAINT ck_background_jobs_errors;
        ALTER TABLE background_jobs ADD CONSTRAINT ck_background_jobs_errors CHECK ((last_error IS NULL OR
                (octet_length(last_error) <= 256 AND last_error IN ('deployment_mismatch', 'capacity_unavailable', 'concurrency_conflict', 'rate_limited', 'dependency_unavailable', 'audit_unavailable', 'unexpected_fault', 'permanent_failure', 'lease_lost', 'deadline_exceeded', 'shutdown', 'unknown_outcome', 'invalid_control', 'finalize_refused', 'finalize_exhausted')))
                AND (quarantine_reason IS NULL OR quarantine_reason IN ('unknown_outcome', 'probe_exhausted', 'invalid_control'))
                AND (terminal_reason IS NULL OR terminal_reason IN ('retired_definition', 'invalid_envelope', 'permanent_failure', 'retry_exhausted', 'finalize_refused', 'finalize_exhausted')));
        ALTER TABLE background_job_attempts DROP CONSTRAINT ck_background_job_attempts_failure_policy;
        ALTER TABLE background_job_attempts DROP COLUMN failure_policy;
        """);
}
