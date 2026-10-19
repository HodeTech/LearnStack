namespace LearnStack.Infrastructure.Jobs;

internal static partial class BackgroundJobTerminalEligibility
{
    // These histories do not assert a phase or reason. Request routing uses the
    // same positive history before freezing its exact uninvokable reason.
    private const string CleanupHeader = """
        j.profile='external' AND j.capacity_group IS NOT NULL AND j.normal_attempts BETWEEN 1 AND 6
        AND j.finalize_attempts=0 AND j.final_outcome_attempt_id IS NULL
        AND j.finalization_status IS NULL AND j.finalization_reason IS NULL
        AND j.current_attempt_id IS NOT NULL AND j.attempt_deadline IS NOT NULL
        AND (SELECT count(*) FROM background_job_attempts a WHERE a.tenant_id=j.tenant_id AND a.job_id=j.id)=j.normal_attempts
        AND (SELECT count(*) FROM background_job_reservations r WHERE r.tenant_id=j.tenant_id AND r.job_id=j.id)=j.normal_attempts
        """;

    // Every earlier ordinal is positively complete. Only the exact current
    // normal attempt may remain unarmed and unreleased. No expiry proves this.
    private const string CleanupNormalHistory = """
         AND NOT EXISTS (SELECT 1 FROM background_job_attempts a LEFT JOIN background_job_reservations r
            ON r.tenant_id=a.tenant_id AND r.job_id=a.job_id AND r.attempt_id=a.id AND r.capacity_group=j.capacity_group
            WHERE a.tenant_id=j.tenant_id AND a.job_id=j.id AND (
                a.organization_id IS DISTINCT FROM j.organization_id OR a.stage<>'normal'
                OR a.ordinal NOT BETWEEN 1 AND j.normal_attempts OR a.final_outcome_attempt_id IS NOT NULL
                OR a.admission_token='00000000-0000-0000-0000-000000000000'::uuid
                OR isfinite(a.admitted_at) IS NOT TRUE OR a.admitted_at<j.original_due_at OR a.admitted_at>clock_timestamp()
                OR isfinite(a.deadline) IS NOT TRUE OR a.deadline<=a.admitted_at OR a.deadline>a.admitted_at+interval '24 hours'
                OR a.transactional_cleanup_at IS NOT NULL OR r.attempt_id IS NULL
                OR a.ordinal=j.normal_attempts AND (a.id<>j.current_attempt_id OR a.deadline IS DISTINCT FROM j.attempt_deadline)
                OR ((a.plan_fingerprint IS NULL AND a.operation_id IS NULL)
                    OR (j.plan_fingerprint IS NOT NULL AND a.plan_fingerprint=j.plan_fingerprint AND a.operation_id=j.operation_id)) IS NOT TRUE
                OR ((a.execute_intent_at IS NULL AND a.outcome_kind IS NULL AND a.outcome_at IS NULL AND a.failure_policy IS NULL
                        AND a.quiescence_id IS NULL AND a.quiescence_kind IS NULL AND a.quiescent_at IS NULL
                        AND ((a.no_start_at IS NOT NULL AND isfinite(a.no_start_at) AND a.no_start_at>=a.admitted_at
                                AND a.no_start_at<=clock_timestamp() AND r.released_at IS NOT NULL AND isfinite(r.released_at)
                                AND r.released_at>=a.no_start_at AND r.released_at<=clock_timestamp() AND r.release_reason='never_started')
                            OR (a.ordinal=j.normal_attempts AND a.no_start_at IS NULL
                                AND r.released_at IS NULL AND r.release_reason IS NULL)))
                    OR (a.ordinal<j.normal_attempts AND j.plan_fingerprint IS NOT NULL
                        AND a.no_start_at IS NULL AND a.plan_fingerprint=j.plan_fingerprint AND a.operation_id=j.operation_id
                        AND a.execute_intent_at IS NOT NULL AND isfinite(a.execute_intent_at)
                        AND a.execute_intent_at>=a.admitted_at AND a.execute_intent_at<=a.deadline
                        AND a.outcome_kind IN ('failed','cancelled') AND a.failure_policy='transient_repeatable'
                        AND a.outcome_at IS NOT NULL AND isfinite(a.outcome_at)
                        AND a.outcome_at>=a.execute_intent_at AND a.outcome_at<=clock_timestamp()
                        AND a.quiescence_id IS NOT NULL AND a.quiescence_id<>'00000000-0000-0000-0000-000000000000'::uuid
                        AND a.quiescence_kind IN ('provider_fenced','execution_terminated')
                        AND a.quiescent_at IS NOT NULL AND isfinite(a.quiescent_at)
                        AND a.quiescent_at>=a.execute_intent_at AND a.quiescent_at<=clock_timestamp()
                        AND r.released_at IS NOT NULL AND isfinite(r.released_at)
                        AND r.released_at>=a.quiescent_at AND r.released_at>=a.outcome_at
                        AND r.released_at<=clock_timestamp() AND r.release_reason='quiescent')) IS NOT TRUE))
        """;

