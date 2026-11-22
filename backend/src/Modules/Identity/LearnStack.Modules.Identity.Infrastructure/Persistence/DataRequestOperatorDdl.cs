using Microsoft.EntityFrameworkCore.Migrations;

namespace LearnStack.Modules.Identity.Infrastructure.Persistence;

/// <summary>
/// P03-10 Step 2 storage for the operator's data-request acts (ADR-0107 § 8, ADR-0108 § 8): the request guard's
/// platform arm, the platform role's closed-column read of the artifact table, and the two partial indexes the
/// inspection pages.
/// </summary>
/// <remarks>
/// <para>
/// <b>The platform arm.</b> <c>learnstack_platform</c> already holds the request table's closed-column <c>SELECT</c>
/// and <c>UPDATE</c> of status, row version, completion time and result (<see cref="DataRequestStorageDdl"/>), and the
/// guard refused every write of it. The guard is replaced with one branch for that role, ahead of the application's,
/// which admits exactly one arrow, <c>erasure: awaiting_privileged_step → completed</c>: every other column but the
/// result is unchanged, the result changes only in the audit log's four retained kinds, the version steps by one, and
/// the arm stamps <c>completed_at</c>, <c>updated_at</c> and <c>updated_by</c> itself. Trigger-assigned columns need no
/// column grant, so the platform's grant does not widen. The application's arms follow byte for byte, so the
/// application role cannot take the platform's arrow and the platform role cannot take one of the application's.
/// </para>
/// <para>
/// <b>The artifact read.</b> The inspection lists expired artifacts whose purge has not run, joined to their request
/// for its group: <c>id</c>, <c>tenant_id</c>, <c>request_id</c>, <c>status</c> and <c>expires_at</c> are what it
/// filters, joins, orders and shows, and nothing more is granted. No subject, size, key version, truncation or actor is
/// readable, and nothing of the ticket or chunk tables; the lifecycle CHECK makes
/// <c>status IN ('available','expired')</c> the same rows as <c>purged_at IS NULL</c>, so the purge time is not granted
/// either.
/// </para>
/// <para>
/// <b>The indexes.</b> The three request lists filter one status and order by when the request fell due,
/// <c>COALESCE(execute_at, created_at)</c>, then by id; the artifact list orders unpurged artifacts by expiry. Each is
/// a partial index on exactly the rows its list can show, so a page reads its rows in key order and never the table.
/// </para>
/// </remarks>
internal static class DataRequestOperatorDdl
{
    internal static void Install(MigrationBuilder migration)
    {
        ArgumentNullException.ThrowIfNull(migration);
        migration.Sql(RequestGuard);
        migration.Sql(ArtifactGrant);
        migration.Sql(InspectionIndexes);
    }

