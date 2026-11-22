using Microsoft.EntityFrameworkCore.Migrations;

namespace LearnStack.Modules.Identity.Infrastructure.Persistence;

/// <summary>
/// P03-10 Step 1 storage for subject data requests: the SQL-only chunk table, the canonical
/// organization-scoped row policy on all four tables, the exact grants of ADR-0107 § 8, the
/// organization immutability and parent-scope triggers, and the guards that hold the request status
/// machine of ADR-0106 § 8 and the artifact, ticket and chunk lifecycles of ADR-0107 § 4.
/// </summary>
/// <remarks>
/// <para>
/// <b>The row policy is Database Standards' canonical organization-scoped template</b>, one AND-ed
/// permissive policy with an explicit WITH CHECK and the two restrictive write guards, written once
/// per table by the loop below; it carries no owner arm. The retained own-scope function of
/// ADR-0106 § 9 adds its owner arm with its own migration.
/// </para>
/// <para>
/// <b>Writers.</b> Every guard admits only the application role. <c>learnstack_platform</c> holds the
/// closed-column SELECT and the status, row-version, completion and result UPDATE on
/// <c>data_subject_requests</c> that ADR-0107 § 8 lists, and nothing on the ticket and chunk tables
/// (<see cref="DataRequestOperatorDdl"/> adds its closed-column read of the artifact table); the request guard
/// refuses its writes except the one arrow that class installs, which an operator act's own arm admits.
/// </para>
/// <para>
/// <b>Fences.</b> A request write holds the Identity tenant fence (shared at least), and its creation
/// also holds the subject's exclusive User fence, under which Membership admission runs, so the frozen
/// Membership snapshot is exactly the subject's Memberships in the group when the row commits.
/// </para>
/// <para>
/// <b>The artifact bracket.</b> An artifact is inserted only while its export is <c>fulfilling</c> and
/// commits only with that export <c>completed</c>, and an export commits <c>completed</c> only with its
/// artifact; no transaction-identity comparison is made, so the guard holds past the first xid epoch
/// and under the savepoints EF Core opens. The chunk and ticket
/// guards read their artifact <c>FOR SHARE</c>, so they serialize behind a revocation or purge in
/// flight, and a chunk is deleted only once its artifact is no longer <c>available</c>.
/// </para>
/// </remarks>
internal static class DataRequestStorageDdl
{
    internal static readonly string[] Tables =
        ["data_subject_requests", "data_export_artifacts", "data_export_tickets", "data_export_chunks"];

    internal static void Install(MigrationBuilder migration)
    {
        ArgumentNullException.ThrowIfNull(migration);
        migration.Sql(ChunkTable);
        foreach (var table in Tables)
        {
            migration.Sql($"""
                ALTER TABLE public.{table} ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public.{table} FORCE ROW LEVEL SECURITY;
                CREATE POLICY {table}_isolation ON public.{table}
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
                        AND (organization_id IS NULL
                            OR organization_id = NULLIF(current_setting('app.organization_id', true), '')::uuid
                            OR NULLIF(current_setting('app.scope', true), '') = 'tenant'))
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
                        AND ((organization_id IS NULL AND NULLIF(current_setting('app.organization_id', true), '') IS NULL)
                            OR organization_id = NULLIF(current_setting('app.organization_id', true), '')::uuid));
                CREATE POLICY {table}_org_write_guard ON public.{table} AS RESTRICTIVE FOR UPDATE
                    USING ((organization_id IS NULL AND NULLIF(current_setting('app.organization_id', true), '') IS NULL)
                        OR organization_id = NULLIF(current_setting('app.organization_id', true), '')::uuid);
                CREATE POLICY {table}_org_delete_guard ON public.{table} AS RESTRICTIVE FOR DELETE
                    USING ((organization_id IS NULL AND NULLIF(current_setting('app.organization_id', true), '') IS NULL)
                        OR organization_id = NULLIF(current_setting('app.organization_id', true), '')::uuid);
                CREATE TRIGGER tg_{table}_organization_id_immutable BEFORE UPDATE ON public.{table}
                    FOR EACH ROW EXECUTE FUNCTION public.fn_organization_id_immutable();
                """);
        }

        migration.Sql(Grants);
        migration.Sql(GroupPageIndex);
        migration.Sql(ArtifactParentScope);
        migration.Sql(TicketParentScope);
        migration.Sql(ChunkParentScope);
        migration.Sql(RequestGuard);
        migration.Sql(ArtifactGuard);
        migration.Sql(TicketGuard);
        migration.Sql(ChunkGuard);
        migration.Sql(Completeness);
        migration.Sql(CompletedExportArtifact);
    }

