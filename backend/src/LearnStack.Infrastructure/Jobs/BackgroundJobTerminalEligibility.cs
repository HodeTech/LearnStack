namespace LearnStack.Infrastructure.Jobs;

/// <summary>Payload-free returned-row proof, shared by claim, fence and historical terminal confirmation.</summary>
internal static partial class BackgroundJobTerminalEligibility
{
    internal const string NoPlan = """
        j.profile='external' AND j.phase='terminal_pending'
        AND j.plan_fingerprint IS NULL AND j.prepared_at IS NULL AND j.operation_id IS NULL
        AND j.finalization_status IS NULL AND j.finalization_reason IS NULL
        AND j.final_outcome_attempt_id IS NULL AND j.finalize_attempts=0 AND NOT j.business_finalization_incomplete
        AND ((j.normal_attempts=0 AND j.current_attempt_id IS NULL AND j.attempt_deadline IS NULL
                AND j.terminal_reason IN ('retired_definition','invalid_envelope')
                AND NOT EXISTS (SELECT 1 FROM background_job_attempts a WHERE a.tenant_id=j.tenant_id AND a.job_id=j.id)
                AND NOT EXISTS (SELECT 1 FROM background_job_reservations r WHERE r.tenant_id=j.tenant_id AND r.job_id=j.id))
            OR (j.normal_attempts BETWEEN 1 AND 6 AND j.capacity_group IS NOT NULL
                AND j.current_attempt_id IS NOT NULL AND j.attempt_deadline IS NOT NULL
                AND (j.terminal_reason IN ('permanent_failure','retired_definition','invalid_envelope') OR j.terminal_reason='retry_exhausted' AND j.normal_attempts=6)
                AND (SELECT count(*) FROM background_job_attempts a WHERE a.tenant_id=j.tenant_id AND a.job_id=j.id)=j.normal_attempts
                AND (SELECT count(*) FROM background_job_reservations r WHERE r.tenant_id=j.tenant_id AND r.job_id=j.id)=j.normal_attempts
                AND NOT EXISTS (SELECT 1 FROM background_job_attempts a LEFT JOIN background_job_reservations r
                    ON r.tenant_id=a.tenant_id AND r.job_id=a.job_id AND r.attempt_id=a.id AND r.capacity_group=j.capacity_group
                    WHERE a.tenant_id=j.tenant_id AND a.job_id=j.id AND (
                        a.organization_id IS DISTINCT FROM j.organization_id OR a.stage<>'normal'
                        OR a.ordinal NOT BETWEEN 1 AND j.normal_attempts OR a.final_outcome_attempt_id IS NOT NULL
                        OR a.plan_fingerprint IS NOT NULL OR a.operation_id IS NOT NULL OR a.execute_intent_at IS NOT NULL
                        OR a.outcome_kind IS NOT NULL OR a.outcome_at IS NOT NULL OR a.failure_policy IS NOT NULL
                        OR a.quiescence_id IS NOT NULL OR a.quiescence_kind IS NOT NULL OR a.quiescent_at IS NOT NULL
                        OR a.transactional_cleanup_at IS NOT NULL OR a.no_start_at IS NULL
                        OR isfinite(a.no_start_at) IS NOT TRUE OR a.no_start_at<a.admitted_at OR a.no_start_at>clock_timestamp()
                        OR a.ordinal=j.normal_attempts AND (a.id<>j.current_attempt_id OR a.deadline IS DISTINCT FROM j.attempt_deadline)
                        OR r.attempt_id IS NULL OR r.released_at IS NULL OR isfinite(r.released_at) IS NOT TRUE
                        OR r.released_at<a.no_start_at OR r.released_at>clock_timestamp()
                        OR r.release_reason IS DISTINCT FROM 'never_started'))))
        """;

