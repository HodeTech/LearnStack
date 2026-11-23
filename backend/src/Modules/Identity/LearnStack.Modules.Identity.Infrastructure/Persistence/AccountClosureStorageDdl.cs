using Microsoft.EntityFrameworkCore.Migrations;

namespace LearnStack.Modules.Identity.Infrastructure.Persistence;

/// <summary>
/// P03-10 Step 3 storage for a global account closure (ADR-0107 §§ 7 and 8, ADR-0072 Amendment 8): the platform role's
/// closed-column grants, the one <c>users</c> trigger exception, the request guard's closure arrows and the INSERT
/// arm's User status witness. Each arrow reads its witnesses in SQL, on the writing connection; none trusts a flag.
/// </summary>
/// <remarks>
/// <para>
/// <b>The grants.</b> <c>learnstack_platform</c> bypasses row security, so its grants are its whole bound. It reads
/// <c>users</c> by <c>id</c>, <c>status</c>, <c>provider_subject</c> (the subject Finalize removes from the provider)
/// and <c>subject_key</c> (the key ADR-0108 § 4's closure plan selects the sentinel tenant's rows by), and updates
/// only <c>status</c> and <c>provider_subject</c>. It reads the participant groups from six <c>memberships</c>
/// columns, and the last-administrator judgment of a participant tenant from exactly the columns the deferred
/// complete-graph guard's administrator predicate reads on the assignment, Role and grant tables; the Membership side
/// of that predicate needs no <c>deleted_by</c>, because <c>ck_memberships_lifecycle</c> makes an Active Membership's
/// deletion columns NULL. It may also write a closure's <c>last_refusal</c>, and execute the two lock observers, which
/// read only the calling backend's own <c>pg_locks</c> rows.
/// </para>
/// <para>
/// <b>The fences.</b> A closure's fences are the subject's User key and the key of every tenant in which the subject
/// holds a Membership of any status, a Revoked tombstone included, each held exclusively on the connection that
/// writes. Memberships are never deleted and a new one is admitted only under the subject's exclusive User key, so
/// while that key is held the participant set cannot change under the check.
/// </para>
/// <para>
/// <b>The <c>users</c> exception.</b> <c>fn_identity_user_immutable</c> still backs the three triggers whose names the
/// table has always had. A DELETE, a TRUNCATE and every role's UPDATE but the platform session's stay refused, the
/// migration owner's included. The platform session's UPDATE passes only when nothing but the status and the raw
/// provider subject changes, the transition is Active to Closing with the subject kept or Closing to Closed with it
/// removed, and the fences above are held; a subject with no Membership has none to hold and is refused, since a
/// closure is filed in a group and a person with no Membership is Phase 11's. The function stamps the row version,
/// the update time and the system actor itself, so the column grant stays at the two columns.
/// </para>
/// <para>
/// <b>The request guard.</b> The platform arm gains a closure's Begin (<c>scheduled → fulfilling</c>), its refusal
/// record (<c>scheduled → scheduled</c> setting <c>last_refusal</c>), its Finalize (<c>fulfilling → completed</c>) and
/// the completion of its fan-out erasures (<c>awaiting_privileged_step → completed</c>), each under the subject's User
/// status and the fences it needs; the erasure completion of Step 2 is unchanged. The application arms are Step 1's
/// character for character but for one change to the INSERT arm, which a unit test pins: a request may be born
/// <c>fulfilling</c> only as a closure's fan-out erasure, and every request is admitted only for a subject in the one
/// status its origin allows, read under the subject's exclusive User fence the arm already requires: Closing for a
/// fan-out, Active for every other. A filing admitted while the person was Active whose transaction takes the User
/// fence only after a Begin or a Finalize committed therefore inserts nothing: no closure stays <c>scheduled</c> behind
/// a closed account, and no administrator files for a person whose account is closing.
/// </para>
/// <para>
/// <b>Finalize's forward migration.</b> <see cref="InstallFinalize"/> replaces the guard once more, so that Finalize's
/// arm also completes a closure of the subject that was never begun, and grants the platform role the request's update
/// stamp, a begun closure's Begin moment. The constants <see cref="Install"/> applied are unchanged.
/// </para>
/// <para>
/// <b>The hardening's forward migration.</b> <see cref="InstallHardening"/> replaces the guard a third time with two changes
/// to the application arms: the UPDATE arm refuses every write of a closure's row but one that keeps it <c>scheduled</c> or
/// cancels it, so a <c>fulfilling</c> closure is the platform's alone, and the INSERT arm asks the subject's status after
/// the snapshot. The constants the earlier migrations applied are unchanged.
/// </para>
/// <para>
/// <b>The witness function.</b> The application role cannot read another person's <c>users</c> row, so the INSERT arm
/// asks <c>fn_identity_user_status</c>, a definer owned like the others, which returns the subject's status, or NULL
/// when there is no such User, outside a trigger, or without the subject's exclusive User fence on the calling backend.
/// </para>
/// </remarks>
internal static class AccountClosureStorageDdl
{
    internal static void Install(MigrationBuilder migration)
    {
        ArgumentNullException.ThrowIfNull(migration);
        migration.Sql(Grants);
        migration.Sql(UserStatusWitness);
        migration.Sql(UserGuard);
        migration.Sql(RequestGuard);
    }

    /// <summary>ADR-0107 § 8, exactly. Column grants are read from the attribute ACLs.</summary>
    private const string Grants = """
        GRANT SELECT (id, status, provider_subject, subject_key) ON public.users TO learnstack_platform;
        GRANT UPDATE (status, provider_subject) ON public.users TO learnstack_platform;
        GRANT SELECT (id, tenant_id, organization_id, user_id, status, deleted_at) ON public.memberships TO learnstack_platform;
        GRANT SELECT (tenant_id, membership_id, role_id) ON public.membership_role_assignments TO learnstack_platform;
        GRANT SELECT (id, tenant_id, built_in_key, permission_scope, deleted_at, deleted_by) ON public.roles TO learnstack_platform;
        GRANT SELECT (tenant_id, role_id, permission_scope, permission_key) ON public.role_permission_grants TO learnstack_platform;
        GRANT UPDATE (last_refusal) ON public.data_subject_requests TO learnstack_platform;
        GRANT EXECUTE ON FUNCTION public.fn_identity_contextual_lock_key(text, uuid) TO learnstack_platform;
        GRANT EXECUTE ON FUNCTION public.fn_identity_lock_held(text, uuid, boolean) TO learnstack_platform;
        """;