    /// <summary>
    /// The ciphertext rows: SQL-only, never tracked by EF (so never captured into an audit snapshot), and
    /// bounded by ADR-0107 § 4's 12-byte nonce, 16-byte tag and 128 KiB chunk.
    /// </summary>
    private const string ChunkTable = """
        CREATE TABLE public.data_export_chunks (
            tenant_id uuid NOT NULL,
            organization_id uuid NULL,
            artifact_id uuid NOT NULL,
            chunk_index integer NOT NULL,
            nonce bytea NOT NULL,
            ciphertext bytea NOT NULL,
            tag bytea NOT NULL,
            CONSTRAINT pk_data_export_chunks PRIMARY KEY (tenant_id, artifact_id, chunk_index),
            CONSTRAINT fk_data_export_chunks_artifact FOREIGN KEY (tenant_id, artifact_id)
                REFERENCES public.data_export_artifacts (tenant_id, id) ON DELETE RESTRICT,
            CONSTRAINT ck_data_export_chunks_tenant
                CHECK (tenant_id NOT IN ('00000000-0000-0000-0000-000000000000'::uuid,'00000000-0000-7000-8000-000000000002'::uuid)),
            CONSTRAINT ck_data_export_chunks_index CHECK (chunk_index >= 0 AND chunk_index < 512),
            CONSTRAINT ck_data_export_chunks_nonce CHECK (octet_length(nonce) = 12),
            CONSTRAINT ck_data_export_chunks_tag CHECK (octet_length(tag) = 16),
            CONSTRAINT ck_data_export_chunks_ciphertext CHECK (octet_length(ciphertext) BETWEEN 1 AND 131072)
        );
        -- AES-GCM ciphertext does not compress; store it out of line without trying.
        ALTER TABLE public.data_export_chunks ALTER COLUMN ciphertext SET STORAGE EXTERNAL;
        CREATE INDEX ix_data_export_chunks_tenant_id_organization_id ON public.data_export_chunks (tenant_id, organization_id);
        """;

    /// <summary>ADR-0107 § 8, exactly. Column-limited UPDATE grants are invisible to table-privilege views.</summary>
    private const string Grants = """
        REVOKE ALL ON public.data_subject_requests, public.data_export_artifacts, public.data_export_tickets, public.data_export_chunks
            FROM PUBLIC, learnstack_app, learnstack_platform, learnstack_outbox_admin, learnstack_identity_ingress, learnstack_identity_rebind;
        GRANT SELECT, INSERT ON public.data_subject_requests TO learnstack_app;
        GRANT UPDATE (status, row_version, progress, result, last_refusal, failure_reason, completed_at, updated_at, updated_by)
            ON public.data_subject_requests TO learnstack_app;
        GRANT SELECT, INSERT ON public.data_export_artifacts TO learnstack_app;
        GRANT UPDATE (status, purged_at, row_version, updated_at, updated_by) ON public.data_export_artifacts TO learnstack_app;
        GRANT SELECT, INSERT ON public.data_export_tickets TO learnstack_app;
        GRANT UPDATE (consumed_at) ON public.data_export_tickets TO learnstack_app;
        GRANT SELECT, INSERT, DELETE ON public.data_export_chunks TO learnstack_app;
        -- learnstack_platform is BYPASSRLS, so these grants are its whole bound: no requester, no actor
        -- stamps, nothing on the artifact, ticket or chunk tables.
        GRANT SELECT (id, tenant_id, organization_id, kind, origin, subject_user_id, status, membership_snapshot, execute_at,
                progress, result, last_refusal, failure_reason, completed_at, created_at, row_version)
            ON public.data_subject_requests TO learnstack_platform;
        GRANT UPDATE (status, row_version, completed_at, result) ON public.data_subject_requests TO learnstack_platform;
        """;