    /// <summary>
    /// The request guard of <see cref="DataRequestStorageDdl"/> with the platform arm ahead of the application's, whose
    /// text is unchanged; a unit test compares the two byte for byte.
    /// </summary>
    private const string RequestGuard = """
        CREATE OR REPLACE FUNCTION public.fn_identity_data_request_guard() RETURNS trigger
            LANGUAGE plpgsql VOLATILE SECURITY INVOKER SET search_path = pg_catalog AS $guard$
        DECLARE snapshot_count integer; snapshot_distinct integer; matched integer; held integer;
        BEGIN
            -- The platform arm (ADR-0107 § 8, ADR-0108 § 8). learnstack_platform completes an erasure's privileged step
            -- and writes nothing else: one arrow, the result's audit kinds, the version step, and the completion stamped
            -- here from the database clock and the system actor, so no caller supplies a time and the columns it holds
            -- no grant on need none. Step 3 adds the closure's arrows to the one marked condition below.
            IF session_user = 'learnstack_platform' AND current_user = 'learnstack_platform' THEN
                IF (TG_TABLE_SCHEMA <> 'public' OR TG_TABLE_NAME <> 'data_subject_requests' OR TG_OP <> 'UPDATE') IS NOT FALSE
                THEN RAISE EXCEPTION 'Data request mutation refused.' USING ERRCODE = '23514'; END IF;
                IF (ROW(NEW.id, NEW.tenant_id, NEW.organization_id, NEW.kind, NEW.origin, NEW.subject_user_id,
                        NEW.requester_user_id, NEW.membership_snapshot, NEW.execute_at, NEW.progress, NEW.last_refusal,
                        NEW.failure_reason, NEW.created_at, NEW.created_by, NEW.updated_at, NEW.updated_by,
                        NEW.deleted_at, NEW.deleted_by)
                    IS DISTINCT FROM ROW(OLD.id, OLD.tenant_id, OLD.organization_id, OLD.kind, OLD.origin, OLD.subject_user_id,
                        OLD.requester_user_id, OLD.membership_snapshot, OLD.execute_at, OLD.progress, OLD.last_refusal,
                        OLD.failure_reason, OLD.created_at, OLD.created_by, OLD.updated_at, OLD.updated_by,
                        OLD.deleted_at, OLD.deleted_by)
                    OR NEW.row_version IS DISTINCT FROM OLD.row_version + 1) IS NOT FALSE
                THEN RAISE EXCEPTION 'Data request immutable state refused.' USING ERRCODE = '23514'; END IF;
                -- The tenant side's retained record stays as its participants wrote it; the redaction sets the audit log's
                -- four kinds and nothing else (ADR-0107 § 6, ADR-0108 § 6).
                IF ((NEW.result - ARRAY['audit_actor_reference','audit_entity_reference','audit_metadata_reference',
                        'unscoped_audit_rows'])
                    IS DISTINCT FROM (OLD.result - ARRAY['audit_actor_reference','audit_entity_reference',
                        'audit_metadata_reference','unscoped_audit_rows'])) IS NOT FALSE
                THEN RAISE EXCEPTION 'Data request result refused.' USING ERRCODE = '23514'; END IF;
                -- The platform's arrows (ADR-0106 § 8). Step 3's closure Begin and Finalize join this condition.
                IF (NOT (OLD.kind = 'erasure' AND OLD.status = 'awaiting_privileged_step' AND NEW.status = 'completed'))
                    IS NOT FALSE
                THEN RAISE EXCEPTION 'Data request transition refused.' USING ERRCODE = '23514'; END IF;
                -- Never earlier than the row's last stamp, which an application clock up to five minutes ahead wrote.
                NEW.updated_at := GREATEST(clock_timestamp(), COALESCE(OLD.updated_at, OLD.created_at));
                NEW.updated_by := '00000000-0000-7000-8000-000000000001'::uuid;
                NEW.completed_at := NEW.updated_at;
                RETURN NEW;
            END IF;
            IF (session_user <> 'learnstack_app' OR current_user <> 'learnstack_app'
                OR TG_TABLE_SCHEMA <> 'public' OR TG_TABLE_NAME <> 'data_subject_requests'
                OR TG_OP NOT IN ('INSERT','UPDATE')) IS NOT FALSE
            THEN RAISE EXCEPTION 'Data request mutation refused.' USING ERRCODE = '23514'; END IF;
            PERFORM public.fn_identity_assert_tenant_fence(NEW.tenant_id, false);
            IF TG_OP = 'INSERT' THEN
                IF public.fn_identity_lock_held('user', NEW.subject_user_id, true) IS NOT TRUE THEN
                    RAISE EXCEPTION 'identity mutation plan is unavailable' USING ERRCODE = 'P0301', CONSTRAINT = 'identity_user_fence';
                END IF;
                IF (NEW.row_version IS DISTINCT FROM 0 OR NEW.updated_at IS NOT NULL OR NEW.updated_by IS NOT NULL
                    OR NEW.progress IS DISTINCT FROM '{}'::jsonb OR NEW.result IS DISTINCT FROM '{}'::jsonb
                    OR NEW.last_refusal IS NOT NULL OR NEW.failure_reason IS NOT NULL OR NEW.completed_at IS NOT NULL
                    OR NEW.status IS DISTINCT FROM (CASE WHEN NEW.kind = 'export' THEN 'requested'
                        WHEN NEW.origin = 'closure_fanout' THEN 'fulfilling' ELSE 'scheduled' END)
                    OR NEW.created_by IS DISTINCT FROM COALESCE(NEW.requester_user_id, '00000000-0000-7000-8000-000000000001'::uuid)
                    OR NOT isfinite(NEW.created_at)
                    OR NEW.created_at > transaction_timestamp() + interval '5 minutes'
                    OR NEW.created_at < transaction_timestamp() - interval '5 minutes') IS NOT FALSE
                THEN RAISE EXCEPTION 'Data request initial state refused.' USING ERRCODE = '23514'; END IF;
                -- The frozen snapshot is exactly the subject's Memberships of any status in the group.
                SELECT count(*), count(DISTINCT element) INTO snapshot_count, snapshot_distinct
                    FROM jsonb_array_elements_text(NEW.membership_snapshot) AS element;
                SELECT count(*) INTO matched FROM public.memberships AS member
                    WHERE member.tenant_id = NEW.tenant_id AND member.user_id = NEW.subject_user_id
                      AND member.organization_id IS NOT DISTINCT FROM NEW.organization_id
                      AND member.id::text IN (SELECT jsonb_array_elements_text(NEW.membership_snapshot));
                SELECT count(*) INTO held FROM public.memberships AS member
                    WHERE member.tenant_id = NEW.tenant_id AND member.user_id = NEW.subject_user_id
                      AND member.organization_id IS NOT DISTINCT FROM NEW.organization_id;
                IF (snapshot_count < 1 OR snapshot_distinct <> snapshot_count OR matched <> snapshot_count
                    OR held <> snapshot_count) IS NOT FALSE
                THEN RAISE EXCEPTION 'Data request snapshot refused.' USING ERRCODE = '23514'; END IF;
                RETURN NEW;
            END IF;
            IF (ROW(NEW.id, NEW.tenant_id, NEW.organization_id, NEW.kind, NEW.origin, NEW.subject_user_id,
                    NEW.requester_user_id, NEW.membership_snapshot, NEW.execute_at, NEW.created_at, NEW.created_by,
                    NEW.deleted_at, NEW.deleted_by)
                IS DISTINCT FROM ROW(OLD.id, OLD.tenant_id, OLD.organization_id, OLD.kind, OLD.origin, OLD.subject_user_id,
                    OLD.requester_user_id, OLD.membership_snapshot, OLD.execute_at, OLD.created_at, OLD.created_by,
                    OLD.deleted_at, OLD.deleted_by)
                OR NEW.row_version IS DISTINCT FROM OLD.row_version + 1
                OR NEW.updated_at IS NULL OR NEW.updated_by IS NULL OR NOT isfinite(NEW.updated_at)
                OR NEW.updated_at < COALESCE(OLD.updated_at, OLD.created_at)
                OR NEW.updated_at > transaction_timestamp() + interval '5 minutes') IS NOT FALSE
            THEN RAISE EXCEPTION 'Data request immutable state refused.' USING ERRCODE = '23514'; END IF;
            IF (OLD.status IN ('completed','blocked','failed','cancelled')) IS NOT FALSE
            THEN RAISE EXCEPTION 'Data request is terminal.' USING ERRCODE = '23514'; END IF;
            -- Progress only grows, and only while the request is fulfilling (ADR-0107 § 1).
            IF (NEW.progress IS DISTINCT FROM OLD.progress
                AND NOT (OLD.status = 'fulfilling' AND NEW.progress @> OLD.progress)) IS NOT FALSE
            THEN RAISE EXCEPTION 'Data request progress refused.' USING ERRCODE = '23514'; END IF;
            IF (NEW.result IS DISTINCT FROM OLD.result AND OLD.status <> 'fulfilling') IS NOT FALSE
            THEN RAISE EXCEPTION 'Data request result refused.' USING ERRCODE = '23514'; END IF;
            IF (NEW.last_refusal IS DISTINCT FROM OLD.last_refusal
                AND NOT ((OLD.status = 'scheduled' AND NEW.status = 'scheduled')
                    OR (OLD.status = 'fulfilling' AND NEW.status = 'blocked'))) IS NOT FALSE
            THEN RAISE EXCEPTION 'Data request refusal record refused.' USING ERRCODE = '23514'; END IF;
            IF (NEW.failure_reason IS DISTINCT FROM OLD.failure_reason
                AND NOT (OLD.status = 'fulfilling' AND NEW.status = 'failed')) IS NOT FALSE
            THEN RAISE EXCEPTION 'Data request failure record refused.' USING ERRCODE = '23514'; END IF;
            IF (NEW.completed_at IS DISTINCT FROM OLD.completed_at
                AND NOT (NEW.status = 'completed' AND NEW.completed_at = NEW.updated_at)) IS NOT FALSE
            THEN RAISE EXCEPTION 'Data request completion refused.' USING ERRCODE = '23514'; END IF;
            -- ADR-0106 § 8, the application role's arrows. An erasure's awaiting_privileged_step -> completed and a
            -- closure's Begin and Finalize are operator acts; they are refused here until their steps add them.
            IF (NEW.status IS DISTINCT FROM OLD.status AND NOT (
                (OLD.kind = 'export' AND ((OLD.status = 'requested' AND NEW.status IN ('fulfilling','cancelled'))
                    OR (OLD.status = 'fulfilling' AND NEW.status IN ('completed','failed'))))
                OR (OLD.kind = 'erasure' AND ((OLD.status = 'scheduled' AND NEW.status = 'fulfilling'
                        AND clock_timestamp() >= OLD.execute_at)
                    OR (OLD.status = 'scheduled' AND NEW.status = 'cancelled' AND clock_timestamp() < OLD.execute_at)
                    OR (OLD.status = 'fulfilling' AND NEW.status IN ('awaiting_privileged_step','blocked','failed'))))
                OR (OLD.kind = 'closure' AND OLD.status = 'scheduled' AND NEW.status = 'cancelled'))) IS NOT FALSE
            THEN RAISE EXCEPTION 'Data request transition refused.' USING ERRCODE = '23514'; END IF;
            RETURN NEW;
        END $guard$;
        REVOKE ALL ON FUNCTION public.fn_identity_data_request_guard() FROM PUBLIC;
        """;

    /// <summary>
    /// The inspection's closed-column read of the artifact table, and nothing on its tickets or chunks.
    /// </summary>
    private const string ArtifactGrant = """
        GRANT SELECT (id, tenant_id, request_id, status, expires_at) ON public.data_export_artifacts TO learnstack_platform;
        """;

    private const string InspectionIndexes = """
        CREATE INDEX ix_data_subject_requests_operator_due ON public.data_subject_requests
            (status, (COALESCE(execute_at, created_at)), id)
            WHERE status IN ('scheduled','fulfilling','awaiting_privileged_step');
        CREATE INDEX ix_data_export_artifacts_unpurged_expiry ON public.data_export_artifacts (expires_at, id)
            WHERE status IN ('available','expired');
        """;
}