    /// <summary>
    /// The INSERT arm's one read of <c>users</c>: the subject's status, read as the owner because the application role
    /// sees no other person's row, inside a trigger and under the subject's exclusive User fence only.
    /// </summary>
    private const string UserStatusWitness = """
        CREATE FUNCTION public.fn_identity_user_status(target uuid) RETURNS text
            LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = pg_catalog
        AS $$
        DECLARE selected text;
        BEGIN
            IF pg_catalog.pg_trigger_depth() < 1 OR public.fn_identity_lock_held('user', target, true) IS NOT TRUE THEN
                RETURN NULL;
            END IF;
            SELECT person.status INTO selected FROM public.users AS person WHERE person.id = target;
            RETURN selected;
        END;
        $$;
        ALTER FUNCTION public.fn_identity_user_status(uuid) OWNER TO learnstack_migration;
        REVOKE ALL ON FUNCTION public.fn_identity_user_status(uuid) FROM PUBLIC;
        GRANT EXECUTE ON FUNCTION public.fn_identity_user_status(uuid) TO learnstack_app;
        """;

    /// <summary>
    /// The one <c>users</c> trigger exception of ADR-0107 § 7 and ADR-0072 Amendment 8. Its body reads rows only through
    /// existence tests, as the census of the first Identity migration's functions requires.
    /// </summary>
    private const string UserGuard = """
        CREATE OR REPLACE FUNCTION public.fn_identity_user_immutable() RETURNS trigger
            LANGUAGE plpgsql SECURITY INVOKER SET search_path=pg_catalog AS $$
        BEGIN
            -- ADR-0107 § 7 and ADR-0072 Amendment 8: the closure's UPDATE by the platform session is the one admitted
            -- change. A DELETE, a TRUNCATE and every other role's UPDATE stay refused, the migration owner's included.
            IF (TG_OP <> 'UPDATE' OR session_user <> 'learnstack_platform' OR current_user <> 'learnstack_platform') IS NOT FALSE
            THEN
                RAISE EXCEPTION 'Identity bindings have no admitted update or removal owner.' USING ERRCODE='23514';
            END IF;
            -- Only the status and the raw provider subject change: the key, the authority and the creation evidence are
            -- the permanent tombstone, and the bookkeeping is stamped below.
            IF (ROW(NEW.id, NEW.authority_id, NEW.subject_key, NEW.creation_audit_id, NEW.creation_audit_timestamp,
                    NEW.created_at, NEW.created_by, NEW.updated_at, NEW.updated_by, NEW.deleted_at, NEW.deleted_by,
                    NEW.row_version)
                IS DISTINCT FROM ROW(OLD.id, OLD.authority_id, OLD.subject_key, OLD.creation_audit_id,
                    OLD.creation_audit_timestamp, OLD.created_at, OLD.created_by, OLD.updated_at, OLD.updated_by,
                    OLD.deleted_at, OLD.deleted_by, OLD.row_version)) IS NOT FALSE
            THEN
                RAISE EXCEPTION 'User closure column refused.' USING ERRCODE='23514';
            END IF;
            -- Active to Closing keeps the raw subject; Closing to Closed removes it. Nothing returns to Active.
            IF (NOT ((OLD.status = 'Active' AND NEW.status = 'Closing'
                        AND NEW.provider_subject IS NOT DISTINCT FROM OLD.provider_subject)
                    OR (OLD.status = 'Closing' AND NEW.status = 'Closed' AND NEW.provider_subject IS NULL))) IS NOT FALSE
            THEN
                RAISE EXCEPTION 'User closure transition refused.' USING ERRCODE='23514';
            END IF;
            -- The fences, observed on this connection: the subject's User key, then the key of every tenant in which the
            -- subject holds a Membership of any status, each exclusively. A subject with no Membership has no closure.
            IF public.fn_identity_lock_held('user', OLD.id, true) IS NOT TRUE THEN
                RAISE EXCEPTION 'User closure fence refused.' USING ERRCODE='23514';
            END IF;
            IF NOT EXISTS (SELECT 1 FROM public.memberships AS member WHERE member.user_id = OLD.id) THEN
                RAISE EXCEPTION 'User closure participant refused.' USING ERRCODE='23514';
            END IF;
            IF EXISTS (SELECT 1 FROM public.memberships AS member WHERE member.user_id = OLD.id
                    GROUP BY member.tenant_id
                    HAVING public.fn_identity_lock_held('tenant', member.tenant_id, true) IS NOT TRUE) THEN
                RAISE EXCEPTION 'User closure tenant fence refused.' USING ERRCODE='23514';
            END IF;
            -- Never earlier than the row's last stamp; the system actor, as every operator-stamped row.
            NEW.updated_at := GREATEST(clock_timestamp(), COALESCE(OLD.updated_at, OLD.created_at));
            NEW.updated_by := '00000000-0000-7000-8000-000000000001'::uuid;
            NEW.row_version := OLD.row_version + 1;
            RETURN NEW;
        END $$;
        REVOKE ALL ON FUNCTION public.fn_identity_user_immutable() FROM PUBLIC;
        """;