    /// <summary>
    /// The administrator's page of a group, newest first: equality on the tenant and the group's organization, then a
    /// seek below the cursor's UUIDv7 in index order, so a page reads its rows and not the group's whole history. The
    /// subject's page uses the subject index.
    /// </summary>
    private const string GroupPageIndex = """
        CREATE INDEX ix_data_subject_requests_group_page ON public.data_subject_requests (tenant_id, organization_id, id);
        """;

    // Database Standards § Parent organization mirrors, in its canonical form, once per child. Each is a literal:
    // a table name composed into a statement is one the audit and entitlement source scans cannot read.

    /// <summary>An artifact mirrors its export request's group.</summary>
    private const string ArtifactParentScope = """
        CREATE FUNCTION public.fn_data_export_artifacts_parent_scope() RETURNS trigger
            LANGUAGE plpgsql
            SECURITY INVOKER
            SET search_path = pg_catalog
        AS $$
        DECLARE
            parent_organization_id uuid;
        BEGIN
            SELECT parent.organization_id INTO parent_organization_id
            FROM public.data_subject_requests AS parent
            WHERE parent.tenant_id = NEW.tenant_id AND parent.id = NEW.request_id
            FOR KEY SHARE;

            IF NOT FOUND OR parent_organization_id IS DISTINCT FROM NEW.organization_id THEN
                RAISE EXCEPTION 'parent scope is unavailable or mismatched (table %)', TG_TABLE_NAME
                    USING ERRCODE = '23514';
            END IF;
            RETURN NEW;
        END;
        $$;
        REVOKE ALL ON FUNCTION public.fn_data_export_artifacts_parent_scope() FROM PUBLIC;
        CREATE TRIGGER tg_data_export_artifacts_parent_scope
            BEFORE INSERT OR UPDATE ON public.data_export_artifacts
            FOR EACH ROW EXECUTE FUNCTION public.fn_data_export_artifacts_parent_scope();
        """;

    /// <summary>A ticket mirrors its artifact's group.</summary>
    private const string TicketParentScope = """
        CREATE FUNCTION public.fn_data_export_tickets_parent_scope() RETURNS trigger
            LANGUAGE plpgsql
            SECURITY INVOKER
            SET search_path = pg_catalog
        AS $$
        DECLARE
            parent_organization_id uuid;
        BEGIN
            SELECT parent.organization_id INTO parent_organization_id
            FROM public.data_export_artifacts AS parent
            WHERE parent.tenant_id = NEW.tenant_id AND parent.id = NEW.artifact_id
            FOR KEY SHARE;

            IF NOT FOUND OR parent_organization_id IS DISTINCT FROM NEW.organization_id THEN
                RAISE EXCEPTION 'parent scope is unavailable or mismatched (table %)', TG_TABLE_NAME
                    USING ERRCODE = '23514';
            END IF;
            RETURN NEW;
        END;
        $$;
        REVOKE ALL ON FUNCTION public.fn_data_export_tickets_parent_scope() FROM PUBLIC;
        CREATE TRIGGER tg_data_export_tickets_parent_scope
            BEFORE INSERT OR UPDATE ON public.data_export_tickets
            FOR EACH ROW EXECUTE FUNCTION public.fn_data_export_tickets_parent_scope();
        """;

    /// <summary>A chunk mirrors its artifact's group.</summary>
    private const string ChunkParentScope = """
        CREATE FUNCTION public.fn_data_export_chunks_parent_scope() RETURNS trigger
            LANGUAGE plpgsql
            SECURITY INVOKER
            SET search_path = pg_catalog
        AS $$
        DECLARE
            parent_organization_id uuid;
        BEGIN
            SELECT parent.organization_id INTO parent_organization_id
            FROM public.data_export_artifacts AS parent
            WHERE parent.tenant_id = NEW.tenant_id AND parent.id = NEW.artifact_id
            FOR KEY SHARE;

            IF NOT FOUND OR parent_organization_id IS DISTINCT FROM NEW.organization_id THEN
                RAISE EXCEPTION 'parent scope is unavailable or mismatched (table %)', TG_TABLE_NAME
                    USING ERRCODE = '23514';
            END IF;
            RETURN NEW;
        END;
        $$;
        REVOKE ALL ON FUNCTION public.fn_data_export_chunks_parent_scope() FROM PUBLIC;
        CREATE TRIGGER tg_data_export_chunks_parent_scope
            BEFORE INSERT OR UPDATE ON public.data_export_chunks
            FOR EACH ROW EXECUTE FUNCTION public.fn_data_export_chunks_parent_scope();
        """;

