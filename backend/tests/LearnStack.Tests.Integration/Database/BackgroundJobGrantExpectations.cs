namespace LearnStack.Tests.Integration.Database;

/// <summary>Exact ADR-0047 runtime grant budgets, independent of applied ACL discovery.</summary>
internal static class BackgroundJobGrantExpectations
{
    public static IEnumerable<string> TableGrants =>
        SchemaTableClasses.JobTenant.Select(table => $"learnstack_app {table} SELECT")
            .Concat(SchemaTableClasses.Control.Concat(["background_job_capacity", "background_job_reservations", "background_job_recipients"]).Select(table => $"learnstack_outbox_admin {table} SELECT"))
            .Concat(SchemaTableClasses.Vendor.Select(table =>
                $"learnstack_outbox_admin {table} " + (table switch
                {
                    "schema" or "state" or "jobparameter" => "SELECT",
                    _ => "DELETE,SELECT",
                })));

    public static string[] Columns(string role, string table, string privilege, IEnumerable<string> allColumns)
    {
        if (privilege == "SELECT" &&
            (role == "learnstack_app" && SchemaTableClasses.JobTenant.Contains(table)
             || role == "learnstack_outbox_admin" && table is "background_job_capacity" or "background_job_reservations"
                or "background_job_recurrences" or "background_job_occurrences" or "background_job_recipients"))
            return allColumns.ToArray();
        var names = (role, table, privilege) switch
        {
            ("learnstack_app", "background_jobs", "INSERT") =>
                "id tenant_id organization_id definition_key profile queue capacity_group parameters original_due_at traceparent causal_actor_user_id causation_event_id parent_job_id recipient_id",
            ("learnstack_app", "background_jobs", "UPDATE") =>
                "phase normal_attempts finalize_attempts current_attempt_id attempt_deadline plan plan_fingerprint prepared_at operation_id final_outcome_attempt_id finalization_status finalization_reason processed_at terminal_reason dead_lettered_at",
            ("learnstack_app", "background_job_attempts", "INSERT") =>
                "id tenant_id organization_id job_id stage ordinal admission_token deadline final_outcome_attempt_id",
            ("learnstack_app", "background_job_attempts", "UPDATE") =>
                "plan_fingerprint operation_id execute_intent_at outcome outcome_kind outcome_at quiescence_id quiescence_kind quiescent_at no_start_at transactional_cleanup_at",
            ("learnstack_app", "background_job_capacity", "INSERT") => "tenant_id capacity_group",
            ("learnstack_app", "background_job_capacity", "UPDATE") => "capacity_group",
            ("learnstack_app", "background_job_reservations", "INSERT") => "tenant_id capacity_group job_id attempt_id",
            ("learnstack_app", "background_job_reservations", "UPDATE") => "released_at release_reason",
            ("learnstack_app", "background_job_recipients", "UPDATE") => "accepted_job_id accepted_at",
            ("learnstack_outbox_admin", "background_jobs", "SELECT") =>
                "id tenant_id organization_id definition_key profile queue capacity_group original_due_at created_at recipient_id plan_fingerprint prepared_at operation_id final_outcome_attempt_id finalization_status finalization_reason business_finalization_incomplete phase normal_attempts finalize_attempts current_attempt_id ownership_token ownership_kind ownership_until attempt_deadline available_after wake_generation bridge_token bridge_until vendor_job_id handoff_check_at probe_after unsuccessful_probes last_error quarantine_reason terminal_reason processed_at dead_lettered_at",
            ("learnstack_outbox_admin", "background_jobs", "UPDATE") =>
                "phase ownership_token ownership_kind ownership_until available_after wake_generation bridge_token bridge_until vendor_job_id handoff_check_at probe_after unsuccessful_probes last_error quarantine_reason terminal_reason dead_lettered_at business_finalization_incomplete",
            ("learnstack_outbox_admin", "background_job_attempts", "SELECT") =>
                "id tenant_id organization_id job_id stage ordinal admission_token admitted_at deadline final_outcome_attempt_id plan_fingerprint operation_id execute_intent_at outcome_kind outcome_at quiescence_id quiescence_kind quiescent_at no_start_at transactional_cleanup_at",
            ("learnstack_outbox_admin", "background_job_recurrences", "INSERT") =>
                "recurrence_key revision manifest_hash target_definition_key schedule activates_at anchor_at period_microseconds jitter_microseconds recipient_policy misfire_policy next_scheduled_at",
            ("learnstack_outbox_admin", "background_job_recurrences", "UPDATE") =>
                "superseded_at next_scheduled_at available_after ownership_token ownership_until last_error quarantined_at",
            ("learnstack_outbox_admin", "background_job_occurrences", "INSERT") =>
                "id recurrence_key revision manifest_hash target_definition_key recipient_policy scheduled_at jitter_microseconds skipped_slots",
            ("learnstack_outbox_admin", "background_job_occurrences", "UPDATE") =>
                "bounds_frozen_at upper_tenant_id upper_organization_id after_tenant_id after_organization_id available_after ownership_token ownership_until completed_at last_error quarantined_at",
            ("learnstack_outbox_admin", "background_job_recipients", "INSERT") =>
                "id occurrence_id tenant_id organization_id recurrence_key revision manifest_hash target_definition_key recipient_policy scheduled_at due_at",
            ("learnstack_outbox_admin", "background_job_recipients", "UPDATE") =>
                "available_after ownership_token ownership_until skipped_at skipped_reason last_error quarantined_at",
            _ => "",
        };
        return names.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }
}