    /// <summary>
    /// The request guard of <see cref="DataRequestOperatorDdl"/> with the closure's arrows in its platform arm and the
    /// User status witness in the application's INSERT arm; a unit test compares the application arms with Step 1's and
    /// pins that one difference.
    /// </summary>
    private const string RequestGuard = """
        CREATE OR REPLACE FUNCTION public.fn_identity_data_request_guard() RETURNS trigger
            LANGUAGE plpgsql VOLATILE SECURITY INVOKER SET search_path = pg_catalog AS $guard$
        DECLARE snapshot_count integer; snapshot_distinct integer; matched integer; held integer;
        BEGIN
            -- The platform arm (ADR-0107 §§ 7 and 8, ADR-0108 § 8). learnstack_platform takes the operator's arrows below
            -- and writes nothing else: every other column as the statement presents it, the version step, and the stamps
            -- written here from the database clock and the system actor, so no caller supplies a time and the columns it
            -- holds no grant on need none. A closure's arrows read their witnesses here, on this connection: the subject's
            -- User status and the fences the connection holds, never a flag the caller passes.
            IF session_user = 'learnstack_platform' AND current_user = 'learnstack_platform' THEN
                IF (TG_TABLE_SCHEMA <> 'public' OR TG_TABLE_NAME <> 'data_subject_requests' OR TG_OP <> 'UPDATE') IS NOT FALSE
                THEN RAISE EXCEPTION 'Data request mutation refused.' USING ERRCODE = '23514'; END IF;
                IF (ROW(NEW.id, NEW.tenant_id, NEW.organization_id, NEW.kind, NEW.origin, NEW.subject_user_id,
                        NEW.requester_user_id, NEW.membership_snapshot, NEW.execute_at, NEW.progress, NEW.failure_reason,
                        NEW.created_at, NEW.created_by, NEW.updated_at, NEW.updated_by, NEW.deleted_at, NEW.deleted_by)
                    IS DISTINCT FROM ROW(OLD.id, OLD.tenant_id, OLD.organization_id, OLD.kind, OLD.origin, OLD.subject_user_id,
                        OLD.requester_user_id, OLD.membership_snapshot, OLD.execute_at, OLD.progress, OLD.failure_reason,
                        OLD.created_at, OLD.created_by, OLD.updated_at, OLD.updated_by, OLD.deleted_at, OLD.deleted_by)
                    OR NEW.row_version IS DISTINCT FROM OLD.row_version + 1) IS NOT FALSE
                THEN RAISE EXCEPTION 'Data request immutable state refused.' USING ERRCODE = '23514'; END IF;
                -- The refusal record changes on a closure's refusal arrow alone.
                IF (NEW.last_refusal IS DISTINCT FROM OLD.last_refusal
                    AND NOT (OLD.kind = 'closure' AND OLD.status = 'scheduled' AND NEW.status = 'scheduled')) IS NOT FALSE
                THEN RAISE EXCEPTION 'Data request refusal record refused.' USING ERRCODE = '23514'; END IF;
                -- Never earlier than the row's last stamp, which an application clock up to five minutes ahead wrote.
                NEW.updated_at := GREATEST(clock_timestamp(), COALESCE(OLD.updated_at, OLD.created_at));
                NEW.updated_by := '00000000-0000-7000-8000-000000000001'::uuid;
                IF OLD.kind = 'erasure' AND OLD.origin <> 'closure_fanout' AND OLD.status = 'awaiting_privileged_step'
                    AND NEW.status = 'completed' THEN
                    -- An erasure's privileged step (ADR-0106 § 8, ADR-0108 § 8). The tenant side's retained record stays as
                    -- its participants wrote it; the redaction sets the audit log's four kinds and nothing else (ADR-0107 § 6).
                    IF ((NEW.result - ARRAY['audit_actor_reference','audit_entity_reference','audit_metadata_reference',
                            'unscoped_audit_rows'])
                        IS DISTINCT FROM (OLD.result - ARRAY['audit_actor_reference','audit_entity_reference',
                            'audit_metadata_reference','unscoped_audit_rows'])) IS NOT FALSE
                    THEN RAISE EXCEPTION 'Data request result refused.' USING ERRCODE = '23514'; END IF;
                    NEW.completed_at := NEW.updated_at;
                ELSIF OLD.kind = 'closure' AND OLD.status = 'scheduled' AND NEW.status = 'scheduled' THEN
                    -- A closure's refusal record (ADR-0106 § 7, ADR-0107 § 7): a Begin that refuses leaves the request
                    -- scheduled with its closed code, so the person can act on it. A code is set or replaced, never
                    -- cleared: a later Begin that succeeds moves the request on, and no application path writes a
                    -- closure's refusal. Recording the standing code again changes nothing and is refused.
                    IF (NEW.last_refusal NOT IN ('blocked_window','blocked_hold','blocked_last_administrator')
                        OR NEW.last_refusal IS NOT DISTINCT FROM OLD.last_refusal) IS NOT FALSE
                    THEN RAISE EXCEPTION 'Data request refusal record refused.' USING ERRCODE = '23514'; END IF;
                    IF (NEW.result IS DISTINCT FROM OLD.result) IS NOT FALSE
                    THEN RAISE EXCEPTION 'Data request result refused.' USING ERRCODE = '23514'; END IF;
                    IF (NEW.completed_at IS DISTINCT FROM OLD.completed_at) IS NOT FALSE
                    THEN RAISE EXCEPTION 'Data request completion refused.' USING ERRCODE = '23514'; END IF;
                ELSIF OLD.kind = 'closure' AND OLD.status = 'scheduled' AND NEW.status = 'fulfilling' THEN
                    -- A closure's Begin (ADR-0106 § 8, ADR-0107 § 7): once its window has passed, with the subject already
                    -- Closing, under the subject's exclusive User fence and the exclusive key of the request's tenant and
                    -- of every tenant in which the subject holds a Membership of any status. The result is not touched.
                    IF (NEW.result IS DISTINCT FROM OLD.result) IS NOT FALSE
                    THEN RAISE EXCEPTION 'Data request result refused.' USING ERRCODE = '23514'; END IF;
                    IF (NEW.completed_at IS DISTINCT FROM OLD.completed_at) IS NOT FALSE
                    THEN RAISE EXCEPTION 'Data request completion refused.' USING ERRCODE = '23514'; END IF;
                    IF (clock_timestamp() < OLD.execute_at) IS NOT FALSE
                    THEN RAISE EXCEPTION 'Data request closure window refused.' USING ERRCODE = '23514'; END IF;
                    IF ((SELECT person.status FROM public.users AS person WHERE person.id = OLD.subject_user_id)
                        IS DISTINCT FROM 'Closing') IS NOT FALSE
                    THEN RAISE EXCEPTION 'Data request closure subject refused.' USING ERRCODE = '23514'; END IF;
                    IF public.fn_identity_lock_held('user', OLD.subject_user_id, true) IS NOT TRUE
                    THEN RAISE EXCEPTION 'Data request closure user fence refused.' USING ERRCODE = '23514'; END IF;
                    IF (public.fn_identity_lock_held('tenant', OLD.tenant_id, true) IS NOT TRUE
                        OR EXISTS (SELECT 1 FROM public.memberships AS member WHERE member.user_id = OLD.subject_user_id
                            GROUP BY member.tenant_id
                            HAVING public.fn_identity_lock_held('tenant', member.tenant_id, true) IS NOT TRUE)) IS NOT FALSE
                    THEN RAISE EXCEPTION 'Data request closure tenant fence refused.' USING ERRCODE = '23514'; END IF;
                ELSIF OLD.kind = 'closure' AND OLD.status = 'fulfilling' AND NEW.status = 'completed' THEN
                    -- A closure's Finalize (ADR-0107 § 7, ADR-0108 § 8): with the subject already Closed, under the fences
                    -- of Begin. The result records the redaction's four audit kinds and the unserved groups, nothing else.
                    IF ((NEW.result - ARRAY['audit_actor_reference','audit_entity_reference','audit_metadata_reference',
                            'unscoped_audit_rows','unserved_group'])
                        IS DISTINCT FROM (OLD.result - ARRAY['audit_actor_reference','audit_entity_reference',
                            'audit_metadata_reference','unscoped_audit_rows','unserved_group'])) IS NOT FALSE
                    THEN RAISE EXCEPTION 'Data request result refused.' USING ERRCODE = '23514'; END IF;
                    IF ((SELECT person.status FROM public.users AS person WHERE person.id = OLD.subject_user_id)
                        IS DISTINCT FROM 'Closed') IS NOT FALSE
                    THEN RAISE EXCEPTION 'Data request closure subject refused.' USING ERRCODE = '23514'; END IF;
                    IF public.fn_identity_lock_held('user', OLD.subject_user_id, true) IS NOT TRUE
                    THEN RAISE EXCEPTION 'Data request closure user fence refused.' USING ERRCODE = '23514'; END IF;
                    IF (public.fn_identity_lock_held('tenant', OLD.tenant_id, true) IS NOT TRUE
                        OR EXISTS (SELECT 1 FROM public.memberships AS member WHERE member.user_id = OLD.subject_user_id
                            GROUP BY member.tenant_id
                            HAVING public.fn_identity_lock_held('tenant', member.tenant_id, true) IS NOT TRUE)) IS NOT FALSE
                    THEN RAISE EXCEPTION 'Data request closure tenant fence refused.' USING ERRCODE = '23514'; END IF;
                    NEW.completed_at := NEW.updated_at;
                ELSIF OLD.kind = 'erasure' AND OLD.origin = 'closure_fanout' AND OLD.status = 'awaiting_privileged_step'
                    AND NEW.status = 'completed' THEN
                    -- A closure's fan-out erasure, completed by that closure's Finalize alone (ADR-0107 § 7): the subject is
                    -- Closing or Closed, and the connection holds the subject's User fence and the request's tenant key,
                    -- both exclusively. The result changes only in the audit log's four kinds, as for any erasure.
                    IF ((NEW.result - ARRAY['audit_actor_reference','audit_entity_reference','audit_metadata_reference',
                            'unscoped_audit_rows'])
                        IS DISTINCT FROM (OLD.result - ARRAY['audit_actor_reference','audit_entity_reference',
                            'audit_metadata_reference','unscoped_audit_rows'])) IS NOT FALSE
                    THEN RAISE EXCEPTION 'Data request result refused.' USING ERRCODE = '23514'; END IF;
                    IF ((SELECT person.status FROM public.users AS person WHERE person.id = OLD.subject_user_id)
                        NOT IN ('Closing','Closed')) IS NOT FALSE
                    THEN RAISE EXCEPTION 'Data request closure subject refused.' USING ERRCODE = '23514'; END IF;
                    IF public.fn_identity_lock_held('user', OLD.subject_user_id, true) IS NOT TRUE
                    THEN RAISE EXCEPTION 'Data request closure user fence refused.' USING ERRCODE = '23514'; END IF;
                    IF public.fn_identity_lock_held('tenant', OLD.tenant_id, true) IS NOT TRUE
                    THEN RAISE EXCEPTION 'Data request closure tenant fence refused.' USING ERRCODE = '23514'; END IF;
                    NEW.completed_at := NEW.updated_at;
                ELSE
                    -- Every other arrow, kind, origin and status (ADR-0106 § 8).
                    RAISE EXCEPTION 'Data request transition refused.' USING ERRCODE = '23514';
                END IF;
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
                -- A request is born fulfilling only as a closure's fan-out erasure, with no window and no requester
                -- (ADR-0107 § 7): such a row stands in for the erasure the profile clearing of a Revoked Membership requires.
                IF (NEW.row_version IS DISTINCT FROM 0 OR NEW.updated_at IS NOT NULL OR NEW.updated_by IS NOT NULL
                    OR NEW.progress IS DISTINCT FROM '{}'::jsonb OR NEW.result IS DISTINCT FROM '{}'::jsonb
                    OR NEW.last_refusal IS NOT NULL OR NEW.failure_reason IS NOT NULL OR NEW.completed_at IS NOT NULL
                    OR NEW.status IS DISTINCT FROM (CASE WHEN NEW.kind = 'export' THEN 'requested'
                        WHEN NEW.origin = 'closure_fanout' THEN 'fulfilling' ELSE 'scheduled' END)
                    OR (NEW.origin = 'closure_fanout' AND (NEW.kind IS DISTINCT FROM 'erasure' OR NEW.execute_at IS NOT NULL
                        OR NEW.requester_user_id IS NOT NULL))
                    OR NEW.created_by IS DISTINCT FROM COALESCE(NEW.requester_user_id, '00000000-0000-7000-8000-000000000001'::uuid)
                    OR NOT isfinite(NEW.created_at)
                    OR NEW.created_at > transaction_timestamp() + interval '5 minutes'
                    OR NEW.created_at < transaction_timestamp() - interval '5 minutes') IS NOT FALSE
                THEN RAISE EXCEPTION 'Data request initial state refused.' USING ERRCODE = '23514'; END IF;
                -- The subject is admitted in the one status its origin allows, read here under the User fence held above:
                -- Closing for a closure's fan-out, Active for every other request (ADR-0107 § 7). A filing admitted while
                -- the person was Active and fenced only after a Begin or a Finalize committed inserts nothing.
                IF (public.fn_identity_user_status(NEW.subject_user_id)
                    IS DISTINCT FROM (CASE WHEN NEW.origin = 'closure_fanout' THEN 'Closing' ELSE 'Active' END)) IS NOT FALSE
                THEN RAISE EXCEPTION 'Data request subject is not admitted.' USING ERRCODE = '23514'; END IF;
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
    /// The closure's Finalize storage (ADR-0107 §§ 7 and 8), a forward migration over <see cref="Install"/>'s: the platform
    /// role reads a request's update stamp, which is a begun closure's Begin moment, and the closure arm of the request guard
    /// completes every open closure of a Closed subject, a never-begun one included, under the same witnesses as the one it
    /// finalizes. A unit test pins that <see cref="FinalizeRequestGuard"/> is <see cref="RequestGuard"/> but for that arm's
    /// condition and comment.
    /// </summary>
    internal static void InstallFinalize(MigrationBuilder migration)
    {
        ArgumentNullException.ThrowIfNull(migration);
        migration.Sql(FinalizeGrants);
        migration.Sql(FinalizeRequestGuard);
    }

    /// <summary>
    /// The update stamp alone: a begun closure's is its Begin moment, which Finalize's readiness compares a participant
    /// erasure's completion with. It names no actor.
    /// </summary>
    private const string FinalizeGrants = """
        GRANT SELECT (updated_at) ON public.data_subject_requests TO learnstack_platform;
        """;

    /// <summary>
    /// <see cref="RequestGuard"/> with the closure's completion arm taking a never-begun closure of the subject too: a person
    /// may hold one open closure in each group, and once the subject is Closed a closure still <c>scheduled</c> can never be
    /// begun, so Finalize completes it with the one it finalizes, under the same witnesses.
    /// </summary>
    private const string FinalizeRequestGuard = """
        CREATE OR REPLACE FUNCTION public.fn_identity_data_request_guard() RETURNS trigger
            LANGUAGE plpgsql VOLATILE SECURITY INVOKER SET search_path = pg_catalog AS $guard$
        DECLARE snapshot_count integer; snapshot_distinct integer; matched integer; held integer;
        BEGIN
            -- The platform arm (ADR-0107 §§ 7 and 8, ADR-0108 § 8). learnstack_platform takes the operator's arrows below
            -- and writes nothing else: every other column as the statement presents it, the version step, and the stamps
            -- written here from the database clock and the system actor, so no caller supplies a time and the columns it
            -- holds no grant on need none. A closure's arrows read their witnesses here, on this connection: the subject's
            -- User status and the fences the connection holds, never a flag the caller passes.
            IF session_user = 'learnstack_platform' AND current_user = 'learnstack_platform' THEN
                IF (TG_TABLE_SCHEMA <> 'public' OR TG_TABLE_NAME <> 'data_subject_requests' OR TG_OP <> 'UPDATE') IS NOT FALSE
                THEN RAISE EXCEPTION 'Data request mutation refused.' USING ERRCODE = '23514'; END IF;
                IF (ROW(NEW.id, NEW.tenant_id, NEW.organization_id, NEW.kind, NEW.origin, NEW.subject_user_id,
                        NEW.requester_user_id, NEW.membership_snapshot, NEW.execute_at, NEW.progress, NEW.failure_reason,
                        NEW.created_at, NEW.created_by, NEW.updated_at, NEW.updated_by, NEW.deleted_at, NEW.deleted_by)
                    IS DISTINCT FROM ROW(OLD.id, OLD.tenant_id, OLD.organization_id, OLD.kind, OLD.origin, OLD.subject_user_id,
                        OLD.requester_user_id, OLD.membership_snapshot, OLD.execute_at, OLD.progress, OLD.failure_reason,
                        OLD.created_at, OLD.created_by, OLD.updated_at, OLD.updated_by, OLD.deleted_at, OLD.deleted_by)
                    OR NEW.row_version IS DISTINCT FROM OLD.row_version + 1) IS NOT FALSE
                THEN RAISE EXCEPTION 'Data request immutable state refused.' USING ERRCODE = '23514'; END IF;
                -- The refusal record changes on a closure's refusal arrow alone.
                IF (NEW.last_refusal IS DISTINCT FROM OLD.last_refusal
                    AND NOT (OLD.kind = 'closure' AND OLD.status = 'scheduled' AND NEW.status = 'scheduled')) IS NOT FALSE
                THEN RAISE EXCEPTION 'Data request refusal record refused.' USING ERRCODE = '23514'; END IF;
                -- Never earlier than the row's last stamp, which an application clock up to five minutes ahead wrote.
                NEW.updated_at := GREATEST(clock_timestamp(), COALESCE(OLD.updated_at, OLD.created_at));
                NEW.updated_by := '00000000-0000-7000-8000-000000000001'::uuid;
                IF OLD.kind = 'erasure' AND OLD.origin <> 'closure_fanout' AND OLD.status = 'awaiting_privileged_step'
                    AND NEW.status = 'completed' THEN
                    -- An erasure's privileged step (ADR-0106 § 8, ADR-0108 § 8). The tenant side's retained record stays as
                    -- its participants wrote it; the redaction sets the audit log's four kinds and nothing else (ADR-0107 § 6).
                    IF ((NEW.result - ARRAY['audit_actor_reference','audit_entity_reference','audit_metadata_reference',
                            'unscoped_audit_rows'])
                        IS DISTINCT FROM (OLD.result - ARRAY['audit_actor_reference','audit_entity_reference',
                            'audit_metadata_reference','unscoped_audit_rows'])) IS NOT FALSE
                    THEN RAISE EXCEPTION 'Data request result refused.' USING ERRCODE = '23514'; END IF;
                    NEW.completed_at := NEW.updated_at;
                ELSIF OLD.kind = 'closure' AND OLD.status = 'scheduled' AND NEW.status = 'scheduled' THEN
                    -- A closure's refusal record (ADR-0106 § 7, ADR-0107 § 7): a Begin that refuses leaves the request
                    -- scheduled with its closed code, so the person can act on it. A code is set or replaced, never
                    -- cleared: a later Begin that succeeds moves the request on, and no application path writes a
                    -- closure's refusal. Recording the standing code again changes nothing and is refused.
                    IF (NEW.last_refusal NOT IN ('blocked_window','blocked_hold','blocked_last_administrator')
                        OR NEW.last_refusal IS NOT DISTINCT FROM OLD.last_refusal) IS NOT FALSE
                    THEN RAISE EXCEPTION 'Data request refusal record refused.' USING ERRCODE = '23514'; END IF;
                    IF (NEW.result IS DISTINCT FROM OLD.result) IS NOT FALSE
                    THEN RAISE EXCEPTION 'Data request result refused.' USING ERRCODE = '23514'; END IF;
                    IF (NEW.completed_at IS DISTINCT FROM OLD.completed_at) IS NOT FALSE
                    THEN RAISE EXCEPTION 'Data request completion refused.' USING ERRCODE = '23514'; END IF;
                ELSIF OLD.kind = 'closure' AND OLD.status = 'scheduled' AND NEW.status = 'fulfilling' THEN
                    -- A closure's Begin (ADR-0106 § 8, ADR-0107 § 7): once its window has passed, with the subject already
                    -- Closing, under the subject's exclusive User fence and the exclusive key of the request's tenant and
                    -- of every tenant in which the subject holds a Membership of any status. The result is not touched.
                    IF (NEW.result IS DISTINCT FROM OLD.result) IS NOT FALSE
                    THEN RAISE EXCEPTION 'Data request result refused.' USING ERRCODE = '23514'; END IF;
                    IF (NEW.completed_at IS DISTINCT FROM OLD.completed_at) IS NOT FALSE
                    THEN RAISE EXCEPTION 'Data request completion refused.' USING ERRCODE = '23514'; END IF;
                    IF (clock_timestamp() < OLD.execute_at) IS NOT FALSE
                    THEN RAISE EXCEPTION 'Data request closure window refused.' USING ERRCODE = '23514'; END IF;
                    IF ((SELECT person.status FROM public.users AS person WHERE person.id = OLD.subject_user_id)
                        IS DISTINCT FROM 'Closing') IS NOT FALSE
                    THEN RAISE EXCEPTION 'Data request closure subject refused.' USING ERRCODE = '23514'; END IF;
                    IF public.fn_identity_lock_held('user', OLD.subject_user_id, true) IS NOT TRUE
                    THEN RAISE EXCEPTION 'Data request closure user fence refused.' USING ERRCODE = '23514'; END IF;
                    IF (public.fn_identity_lock_held('tenant', OLD.tenant_id, true) IS NOT TRUE
                        OR EXISTS (SELECT 1 FROM public.memberships AS member WHERE member.user_id = OLD.subject_user_id
                            GROUP BY member.tenant_id
                            HAVING public.fn_identity_lock_held('tenant', member.tenant_id, true) IS NOT TRUE)) IS NOT FALSE
                    THEN RAISE EXCEPTION 'Data request closure tenant fence refused.' USING ERRCODE = '23514'; END IF;
                ELSIF OLD.kind = 'closure' AND OLD.status IN ('scheduled', 'fulfilling') AND NEW.status = 'completed' THEN
                    -- A closure's Finalize (ADR-0107 § 7, ADR-0108 § 8): with the subject already Closed, under the fences
                    -- of Begin, the closure it finalizes from fulfilling and every other closure of the subject from where
                    -- it stands, begun or not. The result records the redaction's four audit kinds and the unserved groups,
                    -- nothing else.
                    IF ((NEW.result - ARRAY['audit_actor_reference','audit_entity_reference','audit_metadata_reference',
                            'unscoped_audit_rows','unserved_group'])
                        IS DISTINCT FROM (OLD.result - ARRAY['audit_actor_reference','audit_entity_reference',
                            'audit_metadata_reference','unscoped_audit_rows','unserved_group'])) IS NOT FALSE
                    THEN RAISE EXCEPTION 'Data request result refused.' USING ERRCODE = '23514'; END IF;
                    IF ((SELECT person.status FROM public.users AS person WHERE person.id = OLD.subject_user_id)
                        IS DISTINCT FROM 'Closed') IS NOT FALSE
                    THEN RAISE EXCEPTION 'Data request closure subject refused.' USING ERRCODE = '23514'; END IF;
                    IF public.fn_identity_lock_held('user', OLD.subject_user_id, true) IS NOT TRUE
                    THEN RAISE EXCEPTION 'Data request closure user fence refused.' USING ERRCODE = '23514'; END IF;
                    IF (public.fn_identity_lock_held('tenant', OLD.tenant_id, true) IS NOT TRUE
                        OR EXISTS (SELECT 1 FROM public.memberships AS member WHERE member.user_id = OLD.subject_user_id
                            GROUP BY member.tenant_id
                            HAVING public.fn_identity_lock_held('tenant', member.tenant_id, true) IS NOT TRUE)) IS NOT FALSE
                    THEN RAISE EXCEPTION 'Data request closure tenant fence refused.' USING ERRCODE = '23514'; END IF;
                    NEW.completed_at := NEW.updated_at;
                ELSIF OLD.kind = 'erasure' AND OLD.origin = 'closure_fanout' AND OLD.status = 'awaiting_privileged_step'
                    AND NEW.status = 'completed' THEN
                    -- A closure's fan-out erasure, completed by that closure's Finalize alone (ADR-0107 § 7): the subject is
                    -- Closing or Closed, and the connection holds the subject's User fence and the request's tenant key,
                    -- both exclusively. The result changes only in the audit log's four kinds, as for any erasure.
                    IF ((NEW.result - ARRAY['audit_actor_reference','audit_entity_reference','audit_metadata_reference',
                            'unscoped_audit_rows'])
                        IS DISTINCT FROM (OLD.result - ARRAY['audit_actor_reference','audit_entity_reference',
                            'audit_metadata_reference','unscoped_audit_rows'])) IS NOT FALSE
                    THEN RAISE EXCEPTION 'Data request result refused.' USING ERRCODE = '23514'; END IF;
                    IF ((SELECT person.status FROM public.users AS person WHERE person.id = OLD.subject_user_id)
                        NOT IN ('Closing','Closed')) IS NOT FALSE
                    THEN RAISE EXCEPTION 'Data request closure subject refused.' USING ERRCODE = '23514'; END IF;
                    IF public.fn_identity_lock_held('user', OLD.subject_user_id, true) IS NOT TRUE
                    THEN RAISE EXCEPTION 'Data request closure user fence refused.' USING ERRCODE = '23514'; END IF;
                    IF public.fn_identity_lock_held('tenant', OLD.tenant_id, true) IS NOT TRUE
                    THEN RAISE EXCEPTION 'Data request closure tenant fence refused.' USING ERRCODE = '23514'; END IF;
                    NEW.completed_at := NEW.updated_at;
                ELSE
                    -- Every other arrow, kind, origin and status (ADR-0106 § 8).
                    RAISE EXCEPTION 'Data request transition refused.' USING ERRCODE = '23514';
                END IF;
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
                -- A request is born fulfilling only as a closure's fan-out erasure, with no window and no requester
                -- (ADR-0107 § 7): such a row stands in for the erasure the profile clearing of a Revoked Membership requires.
                IF (NEW.row_version IS DISTINCT FROM 0 OR NEW.updated_at IS NOT NULL OR NEW.updated_by IS NOT NULL
                    OR NEW.progress IS DISTINCT FROM '{}'::jsonb OR NEW.result IS DISTINCT FROM '{}'::jsonb
                    OR NEW.last_refusal IS NOT NULL OR NEW.failure_reason IS NOT NULL OR NEW.completed_at IS NOT NULL
                    OR NEW.status IS DISTINCT FROM (CASE WHEN NEW.kind = 'export' THEN 'requested'
                        WHEN NEW.origin = 'closure_fanout' THEN 'fulfilling' ELSE 'scheduled' END)
                    OR (NEW.origin = 'closure_fanout' AND (NEW.kind IS DISTINCT FROM 'erasure' OR NEW.execute_at IS NOT NULL
                        OR NEW.requester_user_id IS NOT NULL))
                    OR NEW.created_by IS DISTINCT FROM COALESCE(NEW.requester_user_id, '00000000-0000-7000-8000-000000000001'::uuid)
                    OR NOT isfinite(NEW.created_at)
                    OR NEW.created_at > transaction_timestamp() + interval '5 minutes'
                    OR NEW.created_at < transaction_timestamp() - interval '5 minutes') IS NOT FALSE
                THEN RAISE EXCEPTION 'Data request initial state refused.' USING ERRCODE = '23514'; END IF;
                -- The subject is admitted in the one status its origin allows, read here under the User fence held above:
                -- Closing for a closure's fan-out, Active for every other request (ADR-0107 § 7). A filing admitted while
                -- the person was Active and fenced only after a Begin or a Finalize committed inserts nothing.
                IF (public.fn_identity_user_status(NEW.subject_user_id)
                    IS DISTINCT FROM (CASE WHEN NEW.origin = 'closure_fanout' THEN 'Closing' ELSE 'Active' END)) IS NOT FALSE
                THEN RAISE EXCEPTION 'Data request subject is not admitted.' USING ERRCODE = '23514'; END IF;
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
    /// The guard's hardening, a forward migration of its own: <see cref="HardenedRequestGuard"/> replaces the function, and
    /// nothing else changes. A unit test pins that it is <see cref="FinalizeRequestGuard"/> but for its two changes.
    /// </summary>
    internal static void InstallHardening(MigrationBuilder migration)
    {
        ArgumentNullException.ThrowIfNull(migration);
        migration.Sql(HardenedRequestGuard);
    }