    /// <summary>
    /// The request guard: the immutable snapshot, the initial state per kind and origin, and ADR-0106
    /// § 8's application-role transitions. Every refusal is written as <c>(condition) IS NOT FALSE</c>,
    /// so a condition that is NULL refuses.
    /// </summary>
    private const string RequestGuard = """
        CREATE FUNCTION public.fn_identity_data_request_guard() RETURNS trigger
            LANGUAGE plpgsql VOLATILE SECURITY INVOKER SET search_path = pg_catalog AS $guard$
        DECLARE snapshot_count integer; snapshot_distinct integer; matched integer; held integer;
        BEGIN
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
        CREATE TRIGGER tg_data_subject_requests_mutation_guard BEFORE INSERT OR UPDATE OR DELETE ON public.data_subject_requests
            FOR EACH ROW EXECUTE FUNCTION public.fn_identity_data_request_guard();
        """;

    private const string ArtifactGuard = """
        CREATE FUNCTION public.fn_identity_data_export_artifact_guard() RETURNS trigger
            LANGUAGE plpgsql VOLATILE SECURITY INVOKER SET search_path = pg_catalog AS $guard$
        DECLARE request_kind text; request_subject uuid; request_status text;
        BEGIN
            IF (session_user <> 'learnstack_app' OR current_user <> 'learnstack_app'
                OR TG_TABLE_SCHEMA <> 'public' OR TG_TABLE_NAME <> 'data_export_artifacts'
                OR TG_OP NOT IN ('INSERT','UPDATE')) IS NOT FALSE
            THEN RAISE EXCEPTION 'Data export artifact mutation refused.' USING ERRCODE = '23514'; END IF;
            IF TG_OP = 'INSERT' THEN
                IF (NEW.status IS DISTINCT FROM 'available' OR NEW.row_version IS DISTINCT FROM 0
                    OR NEW.updated_at IS NOT NULL OR NEW.updated_by IS NOT NULL OR NEW.purged_at IS NOT NULL
                    OR NOT isfinite(NEW.created_at)
                    OR NEW.created_at > transaction_timestamp() + interval '5 minutes'
                    OR NEW.created_at < transaction_timestamp() - interval '5 minutes') IS NOT FALSE
                THEN RAISE EXCEPTION 'Data export artifact initial state refused.' USING ERRCODE = '23514'; END IF;
                -- The artifact of an export that is fulfilling, and only then. The deferred completeness check
                -- requires the same request to be completed when the transaction commits, so an artifact is never
                -- stored for a request that ends fulfilling, failed or rolled back.
                SELECT request.kind, request.subject_user_id, request.status
                    INTO request_kind, request_subject, request_status
                    FROM public.data_subject_requests AS request
                    WHERE request.tenant_id = NEW.tenant_id AND request.id = NEW.request_id;
                IF (NOT FOUND OR request_kind <> 'export' OR request_subject IS DISTINCT FROM NEW.subject_user_id
                    OR request_status <> 'fulfilling') IS NOT FALSE
                THEN RAISE EXCEPTION 'Data export artifact request refused.' USING ERRCODE = '23514'; END IF;
                RETURN NEW;
            END IF;
            IF (ROW(NEW.id, NEW.tenant_id, NEW.organization_id, NEW.request_id, NEW.subject_user_id, NEW.byte_count,
                    NEW.chunk_count, NEW.key_version, NEW.truncation, NEW.expires_at, NEW.created_at, NEW.created_by,
                    NEW.deleted_at, NEW.deleted_by)
                IS DISTINCT FROM ROW(OLD.id, OLD.tenant_id, OLD.organization_id, OLD.request_id, OLD.subject_user_id, OLD.byte_count,
                    OLD.chunk_count, OLD.key_version, OLD.truncation, OLD.expires_at, OLD.created_at, OLD.created_by,
                    OLD.deleted_at, OLD.deleted_by)
                OR NEW.row_version IS DISTINCT FROM OLD.row_version + 1
                OR NEW.updated_at IS NULL OR NEW.updated_by IS NULL OR NOT isfinite(NEW.updated_at)
                OR NEW.updated_at < COALESCE(OLD.updated_at, OLD.created_at)
                OR NEW.updated_at > transaction_timestamp() + interval '5 minutes') IS NOT FALSE
            THEN RAISE EXCEPTION 'Data export artifact immutable state refused.' USING ERRCODE = '23514'; END IF;
            -- ADR-0107 § 4: revoke an unexpired artifact; expire or purge only after its expiry.
            IF (NOT ((OLD.status = 'available' AND NEW.status = 'revoked' AND clock_timestamp() < OLD.expires_at
                        AND NEW.purged_at = NEW.updated_at)
                    OR (OLD.status = 'available' AND NEW.status = 'expired' AND clock_timestamp() >= OLD.expires_at
                        AND NEW.purged_at IS NULL)
                    OR (OLD.status IN ('available','expired') AND NEW.status = 'purged' AND clock_timestamp() >= OLD.expires_at
                        AND NEW.purged_at = NEW.updated_at))) IS NOT FALSE
            THEN RAISE EXCEPTION 'Data export artifact transition refused.' USING ERRCODE = '23514'; END IF;
            RETURN NEW;
        END $guard$;
        REVOKE ALL ON FUNCTION public.fn_identity_data_export_artifact_guard() FROM PUBLIC;
        CREATE TRIGGER tg_data_export_artifacts_mutation_guard BEFORE INSERT OR UPDATE OR DELETE ON public.data_export_artifacts
            FOR EACH ROW EXECUTE FUNCTION public.fn_identity_data_export_artifact_guard();
        """;

