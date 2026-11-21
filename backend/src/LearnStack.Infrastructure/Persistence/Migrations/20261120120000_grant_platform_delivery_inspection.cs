using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LearnStack.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// ADR-0103: the platform role reads the metadata of dead-lettered and pending-terminal rows and quarantined jobs
    /// through named columns, so the database refuses what an inspection must not return.
    /// </summary>
    public partial class grant_platform_delivery_inspection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The role held table-wide SELECT on outbox_messages for the purge of Phase 11, which needs only DELETE and
            // the columns its predicate reads. A column grant added beside the table grant would exclude nothing.
            migrationBuilder.Sql("""
                REVOKE SELECT ON public.outbox_messages FROM learnstack_platform;
                GRANT SELECT (id, tenant_id, organization_id, occurred_at, type, processed_at, attempts, last_error,
                              available_after, locked_until, terminal_reason, dead_lettered_at)
                    ON public.outbox_messages TO learnstack_platform;
                GRANT SELECT (tenant_id, subscription_id, event_id, organization_id, occurred_at, type, processed_at,
                              attempts, last_error, available_after, locked_until, terminal_reason, dead_lettered_at,
                              replay_count, replayed_at)
                    ON public.event_deliveries TO learnstack_platform;
                GRANT SELECT (id, tenant_id, organization_id, definition_key, profile, queue, capacity_group,
                              original_due_at, created_at, phase, normal_attempts, finalize_attempts, available_after,
                              ownership_kind, ownership_until, attempt_deadline, probe_after, unsuccessful_probes,
                              last_error, quarantine_reason, terminal_reason, processed_at, dead_lettered_at,
                              finalization_status, finalization_reason, business_finalization_incomplete,
                              wake_generation)
                    ON public.background_jobs TO learnstack_platform;
                GRANT SELECT (tenant_id, capacity_group, job_id, attempt_id, reserved_at, released_at, release_reason)
                    ON public.background_job_reservations TO learnstack_platform;

                CREATE INDEX ix_outbox_messages_dead_lettered
                    ON public.outbox_messages (dead_lettered_at, id) WHERE dead_lettered_at IS NOT NULL;
                CREATE INDEX ix_event_deliveries_dead_lettered
                    ON public.event_deliveries (dead_lettered_at, tenant_id, subscription_id, event_id)
                    WHERE dead_lettered_at IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("The platform role's column grants require a reviewed forward migration.");
        }
    }
}