    internal const string NoPlanCleanupHistory = CleanupHeader + """
         AND j.plan_fingerprint IS NULL AND j.operation_id IS NULL AND j.prepared_at IS NULL
        """ + CleanupNormalHistory;

    internal const string PreparedCleanupHistory = CleanupHeader + """
         AND j.plan_fingerprint IS NOT NULL AND j.plan_fingerprint COLLATE "C" ~ '^[0-9a-f]{64}$'
         AND j.operation_id IS NOT NULL AND j.operation_id<>'00000000-0000-0000-0000-000000000000'::uuid
         AND j.prepared_at IS NOT NULL AND isfinite(j.prepared_at) AND j.prepared_at<=clock_timestamp()
        """ + CleanupNormalHistory;

    internal const string TerminalNoStartBefore = """
        j.phase='terminal_pending' AND j.processed_at IS NULL AND j.dead_lettered_at IS NULL
        AND NOT j.business_finalization_incomplete AND (
        """ + "((" + NoPlanCleanupHistory + ") AND (j.terminal_reason IN ('permanent_failure','retired_definition','invalid_envelope')"
        + " OR j.terminal_reason='retry_exhausted' AND j.normal_attempts=6)) OR ((" + PreparedCleanupHistory
        + ") AND j.terminal_reason IN ('retired_definition','invalid_envelope')))";

    // The final proofs stay strict. This after-state only settles historical
    // cleanup; it cannot issue a receipt, current binding or executable root.
    internal const string TerminalNoStartAfter = "((" + NoPlan + ") AND " + """
        j.processed_at IS NULL AND j.dead_lettered_at IS NOT NULL AND isfinite(j.dead_lettered_at)
        AND j.dead_lettered_at<=clock_timestamp()
        AND NOT EXISTS (SELECT 1 FROM background_job_attempts a WHERE a.tenant_id=j.tenant_id AND a.job_id=j.id
            AND a.admitted_at>j.dead_lettered_at)
        AND NOT EXISTS (SELECT 1 FROM background_job_reservations r WHERE r.tenant_id=j.tenant_id AND r.job_id=j.id
            AND r.released_at>j.dead_lettered_at)
        """ + ") OR ((" + PreparedAfter + ") AND j.finalize_attempts=0 AND j.final_outcome_attempt_id IS NULL"
        + " AND EXISTS (SELECT 1 FROM background_job_attempts a WHERE a.tenant_id=j.tenant_id AND a.job_id=j.id"
        + " AND a.id=j.current_attempt_id AND a.no_start_at IS NOT NULL))";

    internal const string TerminalNoStartHistory = "(" + TerminalNoStartBefore + ") OR (" + TerminalNoStartAfter + ")";
}