    private const string TicketGuard = """
        CREATE FUNCTION public.fn_identity_data_export_ticket_guard() RETURNS trigger
            LANGUAGE plpgsql VOLATILE SECURITY INVOKER SET search_path = pg_catalog AS $guard$
        DECLARE artifact_subject uuid; artifact_status text; artifact_expires timestamptz;
        BEGIN
            IF (session_user <> 'learnstack_app' OR current_user <> 'learnstack_app'
                OR TG_TABLE_SCHEMA <> 'public' OR TG_TABLE_NAME <> 'data_export_tickets'
                OR TG_OP NOT IN ('INSERT','UPDATE')) IS NOT FALSE
            THEN RAISE EXCEPTION 'Data export ticket mutation refused.' USING ERRCODE = '23514'; END IF;
            IF TG_OP = 'INSERT' THEN
                IF (NEW.consumed_at IS NOT NULL OR NOT isfinite(NEW.created_at)
                    OR NEW.created_at > transaction_timestamp() + interval '5 minutes'
                    OR NEW.created_at < transaction_timestamp() - interval '5 minutes') IS NOT FALSE
                THEN RAISE EXCEPTION 'Data export ticket initial state refused.' USING ERRCODE = '23514'; END IF;
                -- Subject-only retrieval of a readable artifact (ADR-0107 § 4). FOR SHARE waits for a revocation or
                -- purge in flight and reads the status it commits, so a ticket is never minted over a withdrawn artifact.
                SELECT artifact.subject_user_id, artifact.status, artifact.expires_at
                    INTO artifact_subject, artifact_status, artifact_expires
                    FROM public.data_export_artifacts AS artifact
                    WHERE artifact.tenant_id = NEW.tenant_id AND artifact.id = NEW.artifact_id
                    FOR SHARE;
                IF (NOT FOUND OR artifact_status <> 'available' OR clock_timestamp() >= artifact_expires
                    OR artifact_subject IS DISTINCT FROM NEW.created_by) IS NOT FALSE
                THEN RAISE EXCEPTION 'Data export ticket artifact refused.' USING ERRCODE = '23514'; END IF;
                RETURN NEW;
            END IF;
            IF (ROW(NEW.id, NEW.tenant_id, NEW.organization_id, NEW.artifact_id, NEW.digest, NEW.session_binding_digest,
                    NEW.created_at, NEW.created_by, NEW.expires_at)
                IS DISTINCT FROM ROW(OLD.id, OLD.tenant_id, OLD.organization_id, OLD.artifact_id, OLD.digest,
                    OLD.session_binding_digest, OLD.created_at, OLD.created_by, OLD.expires_at)
                OR OLD.consumed_at IS NOT NULL OR NEW.consumed_at IS NULL
                OR NEW.consumed_at < OLD.created_at OR NEW.consumed_at >= OLD.expires_at
                OR NEW.consumed_at > transaction_timestamp() + interval '5 minutes'
                OR clock_timestamp() >= OLD.expires_at) IS NOT FALSE
            THEN RAISE EXCEPTION 'Data export ticket consumption refused.' USING ERRCODE = '23514'; END IF;
            RETURN NEW;
        END $guard$;
        REVOKE ALL ON FUNCTION public.fn_identity_data_export_ticket_guard() FROM PUBLIC;
        CREATE TRIGGER tg_data_export_tickets_mutation_guard BEFORE INSERT OR UPDATE OR DELETE ON public.data_export_tickets
            FOR EACH ROW EXECUTE FUNCTION public.fn_identity_data_export_ticket_guard();
        """;