    /// <summary>
    /// The request guard hardened (<c>20261123130000_harden_closure_request_guard</c>): <see cref="FinalizeRequestGuard"/> but for
    /// two changes to the application arms, which a unit test pins. The UPDATE arm refuses every write of a closure's row
    /// except one that leaves it <c>scheduled</c> or cancels it, so a closure the platform made <c>fulfilling</c> is written
    /// by the platform's arrows alone and its Begin moment, result and version are the ones Finalize reads. The INSERT arm
    /// asks the subject's status after the snapshot, so the two refusals say nothing about the status of a person whose
    /// snapshot is refused.
    /// </summary>
    private const string HardenedRequestGuard = """
        CREATE OR REPLACE FUNCTION public.fn_identity_data_request_guard() RETURNS trigger
            LANGUAGE plpgsql VOLATILE SECURITY INVOKER SET search_path = pg_catalog AS $guard$
        DECLARE snapshot_count integer; snapshot_distinct integer; matched integer; held integer;
        BEGIN
            -- The platform arm (ADR-0107 §§ 7 and 8, ADR-0108 § 8). learnstack_platform takes the operator's arrows below
            -- and writes nothing else: every other column as the statement presents it, the version step, and the stamps
            -- written here from the database clock and the system actor, so no caller supplies a time and the columns it
            -- holds no grant on need none. A closure's arrows read their witnesses here, on this connection: the subject's
            -- User status and the fences the connection holds, never a flag the caller passes.
            IF session_user = 'learnstack_platform' AND current_user = 'learnstack_platform' THEN
                IF (TG_TABLE_SCHEMA <> 'public' OR TG_TABLE_NAME <> 'data_subject_requests' OR TG_OP <> 'UPDATE') IS NOT FALSE
                THEN RAISE EXCEPTION 'Data request mutation refused.' USING ERRCODE = '23514'; END IF;
                IF (ROW(NEW.id, NEW.tenant_id, NEW.organization_id, NEW.kind, NEW.origin, NEW.subject_user_id,
                        NEW.requester_user_id, NEW.membership_snapshot, NEW.execute_at, NEW.progress, NEW.failure_reason,
                        NEW.created_at, NEW.created_by, NEW.updated_at, NEW.updated_by, NEW.deleted_at, NEW.deleted_by)
                    IS DISTINCT FROM ROW(OLD.id, OLD.tenant_id, OLD.organization_id, OLD.kind, OLD.origin, OLD.subject_user_id,
                        OLD.requester_user_id, OLD.membership_snapshot, OLD.execute_at, OLD.progress, OLD.failure_reason,
                        OLD.created_at, OLD.created_by, OLD.updated_at, OLD.updated_by, OLD.deleted_at, OLD.deleted_by)
                    OR NEW.row_version IS DISTINCT FROM OLD.row_version + 1) IS NOT FALSE
                THEN RAISE EXCEPTION 'Data request immutable state refused.' USING ERRCODE = '23514'; END IF;
                -- The refusal record changes on a closure's refusal arrow alone.
                IF (NEW.last_refusal IS DISTINCT FROM OLD.last_refusal
                    AND NOT (OLD.kind = 'closure' AND OLD.status = 'scheduled' AND NEW.status = 'scheduled')) IS NOT FALSE
                THEN RAISE EXCEPTION 'Data request refusal record refused.' USING ERRCODE = '23514'; END IF;
                -- Never earlier than the row's last stamp, which an application clock up to five minutes ahead wrote.
                NEW.updated_at := GREATEST(clock_timestamp(), COALESCE(OLD.updated_at, OLD.created_at));
                NEW.updated_by := '00000000-0000-7000-8000-000000000001'::uuid;
                IF OLD.kind = 'erasure' AND OLD.origin <> 'closure_fanout' AND OLD.status = 'awaiting_privileged_step'
                    AND NEW.status = 'completed' THEN
                    -- An erasure's privileged step (ADR-0106 § 8, ADR-0108 § 8). The tenant side's retained record stays as
                    -- its participants wrote it; the redaction sets the audit log's four kinds and nothing else (ADR-0107 § 6).
                    IF ((NEW.result - ARRAY['audit_actor_reference','audit_entity_reference','audit_metadata_reference',
                            'unscoped_audit_rows'])
                        IS DISTINCT FROM (OLD.result - ARRAY['audit_actor_reference','audit_entity_reference',
                            'audit_metadata_reference','unscoped_audit_rows'])) IS NOT FALSE
                    THEN RAISE EXCEPTION 'Data request result refused.' USING ERRCODE = '23514'; END IF;
                    NEW.completed_at := NEW.updated_at;
                ELSIF OLD.kind = 'closure' AND OLD.status = 'scheduled' AND NEW.status = 'scheduled' THEN
                    -- A closure's refusal record (ADR-0106 § 7, ADR-0107 § 7): a Begin that refuses leaves the request
                    -- scheduled with its closed code, so the person can act on it. A code is set or replaced, never
                    -- cleared: a later Begin that succeeds moves the request on, and no application path writes a
                    -- closure's refusal. Recording the standing code again changes nothing and is refused.
                    IF (NEW.last_refusal NOT IN ('blocked_window','blocked_hold','blocked_last_administrator')
                        OR NEW.last_refusal IS NOT DISTINCT FROM OLD.last_refusal) IS NOT FALSE
                    THEN RAISE EXCEPTION 'Data request refusal record refused.' USING ERRCODE = '23514'; END IF;
                    IF (NEW.result IS DISTINCT FROM OLD.result) IS NOT FALSE
                    THEN RAISE EXCEPTION 'Data request result refused.' USING ERRCODE = '23514'; END IF;
                    IF (NEW.completed_at IS DISTINCT FROM OLD.completed_at) IS NOT FALSE
                    THEN RAISE EXCEPTION 'Data request completion refused.' USING ERRCODE = '23514'; END IF;
                ELSIF OLD.kind = 'closure' AND OLD.status = 'scheduled' AND NEW.status = 'fulfilling' THEN
                    -- A closure's Begin (ADR-0106 § 8, ADR-0107 § 7): once its window has passed, with the subject already
                    -- Closing, under the subject's exclusive User fence and the exclusive key of the request's tenant and
                    -- of every tenant in which the subject holds a Membership of any status. The result is not touched.
                    IF (NEW.result IS DISTINCT FROM OLD.result) IS NOT FALSE
                    THEN RAISE EXCEPTION 'Data request result refused.' USING ERRCODE = '23514'; END IF;
                    IF (NEW.completed_at IS DISTINCT FROM OLD.completed_at) IS NOT FALSE
                    THEN RAISE EXCEPTION 'Data request completion refused.' USING ERRCODE = '23514'; END IF;
                    IF (clock_timestamp() < OLD.execute_at) IS NOT FALSE
                    THEN RAISE EXCEPTION 'Data request closure window refused.' USING ERRCODE = '23514'; END IF;
                    IF ((SELECT person.status FROM public.users AS person WHERE person.id = OLD.subject_user_id)
                        IS DISTINCT FROM 'Closing') IS NOT FALSE
                    THEN RAISE EXCEPTION 'Data request closure subject refused.' USING ERRCODE = '23514'; END IF;
                    IF public.fn_identity_lock_held('user', OLD.subject_user_id, true) IS NOT TRUE
                    THEN RAISE EXCEPTION 'Data request closure user fence refused.' USING ERRCODE = '23514'; END IF;
                    IF (public.fn_identity_lock_held('tenant', OLD.tenant_id, true) IS NOT TRUE
                        OR EXISTS (SELECT 1 FROM public.memberships AS member WHERE member.user_id = OLD.subject_user_id
                            GROUP BY member.tenant_id
                            HAVING public.fn_identity_lock_held('tenant', member.tenant_id, true) IS NOT TRUE)) IS NOT FALSE
                    THEN RAISE EXCEPTION 'Data request closure tenant fence refused.' USING ERRCODE = '23514'; END IF;
                ELSIF OLD.kind = 'closure' AND OLD.status IN ('scheduled', 'fulfilling') AND NEW.status = 'completed' THEN
                    -- A closure's Finalize (ADR-0107 § 7, ADR-0108 § 8): with the subject already Closed, under the fences
                    -- of Begin, the closure it finalizes from fulfilling and every other closure of the subject from where
                    -- it stands, begun or not. The result records the redaction's four audit kinds and the unserved groups,
                    -- nothing else.
                    IF ((NEW.result - ARRAY['audit_actor_reference','audit_entity_reference','audit_metadata_reference',
                            'unscoped_audit_rows','unserved_group'])
                        IS DISTINCT FROM (OLD.result - ARRAY['audit_actor_reference','audit_entity_reference',
                            'audit_metadata_reference','unscoped_audit_rows','unserved_group'])) IS NOT FALSE
                    THEN RAISE EXCEPTION 'Data request result refused.' USING ERRCODE = '23514'; END IF;
                    IF ((SELECT person.status FROM public.users AS person WHERE person.id = OLD.subject_user_id)
                        IS DISTINCT FROM 'Closed') IS NOT FALSE
                    THEN RAISE EXCEPTION 'Data request closure subject refused.' USING ERRCODE = '23514'; END IF;
                    IF public.fn_identity_lock_held('user', OLD.subject_user_id, true) IS NOT TRUE
                    THEN RAISE EXCEPTION 'Data request closure user fence refused.' USING ERRCODE = '23514'; END IF;
                    IF (public.fn_identity_lock_held('tenant', OLD.tenant_id, true) IS NOT TRUE
                        OR EXISTS (SELECT 1 FROM public.memberships AS member WHERE member.user_id = OLD.subject_user_id
                            GROUP BY member.tenant_id
                            HAVING public.fn_identity_lock_held('tenant', member.tenant_id, true) IS NOT TRUE)) IS NOT FALSE
                    THEN RAISE EXCEPTION 'Data request closure tenant fence refused.' USING ERRCODE = '23514'; END IF;
                    NEW.completed_at := NEW.updated_at;
                ELSIF OLD.kind = 'erasure' AND OLD.origin = 'closure_fanout' AND OLD.status = 'awaiting_privileged_step'
                    AND NEW.status = 'completed' THEN
                    -- A closure's fan-out erasure, completed by that closure's Finalize alone (ADR-0107 § 7): the subject is
                    -- Closing or Closed, and the connection holds the subject's User fence and the request's tenant key,
                    -- both exclusively. The result changes only in the audit log's four kinds, as for any erasure.
                    IF ((NEW.result - ARRAY['audit_actor_reference','audit_entity_reference','audit_metadata_reference',
                            'unscoped_audit_rows'])
                        IS DISTINCT FROM (OLD.result - ARRAY['audit_actor_reference','audit_entity_reference',
                            'audit_metadata_reference','unscoped_audit_rows'])) IS NOT FALSE
                    THEN RAISE EXCEPTION 'Data request result refused.' USING ERRCODE = '23514'; END IF;
                    IF ((SELECT person.status FROM public.users AS person WHERE person.id = OLD.subject_user_id)
                        NOT IN ('Closing','Closed')) IS NOT FALSE
                    THEN RAISE EXCEPTION 'Data request closure subject refused.' USING ERRCODE = '23514'; END IF;
                    IF public.fn_identity_lock_held('user', OLD.subject_user_id, true) IS NOT TRUE
                    THEN RAISE EXCEPTION 'Data request closure user fence refused.' USING ERRCODE = '23514'; END IF;
                    IF public.fn_identity_lock_held('tenant', OLD.tenant_id, true) IS NOT TRUE
                    THEN RAISE EXCEPTION 'Data request closure tenant fence refused.' USING ERRCODE = '23514'; END IF;
                    NEW.completed_at := NEW.updated_at;
                ELSE
                    -- Every other arrow, kind, origin and status (ADR-0106 § 8).
                    RAISE EXCEPTION 'Data request transition refused.' USING ERRCODE = '23514';
                END IF;
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
                -- A request is born fulfilling only as a closure's fan-out erasure, with no window and no requester
                -- (ADR-0107 § 7): such a row stands in for the erasure the profile clearing of a Revoked Membership requires.
                IF (NEW.row_version IS DISTINCT FROM 0 OR NEW.updated_at IS NOT NULL OR NEW.updated_by IS NOT NULL
                    OR NEW.progress IS DISTINCT FROM '{}'::jsonb OR NEW.result IS DISTINCT FROM '{}'::jsonb
                    OR NEW.last_refusal IS NOT NULL OR NEW.failure_reason IS NOT NULL OR NEW.completed_at IS NOT NULL
                    OR NEW.status IS DISTINCT FROM (CASE WHEN NEW.kind = 'export' THEN 'requested'
                        WHEN NEW.origin = 'closure_fanout' THEN 'fulfilling' ELSE 'scheduled' END)
                    OR (NEW.origin = 'closure_fanout' AND (NEW.kind IS DISTINCT FROM 'erasure' OR NEW.execute_at IS NOT NULL
                        OR NEW.requester_user_id IS NOT NULL))
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
                -- The subject is admitted in the one status its origin allows, read here under the User fence held above:
                -- Closing for a closure's fan-out, Active for every other request (ADR-0107 § 7). A filing admitted while
                -- the person was Active and fenced only after a Begin or a Finalize committed inserts nothing. It is asked
                -- after the snapshot, so a refused snapshot says nothing about the subject's status.
                IF (public.fn_identity_user_status(NEW.subject_user_id)
                    IS DISTINCT FROM (CASE WHEN NEW.origin = 'closure_fanout' THEN 'Closing' ELSE 'Active' END)) IS NOT FALSE
                THEN RAISE EXCEPTION 'Data request subject is not admitted.' USING ERRCODE = '23514'; END IF;
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
            -- A closure is the application's only while it is scheduled (ADR-0107 § 7): the person cancels it, and once it
            -- leaves scheduled every write of its row is the platform's arrows, so its Begin moment, its result and its
            -- version change only as Finalize reads them.
            IF (OLD.kind = 'closure' AND NOT (OLD.status = 'scheduled' AND NEW.status IN ('scheduled','cancelled'))) IS NOT FALSE
            THEN RAISE EXCEPTION 'Data request transition refused.' USING ERRCODE = '23514'; END IF;
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
}
