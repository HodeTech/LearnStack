using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LearnStack.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class add_background_job_state : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Raw Platform infrastructure; no EF entity mapping. Canonical SQL lives
            // in Standards 05, Background jobs. Tenancy migrations run first.
            migrationBuilder.Sql("""
                CREATE TABLE background_jobs (
                    id uuid PRIMARY KEY,
                    tenant_id uuid NOT NULL,
                    organization_id uuid NULL,
                    definition_key text NOT NULL,
                    profile text NOT NULL,
                    queue text NOT NULL,
                    capacity_group text NULL,
                    parameters text NOT NULL,
                    original_due_at timestamptz NOT NULL,
                    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
                    traceparent text NOT NULL,
                    causal_actor_user_id uuid NULL,
                    causation_event_id uuid NULL,
                    parent_job_id uuid NULL,
                    recipient_id uuid NULL,
                    plan text NULL,
                    plan_fingerprint text NULL,
                    prepared_at timestamptz NULL,
                    operation_id uuid NULL,
                    final_outcome_attempt_id uuid NULL,
                    finalization_status text NULL,
                    finalization_reason text NULL,
                    business_finalization_incomplete boolean NOT NULL DEFAULT false,
                    phase text NOT NULL DEFAULT 'pending',
                    normal_attempts integer NOT NULL DEFAULT 0,
                    finalize_attempts integer NOT NULL DEFAULT 0,
                    current_attempt_id uuid NULL,
                    ownership_token uuid NULL,
                    ownership_kind text NULL,
                    ownership_until timestamptz NULL,
                    attempt_deadline timestamptz NULL,
                    available_after timestamptz NOT NULL DEFAULT clock_timestamp(),
                    wake_generation bigint NOT NULL DEFAULT 1,
                    bridge_token uuid NULL,
                    bridge_until timestamptz NULL,
                    vendor_job_id text NULL,
                    handoff_check_at timestamptz NOT NULL DEFAULT clock_timestamp(),
                    probe_after timestamptz NULL,
                    unsuccessful_probes integer NOT NULL DEFAULT 0,
                    last_error text NULL,
                    quarantine_reason text NULL,
                    terminal_reason text NULL,
                    processed_at timestamptz NULL,
                    dead_lettered_at timestamptz NULL,
                    CONSTRAINT ux_background_jobs_tenant_id_id UNIQUE (tenant_id, id),
                    CONSTRAINT ux_background_jobs_recipient UNIQUE (recipient_id),
                    CONSTRAINT ck_background_jobs_tenant CHECK (tenant_id NOT IN ('00000000-0000-0000-0000-000000000000'::uuid, '00000000-0000-7000-8000-000000000002'::uuid)),
                CONSTRAINT fk_background_jobs_tenant FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT,
                CONSTRAINT ck_background_jobs_organization CHECK ((organization_id IS NULL OR organization_id <> '00000000-0000-0000-0000-000000000000'::uuid)),
                CONSTRAINT fk_background_jobs_organization FOREIGN KEY (tenant_id, organization_id) REFERENCES organizations (tenant_id, id) ON DELETE RESTRICT,
                    CONSTRAINT fk_background_jobs_parent FOREIGN KEY (tenant_id, parent_job_id)
                        REFERENCES background_jobs (tenant_id, id) ON DELETE RESTRICT,
                    CONSTRAINT ck_background_jobs_ids CHECK (uuid_extract_version(id) IS NOT DISTINCT FROM 7
                        AND (causal_actor_user_id IS NULL OR causal_actor_user_id <> '00000000-0000-0000-0000-000000000000'::uuid) AND (causation_event_id IS NULL OR causation_event_id <> '00000000-0000-0000-0000-000000000000'::uuid)
                        AND (ownership_token IS NULL OR ownership_token <> '00000000-0000-0000-0000-000000000000'::uuid) AND (bridge_token IS NULL OR bridge_token <> '00000000-0000-0000-0000-000000000000'::uuid)
                        AND (operation_id IS NULL OR operation_id <> '00000000-0000-0000-0000-000000000000'::uuid) AND parent_job_id IS DISTINCT FROM id),
                    CONSTRAINT ck_background_jobs_names CHECK ((length(definition_key) <= 150 AND definition_key COLLATE "C" ~ '^[a-z][a-z0-9]*\.[a-z][a-z0-9_]*\.v[1-9][0-9]*$')
                        AND (length(queue) <= 50 AND queue COLLATE "C" ~ '^[a-z][a-z0-9_]*$')
                        AND (capacity_group IS NULL OR (length(capacity_group) <= 150 AND capacity_group COLLATE "C" ~ '^[a-z][a-z0-9]*\.[a-z][a-z0-9_]*$'))),
                    CONSTRAINT ck_background_jobs_profile CHECK (profile IN ('transactional', 'external')
                        AND (profile <> 'external' OR capacity_group IS NOT NULL)),
                    CONSTRAINT ck_background_jobs_parameters CHECK (
                        octet_length(parameters) <= 262144 AND parameters IS JSON OBJECT WITH UNIQUE KEYS),
                    CONSTRAINT ck_background_jobs_trace CHECK (
                        traceparent COLLATE "C" ~ '^00-[0-9a-f]{32}-[0-9a-f]{16}-[0-9a-f]{2}$'
                        AND substring(traceparent FROM 4 FOR 32) <> repeat('0', 32)
                        AND substring(traceparent FROM 37 FOR 16) <> repeat('0', 16)),
                    CONSTRAINT ck_background_jobs_plan CHECK (
                        (plan IS NULL AND plan_fingerprint IS NULL AND prepared_at IS NULL AND operation_id IS NULL)
                        OR (profile = 'external' AND plan IS NOT NULL AND plan_fingerprint IS NOT NULL
                            AND prepared_at IS NOT NULL AND operation_id IS NOT NULL
                            AND octet_length(plan) <= 65536 AND plan IS JSON OBJECT WITH UNIQUE KEYS
                            AND plan_fingerprint COLLATE "C" ~ '^[0-9a-f]{64}$')),
                    CONSTRAINT ck_background_jobs_finalization CHECK (
                        (finalization_status IS NULL AND finalization_reason IS NULL AND final_outcome_attempt_id IS NULL)
                        OR (profile = 'external' AND plan IS NOT NULL AND final_outcome_attempt_id IS NOT NULL
                            AND finalization_status IS NOT NULL AND finalization_reason IS NOT NULL
                            AND ((finalization_status = 'succeeded' AND finalization_reason = 'none')
                                 OR (finalization_status = 'failed' AND finalization_reason IN
                                     ('external_failed', 'external_cancelled', 'normal_attempts_exhausted', 'permanent_failure'))))),
                    CONSTRAINT ck_background_jobs_phase CHECK (phase IN ('pending', 'preparing', 'prepared', 'executing',
                        'reconciling', 'finalize_pending', 'finalizing', 'terminal_pending', 'completed', 'quarantined')),
                    CONSTRAINT ck_background_jobs_counts CHECK (
                        normal_attempts BETWEEN 0 AND 6 AND finalize_attempts BETWEEN 0 AND 6
                        AND unsuccessful_probes BETWEEN 0 AND 6 AND wake_generation > 0),
                    CONSTRAINT ck_background_jobs_ownership CHECK (
                        (ownership_token IS NULL AND ownership_kind IS NULL AND ownership_until IS NULL)
                        OR (ownership_token IS NOT NULL AND ownership_kind IS NOT NULL AND ownership_kind IN ('routing', 'execution', 'recovery')
                            AND ownership_until IS NOT NULL)),
                    CONSTRAINT ck_background_jobs_bridge CHECK (
                        (bridge_token IS NULL AND bridge_until IS NULL) OR (bridge_token IS NOT NULL AND bridge_until IS NOT NULL)),
                    CONSTRAINT ck_background_jobs_vendor CHECK (vendor_job_id IS NULL OR
                        (length(vendor_job_id) <= 20 AND vendor_job_id COLLATE "C" ~ '^[1-9][0-9]*$')),
                    CONSTRAINT ck_background_jobs_errors CHECK ((last_error IS NULL OR
                        (octet_length(last_error) <= 256 AND last_error IN ('deployment_mismatch', 'capacity_unavailable', 'concurrency_conflict', 'rate_limited', 'dependency_unavailable', 'audit_unavailable', 'unexpected_fault', 'permanent_failure', 'lease_lost', 'deadline_exceeded', 'shutdown', 'unknown_outcome', 'invalid_control', 'finalize_refused', 'finalize_exhausted')))
                        AND (quarantine_reason IS NULL OR quarantine_reason IN ('unknown_outcome', 'probe_exhausted', 'invalid_control'))
                        AND (terminal_reason IS NULL OR terminal_reason IN ('retired_definition', 'invalid_envelope', 'permanent_failure', 'retry_exhausted', 'finalize_refused', 'finalize_exhausted'))),
                    CONSTRAINT ck_background_jobs_state CHECK (
                        (phase <> 'completed' OR processed_at IS NOT NULL)
                        AND (processed_at IS NULL OR (phase = 'completed' AND dead_lettered_at IS NULL AND terminal_reason IS NULL))
                        AND (dead_lettered_at IS NULL OR (phase = 'terminal_pending' AND terminal_reason IS NOT NULL AND processed_at IS NULL))
                        AND ((phase = 'terminal_pending') = (terminal_reason IS NOT NULL))
                        AND ((phase = 'quarantined') = (quarantine_reason IS NOT NULL))
                        AND (phase NOT IN ('finalize_pending', 'finalizing') OR finalization_status IS NOT NULL)
                        AND (phase NOT IN ('preparing', 'prepared') OR profile = 'external')
                        AND (phase <> 'preparing' OR plan IS NULL)
                        AND (phase <> 'prepared' OR plan IS NOT NULL)
                        AND (phase <> 'executing' OR profile = 'transactional' OR plan IS NOT NULL)
                        AND (phase NOT IN ('preparing', 'prepared', 'executing', 'finalizing')
                            OR (ownership_kind = 'execution' AND ownership_token IS NOT NULL AND current_attempt_id IS NOT NULL))
                        AND (phase <> 'reconciling' OR current_attempt_id IS NOT NULL)
                        AND (NOT business_finalization_incomplete OR (plan IS NOT NULL AND dead_lettered_at IS NOT NULL))
                        AND (processed_at IS NULL AND dead_lettered_at IS NULL OR ownership_token IS NULL)),
                    CONSTRAINT ck_background_jobs_time CHECK (isfinite(original_due_at) AND isfinite(created_at)
                        AND isfinite(available_after) AND isfinite(handoff_check_at)
                        AND (ownership_until IS NULL OR isfinite(ownership_until))
                        AND (attempt_deadline IS NULL OR isfinite(attempt_deadline))
                        AND (bridge_until IS NULL OR isfinite(bridge_until))
                        AND (probe_after IS NULL OR isfinite(probe_after))
                        AND (prepared_at IS NULL OR isfinite(prepared_at))
                        AND (processed_at IS NULL OR isfinite(processed_at))
                        AND (dead_lettered_at IS NULL OR isfinite(dead_lettered_at)))
                );
                CREATE TABLE background_job_attempts (
                    id uuid PRIMARY KEY,
                    tenant_id uuid NOT NULL,
                    organization_id uuid NULL,
                    job_id uuid NOT NULL,
                    stage text NOT NULL,
                    ordinal integer NOT NULL,
                    admission_token uuid NOT NULL,
                    admitted_at timestamptz NOT NULL DEFAULT clock_timestamp(),
                    deadline timestamptz NOT NULL,
                    final_outcome_attempt_id uuid NULL,
                    plan_fingerprint text NULL,
                    operation_id uuid NULL,
                    execute_intent_at timestamptz NULL,
                    outcome text NULL,
                    outcome_kind text NULL,
                    outcome_at timestamptz NULL,
                    quiescence_id uuid NULL,
                    quiescence_kind text NULL,
                    quiescent_at timestamptz NULL,
                    no_start_at timestamptz NULL,
                    CONSTRAINT ux_background_job_attempts_tenant_job_id UNIQUE (tenant_id, job_id, id),
                    CONSTRAINT ux_background_job_attempts_stage_ordinal UNIQUE (job_id, stage, ordinal),
                    CONSTRAINT ck_background_job_attempts_tenant CHECK (tenant_id NOT IN ('00000000-0000-0000-0000-000000000000'::uuid, '00000000-0000-7000-8000-000000000002'::uuid)),
                CONSTRAINT fk_background_job_attempts_tenant FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT,
                CONSTRAINT ck_background_job_attempts_organization CHECK ((organization_id IS NULL OR organization_id <> '00000000-0000-0000-0000-000000000000'::uuid)),
                CONSTRAINT fk_background_job_attempts_organization FOREIGN KEY (tenant_id, organization_id) REFERENCES organizations (tenant_id, id) ON DELETE RESTRICT,
                    CONSTRAINT fk_background_job_attempts_job FOREIGN KEY (tenant_id, job_id)
                        REFERENCES background_jobs (tenant_id, id) ON DELETE RESTRICT,
                    CONSTRAINT fk_background_job_attempts_outcome FOREIGN KEY (tenant_id, job_id, final_outcome_attempt_id)
                        REFERENCES background_job_attempts (tenant_id, job_id, id) ON DELETE RESTRICT,
                    CONSTRAINT ck_background_job_attempts_identity CHECK (uuid_extract_version(id) IS NOT DISTINCT FROM 7
                        AND admission_token <> '00000000-0000-0000-0000-000000000000'::uuid AND stage IN ('normal', 'finalize') AND ordinal BETWEEN 1 AND 6
                        AND ((stage = 'normal' AND final_outcome_attempt_id IS NULL)
                             OR (stage = 'finalize' AND final_outcome_attempt_id IS NOT NULL))),
                    CONSTRAINT ck_background_job_attempts_plan CHECK (
                        (plan_fingerprint IS NULL AND operation_id IS NULL)
                        OR (plan_fingerprint IS NOT NULL AND operation_id IS NOT NULL AND operation_id <> '00000000-0000-0000-0000-000000000000'::uuid
                            AND plan_fingerprint COLLATE "C" ~ '^[0-9a-f]{64}$')),
                    CONSTRAINT ck_background_job_attempts_execute CHECK (execute_intent_at IS NULL OR
                        (stage = 'normal' AND plan_fingerprint IS NOT NULL AND no_start_at IS NULL)),
                    CONSTRAINT ck_background_job_attempts_outcome CHECK (
                        (outcome IS NULL AND outcome_kind IS NULL AND outcome_at IS NULL)
                        OR (stage = 'normal' AND outcome IS NOT NULL AND outcome_kind IS NOT NULL AND outcome_kind IN ('succeeded', 'failed', 'cancelled')
                            AND outcome_at IS NOT NULL AND execute_intent_at IS NOT NULL
                            AND octet_length(outcome) <= 65536 AND outcome IS JSON OBJECT WITH UNIQUE KEYS
                            AND quiescent_at IS NOT NULL)),
                    CONSTRAINT ck_background_job_attempts_quiescence CHECK (
                        (quiescence_id IS NULL AND quiescence_kind IS NULL AND quiescent_at IS NULL)
                        OR (stage = 'normal' AND quiescence_id IS NOT NULL AND quiescence_id <> '00000000-0000-0000-0000-000000000000'::uuid
                            AND quiescence_kind IS NOT NULL AND quiescence_kind IN ('provider_fenced', 'execution_terminated')
                            AND quiescent_at IS NOT NULL AND execute_intent_at IS NOT NULL)),
                    CONSTRAINT ck_background_job_attempts_no_start CHECK (no_start_at IS NULL OR
                        (stage = 'normal' AND execute_intent_at IS NULL AND outcome IS NULL AND quiescent_at IS NULL)),
                    CONSTRAINT ck_background_job_attempts_time CHECK (isfinite(admitted_at) AND isfinite(deadline)
                        AND deadline > admitted_at AND deadline <= admitted_at + interval '24 hours'
                        AND (execute_intent_at IS NULL OR isfinite(execute_intent_at))
                        AND (outcome_at IS NULL OR isfinite(outcome_at))
                        AND (quiescent_at IS NULL OR isfinite(quiescent_at))
                        AND (no_start_at IS NULL OR isfinite(no_start_at)))
                );
                ALTER TABLE background_jobs ADD CONSTRAINT fk_background_jobs_current_attempt
                    FOREIGN KEY (tenant_id, id, current_attempt_id)
                    REFERENCES background_job_attempts (tenant_id, job_id, id) ON DELETE RESTRICT
                    DEFERRABLE INITIALLY DEFERRED;
                ALTER TABLE background_jobs ADD CONSTRAINT fk_background_jobs_final_outcome
                    FOREIGN KEY (tenant_id, id, final_outcome_attempt_id)
                    REFERENCES background_job_attempts (tenant_id, job_id, id) ON DELETE RESTRICT
                    DEFERRABLE INITIALLY DEFERRED;
                CREATE TABLE background_job_capacity (
                    tenant_id uuid NOT NULL,
                    capacity_group text NOT NULL,
                    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
                    CONSTRAINT pk_background_job_capacity PRIMARY KEY (tenant_id, capacity_group),
                    CONSTRAINT ck_background_job_capacity_tenant CHECK (tenant_id NOT IN ('00000000-0000-0000-0000-000000000000'::uuid, '00000000-0000-7000-8000-000000000002'::uuid)),
                CONSTRAINT fk_background_job_capacity_tenant FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT,
                    CONSTRAINT ck_background_job_capacity_group CHECK ((length(capacity_group) <= 150 AND capacity_group COLLATE "C" ~ '^[a-z][a-z0-9]*\.[a-z][a-z0-9_]*$') AND isfinite(created_at))
                );
                CREATE TABLE background_job_reservations (
                    tenant_id uuid NOT NULL,
                    capacity_group text NOT NULL,
                    job_id uuid NOT NULL,
                    attempt_id uuid NOT NULL,
                    reserved_at timestamptz NOT NULL DEFAULT clock_timestamp(),
                    released_at timestamptz NULL,
                    release_reason text NULL,
                    CONSTRAINT pk_background_job_reservations PRIMARY KEY (tenant_id, job_id, attempt_id),
                    CONSTRAINT fk_background_job_reservations_capacity FOREIGN KEY (tenant_id, capacity_group)
                        REFERENCES background_job_capacity (tenant_id, capacity_group) ON DELETE RESTRICT,
                    CONSTRAINT fk_background_job_reservations_attempt FOREIGN KEY (tenant_id, job_id, attempt_id)
                        REFERENCES background_job_attempts (tenant_id, job_id, id) ON DELETE RESTRICT,
                    CONSTRAINT ck_background_job_reservations_reserved CHECK (isfinite(reserved_at)),
                    CONSTRAINT ck_background_job_reservations_release CHECK (
                        (released_at IS NULL AND release_reason IS NULL)
                        OR (released_at IS NOT NULL AND release_reason IS NOT NULL AND release_reason IN ('completed', 'never_started', 'quiescent')
                            AND isfinite(released_at) AND released_at >= reserved_at))
                );
                CREATE TABLE background_job_recurrences (
                    recurrence_key text NOT NULL,
                    revision bigint NOT NULL,
                    manifest_hash text NOT NULL,
                    target_definition_key text NOT NULL,
                    schedule text NOT NULL,
                    activates_at timestamptz NOT NULL,
                    anchor_at timestamptz NOT NULL,
                    period_microseconds bigint NOT NULL,
                    jitter_microseconds bigint NOT NULL,
                    recipient_policy text NOT NULL,
                    misfire_policy text NOT NULL DEFAULT 'coalesce_latest',
                    superseded_at timestamptz NULL,
                    next_scheduled_at timestamptz NOT NULL,
                    available_after timestamptz NOT NULL DEFAULT clock_timestamp(),
                    ownership_token uuid NULL,
                    ownership_until timestamptz NULL,
                    last_error text NULL,
                    quarantined_at timestamptz NULL,
                    CONSTRAINT pk_background_job_recurrences PRIMARY KEY (recurrence_key, revision),
                    CONSTRAINT ck_background_job_recurrences_manifest CHECK (
                        (length(recurrence_key) <= 150 AND recurrence_key COLLATE "C" ~ '^[a-z][a-z0-9]*\.[a-z][a-z0-9_]*$') AND (length(target_definition_key) <= 150 AND target_definition_key COLLATE "C" ~ '^[a-z][a-z0-9]*\.[a-z][a-z0-9_]*\.v[1-9][0-9]*$')
                        AND revision > 0 AND manifest_hash COLLATE "C" ~ '^[0-9a-f]{64}$'
                        AND schedule IN ('interval', 'daily') AND misfire_policy = 'coalesce_latest'
                        AND period_microseconds BETWEEN 60000000 AND 31622400000000
                        AND (schedule <> 'daily' OR period_microseconds = 86400000000)
                        AND jitter_microseconds BETWEEN 0 AND LEAST(period_microseconds, 7200000000)
                        AND recipient_policy IN ('provisioned_non_archived_tenants', 'serving_tenants', 'serving_organizations')),
                    CONSTRAINT ck_background_job_recurrences_time CHECK (isfinite(activates_at) AND isfinite(anchor_at)
                        AND isfinite(next_scheduled_at) AND next_scheduled_at >= activates_at AND isfinite(available_after)
                        AND (superseded_at IS NULL OR (isfinite(superseded_at) AND superseded_at >= activates_at))
                        AND (quarantined_at IS NULL OR isfinite(quarantined_at))),
                    CONSTRAINT ck_background_job_recurrences_lease CHECK (
                        (ownership_token IS NULL AND ownership_until IS NULL)
                        OR (ownership_token IS NOT NULL AND ownership_token <> '00000000-0000-0000-0000-000000000000'::uuid AND ownership_until IS NOT NULL AND isfinite(ownership_until))),
                    CONSTRAINT ck_background_job_recurrences_error CHECK (last_error IS NULL OR last_error IN ('deployment_mismatch', 'capacity_unavailable', 'concurrency_conflict', 'rate_limited', 'dependency_unavailable', 'audit_unavailable', 'unexpected_fault', 'permanent_failure', 'lease_lost', 'deadline_exceeded', 'shutdown', 'unknown_outcome', 'invalid_control', 'finalize_refused', 'finalize_exhausted'))
                );
                CREATE TABLE background_job_occurrences (
                    id uuid PRIMARY KEY,
                    recurrence_key text NOT NULL,
                    revision bigint NOT NULL,
                    manifest_hash text NOT NULL,
                    target_definition_key text NOT NULL,
                    recipient_policy text NOT NULL,
                    scheduled_at timestamptz NOT NULL,
                    jitter_microseconds bigint NOT NULL,
                    skipped_slots bigint NOT NULL,
                    bounds_frozen_at timestamptz NULL,
                    upper_tenant_id uuid NULL,
                    upper_organization_id uuid NULL,
                    after_tenant_id uuid NULL,
                    after_organization_id uuid NULL,
                    available_after timestamptz NOT NULL DEFAULT clock_timestamp(),
                    ownership_token uuid NULL,
                    ownership_until timestamptz NULL,
                    completed_at timestamptz NULL,
                    last_error text NULL,
                    quarantined_at timestamptz NULL,
                    CONSTRAINT ux_background_job_occurrences_slot UNIQUE (recurrence_key, scheduled_at),
                    CONSTRAINT fk_background_job_occurrences_recurrence FOREIGN KEY (recurrence_key, revision)
                        REFERENCES background_job_recurrences (recurrence_key, revision) ON DELETE RESTRICT,
                    CONSTRAINT ck_background_job_occurrences_identity CHECK (uuid_extract_version(id) IS NOT DISTINCT FROM 7 AND skipped_slots >= 0),
                    CONSTRAINT ck_background_job_occurrences_bounds CHECK (
                        (bounds_frozen_at IS NOT NULL OR (upper_tenant_id IS NULL AND upper_organization_id IS NULL
                            AND after_tenant_id IS NULL AND after_organization_id IS NULL AND completed_at IS NULL))
                        AND (upper_organization_id IS NULL OR upper_tenant_id IS NOT NULL)
                        AND (after_organization_id IS NULL OR after_tenant_id IS NOT NULL)
                        AND (after_tenant_id IS NULL OR (upper_tenant_id IS NOT NULL
                            AND ROW(after_tenant_id, COALESCE(after_organization_id, '00000000-0000-0000-0000-000000000000'::uuid))
                                <= ROW(upper_tenant_id, COALESCE(upper_organization_id, '00000000-0000-0000-0000-000000000000'::uuid))))
                        AND (recipient_policy = 'serving_organizations' OR (upper_organization_id IS NULL AND after_organization_id IS NULL))
                        AND (upper_tenant_id IS NULL OR upper_tenant_id <> '00000000-0000-0000-0000-000000000000'::uuid) AND (upper_organization_id IS NULL OR upper_organization_id <> '00000000-0000-0000-0000-000000000000'::uuid)
                        AND (after_tenant_id IS NULL OR after_tenant_id <> '00000000-0000-0000-0000-000000000000'::uuid) AND (after_organization_id IS NULL OR after_organization_id <> '00000000-0000-0000-0000-000000000000'::uuid)),
                    CONSTRAINT ck_background_job_occurrences_lease CHECK (
                        (ownership_token IS NULL AND ownership_until IS NULL)
                        OR (ownership_token IS NOT NULL AND ownership_token <> '00000000-0000-0000-0000-000000000000'::uuid AND ownership_until IS NOT NULL AND isfinite(ownership_until))),
                    CONSTRAINT ck_background_job_occurrences_error CHECK (last_error IS NULL OR last_error IN ('deployment_mismatch', 'capacity_unavailable', 'concurrency_conflict', 'rate_limited', 'dependency_unavailable', 'audit_unavailable', 'unexpected_fault', 'permanent_failure', 'lease_lost', 'deadline_exceeded', 'shutdown', 'unknown_outcome', 'invalid_control', 'finalize_refused', 'finalize_exhausted')),
                    CONSTRAINT ck_background_job_occurrences_time CHECK (isfinite(scheduled_at) AND isfinite(available_after)
                        AND (bounds_frozen_at IS NULL OR isfinite(bounds_frozen_at))
                        AND (completed_at IS NULL OR isfinite(completed_at))
                        AND (quarantined_at IS NULL OR isfinite(quarantined_at)))
                );
                CREATE TABLE background_job_recipients (
                    id uuid PRIMARY KEY,
                    occurrence_id uuid NOT NULL,
                    tenant_id uuid NOT NULL,
                    organization_id uuid NULL,
                    recurrence_key text NOT NULL,
                    revision bigint NOT NULL,
                    manifest_hash text NOT NULL,
                    target_definition_key text NOT NULL,
                    recipient_policy text NOT NULL,
                    scheduled_at timestamptz NOT NULL,
                    due_at timestamptz NOT NULL,
                    available_after timestamptz NOT NULL DEFAULT clock_timestamp(),
                    ownership_token uuid NULL,
                    ownership_until timestamptz NULL,
                    accepted_job_id uuid NULL,
                    accepted_at timestamptz NULL,
                    skipped_at timestamptz NULL,
                    skipped_reason text NULL,
                    last_error text NULL,
                    quarantined_at timestamptz NULL,
                    CONSTRAINT ux_background_job_recipients_tenant_id_id UNIQUE (tenant_id, id),
                    CONSTRAINT ux_background_job_recipients_route UNIQUE NULLS NOT DISTINCT (occurrence_id, tenant_id, organization_id),
                    CONSTRAINT ck_background_job_recipients_tenant CHECK (tenant_id NOT IN ('00000000-0000-0000-0000-000000000000'::uuid, '00000000-0000-7000-8000-000000000002'::uuid)),
                CONSTRAINT fk_background_job_recipients_tenant FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT,
                CONSTRAINT ck_background_job_recipients_organization CHECK ((organization_id IS NULL OR organization_id <> '00000000-0000-0000-0000-000000000000'::uuid)),
                CONSTRAINT fk_background_job_recipients_organization FOREIGN KEY (tenant_id, organization_id) REFERENCES organizations (tenant_id, id) ON DELETE RESTRICT,
                    CONSTRAINT fk_background_job_recipients_occurrence FOREIGN KEY (occurrence_id)
                        REFERENCES background_job_occurrences (id) ON DELETE RESTRICT,
                    CONSTRAINT fk_background_job_recipients_job FOREIGN KEY (tenant_id, accepted_job_id)
                        REFERENCES background_jobs (tenant_id, id) ON DELETE RESTRICT DEFERRABLE INITIALLY DEFERRED,
                    CONSTRAINT ck_background_job_recipients_identity CHECK (uuid_extract_version(id) IS NOT DISTINCT FROM 7),
                    CONSTRAINT ck_background_job_recipients_lease CHECK (
                        (ownership_token IS NULL AND ownership_until IS NULL)
                        OR (ownership_token IS NOT NULL AND ownership_token <> '00000000-0000-0000-0000-000000000000'::uuid AND ownership_until IS NOT NULL AND isfinite(ownership_until))),
                    CONSTRAINT ck_background_job_recipients_receipt CHECK (
                        (accepted_job_id IS NULL AND accepted_at IS NULL)
                        OR (accepted_job_id IS NOT NULL AND accepted_at IS NOT NULL AND skipped_at IS NULL)),
                    CONSTRAINT ck_background_job_recipients_skip CHECK (
                        (skipped_at IS NULL AND skipped_reason IS NULL)
                        OR (skipped_at IS NOT NULL AND skipped_reason IS NOT NULL AND skipped_reason IN ('ineligible', 'deleted') AND accepted_job_id IS NULL)),
                    CONSTRAINT ck_background_job_recipients_error CHECK (last_error IS NULL OR last_error IN ('deployment_mismatch', 'capacity_unavailable', 'concurrency_conflict', 'rate_limited', 'dependency_unavailable', 'audit_unavailable', 'unexpected_fault', 'permanent_failure', 'lease_lost', 'deadline_exceeded', 'shutdown', 'unknown_outcome', 'invalid_control', 'finalize_refused', 'finalize_exhausted')),
                    CONSTRAINT ck_background_job_recipients_time CHECK (isfinite(scheduled_at) AND isfinite(due_at)
                        AND due_at >= scheduled_at AND isfinite(available_after)
                        AND (accepted_at IS NULL OR isfinite(accepted_at))
                        AND (skipped_at IS NULL OR isfinite(skipped_at))
                        AND (quarantined_at IS NULL OR isfinite(quarantined_at)))
                );
                ALTER TABLE background_jobs ADD CONSTRAINT fk_background_jobs_recipient
                    FOREIGN KEY (tenant_id, recipient_id) REFERENCES background_job_recipients (tenant_id, id)
                    ON DELETE RESTRICT;

                CREATE FUNCTION public.fn_background_job_no_delete() RETURNS trigger
                LANGUAGE plpgsql SECURITY INVOKER SET search_path = pg_catalog AS $$
                BEGIN
                    RAISE EXCEPTION 'background job history cannot be removed (%)', TG_TABLE_NAME USING ERRCODE = '23514';
                END;
                $$;
                CREATE FUNCTION public.fn_background_jobs_envelope() RETURNS trigger
                LANGUAGE plpgsql SECURITY INVOKER SET search_path = pg_catalog AS $$
                DECLARE parent_org uuid; recipient record;
                BEGIN
                    IF TG_OP = 'UPDATE' THEN
                        IF ROW(NEW.id, NEW.tenant_id, NEW.organization_id, NEW.definition_key, NEW.profile,
                               NEW.queue, NEW.capacity_group, NEW.parameters, NEW.original_due_at, NEW.created_at,
                               NEW.traceparent, NEW.causal_actor_user_id, NEW.causation_event_id, NEW.parent_job_id, NEW.recipient_id)
                            IS DISTINCT FROM
                           ROW(OLD.id, OLD.tenant_id, OLD.organization_id, OLD.definition_key, OLD.profile,
                               OLD.queue, OLD.capacity_group, OLD.parameters, OLD.original_due_at, OLD.created_at,
                               OLD.traceparent, OLD.causal_actor_user_id, OLD.causation_event_id, OLD.parent_job_id, OLD.recipient_id) THEN
                            RAISE EXCEPTION 'job envelope is immutable' USING ERRCODE = '23514';
                        END IF;
                        RETURN NEW;
                    END IF;
                    IF NEW.phase <> 'pending' OR NEW.normal_attempts <> 0 OR NEW.finalize_attempts <> 0
                       OR NEW.wake_generation <> 1 OR NEW.current_attempt_id IS NOT NULL OR NEW.ownership_token IS NOT NULL
                       OR NEW.bridge_token IS NOT NULL OR NEW.vendor_job_id IS NOT NULL OR NEW.plan IS NOT NULL
                       OR NEW.finalization_status IS NOT NULL OR NEW.processed_at IS NOT NULL OR NEW.dead_lettered_at IS NOT NULL
                       OR NEW.terminal_reason IS NOT NULL OR NEW.quarantine_reason IS NOT NULL OR NEW.attempt_deadline IS NOT NULL
                       OR NEW.probe_after IS NOT NULL OR NEW.unsuccessful_probes <> 0 OR NEW.last_error IS NOT NULL
                       OR NEW.business_finalization_incomplete THEN
                        RAISE EXCEPTION 'job insert accepts only a new envelope' USING ERRCODE = '23514';
                    END IF;
                    NEW.created_at := clock_timestamp();
                    NEW.available_after := NEW.original_due_at;
                    NEW.handoff_check_at := NEW.created_at;
                    IF NEW.parent_job_id IS NOT NULL THEN
                        SELECT parent.organization_id INTO parent_org FROM public.background_jobs AS parent
                        WHERE parent.tenant_id = NEW.tenant_id AND parent.id = NEW.parent_job_id FOR KEY SHARE;
                        IF NOT FOUND OR parent_org IS DISTINCT FROM NEW.organization_id THEN
                            RAISE EXCEPTION 'job parent scope is unavailable or mismatched' USING ERRCODE = '23514';
                        END IF;
                    END IF;
                    IF NEW.recipient_id IS NOT NULL THEN
                        SELECT r.organization_id, r.target_definition_key, r.due_at, r.ownership_until,
                               r.accepted_job_id, r.skipped_at, r.quarantined_at
                          INTO recipient FROM public.background_job_recipients r
                          WHERE r.tenant_id = NEW.tenant_id AND r.id = NEW.recipient_id FOR KEY SHARE;
                        IF NOT FOUND OR recipient.organization_id IS DISTINCT FROM NEW.organization_id
                           OR recipient.target_definition_key <> NEW.definition_key OR recipient.due_at <> NEW.original_due_at
                           OR recipient.ownership_until IS NULL OR recipient.ownership_until <= clock_timestamp()
                           OR recipient.accepted_job_id IS NOT NULL OR recipient.skipped_at IS NOT NULL
                           OR recipient.quarantined_at IS NOT NULL THEN
                            RAISE EXCEPTION 'job recipient is unavailable or mismatched' USING ERRCODE = '23514';
                        END IF;
                    END IF;
                    RETURN NEW;
                END;
                $$;
                CREATE FUNCTION public.fn_background_jobs_transition() RETURNS trigger
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
                    IF NEW.phase = 'pending' AND OLD.phase <> NEW.phase AND NEW.profile = 'external'
                       AND EXISTS (SELECT 1 FROM public.background_job_attempts a WHERE a.tenant_id = NEW.tenant_id
                           AND a.job_id = NEW.id AND a.stage = 'normal' AND a.no_start_at IS NULL AND a.quiescent_at IS NULL) THEN
                        RAISE EXCEPTION 'external retry requires positive prior-attempt evidence' USING ERRCODE = '23514';
                    END IF;
                    IF NEW.phase IN ('pending', 'finalize_pending', 'terminal_pending') AND OLD.phase <> NEW.phase AND NEW.profile = 'external'
                       AND EXISTS (SELECT 1 FROM public.background_job_attempts a WHERE a.tenant_id = NEW.tenant_id
                           AND a.job_id = NEW.id AND a.execute_intent_at IS NOT NULL AND a.quiescent_at IS NULL) THEN
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
                CREATE FUNCTION public.fn_background_job_attempts_guard() RETURNS trigger
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
                CREATE FUNCTION public.fn_background_job_attempt_counts() RETURNS trigger
                LANGUAGE plpgsql SECURITY INVOKER SET search_path = pg_catalog AS $$
                DECLARE target_tenant uuid; target_job uuid; job record; normal_count bigint; finalize_count bigint;
                BEGIN
                    target_tenant := NEW.tenant_id;
                    IF TG_TABLE_NAME = 'background_jobs' THEN target_job := NEW.id; ELSE target_job := NEW.job_id; END IF;
                    SELECT j.normal_attempts, j.finalize_attempts, j.current_attempt_id, j.capacity_group INTO job
                      FROM public.background_jobs j WHERE j.tenant_id = target_tenant AND j.id = target_job FOR UPDATE;
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
                CREATE FUNCTION public.fn_background_job_capacity_immutable() RETURNS trigger
                LANGUAGE plpgsql SECURITY INVOKER SET search_path = pg_catalog AS $$
                BEGIN
                    IF NEW IS DISTINCT FROM OLD THEN
                        RAISE EXCEPTION 'capacity mutex facts are immutable' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END;
                $$;
                CREATE FUNCTION public.fn_background_job_reservations_guard() RETURNS trigger
                LANGUAGE plpgsql SECURITY INVOKER SET search_path = pg_catalog AS $$
                DECLARE attempt record;
                BEGIN
                    SELECT a.organization_id, a.stage, a.no_start_at, a.quiescent_at, j.capacity_group,
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
                            OR (NEW.release_reason = 'quiescent' AND attempt.quiescent_at IS NOT NULL)) THEN
                            RAISE EXCEPTION 'reservation release requires positive completion or quiescence' USING ERRCODE = '23514';
                        END IF;
                    END IF;
                    RETURN NEW;
                END;
                $$;

                CREATE FUNCTION public.fn_background_job_control_lease() RETURNS trigger
                LANGUAGE plpgsql SECURITY INVOKER SET search_path = pg_catalog AS $$
                BEGIN
                    IF NEW.ownership_token IS NOT NULL THEN
                        IF NEW.ownership_until > clock_timestamp() + interval '10 minutes' THEN
                            RAISE EXCEPTION 'control lease exceeds its bound' USING ERRCODE = '23514';
                        END IF;
                        IF TG_OP = 'UPDATE' THEN
                            IF NEW.ownership_token = OLD.ownership_token AND NEW.ownership_until > OLD.ownership_until
                               AND OLD.ownership_until <= clock_timestamp() THEN
                                RAISE EXCEPTION 'expired control lease cannot renew' USING ERRCODE = '23514';
                            END IF;
                            IF NEW.ownership_token IS DISTINCT FROM OLD.ownership_token AND OLD.ownership_token IS NOT NULL
                               AND OLD.ownership_until > clock_timestamp() THEN
                                RAISE EXCEPTION 'live control lease cannot be replaced' USING ERRCODE = '23514';
                            END IF;
                        END IF;
                    END IF;
                    RETURN NEW;
                END;
                $$;
                CREATE FUNCTION public.fn_background_job_recurrences_guard() RETURNS trigger
                LANGUAGE plpgsql SECURITY INVOKER SET search_path = pg_catalog AS $$
                DECLARE previous record;
                BEGIN
                    IF TG_OP = 'INSERT' THEN
                        PERFORM pg_advisory_xact_lock(hashtextextended(NEW.recurrence_key, 0));
                        SELECT r.revision, r.activates_at, r.superseded_at INTO previous
                          FROM public.background_job_recurrences r WHERE r.recurrence_key = NEW.recurrence_key
                          ORDER BY r.revision DESC LIMIT 1 FOR UPDATE;
                        IF FOUND THEN
                            IF NEW.revision <= previous.revision OR NEW.activates_at < previous.activates_at THEN
                                RAISE EXCEPTION 'recurrence revision and activation cannot move backward' USING ERRCODE = '23514';
                            END IF;
                            UPDATE public.background_job_recurrences SET superseded_at = NEW.activates_at
                              WHERE recurrence_key = NEW.recurrence_key AND revision = previous.revision;
                        END IF;
                        IF NEW.superseded_at IS NOT NULL OR NEW.ownership_token IS NOT NULL
                           OR NEW.quarantined_at IS NOT NULL OR NEW.last_error IS NOT NULL THEN
                            RAISE EXCEPTION 'recurrence insert accepts only a new manifest' USING ERRCODE = '23514';
                        END IF;
                    ELSE
                        IF ROW(NEW.recurrence_key, NEW.revision, NEW.manifest_hash, NEW.target_definition_key,
                               NEW.schedule, NEW.activates_at, NEW.anchor_at, NEW.period_microseconds,
                               NEW.jitter_microseconds, NEW.recipient_policy, NEW.misfire_policy)
                           IS DISTINCT FROM ROW(OLD.recurrence_key, OLD.revision, OLD.manifest_hash, OLD.target_definition_key,
                               OLD.schedule, OLD.activates_at, OLD.anchor_at, OLD.period_microseconds,
                               OLD.jitter_microseconds, OLD.recipient_policy, OLD.misfire_policy)
                           OR (OLD.superseded_at IS NOT NULL AND NEW.superseded_at IS DISTINCT FROM OLD.superseded_at)
                           OR NEW.next_scheduled_at < OLD.next_scheduled_at
                           OR (OLD.quarantined_at IS NOT NULL AND NEW IS DISTINCT FROM OLD) THEN
                            RAISE EXCEPTION 'recurrence manifest and progress are immutable or monotonic' USING ERRCODE = '23514';
                        END IF;
                    END IF;
                    RETURN NEW;
                END;
                $$;
                CREATE FUNCTION public.fn_background_job_occurrences_guard() RETURNS trigger
                LANGUAGE plpgsql SECURITY INVOKER SET search_path = pg_catalog AS $$
                DECLARE manifest record;
                BEGIN
                    IF TG_OP = 'INSERT' THEN
                        SELECT r.manifest_hash, r.target_definition_key, r.recipient_policy, r.jitter_microseconds,
                               r.activates_at, r.superseded_at, r.anchor_at, r.period_microseconds INTO manifest
                          FROM public.background_job_recurrences r
                          WHERE r.recurrence_key = NEW.recurrence_key AND r.revision = NEW.revision FOR KEY SHARE;
                        IF NOT FOUND OR ROW(NEW.manifest_hash, NEW.target_definition_key, NEW.recipient_policy, NEW.jitter_microseconds)
                           IS DISTINCT FROM ROW(manifest.manifest_hash, manifest.target_definition_key, manifest.recipient_policy, manifest.jitter_microseconds)
                           OR NEW.scheduled_at < manifest.activates_at
                           OR (manifest.superseded_at IS NOT NULL AND NEW.scheduled_at >= manifest.superseded_at)
                           OR mod(extract(epoch FROM NEW.scheduled_at - manifest.anchor_at) * 1000000, manifest.period_microseconds) <> 0
                           OR NEW.scheduled_at > clock_timestamp() OR NEW.bounds_frozen_at IS NOT NULL
                           OR NEW.ownership_token IS NOT NULL OR NEW.completed_at IS NOT NULL
                           OR NEW.quarantined_at IS NOT NULL OR NEW.last_error IS NOT NULL THEN
                            RAISE EXCEPTION 'occurrence does not match its eligible frozen manifest' USING ERRCODE = '23514';
                        END IF;
                    ELSE
                        IF ROW(NEW.id, NEW.recurrence_key, NEW.revision, NEW.manifest_hash, NEW.target_definition_key,
                               NEW.recipient_policy, NEW.scheduled_at, NEW.jitter_microseconds, NEW.skipped_slots)
                           IS DISTINCT FROM ROW(OLD.id, OLD.recurrence_key, OLD.revision, OLD.manifest_hash, OLD.target_definition_key,
                               OLD.recipient_policy, OLD.scheduled_at, OLD.jitter_microseconds, OLD.skipped_slots)
                           OR (OLD.bounds_frozen_at IS NOT NULL AND ROW(NEW.bounds_frozen_at, NEW.upper_tenant_id, NEW.upper_organization_id)
                               IS DISTINCT FROM ROW(OLD.bounds_frozen_at, OLD.upper_tenant_id, OLD.upper_organization_id))
                           OR (OLD.after_tenant_id IS NOT NULL AND (NEW.after_tenant_id IS NULL
                               OR ROW(NEW.after_tenant_id, COALESCE(NEW.after_organization_id, '00000000-0000-0000-0000-000000000000'::uuid))
                                < ROW(OLD.after_tenant_id, COALESCE(OLD.after_organization_id, '00000000-0000-0000-0000-000000000000'::uuid))))
                           OR ((OLD.completed_at IS NOT NULL OR OLD.quarantined_at IS NOT NULL) AND NEW IS DISTINCT FROM OLD) THEN
                            RAISE EXCEPTION 'occurrence facts and progress are immutable or monotonic' USING ERRCODE = '23514';
                        END IF;
                        IF ROW(NEW.bounds_frozen_at, NEW.after_tenant_id, NEW.after_organization_id, NEW.completed_at)
                             IS DISTINCT FROM ROW(OLD.bounds_frozen_at, OLD.after_tenant_id, OLD.after_organization_id, OLD.completed_at)
                           AND (OLD.ownership_token IS NULL OR OLD.ownership_until <= clock_timestamp()) THEN
                            RAISE EXCEPTION 'occurrence checkpoint requires live ownership' USING ERRCODE = '23514';
                        END IF;
                    END IF;
                    RETURN NEW;
                END;
                $$;
                CREATE FUNCTION public.fn_background_job_recipients_guard() RETURNS trigger
                LANGUAGE plpgsql SECURITY INVOKER SET search_path = pg_catalog AS $$
                DECLARE occurrence record; child record;
                BEGIN
                    IF TG_OP = 'INSERT' THEN
                        SELECT o.recurrence_key, o.revision, o.manifest_hash, o.target_definition_key, o.recipient_policy,
                               o.scheduled_at, o.jitter_microseconds, o.bounds_frozen_at, o.ownership_until,
                               o.completed_at, o.quarantined_at, o.upper_tenant_id, o.upper_organization_id,
                               o.after_tenant_id, o.after_organization_id INTO occurrence
                          FROM public.background_job_occurrences o WHERE o.id = NEW.occurrence_id FOR KEY SHARE;
                        IF NOT FOUND OR ROW(NEW.recurrence_key, NEW.revision, NEW.manifest_hash, NEW.target_definition_key,
                               NEW.recipient_policy, NEW.scheduled_at)
                            IS DISTINCT FROM ROW(occurrence.recurrence_key, occurrence.revision, occurrence.manifest_hash,
                               occurrence.target_definition_key, occurrence.recipient_policy, occurrence.scheduled_at)
                           OR occurrence.bounds_frozen_at IS NULL OR occurrence.ownership_until IS NULL
                           OR occurrence.ownership_until <= clock_timestamp() OR occurrence.completed_at IS NOT NULL
                           OR occurrence.quarantined_at IS NOT NULL OR occurrence.upper_tenant_id IS NULL
                           OR ROW(NEW.tenant_id, COALESCE(NEW.organization_id, '00000000-0000-0000-0000-000000000000'::uuid))
                               > ROW(occurrence.upper_tenant_id, COALESCE(occurrence.upper_organization_id, '00000000-0000-0000-0000-000000000000'::uuid))
                           OR (occurrence.after_tenant_id IS NOT NULL AND
                               ROW(NEW.tenant_id, COALESCE(NEW.organization_id, '00000000-0000-0000-0000-000000000000'::uuid))
                                   <= ROW(occurrence.after_tenant_id, COALESCE(occurrence.after_organization_id, '00000000-0000-0000-0000-000000000000'::uuid)))
                           OR NEW.due_at > NEW.scheduled_at + occurrence.jitter_microseconds * interval '1 microsecond'
                           OR ((NEW.organization_id IS NOT NULL) <> (NEW.recipient_policy = 'serving_organizations'))
                           OR NEW.accepted_job_id IS NOT NULL OR NEW.skipped_at IS NOT NULL
                           OR NEW.ownership_token IS NOT NULL OR NEW.quarantined_at IS NOT NULL OR NEW.last_error IS NOT NULL THEN
                            RAISE EXCEPTION 'recipient does not match its claimed occurrence route' USING ERRCODE = '23514';
                        END IF;
                        NEW.available_after := NEW.due_at;
                    ELSE
                        IF ROW(NEW.id, NEW.occurrence_id, NEW.tenant_id, NEW.organization_id, NEW.recurrence_key,
                               NEW.revision, NEW.manifest_hash, NEW.target_definition_key, NEW.recipient_policy, NEW.scheduled_at, NEW.due_at)
                           IS DISTINCT FROM ROW(OLD.id, OLD.occurrence_id, OLD.tenant_id, OLD.organization_id, OLD.recurrence_key,
                               OLD.revision, OLD.manifest_hash, OLD.target_definition_key, OLD.recipient_policy, OLD.scheduled_at, OLD.due_at)
                           OR ((OLD.accepted_job_id IS NOT NULL OR OLD.skipped_at IS NOT NULL OR OLD.quarantined_at IS NOT NULL)
                               AND NEW IS DISTINCT FROM OLD) THEN
                            RAISE EXCEPTION 'recipient route and final receipt are immutable' USING ERRCODE = '23514';
                        END IF;
                        IF NEW.skipped_at IS NOT NULL AND OLD.skipped_at IS NULL AND
                           (OLD.ownership_token IS NULL OR OLD.ownership_until <= clock_timestamp()) THEN
                            RAISE EXCEPTION 'recipient skip requires live claim' USING ERRCODE = '23514';
                        END IF;
                        IF NEW.accepted_job_id IS NOT NULL AND OLD.accepted_job_id IS NULL THEN
                            IF current_user = 'learnstack_outbox_admin' OR OLD.ownership_token IS NULL
                               OR OLD.ownership_until <= clock_timestamp() THEN
                                RAISE EXCEPTION 'recipient acceptance requires app role and live claim' USING ERRCODE = '23514';
                            END IF;
                            SELECT j.organization_id, j.recipient_id, j.definition_key, j.original_due_at INTO child
                              FROM public.background_jobs j WHERE j.tenant_id = NEW.tenant_id AND j.id = NEW.accepted_job_id FOR KEY SHARE;
                            IF NOT FOUND OR ROW(child.organization_id, child.recipient_id, child.definition_key, child.original_due_at)
                                IS DISTINCT FROM ROW(NEW.organization_id, NEW.id, NEW.target_definition_key, NEW.due_at) THEN
                                RAISE EXCEPTION 'recipient child receipt is mismatched' USING ERRCODE = '23514';
                            END IF;
                            NEW.accepted_at := clock_timestamp();
                            NEW.ownership_token := NULL; NEW.ownership_until := NULL;
                        END IF;
                    END IF;
                    RETURN NEW;
                END;
                $$;
                CREATE FUNCTION public.fn_background_job_recipient_receipt() RETURNS trigger
                LANGUAGE plpgsql SECURITY INVOKER SET search_path = pg_catalog AS $$
                DECLARE target_tenant uuid; target_recipient uuid; recipient record; child record;
                BEGIN
                    target_tenant := NEW.tenant_id;
                    IF TG_TABLE_NAME = 'background_jobs' THEN target_recipient := NEW.recipient_id;
                    ELSE target_recipient := NEW.id; END IF;
                    IF target_recipient IS NULL THEN RETURN NULL; END IF;
                    SELECT r.organization_id, r.accepted_job_id, r.target_definition_key, r.due_at INTO recipient
                      FROM public.background_job_recipients r
                      WHERE r.tenant_id = target_tenant AND r.id = target_recipient FOR KEY SHARE;
                    IF NOT FOUND THEN RAISE EXCEPTION 'recipient receipt unavailable' USING ERRCODE = '23514'; END IF;
                    SELECT j.id, j.organization_id, j.definition_key, j.original_due_at INTO child
                      FROM public.background_jobs j WHERE j.tenant_id = target_tenant AND j.recipient_id = target_recipient FOR KEY SHARE;
                    IF (FOUND AND (recipient.accepted_job_id IS DISTINCT FROM child.id
                                  OR ROW(recipient.organization_id, recipient.target_definition_key, recipient.due_at)
                                     IS DISTINCT FROM ROW(child.organization_id, child.definition_key, child.original_due_at)))
                       OR (NOT FOUND AND recipient.accepted_job_id IS NOT NULL) THEN
                        RAISE EXCEPTION 'child insertion and reciprocal recipient receipt must commit together' USING ERRCODE = '23514';
                    END IF;
                    RETURN NULL;
                END;
                $$;

                CREATE INDEX ix_background_jobs_tenant_id_organization_id ON background_jobs (tenant_id, organization_id);

                CREATE INDEX ix_background_job_attempts_tenant_id_organization_id ON background_job_attempts (tenant_id, organization_id);

                CREATE INDEX ix_background_job_recipients_tenant_id_organization_id ON background_job_recipients (tenant_id, organization_id);

                CREATE INDEX ix_background_jobs_parent ON background_jobs (tenant_id, parent_job_id);
                CREATE INDEX ix_background_jobs_recipient ON background_jobs (tenant_id, recipient_id);
                CREATE INDEX ix_background_jobs_current_attempt ON background_jobs (tenant_id, id, current_attempt_id);
                CREATE INDEX ix_background_jobs_final_outcome ON background_jobs (tenant_id, id, final_outcome_attempt_id);
                CREATE INDEX ix_background_job_attempts_outcome ON background_job_attempts (tenant_id, job_id, final_outcome_attempt_id);
                CREATE INDEX ix_background_job_reservations_capacity ON background_job_reservations (tenant_id, capacity_group);
                CREATE INDEX ix_background_job_recipients_job ON background_job_recipients (tenant_id, accepted_job_id);
                CREATE INDEX ix_background_job_occurrences_recurrence ON background_job_occurrences (recurrence_key, revision);
                CREATE INDEX ix_background_jobs_pending ON background_jobs (available_after, created_at, id)
                    WHERE phase = 'pending' AND processed_at IS NULL AND dead_lettered_at IS NULL
                      AND terminal_reason IS NULL AND normal_attempts < 6 AND ownership_token IS NULL;
                CREATE INDEX ix_background_jobs_finalize ON background_jobs (available_after, created_at, id)
                    WHERE phase = 'finalize_pending' AND processed_at IS NULL AND dead_lettered_at IS NULL
                      AND terminal_reason IS NULL AND finalize_attempts < 6 AND ownership_token IS NULL;
                CREATE INDEX ix_background_jobs_recovery ON background_jobs (ownership_until, created_at, id)
                    WHERE ownership_token IS NOT NULL AND processed_at IS NULL AND dead_lettered_at IS NULL
                      AND terminal_reason IS NULL AND phase <> 'quarantined';
                CREATE INDEX ix_background_jobs_exhausted ON background_jobs (available_after, created_at, id)
                    WHERE ownership_token IS NULL AND processed_at IS NULL AND dead_lettered_at IS NULL
                      AND terminal_reason IS NULL AND ((phase = 'pending' AND normal_attempts = 6)
                          OR (phase = 'finalize_pending' AND finalize_attempts = 6));
                CREATE INDEX ix_background_jobs_terminal ON background_jobs (available_after, created_at, id)
                    WHERE phase = 'terminal_pending' AND terminal_reason IS NOT NULL
                      AND processed_at IS NULL AND dead_lettered_at IS NULL;
                CREATE INDEX ix_background_jobs_bridge ON background_jobs (handoff_check_at, created_at, id)
                    WHERE phase IN ('pending', 'finalize_pending') AND processed_at IS NULL AND dead_lettered_at IS NULL
                      AND terminal_reason IS NULL AND ownership_token IS NULL;
                CREATE INDEX ix_background_jobs_probes ON background_jobs (probe_after, created_at, id)
                    WHERE phase = 'reconciling' AND ownership_token IS NULL AND probe_after IS NOT NULL
                      AND processed_at IS NULL AND dead_lettered_at IS NULL AND terminal_reason IS NULL;
                CREATE INDEX ix_background_job_reservations_occupied ON background_job_reservations (tenant_id, capacity_group)
                    WHERE released_at IS NULL;
                CREATE INDEX ix_background_job_recurrences_pending ON background_job_recurrences (available_after, recurrence_key, revision)
                    WHERE quarantined_at IS NULL AND (superseded_at IS NULL OR next_scheduled_at < superseded_at);
                CREATE INDEX ix_background_job_occurrences_pending ON background_job_occurrences (available_after, scheduled_at, id)
                    WHERE completed_at IS NULL AND quarantined_at IS NULL;
                CREATE INDEX ix_background_job_recipients_pending ON background_job_recipients (available_after, id)
                    WHERE accepted_job_id IS NULL AND skipped_at IS NULL AND quarantined_at IS NULL;

                ALTER TABLE background_jobs ENABLE ROW LEVEL SECURITY;
                ALTER TABLE background_jobs FORCE ROW LEVEL SECURITY;

                CREATE POLICY background_jobs_isolation ON background_jobs
                            USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
                                AND (organization_id IS NULL
                                    OR organization_id = NULLIF(current_setting('app.organization_id', true), '')::uuid
                                    OR current_setting('app.scope', true) = 'tenant'))
                            WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
                                AND ((organization_id IS NULL AND NULLIF(current_setting('app.organization_id', true), '') IS NULL)
                                    OR organization_id = NULLIF(current_setting('app.organization_id', true), '')::uuid));
                        CREATE POLICY background_jobs_org_write_guard ON background_jobs AS RESTRICTIVE FOR UPDATE
                            USING ((organization_id IS NULL AND NULLIF(current_setting('app.organization_id', true), '') IS NULL)
                                OR organization_id = NULLIF(current_setting('app.organization_id', true), '')::uuid);
                        CREATE POLICY background_jobs_org_delete_guard ON background_jobs AS RESTRICTIVE FOR DELETE
                            USING ((organization_id IS NULL AND NULLIF(current_setting('app.organization_id', true), '') IS NULL)
                                OR organization_id = NULLIF(current_setting('app.organization_id', true), '')::uuid);
                        CREATE TRIGGER tg_background_jobs_organization_id_immutable BEFORE UPDATE ON background_jobs
                            FOR EACH ROW EXECUTE FUNCTION fn_organization_id_immutable();

                ALTER TABLE background_job_attempts ENABLE ROW LEVEL SECURITY;
                ALTER TABLE background_job_attempts FORCE ROW LEVEL SECURITY;

                CREATE POLICY background_job_attempts_isolation ON background_job_attempts
                            USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
                                AND (organization_id IS NULL
                                    OR organization_id = NULLIF(current_setting('app.organization_id', true), '')::uuid
                                    OR current_setting('app.scope', true) = 'tenant'))
                            WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
                                AND ((organization_id IS NULL AND NULLIF(current_setting('app.organization_id', true), '') IS NULL)
                                    OR organization_id = NULLIF(current_setting('app.organization_id', true), '')::uuid));
                        CREATE POLICY background_job_attempts_org_write_guard ON background_job_attempts AS RESTRICTIVE FOR UPDATE
                            USING ((organization_id IS NULL AND NULLIF(current_setting('app.organization_id', true), '') IS NULL)
                                OR organization_id = NULLIF(current_setting('app.organization_id', true), '')::uuid);
                        CREATE POLICY background_job_attempts_org_delete_guard ON background_job_attempts AS RESTRICTIVE FOR DELETE
                            USING ((organization_id IS NULL AND NULLIF(current_setting('app.organization_id', true), '') IS NULL)
                                OR organization_id = NULLIF(current_setting('app.organization_id', true), '')::uuid);
                        CREATE TRIGGER tg_background_job_attempts_organization_id_immutable BEFORE UPDATE ON background_job_attempts
                            FOR EACH ROW EXECUTE FUNCTION fn_organization_id_immutable();

                ALTER TABLE background_job_capacity ENABLE ROW LEVEL SECURITY;
                ALTER TABLE background_job_capacity FORCE ROW LEVEL SECURITY;

                CREATE POLICY background_job_capacity_isolation ON background_job_capacity
                            USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                            WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

                ALTER TABLE background_job_reservations ENABLE ROW LEVEL SECURITY;
                ALTER TABLE background_job_reservations FORCE ROW LEVEL SECURITY;

                CREATE POLICY background_job_reservations_isolation ON background_job_reservations
                            USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                            WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

                ALTER TABLE background_job_recurrences ENABLE ROW LEVEL SECURITY;
                ALTER TABLE background_job_recurrences FORCE ROW LEVEL SECURITY;

                ALTER TABLE background_job_occurrences ENABLE ROW LEVEL SECURITY;
                ALTER TABLE background_job_occurrences FORCE ROW LEVEL SECURITY;

                ALTER TABLE background_job_recipients ENABLE ROW LEVEL SECURITY;
                ALTER TABLE background_job_recipients FORCE ROW LEVEL SECURITY;

                CREATE POLICY background_job_recipients_isolation ON background_job_recipients
                            USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
                                AND (organization_id IS NULL
                                    OR organization_id = NULLIF(current_setting('app.organization_id', true), '')::uuid
                                    OR current_setting('app.scope', true) = 'tenant'))
                            WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
                                AND ((organization_id IS NULL AND NULLIF(current_setting('app.organization_id', true), '') IS NULL)
                                    OR organization_id = NULLIF(current_setting('app.organization_id', true), '')::uuid));
                        CREATE POLICY background_job_recipients_org_write_guard ON background_job_recipients AS RESTRICTIVE FOR UPDATE
                            USING ((organization_id IS NULL AND NULLIF(current_setting('app.organization_id', true), '') IS NULL)
                                OR organization_id = NULLIF(current_setting('app.organization_id', true), '')::uuid);
                        CREATE POLICY background_job_recipients_org_delete_guard ON background_job_recipients AS RESTRICTIVE FOR DELETE
                            USING ((organization_id IS NULL AND NULLIF(current_setting('app.organization_id', true), '') IS NULL)
                                OR organization_id = NULLIF(current_setting('app.organization_id', true), '')::uuid);
                        CREATE TRIGGER tg_background_job_recipients_organization_id_immutable BEFORE UPDATE ON background_job_recipients
                            FOR EACH ROW EXECUTE FUNCTION fn_organization_id_immutable();

                CREATE TRIGGER tg_background_jobs_no_delete BEFORE DELETE ON background_jobs
                        FOR EACH ROW EXECUTE FUNCTION public.fn_background_job_no_delete();
                    CREATE TRIGGER tg_background_jobs_no_truncate BEFORE TRUNCATE ON background_jobs
                        FOR EACH STATEMENT EXECUTE FUNCTION public.fn_background_job_no_delete();

                CREATE TRIGGER tg_background_job_attempts_no_delete BEFORE DELETE ON background_job_attempts
                        FOR EACH ROW EXECUTE FUNCTION public.fn_background_job_no_delete();
                    CREATE TRIGGER tg_background_job_attempts_no_truncate BEFORE TRUNCATE ON background_job_attempts
                        FOR EACH STATEMENT EXECUTE FUNCTION public.fn_background_job_no_delete();

                CREATE TRIGGER tg_background_job_capacity_no_delete BEFORE DELETE ON background_job_capacity
                        FOR EACH ROW EXECUTE FUNCTION public.fn_background_job_no_delete();
                    CREATE TRIGGER tg_background_job_capacity_no_truncate BEFORE TRUNCATE ON background_job_capacity
                        FOR EACH STATEMENT EXECUTE FUNCTION public.fn_background_job_no_delete();

                CREATE TRIGGER tg_background_job_reservations_no_delete BEFORE DELETE ON background_job_reservations
                        FOR EACH ROW EXECUTE FUNCTION public.fn_background_job_no_delete();
                    CREATE TRIGGER tg_background_job_reservations_no_truncate BEFORE TRUNCATE ON background_job_reservations
                        FOR EACH STATEMENT EXECUTE FUNCTION public.fn_background_job_no_delete();

                CREATE TRIGGER tg_background_job_recurrences_no_delete BEFORE DELETE ON background_job_recurrences
                        FOR EACH ROW EXECUTE FUNCTION public.fn_background_job_no_delete();
                    CREATE TRIGGER tg_background_job_recurrences_no_truncate BEFORE TRUNCATE ON background_job_recurrences
                        FOR EACH STATEMENT EXECUTE FUNCTION public.fn_background_job_no_delete();

                CREATE TRIGGER tg_background_job_occurrences_no_delete BEFORE DELETE ON background_job_occurrences
                        FOR EACH ROW EXECUTE FUNCTION public.fn_background_job_no_delete();
                    CREATE TRIGGER tg_background_job_occurrences_no_truncate BEFORE TRUNCATE ON background_job_occurrences
                        FOR EACH STATEMENT EXECUTE FUNCTION public.fn_background_job_no_delete();

                CREATE TRIGGER tg_background_job_recipients_no_delete BEFORE DELETE ON background_job_recipients
                        FOR EACH ROW EXECUTE FUNCTION public.fn_background_job_no_delete();
                    CREATE TRIGGER tg_background_job_recipients_no_truncate BEFORE TRUNCATE ON background_job_recipients
                        FOR EACH STATEMENT EXECUTE FUNCTION public.fn_background_job_no_delete();

                CREATE TRIGGER tg_background_jobs_envelope BEFORE INSERT OR UPDATE ON background_jobs
                    FOR EACH ROW EXECUTE FUNCTION public.fn_background_jobs_envelope();
                CREATE TRIGGER tg_background_jobs_transition BEFORE UPDATE ON background_jobs
                    FOR EACH ROW EXECUTE FUNCTION public.fn_background_jobs_transition();
                CREATE TRIGGER tg_background_job_attempts_guard BEFORE INSERT OR UPDATE ON background_job_attempts
                    FOR EACH ROW EXECUTE FUNCTION public.fn_background_job_attempts_guard();
                CREATE TRIGGER tg_background_job_capacity_immutable BEFORE UPDATE ON background_job_capacity
                    FOR EACH ROW EXECUTE FUNCTION public.fn_background_job_capacity_immutable();
                CREATE TRIGGER tg_background_job_reservations_guard BEFORE INSERT OR UPDATE ON background_job_reservations
                    FOR EACH ROW EXECUTE FUNCTION public.fn_background_job_reservations_guard();
                CREATE TRIGGER tg_background_job_recurrences_guard BEFORE INSERT OR UPDATE ON background_job_recurrences
                    FOR EACH ROW EXECUTE FUNCTION public.fn_background_job_recurrences_guard();
                CREATE TRIGGER tg_background_job_occurrences_guard BEFORE INSERT OR UPDATE ON background_job_occurrences
                    FOR EACH ROW EXECUTE FUNCTION public.fn_background_job_occurrences_guard();
                CREATE TRIGGER tg_background_job_recipients_guard BEFORE INSERT OR UPDATE ON background_job_recipients
                    FOR EACH ROW EXECUTE FUNCTION public.fn_background_job_recipients_guard();
                CREATE CONSTRAINT TRIGGER ct_background_jobs_attempt_counts AFTER INSERT OR UPDATE ON background_jobs
                    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.fn_background_job_attempt_counts();
                CREATE CONSTRAINT TRIGGER ct_background_job_attempts_counts AFTER INSERT OR UPDATE ON background_job_attempts
                    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.fn_background_job_attempt_counts();
                CREATE CONSTRAINT TRIGGER ct_background_jobs_recipient AFTER INSERT OR UPDATE ON background_jobs
                    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.fn_background_job_recipient_receipt();
                CREATE CONSTRAINT TRIGGER ct_background_job_recipients_receipt AFTER INSERT OR UPDATE ON background_job_recipients
                    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.fn_background_job_recipient_receipt();

                CREATE TRIGGER tg_background_job_recurrences_lease BEFORE INSERT OR UPDATE ON background_job_recurrences
                        FOR EACH ROW EXECUTE FUNCTION public.fn_background_job_control_lease();

                CREATE TRIGGER tg_background_job_occurrences_lease BEFORE INSERT OR UPDATE ON background_job_occurrences
                        FOR EACH ROW EXECUTE FUNCTION public.fn_background_job_control_lease();

                CREATE TRIGGER tg_background_job_recipients_lease BEFORE INSERT OR UPDATE ON background_job_recipients
                        FOR EACH ROW EXECUTE FUNCTION public.fn_background_job_control_lease();

                GRANT SELECT ON background_jobs, background_job_attempts, background_job_capacity,
                    background_job_reservations, background_job_recipients TO learnstack_app;
                GRANT INSERT (id, tenant_id, organization_id, definition_key, profile, queue, capacity_group,
                    parameters, original_due_at, traceparent, causal_actor_user_id, causation_event_id, parent_job_id, recipient_id)
                    ON background_jobs TO learnstack_app;
                GRANT UPDATE (phase, normal_attempts, finalize_attempts, current_attempt_id, attempt_deadline,
                    plan, plan_fingerprint, prepared_at, operation_id, final_outcome_attempt_id, finalization_status,
                    finalization_reason, processed_at, terminal_reason, dead_lettered_at)
                    ON background_jobs TO learnstack_app;
                GRANT INSERT (id, tenant_id, organization_id, job_id, stage, ordinal, admission_token, deadline, final_outcome_attempt_id)
                    ON background_job_attempts TO learnstack_app;
                GRANT UPDATE (plan_fingerprint, operation_id, execute_intent_at, outcome, outcome_kind, outcome_at,
                    quiescence_id, quiescence_kind, quiescent_at, no_start_at) ON background_job_attempts TO learnstack_app;
                GRANT INSERT (tenant_id, capacity_group) ON background_job_capacity TO learnstack_app;
                -- PostgreSQL row locking requires an UPDATE privilege. The column is immutable;
                -- this narrow grant permits SELECT FOR UPDATE, never a mutex identity mutation.
                GRANT UPDATE (capacity_group) ON background_job_capacity TO learnstack_app;
                GRANT INSERT (tenant_id, capacity_group, job_id, attempt_id) ON background_job_reservations TO learnstack_app;
                GRANT UPDATE (released_at, release_reason) ON background_job_reservations TO learnstack_app;
                GRANT UPDATE (accepted_job_id, accepted_at) ON background_job_recipients TO learnstack_app;
                GRANT SELECT (id, tenant_id, organization_id, definition_key, profile, queue, capacity_group,
                    original_due_at, created_at, recipient_id, plan_fingerprint, prepared_at, operation_id,
                    final_outcome_attempt_id, finalization_status, finalization_reason, business_finalization_incomplete,
                    phase, normal_attempts, finalize_attempts, current_attempt_id, ownership_token, ownership_kind,
                    ownership_until, attempt_deadline, available_after, wake_generation, bridge_token, bridge_until,
                    vendor_job_id, handoff_check_at, probe_after, unsuccessful_probes, last_error, quarantine_reason,
                    terminal_reason, processed_at, dead_lettered_at) ON background_jobs TO learnstack_outbox_admin;
                GRANT UPDATE (phase, ownership_token, ownership_kind, ownership_until, available_after,
                    wake_generation, bridge_token, bridge_until, vendor_job_id, handoff_check_at, probe_after,
                    unsuccessful_probes, last_error, quarantine_reason, terminal_reason, dead_lettered_at,
                    business_finalization_incomplete) ON background_jobs TO learnstack_outbox_admin;
                GRANT SELECT (id, tenant_id, organization_id, job_id, stage, ordinal, admission_token, admitted_at,
                    deadline, final_outcome_attempt_id, plan_fingerprint, operation_id, execute_intent_at, outcome_kind,
                    outcome_at, quiescence_id, quiescence_kind, quiescent_at, no_start_at)
                    ON background_job_attempts TO learnstack_outbox_admin;
                GRANT SELECT ON background_job_capacity, background_job_reservations,
                    background_job_recurrences, background_job_occurrences, background_job_recipients TO learnstack_outbox_admin;
                GRANT INSERT (recurrence_key, revision, manifest_hash, target_definition_key, schedule, activates_at,
                    anchor_at, period_microseconds, jitter_microseconds, recipient_policy, misfire_policy, next_scheduled_at)
                    ON background_job_recurrences TO learnstack_outbox_admin;
                GRANT UPDATE (superseded_at, next_scheduled_at, available_after, ownership_token, ownership_until,
                    last_error, quarantined_at) ON background_job_recurrences TO learnstack_outbox_admin;
                GRANT INSERT (id, recurrence_key, revision, manifest_hash, target_definition_key, recipient_policy,
                    scheduled_at, jitter_microseconds, skipped_slots) ON background_job_occurrences TO learnstack_outbox_admin;
                GRANT UPDATE (bounds_frozen_at, upper_tenant_id, upper_organization_id, after_tenant_id,
                    after_organization_id, available_after, ownership_token, ownership_until, completed_at,
                    last_error, quarantined_at) ON background_job_occurrences TO learnstack_outbox_admin;
                GRANT INSERT (id, occurrence_id, tenant_id, organization_id, recurrence_key, revision,
                    manifest_hash, target_definition_key, recipient_policy, scheduled_at, due_at)
                    ON background_job_recipients TO learnstack_outbox_admin;
                GRANT UPDATE (available_after, ownership_token, ownership_until, skipped_at, skipped_reason,
                    last_error, quarantined_at) ON background_job_recipients TO learnstack_outbox_admin;

                REVOKE ALL ON FUNCTION public.fn_background_job_no_delete() FROM PUBLIC;

                REVOKE ALL ON FUNCTION public.fn_background_jobs_envelope() FROM PUBLIC;

                REVOKE ALL ON FUNCTION public.fn_background_jobs_transition() FROM PUBLIC;

                REVOKE ALL ON FUNCTION public.fn_background_job_attempts_guard() FROM PUBLIC;

                REVOKE ALL ON FUNCTION public.fn_background_job_attempt_counts() FROM PUBLIC;

                REVOKE ALL ON FUNCTION public.fn_background_job_capacity_immutable() FROM PUBLIC;

                REVOKE ALL ON FUNCTION public.fn_background_job_reservations_guard() FROM PUBLIC;

                REVOKE ALL ON FUNCTION public.fn_background_job_control_lease() FROM PUBLIC;

                REVOKE ALL ON FUNCTION public.fn_background_job_recurrences_guard() FROM PUBLIC;

                REVOKE ALL ON FUNCTION public.fn_background_job_occurrences_guard() FROM PUBLIC;

                REVOKE ALL ON FUNCTION public.fn_background_job_recipients_guard() FROM PUBLIC;

                REVOKE ALL ON FUNCTION public.fn_background_job_recipient_receipt() FROM PUBLIC;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Disposable-schema reversal only. Live rollback uses a forward fix;
            // remove the later vendor migration before reversing this one.
            migrationBuilder.Sql("""
                ALTER TABLE background_jobs DROP CONSTRAINT fk_background_jobs_recipient;
                ALTER TABLE background_jobs DROP CONSTRAINT fk_background_jobs_current_attempt;
                ALTER TABLE background_jobs DROP CONSTRAINT fk_background_jobs_final_outcome;
                DROP TABLE background_job_recipients;
                DROP TABLE background_job_occurrences;
                DROP TABLE background_job_recurrences;
                DROP TABLE background_job_reservations;
                DROP TABLE background_job_capacity;
                DROP TABLE background_job_attempts;
                DROP TABLE background_jobs;
                DROP FUNCTION public.fn_background_job_no_delete();
                DROP FUNCTION public.fn_background_jobs_envelope();
                DROP FUNCTION public.fn_background_jobs_transition();
                DROP FUNCTION public.fn_background_job_attempts_guard();
                DROP FUNCTION public.fn_background_job_attempt_counts();
                DROP FUNCTION public.fn_background_job_capacity_immutable();
                DROP FUNCTION public.fn_background_job_reservations_guard();
                DROP FUNCTION public.fn_background_job_control_lease();
                DROP FUNCTION public.fn_background_job_recurrences_guard();
                DROP FUNCTION public.fn_background_job_occurrences_guard();
                DROP FUNCTION public.fn_background_job_recipients_guard();
                DROP FUNCTION public.fn_background_job_recipient_receipt();
                """);
        }
    }
}