    private const string ChunkGuard = """
        CREATE FUNCTION public.fn_identity_data_export_chunk_guard() RETURNS trigger
            LANGUAGE plpgsql VOLATILE SECURITY INVOKER SET search_path = pg_catalog AS $guard$
        DECLARE artifact_status text; artifact_chunks integer;
        BEGIN
            IF (session_user <> 'learnstack_app' OR current_user <> 'learnstack_app'
                OR TG_TABLE_SCHEMA <> 'public' OR TG_TABLE_NAME <> 'data_export_chunks'
                OR TG_OP NOT IN ('INSERT','DELETE')) IS NOT FALSE
            THEN RAISE EXCEPTION 'Data export chunk mutation refused.' USING ERRCODE = '23514'; END IF;
            -- Both arms read the artifact FOR SHARE, which conflicts with the row lock of a revocation or purge in
            -- flight: the statement waits for it and then reads the status it committed.
            IF TG_OP = 'DELETE' THEN
                -- A chunk leaves only an artifact already withdrawn or expired (the store saves the transition first):
                -- the ciphertext of an available artifact is never replaced or removed piecemeal.
                SELECT artifact.status INTO artifact_status
                    FROM public.data_export_artifacts AS artifact
                    WHERE artifact.tenant_id = OLD.tenant_id AND artifact.id = OLD.artifact_id
                    FOR SHARE;
                IF (NOT FOUND OR artifact_status = 'available') IS NOT FALSE
                THEN RAISE EXCEPTION 'Data export chunk deletion refused.' USING ERRCODE = '23514'; END IF;
                RETURN OLD;
            END IF;
            -- A chunk is written only onto an available artifact, so never onto one being withdrawn.
            SELECT artifact.status, artifact.chunk_count INTO artifact_status, artifact_chunks
                FROM public.data_export_artifacts AS artifact
                WHERE artifact.tenant_id = NEW.tenant_id AND artifact.id = NEW.artifact_id
                FOR SHARE;
            IF (NOT FOUND OR artifact_status <> 'available' OR NEW.chunk_index >= artifact_chunks) IS NOT FALSE
            THEN RAISE EXCEPTION 'Data export chunk refused.' USING ERRCODE = '23514'; END IF;
            RETURN NEW;
        END $guard$;
        REVOKE ALL ON FUNCTION public.fn_identity_data_export_chunk_guard() FROM PUBLIC;
        CREATE TRIGGER tg_data_export_chunks_mutation_guard BEFORE INSERT OR UPDATE OR DELETE ON public.data_export_chunks
            FOR EACH ROW EXECUTE FUNCTION public.fn_identity_data_export_chunk_guard();
        """;

