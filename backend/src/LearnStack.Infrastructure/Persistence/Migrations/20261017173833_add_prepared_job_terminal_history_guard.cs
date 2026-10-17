using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LearnStack.Infrastructure.Persistence.Migrations;

/// <summary>Require complete positive prepared history for control terminal completion.</summary>
public partial class add_prepared_job_terminal_history_guard : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE OR REPLACE FUNCTION public.fn_background_jobs_transition() RETURNS trigger
        LANGUAGE plpgsql SECURITY INVOKER SET search_path = pg_catalog AS $$
        DECLARE attempt record; final_attempt record; admission boolean;
            expected_status text; expected_reason text; expected_terminal text; recovery_cycle boolean := false;
        BEGIN
            SELECT NULL::text AS stage, NULL::timestamptz AS execute_intent_at, NULL::timestamptz AS quiescent_at,
                   NULL::timestamptz AS no_start_at, NULL::text AS outcome_kind, NULL::text AS failure_policy, NULL::text AS plan_fingerprint,
                   NULL::uuid AS operation_id, NULL::integer AS ordinal, NULL::timestamptz AS deadline,
                   NULL::uuid AS organization_id, NULL::uuid AS admission_token INTO attempt;
            IF OLD.processed_at IS NOT NULL OR OLD.dead_lettered_at IS NOT NULL OR OLD.phase = 'quarantined' THEN
                IF NEW IS DISTINCT FROM OLD THEN
                    RAISE EXCEPTION 'completed or quarantined job is frozen' USING ERRCODE = '23514';
                END IF;
                RETURN NEW;
            END IF;
            IF OLD.terminal_reason IS NOT NULL AND NEW.terminal_reason IS DISTINCT FROM OLD.terminal_reason THEN
                RAISE EXCEPTION 'a terminal request retains its original reason' USING ERRCODE='23514';
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
                       a.plan_fingerprint, a.operation_id, a.ordinal, a.deadline, a.organization_id, a.admission_token INTO attempt
                  FROM public.background_job_attempts a
                  WHERE a.tenant_id = NEW.tenant_id AND a.job_id = NEW.id AND a.id = NEW.current_attempt_id;
                IF NOT FOUND THEN RAISE EXCEPTION 'current attempt unavailable' USING ERRCODE = '23514'; END IF;
            END IF;
            -- A parked external wake is scheduling only. Clearing an owner must
            -- independently reject a still-live lease; the non-NULL takeover guard
            -- below cannot protect a transition whose new token is NULL.
            IF NOT admission AND NEW.profile='external' AND NEW.phase='reconciling' AND NEW.ownership_token IS NULL THEN
                IF NEW.finalization_status IS NOT NULL OR NEW.final_outcome_attempt_id IS NOT NULL OR NEW.finalize_attempts<>0
                   OR attempt.stage IS DISTINCT FROM 'normal' OR attempt.ordinal IS DISTINCT FROM NEW.normal_attempts
                   OR attempt.deadline IS DISTINCT FROM NEW.attempt_deadline
                   OR attempt.organization_id IS DISTINCT FROM NEW.organization_id
                   OR NEW.normal_attempts NOT BETWEEN 1 AND 6 OR NEW.capacity_group IS NULL THEN
                    RAISE EXCEPTION 'external recovery wake requires the current normal attempt' USING ERRCODE='23514';
                END IF;
                IF OLD.ownership_token IS NOT NULL AND
                   (current_user<>'learnstack_outbox_admin' OR OLD.ownership_until>clock_timestamp()
                    OR NEW.probe_after IS NULL OR isfinite(NEW.probe_after) IS NOT TRUE
                    OR NOT ((OLD.phase IN ('preparing','prepared','executing') AND OLD.ownership_kind='execution'
                             AND OLD.ownership_token=attempt.admission_token)
                         OR (OLD.phase='reconciling' AND OLD.ownership_kind='recovery'
                             AND OLD.ownership_token<>attempt.admission_token))) THEN
                    RAISE EXCEPTION 'external recovery wake cannot discard live or unrelated ownership' USING ERRCODE='23514';
                END IF;
                IF OLD.phase='reconciling' AND OLD.ownership_token IS NULL AND OLD.probe_after IS NOT NULL
                   AND NEW.probe_after IS DISTINCT FROM OLD.probe_after THEN
                    RAISE EXCEPTION 'an unowned recovery wake retains its scheduling cycle' USING ERRCODE='23514';
                END IF;
                recovery_cycle := current_user='learnstack_outbox_admin' AND NEW.probe_after IS NOT NULL
                    AND isfinite(NEW.probe_after) AND (OLD.ownership_token IS NOT NULL
                        OR (OLD.phase='reconciling' AND OLD.probe_after IS NULL));
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
                -- The request may precede NoStart. Only its first durable completion
                -- requires complete positive no-effect and released-capacity history.
                IF NEW.profile='external' AND NEW.plan IS NULL THEN
                    IF (current_user='learnstack_outbox_admin' AND OLD.phase='terminal_pending'
                        AND NEW.phase='terminal_pending' AND OLD.terminal_reason IS NOT NULL
                        AND OLD.ownership_kind='recovery' AND OLD.ownership_token IS NOT NULL
                        AND OLD.ownership_until>clock_timestamp()
                        AND NEW.ownership_token=OLD.ownership_token AND NEW.ownership_kind='recovery'
                        AND NEW.processed_at IS NULL AND NEW.plan_fingerprint IS NULL
                        AND NEW.prepared_at IS NULL AND NEW.operation_id IS NULL
                        AND NEW.finalization_status IS NULL AND NEW.finalization_reason IS NULL
                        AND NEW.final_outcome_attempt_id IS NULL AND NEW.finalize_attempts=0
                        AND NOT NEW.business_finalization_incomplete) IS NOT TRUE THEN
                        RAISE EXCEPTION 'no-plan terminal completion requires its exact live control owner' USING ERRCODE='23514';
                    END IF;
                    IF NEW.normal_attempts=0 THEN
                        IF NEW.terminal_reason NOT IN ('retired_definition','invalid_envelope')
                           OR NEW.current_attempt_id IS NOT NULL OR NEW.attempt_deadline IS NOT NULL
                           OR EXISTS (SELECT 1 FROM public.background_job_attempts a
                               WHERE a.tenant_id=NEW.tenant_id AND a.job_id=NEW.id)
                           OR EXISTS (SELECT 1 FROM public.background_job_reservations r
                               WHERE r.tenant_id=NEW.tenant_id AND r.job_id=NEW.id) THEN
                            RAISE EXCEPTION 'unadmitted terminal completion cannot invent attempt history' USING ERRCODE='23514';
                        END IF;
                    ELSE
                        IF (NEW.normal_attempts BETWEEN 1 AND 6 AND NEW.capacity_group IS NOT NULL
                            AND (NEW.terminal_reason='permanent_failure'
                                OR NEW.terminal_reason='retry_exhausted' AND NEW.normal_attempts=6)
                            AND NEW.current_attempt_id IS NOT NULL AND NEW.attempt_deadline IS NOT NULL) IS NOT TRUE
                           OR (SELECT count(*) FROM public.background_job_attempts a
                               WHERE a.tenant_id=NEW.tenant_id AND a.job_id=NEW.id)<>NEW.normal_attempts
                           OR (SELECT count(*) FROM public.background_job_reservations r
                               WHERE r.tenant_id=NEW.tenant_id AND r.job_id=NEW.id)<>NEW.normal_attempts
                           OR EXISTS (SELECT 1 FROM public.background_job_attempts a
                               LEFT JOIN public.background_job_reservations r ON r.tenant_id=a.tenant_id
                                   AND r.job_id=a.job_id AND r.attempt_id=a.id AND r.capacity_group=NEW.capacity_group
                               WHERE a.tenant_id=NEW.tenant_id AND a.job_id=NEW.id AND (
                                   a.organization_id IS DISTINCT FROM NEW.organization_id OR a.stage<>'normal'
                                   OR a.ordinal NOT BETWEEN 1 AND NEW.normal_attempts
                                   OR a.final_outcome_attempt_id IS NOT NULL OR a.plan_fingerprint IS NOT NULL
                                   OR a.operation_id IS NOT NULL OR a.execute_intent_at IS NOT NULL
                                   OR a.outcome_kind IS NOT NULL OR a.outcome_at IS NOT NULL
                                   OR a.failure_policy IS NOT NULL OR a.quiescence_id IS NOT NULL
                                   OR a.quiescence_kind IS NOT NULL OR a.quiescent_at IS NOT NULL
                                   OR a.transactional_cleanup_at IS NOT NULL
                                   OR a.no_start_at IS NULL OR isfinite(a.no_start_at) IS NOT TRUE
                                   OR a.no_start_at<a.admitted_at OR a.no_start_at>clock_timestamp()
                                   OR a.ordinal=NEW.normal_attempts AND (a.id<>NEW.current_attempt_id
                                       OR a.deadline IS DISTINCT FROM NEW.attempt_deadline)
                                   OR r.attempt_id IS NULL OR r.released_at IS NULL OR isfinite(r.released_at) IS NOT TRUE
                                   OR r.released_at<a.no_start_at OR r.released_at>clock_timestamp()
                                   OR r.release_reason IS DISTINCT FROM 'never_started')) THEN
                            RAISE EXCEPTION 'no-plan terminal completion requires complete positive no-start history' USING ERRCODE='23514';
                        END IF;
                    END IF;
                END IF;
                IF current_user = 'learnstack_app' AND
                    (OLD.phase <> 'finalizing' OR NEW.finalization_status IS DISTINCT FROM 'failed' OR NEW.plan IS NULL) THEN
                    RAISE EXCEPTION 'app terminal completion requires failure finalization' USING ERRCODE = '23514';
                END IF;
                IF current_user = 'learnstack_outbox_admin' AND NEW.plan IS NOT NULL THEN
                    IF (OLD.phase='terminal_pending' AND NEW.phase='terminal_pending'
                        AND OLD.terminal_reason IS NOT NULL AND NEW.terminal_reason=OLD.terminal_reason
                        AND OLD.ownership_kind='recovery' AND OLD.ownership_token IS NOT NULL
                        AND OLD.ownership_until>clock_timestamp()
                        AND NEW.ownership_token=OLD.ownership_token AND NEW.ownership_kind='recovery'
                        AND NOT OLD.business_finalization_incomplete AND NOT NEW.business_finalization_incomplete
                        AND isfinite(NEW.dead_lettered_at) AND NEW.dead_lettered_at>=NEW.prepared_at
                        AND NEW.dead_lettered_at<=clock_timestamp()
                        AND NOT EXISTS (SELECT 1 FROM public.background_job_attempts a WHERE a.tenant_id=NEW.tenant_id AND a.job_id=NEW.id
                            AND a.admitted_at>NEW.dead_lettered_at)
                        AND NOT EXISTS (SELECT 1 FROM public.background_job_reservations r WHERE r.tenant_id=NEW.tenant_id AND r.job_id=NEW.id
                            AND r.released_at>NEW.dead_lettered_at)
                        AND (
                            NEW.profile='external' AND NEW.phase='terminal_pending' AND NEW.processed_at IS NULL
                            AND NEW.capacity_group IS NOT NULL AND NEW.normal_attempts BETWEEN 1 AND 6 AND NEW.finalize_attempts BETWEEN 0 AND 6
                            AND NEW.plan_fingerprint IS NOT NULL AND NEW.plan_fingerprint COLLATE "C" ~ '^[0-9a-f]{64}$'
                            AND NEW.operation_id IS NOT NULL AND NEW.operation_id<>'00000000-0000-0000-0000-000000000000'::uuid
                            AND NEW.prepared_at IS NOT NULL AND isfinite(NEW.prepared_at) AND NEW.prepared_at<=clock_timestamp()
                            AND NEW.current_attempt_id IS NOT NULL AND NEW.attempt_deadline IS NOT NULL
                            AND (NEW.terminal_reason IN ('retired_definition','invalid_envelope')
                                OR NEW.terminal_reason='finalize_refused' AND NEW.finalize_attempts BETWEEN 1 AND 6
                                OR NEW.terminal_reason='finalize_exhausted' AND NEW.finalize_attempts=6)
                            AND (SELECT count(*) FROM public.background_job_attempts a WHERE a.tenant_id=NEW.tenant_id AND a.job_id=NEW.id)=NEW.normal_attempts+NEW.finalize_attempts
                            AND (SELECT count(*) FROM public.background_job_attempts a WHERE a.tenant_id=NEW.tenant_id AND a.job_id=NEW.id AND a.stage='normal')=NEW.normal_attempts
                            AND (SELECT count(*) FROM public.background_job_reservations r WHERE r.tenant_id=NEW.tenant_id AND r.job_id=NEW.id)=NEW.normal_attempts
                            AND NOT EXISTS (SELECT 1 FROM public.background_job_reservations r
                                WHERE r.tenant_id=NEW.tenant_id AND r.job_id=NEW.id AND r.released_at IS NULL)
                            AND NOT EXISTS (SELECT 1 FROM public.background_job_attempts a LEFT JOIN public.background_job_reservations r
                                ON r.tenant_id=a.tenant_id AND r.job_id=a.job_id AND r.attempt_id=a.id AND r.capacity_group=NEW.capacity_group
                                WHERE a.tenant_id=NEW.tenant_id AND a.job_id=NEW.id AND (
                                    a.organization_id IS DISTINCT FROM NEW.organization_id
                                    OR a.admission_token='00000000-0000-0000-0000-000000000000'::uuid
                                    OR isfinite(a.admitted_at) IS NOT TRUE OR a.admitted_at>clock_timestamp()
                                    OR isfinite(a.deadline) IS NOT TRUE OR a.deadline<=a.admitted_at OR a.deadline>a.admitted_at+interval '24 hours'
                                    OR a.transactional_cleanup_at IS NOT NULL
                                    OR (a.stage='normal' AND (
                                        a.ordinal NOT BETWEEN 1 AND NEW.normal_attempts OR a.final_outcome_attempt_id IS NOT NULL
                                        OR ((a.plan_fingerprint IS NULL AND a.operation_id IS NULL)
                                            OR (a.plan_fingerprint=NEW.plan_fingerprint AND a.operation_id=NEW.operation_id)) IS NOT TRUE
                                        OR (a.ordinal<NEW.normal_attempts AND a.no_start_at IS NULL
                                            AND (a.outcome_kind IN ('failed','cancelled') AND a.failure_policy='transient_repeatable') IS NOT TRUE)
                                        OR ((a.no_start_at IS NOT NULL AND isfinite(a.no_start_at) AND a.no_start_at>=a.admitted_at
                                                AND a.no_start_at<=clock_timestamp() AND a.execute_intent_at IS NULL
                                                AND a.outcome_kind IS NULL AND a.outcome_at IS NULL AND a.failure_policy IS NULL
                                                AND a.quiescence_id IS NULL AND a.quiescence_kind IS NULL AND a.quiescent_at IS NULL
                                                AND r.attempt_id IS NOT NULL AND r.released_at IS NOT NULL AND isfinite(r.released_at)
                                                AND r.released_at>=a.no_start_at AND r.released_at<=clock_timestamp() AND r.release_reason='never_started')
                                            OR (a.no_start_at IS NULL AND a.plan_fingerprint=NEW.plan_fingerprint AND a.operation_id=NEW.operation_id
                                                AND a.execute_intent_at IS NOT NULL AND isfinite(a.execute_intent_at)
                                                AND a.execute_intent_at>=a.admitted_at AND a.execute_intent_at<=a.deadline
                                                AND a.outcome_at IS NOT NULL AND isfinite(a.outcome_at)
                                                AND a.outcome_at>=a.execute_intent_at AND a.outcome_at<=clock_timestamp()
                                                AND ((a.outcome_kind='succeeded' AND a.failure_policy IS NULL)
                                                    OR (a.outcome_kind='failed' AND a.failure_policy='permanent')
                                                    OR (a.outcome_kind IN ('failed','cancelled') AND a.failure_policy IN ('transient_repeatable','transient_not_repeatable')))
                                                AND a.quiescence_id IS NOT NULL AND a.quiescence_id<>'00000000-0000-0000-0000-000000000000'::uuid
                                                AND a.quiescence_kind IN ('provider_fenced','execution_terminated')
                                                AND a.quiescent_at IS NOT NULL AND isfinite(a.quiescent_at)
                                                AND a.quiescent_at>=a.execute_intent_at AND a.quiescent_at<=clock_timestamp()
                                                AND r.attempt_id IS NOT NULL AND r.released_at IS NOT NULL AND isfinite(r.released_at)
                                                AND r.released_at>=a.quiescent_at AND r.released_at>=a.outcome_at
                                                AND r.released_at<=clock_timestamp() AND r.release_reason='quiescent')) IS NOT TRUE))
                                    OR (a.stage='finalize' AND (
                                        a.ordinal NOT BETWEEN 1 AND NEW.finalize_attempts OR NEW.final_outcome_attempt_id IS NULL
                                        OR a.final_outcome_attempt_id IS DISTINCT FROM NEW.final_outcome_attempt_id
                                        OR a.plan_fingerprint IS NOT NULL OR a.operation_id IS NOT NULL OR a.execute_intent_at IS NOT NULL
                                        OR a.outcome_kind IS NOT NULL OR a.outcome_at IS NOT NULL OR a.failure_policy IS NOT NULL
                                        OR a.quiescence_id IS NOT NULL OR a.quiescence_kind IS NOT NULL OR a.quiescent_at IS NOT NULL
                                        OR a.no_start_at IS NOT NULL OR r.attempt_id IS NOT NULL))
                                    OR a.stage NOT IN ('normal','finalize')))
                            AND EXISTS (SELECT 1 FROM public.background_job_attempts current_attempt
                                WHERE current_attempt.tenant_id=NEW.tenant_id AND current_attempt.job_id=NEW.id AND current_attempt.id=NEW.current_attempt_id
                                    AND current_attempt.organization_id IS NOT DISTINCT FROM NEW.organization_id
                                    AND current_attempt.deadline=NEW.attempt_deadline
                                    AND ((NEW.finalize_attempts=0 AND current_attempt.stage='normal' AND current_attempt.ordinal=NEW.normal_attempts)
                                        OR (NEW.finalize_attempts>0 AND current_attempt.stage='finalize' AND current_attempt.ordinal=NEW.finalize_attempts
                                            AND current_attempt.final_outcome_attempt_id=NEW.final_outcome_attempt_id)))
                            AND ((NEW.final_outcome_attempt_id IS NULL AND NEW.finalization_status IS NULL AND NEW.finalization_reason IS NULL
                                    AND NEW.finalize_attempts=0 AND NEW.terminal_reason IN ('retired_definition','invalid_envelope'))
                                OR (NEW.final_outcome_attempt_id IS NOT NULL AND EXISTS (SELECT 1 FROM public.background_job_attempts selected
                                    WHERE selected.tenant_id=NEW.tenant_id AND selected.job_id=NEW.id AND selected.id=NEW.final_outcome_attempt_id
                                        AND selected.organization_id IS NOT DISTINCT FROM NEW.organization_id AND selected.stage='normal'
                                        AND selected.ordinal=NEW.normal_attempts AND selected.plan_fingerprint=NEW.plan_fingerprint AND selected.operation_id=NEW.operation_id
                                        AND ((selected.outcome_kind='succeeded' AND selected.failure_policy IS NULL
                                                AND NEW.finalization_status='succeeded' AND NEW.finalization_reason='none')
                                            OR (selected.outcome_kind='failed' AND selected.failure_policy='permanent'
                                                AND NEW.finalization_status='failed' AND NEW.finalization_reason='external_failed')
                                            OR (selected.outcome_kind IN ('failed','cancelled') AND selected.failure_policy='transient_not_repeatable'
                                                AND NEW.finalization_status='failed' AND NEW.finalization_reason=CASE selected.outcome_kind
                                                    WHEN 'failed' THEN 'external_failed' ELSE 'external_cancelled' END)
                                            OR (selected.outcome_kind IN ('failed','cancelled') AND selected.failure_policy='transient_repeatable'
                                                AND NEW.normal_attempts=6 AND NEW.finalization_status='failed' AND NEW.finalization_reason='normal_attempts_exhausted')
                                            OR (selected.outcome_kind IS NULL AND selected.no_start_at IS NOT NULL AND NEW.finalization_status='failed'
                                                AND (NEW.finalization_reason='permanent_failure' OR NEW.finalization_reason='normal_attempts_exhausted' AND NEW.normal_attempts=6))))))
                        )) IS NOT TRUE THEN
                        RAISE EXCEPTION 'prepared terminal completion requires exact live control and positive history' USING ERRCODE='23514';
                    END IF;
                    -- Recheck after the bounded history reads, not only before their work.
                    IF OLD.ownership_until<=clock_timestamp() THEN
                        RAISE EXCEPTION 'prepared terminal completion lost its live control owner' USING ERRCODE='23514';
                    END IF;
                    NEW.business_finalization_incomplete := true;
                END IF;
            END IF;
            IF NEW.ownership_until > clock_timestamp() + interval '10 minutes' THEN
                RAISE EXCEPTION 'job ownership exceeds its lease bound' USING ERRCODE = '23514';
            END IF;
            IF NEW.bridge_token IS NOT NULL THEN
                IF (NEW.bridge_token IS DISTINCT FROM OLD.bridge_token AND
                        ((NEW.phase NOT IN ('pending', 'finalize_pending') AND NOT (NEW.profile='external'
                            AND NEW.phase='reconciling' AND NEW.probe_after IS NOT NULL AND isfinite(NEW.probe_after)
                            AND NEW.normal_attempts BETWEEN 1 AND 6 AND NEW.finalize_attempts=0
                            AND NEW.finalization_status IS NULL AND NEW.final_outcome_attempt_id IS NULL))
                        OR NEW.ownership_token IS NOT NULL))
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
            IF (NEW.phase IN ('pending', 'finalize_pending') AND NEW.phase <> OLD.phase) OR recovery_cycle THEN
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
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE OR REPLACE FUNCTION public.fn_background_jobs_transition() RETURNS trigger
        LANGUAGE plpgsql SECURITY INVOKER SET search_path = pg_catalog AS $$
        DECLARE attempt record; final_attempt record; admission boolean;
            expected_status text; expected_reason text; expected_terminal text; recovery_cycle boolean := false;
        BEGIN
            SELECT NULL::text AS stage, NULL::timestamptz AS execute_intent_at, NULL::timestamptz AS quiescent_at,
                   NULL::timestamptz AS no_start_at, NULL::text AS outcome_kind, NULL::text AS failure_policy, NULL::text AS plan_fingerprint,
                   NULL::uuid AS operation_id, NULL::integer AS ordinal, NULL::timestamptz AS deadline,
                   NULL::uuid AS organization_id, NULL::uuid AS admission_token INTO attempt;
            IF OLD.processed_at IS NOT NULL OR OLD.dead_lettered_at IS NOT NULL OR OLD.phase = 'quarantined' THEN
                IF NEW IS DISTINCT FROM OLD THEN
                    RAISE EXCEPTION 'completed or quarantined job is frozen' USING ERRCODE = '23514';
                END IF;
                RETURN NEW;
            END IF;
            IF OLD.terminal_reason IS NOT NULL AND NEW.terminal_reason IS DISTINCT FROM OLD.terminal_reason THEN
                RAISE EXCEPTION 'a terminal request retains its original reason' USING ERRCODE='23514';
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
                       a.plan_fingerprint, a.operation_id, a.ordinal, a.deadline, a.organization_id, a.admission_token INTO attempt
                  FROM public.background_job_attempts a
                  WHERE a.tenant_id = NEW.tenant_id AND a.job_id = NEW.id AND a.id = NEW.current_attempt_id;
                IF NOT FOUND THEN RAISE EXCEPTION 'current attempt unavailable' USING ERRCODE = '23514'; END IF;
            END IF;
            -- A parked external wake is scheduling only. Clearing an owner must
            -- independently reject a still-live lease; the non-NULL takeover guard
            -- below cannot protect a transition whose new token is NULL.
            IF NOT admission AND NEW.profile='external' AND NEW.phase='reconciling' AND NEW.ownership_token IS NULL THEN
                IF NEW.finalization_status IS NOT NULL OR NEW.final_outcome_attempt_id IS NOT NULL OR NEW.finalize_attempts<>0
                   OR attempt.stage IS DISTINCT FROM 'normal' OR attempt.ordinal IS DISTINCT FROM NEW.normal_attempts
                   OR attempt.deadline IS DISTINCT FROM NEW.attempt_deadline
                   OR attempt.organization_id IS DISTINCT FROM NEW.organization_id
                   OR NEW.normal_attempts NOT BETWEEN 1 AND 6 OR NEW.capacity_group IS NULL THEN
                    RAISE EXCEPTION 'external recovery wake requires the current normal attempt' USING ERRCODE='23514';
                END IF;
                IF OLD.ownership_token IS NOT NULL AND
                   (current_user<>'learnstack_outbox_admin' OR OLD.ownership_until>clock_timestamp()
                    OR NEW.probe_after IS NULL OR isfinite(NEW.probe_after) IS NOT TRUE
                    OR NOT ((OLD.phase IN ('preparing','prepared','executing') AND OLD.ownership_kind='execution'
                             AND OLD.ownership_token=attempt.admission_token)
                         OR (OLD.phase='reconciling' AND OLD.ownership_kind='recovery'
                             AND OLD.ownership_token<>attempt.admission_token))) THEN
                    RAISE EXCEPTION 'external recovery wake cannot discard live or unrelated ownership' USING ERRCODE='23514';
                END IF;
                IF OLD.phase='reconciling' AND OLD.ownership_token IS NULL AND OLD.probe_after IS NOT NULL
                   AND NEW.probe_after IS DISTINCT FROM OLD.probe_after THEN
                    RAISE EXCEPTION 'an unowned recovery wake retains its scheduling cycle' USING ERRCODE='23514';
                END IF;
                recovery_cycle := current_user='learnstack_outbox_admin' AND NEW.probe_after IS NOT NULL
                    AND isfinite(NEW.probe_after) AND (OLD.ownership_token IS NOT NULL
                        OR (OLD.phase='reconciling' AND OLD.probe_after IS NULL));
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
                -- The request may precede NoStart. Only its first durable completion
                -- requires complete positive no-effect and released-capacity history.
                IF NEW.profile='external' AND NEW.plan IS NULL THEN
                    IF (current_user='learnstack_outbox_admin' AND OLD.phase='terminal_pending'
                        AND NEW.phase='terminal_pending' AND OLD.terminal_reason IS NOT NULL
                        AND OLD.ownership_kind='recovery' AND OLD.ownership_token IS NOT NULL
                        AND OLD.ownership_until>clock_timestamp()
                        AND NEW.ownership_token=OLD.ownership_token AND NEW.ownership_kind='recovery'
                        AND NEW.processed_at IS NULL AND NEW.plan_fingerprint IS NULL
                        AND NEW.prepared_at IS NULL AND NEW.operation_id IS NULL
                        AND NEW.finalization_status IS NULL AND NEW.finalization_reason IS NULL
                        AND NEW.final_outcome_attempt_id IS NULL AND NEW.finalize_attempts=0
                        AND NOT NEW.business_finalization_incomplete) IS NOT TRUE THEN
                        RAISE EXCEPTION 'no-plan terminal completion requires its exact live control owner' USING ERRCODE='23514';
                    END IF;
                    IF NEW.normal_attempts=0 THEN
                        IF NEW.terminal_reason NOT IN ('retired_definition','invalid_envelope')
                           OR NEW.current_attempt_id IS NOT NULL OR NEW.attempt_deadline IS NOT NULL
                           OR EXISTS (SELECT 1 FROM public.background_job_attempts a
                               WHERE a.tenant_id=NEW.tenant_id AND a.job_id=NEW.id)
                           OR EXISTS (SELECT 1 FROM public.background_job_reservations r
                               WHERE r.tenant_id=NEW.tenant_id AND r.job_id=NEW.id) THEN
                            RAISE EXCEPTION 'unadmitted terminal completion cannot invent attempt history' USING ERRCODE='23514';
                        END IF;
                    ELSE
                        IF (NEW.normal_attempts BETWEEN 1 AND 6 AND NEW.capacity_group IS NOT NULL
                            AND (NEW.terminal_reason='permanent_failure'
                                OR NEW.terminal_reason='retry_exhausted' AND NEW.normal_attempts=6)
                            AND NEW.current_attempt_id IS NOT NULL AND NEW.attempt_deadline IS NOT NULL) IS NOT TRUE
                           OR (SELECT count(*) FROM public.background_job_attempts a
                               WHERE a.tenant_id=NEW.tenant_id AND a.job_id=NEW.id)<>NEW.normal_attempts
                           OR (SELECT count(*) FROM public.background_job_reservations r
                               WHERE r.tenant_id=NEW.tenant_id AND r.job_id=NEW.id)<>NEW.normal_attempts
                           OR EXISTS (SELECT 1 FROM public.background_job_attempts a
                               LEFT JOIN public.background_job_reservations r ON r.tenant_id=a.tenant_id
                                   AND r.job_id=a.job_id AND r.attempt_id=a.id AND r.capacity_group=NEW.capacity_group
                               WHERE a.tenant_id=NEW.tenant_id AND a.job_id=NEW.id AND (
                                   a.organization_id IS DISTINCT FROM NEW.organization_id OR a.stage<>'normal'
                                   OR a.ordinal NOT BETWEEN 1 AND NEW.normal_attempts
                                   OR a.final_outcome_attempt_id IS NOT NULL OR a.plan_fingerprint IS NOT NULL
                                   OR a.operation_id IS NOT NULL OR a.execute_intent_at IS NOT NULL
                                   OR a.outcome_kind IS NOT NULL OR a.outcome_at IS NOT NULL
                                   OR a.failure_policy IS NOT NULL OR a.quiescence_id IS NOT NULL
                                   OR a.quiescence_kind IS NOT NULL OR a.quiescent_at IS NOT NULL
                                   OR a.transactional_cleanup_at IS NOT NULL
                                   OR a.no_start_at IS NULL OR isfinite(a.no_start_at) IS NOT TRUE
                                   OR a.no_start_at<a.admitted_at OR a.no_start_at>clock_timestamp()
                                   OR a.ordinal=NEW.normal_attempts AND (a.id<>NEW.current_attempt_id
                                       OR a.deadline IS DISTINCT FROM NEW.attempt_deadline)
                                   OR r.attempt_id IS NULL OR r.released_at IS NULL OR isfinite(r.released_at) IS NOT TRUE
                                   OR r.released_at<a.no_start_at OR r.released_at>clock_timestamp()
                                   OR r.release_reason IS DISTINCT FROM 'never_started')) THEN
                            RAISE EXCEPTION 'no-plan terminal completion requires complete positive no-start history' USING ERRCODE='23514';
                        END IF;
                    END IF;
                END IF;
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
                        ((NEW.phase NOT IN ('pending', 'finalize_pending') AND NOT (NEW.profile='external'
                            AND NEW.phase='reconciling' AND NEW.probe_after IS NOT NULL AND isfinite(NEW.probe_after)
                            AND NEW.normal_attempts BETWEEN 1 AND 6 AND NEW.finalize_attempts=0
                            AND NEW.finalization_status IS NULL AND NEW.final_outcome_attempt_id IS NULL))
                        OR NEW.ownership_token IS NOT NULL))
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
            IF (NEW.phase IN ('pending', 'finalize_pending') AND NEW.phase <> OLD.phase) OR recovery_cycle THEN
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
        """);
}