    // Stable metadata-only history: usable before and after the trigger stamps incomplete.
    internal const string PreparedHistory = """
        j.profile='external' AND j.phase='terminal_pending' AND j.processed_at IS NULL
        AND j.capacity_group IS NOT NULL AND j.normal_attempts BETWEEN 1 AND 6 AND j.finalize_attempts BETWEEN 0 AND 6
        AND j.plan_fingerprint IS NOT NULL AND j.plan_fingerprint COLLATE "C" ~ '^[0-9a-f]{64}$'
        AND j.operation_id IS NOT NULL AND j.operation_id<>'00000000-0000-0000-0000-000000000000'::uuid
        AND j.prepared_at IS NOT NULL AND isfinite(j.prepared_at) AND j.prepared_at<=clock_timestamp()
        AND j.current_attempt_id IS NOT NULL AND j.attempt_deadline IS NOT NULL
        AND (j.terminal_reason IN ('retired_definition','invalid_envelope')
            OR j.terminal_reason='finalize_refused' AND j.finalize_attempts BETWEEN 1 AND 6
            OR j.terminal_reason='finalize_exhausted' AND j.finalize_attempts=6)
        AND (SELECT count(*) FROM background_job_attempts a WHERE a.tenant_id=j.tenant_id AND a.job_id=j.id)=j.normal_attempts+j.finalize_attempts
        AND (SELECT count(*) FROM background_job_attempts a WHERE a.tenant_id=j.tenant_id AND a.job_id=j.id AND a.stage='normal')=j.normal_attempts
        AND (SELECT count(*) FROM background_job_reservations r WHERE r.tenant_id=j.tenant_id AND r.job_id=j.id)=j.normal_attempts
        AND NOT EXISTS (SELECT 1 FROM background_job_reservations r
            WHERE r.tenant_id=j.tenant_id AND r.job_id=j.id AND r.released_at IS NULL)
        AND NOT EXISTS (SELECT 1 FROM background_job_attempts a LEFT JOIN background_job_reservations r
            ON r.tenant_id=a.tenant_id AND r.job_id=a.job_id AND r.attempt_id=a.id AND r.capacity_group=j.capacity_group
            WHERE a.tenant_id=j.tenant_id AND a.job_id=j.id AND (
                a.organization_id IS DISTINCT FROM j.organization_id
                OR a.admission_token='00000000-0000-0000-0000-000000000000'::uuid
                OR isfinite(a.admitted_at) IS NOT TRUE OR a.admitted_at>clock_timestamp()
                OR isfinite(a.deadline) IS NOT TRUE OR a.deadline<=a.admitted_at OR a.deadline>a.admitted_at+interval '24 hours'
                OR a.transactional_cleanup_at IS NOT NULL
                OR (a.stage='normal' AND (
                    a.ordinal NOT BETWEEN 1 AND j.normal_attempts OR a.final_outcome_attempt_id IS NOT NULL
                    OR ((a.plan_fingerprint IS NULL AND a.operation_id IS NULL)
                        OR (a.plan_fingerprint=j.plan_fingerprint AND a.operation_id=j.operation_id)) IS NOT TRUE
                    OR (a.ordinal<j.normal_attempts AND a.no_start_at IS NULL
                        AND (a.outcome_kind IN ('failed','cancelled') AND a.failure_policy='transient_repeatable') IS NOT TRUE)
                    OR ((a.no_start_at IS NOT NULL AND isfinite(a.no_start_at) AND a.no_start_at>=a.admitted_at
                            AND a.no_start_at<=clock_timestamp() AND a.execute_intent_at IS NULL
                            AND a.outcome_kind IS NULL AND a.outcome_at IS NULL AND a.failure_policy IS NULL
                            AND a.quiescence_id IS NULL AND a.quiescence_kind IS NULL AND a.quiescent_at IS NULL
                            AND r.attempt_id IS NOT NULL AND r.released_at IS NOT NULL AND isfinite(r.released_at)
                            AND r.released_at>=a.no_start_at AND r.released_at<=clock_timestamp() AND r.release_reason='never_started')
                        OR (a.no_start_at IS NULL AND a.plan_fingerprint=j.plan_fingerprint AND a.operation_id=j.operation_id
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
                    a.ordinal NOT BETWEEN 1 AND j.finalize_attempts OR j.final_outcome_attempt_id IS NULL
                    OR a.final_outcome_attempt_id IS DISTINCT FROM j.final_outcome_attempt_id
                    OR a.plan_fingerprint IS NOT NULL OR a.operation_id IS NOT NULL OR a.execute_intent_at IS NOT NULL
                    OR a.outcome_kind IS NOT NULL OR a.outcome_at IS NOT NULL OR a.failure_policy IS NOT NULL
                    OR a.quiescence_id IS NOT NULL OR a.quiescence_kind IS NOT NULL OR a.quiescent_at IS NOT NULL
                    OR a.no_start_at IS NOT NULL OR r.attempt_id IS NOT NULL))
                OR a.stage NOT IN ('normal','finalize')))
        AND EXISTS (SELECT 1 FROM background_job_attempts current_attempt
            WHERE current_attempt.tenant_id=j.tenant_id AND current_attempt.job_id=j.id AND current_attempt.id=j.current_attempt_id
                AND current_attempt.organization_id IS NOT DISTINCT FROM j.organization_id
                AND current_attempt.deadline=j.attempt_deadline
                AND ((j.finalize_attempts=0 AND current_attempt.stage='normal' AND current_attempt.ordinal=j.normal_attempts)
                    OR (j.finalize_attempts>0 AND current_attempt.stage='finalize' AND current_attempt.ordinal=j.finalize_attempts
                        AND current_attempt.final_outcome_attempt_id=j.final_outcome_attempt_id)))
        AND ((j.final_outcome_attempt_id IS NULL AND j.finalization_status IS NULL AND j.finalization_reason IS NULL
                AND j.finalize_attempts=0 AND j.terminal_reason IN ('retired_definition','invalid_envelope'))
            OR (j.final_outcome_attempt_id IS NOT NULL AND EXISTS (SELECT 1 FROM background_job_attempts selected
                WHERE selected.tenant_id=j.tenant_id AND selected.job_id=j.id AND selected.id=j.final_outcome_attempt_id
                    AND selected.organization_id IS NOT DISTINCT FROM j.organization_id AND selected.stage='normal'
                    AND selected.ordinal=j.normal_attempts AND selected.plan_fingerprint=j.plan_fingerprint AND selected.operation_id=j.operation_id
                    AND ((selected.outcome_kind='succeeded' AND selected.failure_policy IS NULL
                            AND j.finalization_status='succeeded' AND j.finalization_reason='none')
                        OR (selected.outcome_kind='failed' AND selected.failure_policy='permanent'
                            AND j.finalization_status='failed' AND j.finalization_reason='external_failed')
                        OR (selected.outcome_kind IN ('failed','cancelled') AND selected.failure_policy='transient_not_repeatable'
                            AND j.finalization_status='failed' AND j.finalization_reason=CASE selected.outcome_kind
                                WHEN 'failed' THEN 'external_failed' ELSE 'external_cancelled' END)
                        OR (selected.outcome_kind IN ('failed','cancelled') AND selected.failure_policy='transient_repeatable'
                            AND j.normal_attempts=6 AND j.finalization_status='failed' AND j.finalization_reason='normal_attempts_exhausted')
                        OR (selected.outcome_kind IS NULL AND selected.no_start_at IS NOT NULL AND j.finalization_status='failed'
                            AND (j.finalization_reason='permanent_failure' OR j.finalization_reason='normal_attempts_exhausted' AND j.normal_attempts=6))))))
        """;

    internal const string PreparedBefore = "(" + PreparedHistory
        + ") AND j.dead_lettered_at IS NULL AND NOT j.business_finalization_incomplete";
    internal const string PreparedAfter = "(" + PreparedHistory + ") AND " + PreparedCompletedTime
        + " AND j.business_finalization_incomplete";
    internal const string PreparedCompletedTime = """
        j.dead_lettered_at IS NOT NULL AND isfinite(j.dead_lettered_at)
        AND j.dead_lettered_at>=j.prepared_at AND j.dead_lettered_at<=clock_timestamp()
        AND NOT EXISTS (SELECT 1 FROM background_job_attempts a WHERE a.tenant_id=j.tenant_id AND a.job_id=j.id
            AND a.admitted_at>j.dead_lettered_at)
        AND NOT EXISTS (SELECT 1 FROM background_job_reservations r WHERE r.tenant_id=j.tenant_id AND r.job_id=j.id
            AND r.released_at>j.dead_lettered_at)
        """;
    internal const string External = "(" + NoPlan + ") OR (" + PreparedBefore + ")";
    internal const string Profile = "(j.profile='transactional' OR (" + External + "))";
}