    /// <summary>
    /// At commit, an available artifact holds exactly its chunk count and a revoked or purged one holds
    /// none: a bundle is never half-stored and a revocation never leaves ciphertext behind. An artifact
    /// inserted by the transaction also needs its request completed by it: the artifact guard admits the
    /// insert only while the request is fulfilling, and this is the other half of that bracket.
    /// </summary>
    private const string Completeness = """
        CREATE FUNCTION public.fn_identity_data_export_completeness() RETURNS trigger
            LANGUAGE plpgsql VOLATILE SECURITY INVOKER SET search_path = pg_catalog AS $complete$
        DECLARE target_tenant uuid; target_artifact uuid; artifact_status text; artifact_chunks integer;
            artifact_request uuid; request_status text; stored bigint;
        BEGIN
            IF TG_TABLE_NAME = 'data_export_artifacts' THEN
                target_tenant := NEW.tenant_id; target_artifact := NEW.id;
            ELSE
                target_tenant := OLD.tenant_id; target_artifact := OLD.artifact_id;
            END IF;
            SELECT artifact.status, artifact.chunk_count, artifact.request_id INTO artifact_status, artifact_chunks, artifact_request
                FROM public.data_export_artifacts AS artifact
                WHERE artifact.tenant_id = target_tenant AND artifact.id = target_artifact;
            IF TG_TABLE_NAME = 'data_export_artifacts' AND TG_OP = 'INSERT' THEN
                SELECT request.status INTO request_status FROM public.data_subject_requests AS request
                    WHERE request.tenant_id = target_tenant AND request.id = artifact_request;
                IF (request_status IS DISTINCT FROM 'completed') IS NOT FALSE
                THEN RAISE EXCEPTION 'Data export artifact request is not completed.'
                    USING ERRCODE = '23514', CONSTRAINT = 'data_export_artifact_request'; END IF;
            END IF;
            SELECT count(*) INTO stored FROM public.data_export_chunks AS chunk
                WHERE chunk.tenant_id = target_tenant AND chunk.artifact_id = target_artifact;
            IF (artifact_status IS NULL
                OR (artifact_status = 'available' AND stored <> artifact_chunks)
                OR (artifact_status IN ('revoked','purged') AND stored <> 0)) IS NOT FALSE
            THEN RAISE EXCEPTION 'Data export artifact chunks are inconsistent.'
                USING ERRCODE = '23514', CONSTRAINT = 'data_export_artifact_chunks'; END IF;
            RETURN NULL;
        END $complete$;
        REVOKE ALL ON FUNCTION public.fn_identity_data_export_completeness() FROM PUBLIC;
        CREATE CONSTRAINT TRIGGER tg_data_export_artifacts_complete AFTER INSERT OR UPDATE ON public.data_export_artifacts
            DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.fn_identity_data_export_completeness();
        CREATE CONSTRAINT TRIGGER tg_data_export_chunks_complete AFTER DELETE ON public.data_export_chunks
            DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.fn_identity_data_export_completeness();
        """;

    /// <summary>
    /// The reverse half of the bracket: an export that reaches <c>completed</c> has its artifact, of any
    /// status, when the transaction commits. With the artifact guard and the completeness check above, a
    /// completed export and its artifact commit together or not at all.
    /// </summary>
    private const string CompletedExportArtifact = """
        CREATE FUNCTION public.fn_identity_data_export_request_artifact() RETURNS trigger
            LANGUAGE plpgsql VOLATILE SECURITY INVOKER SET search_path = pg_catalog AS $complete$
        BEGIN
            IF NOT EXISTS (SELECT 1 FROM public.data_export_artifacts AS artifact
                    WHERE artifact.tenant_id = NEW.tenant_id AND artifact.request_id = NEW.id)
            THEN RAISE EXCEPTION 'Data export request completed without its artifact.'
                USING ERRCODE = '23514', CONSTRAINT = 'data_export_request_artifact'; END IF;
            RETURN NULL;
        END $complete$;
        REVOKE ALL ON FUNCTION public.fn_identity_data_export_request_artifact() FROM PUBLIC;
        CREATE CONSTRAINT TRIGGER tg_data_subject_requests_export_artifact AFTER UPDATE ON public.data_subject_requests
            DEFERRABLE INITIALLY DEFERRED FOR EACH ROW
            WHEN (NEW.kind = 'export' AND NEW.status = 'completed' AND OLD.status IS DISTINCT FROM 'completed')
            EXECUTE FUNCTION public.fn_identity_data_export_request_artifact();
        """;
}
