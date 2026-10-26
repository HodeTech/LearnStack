using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.Permissions;
using Microsoft.EntityFrameworkCore.Migrations;

namespace LearnStack.Modules.Identity.Infrastructure.Persistence;

/// <summary>Contextual Identity guards. Lock observations never acquire authority.</summary>
public static class ContextualIdentityStorageDdl
{
    public static void Install(MigrationBuilder migration)
    {
        ArgumentNullException.ThrowIfNull(migration);
        // Keep the original migration's SQL unchanged; the forward correction
        // replaces only the mutation guard on both existing and fresh databases.
        migration.Sql(Guards + "\n\nCREATE " + MutationGuardSql(correctParentMirror: false)
            + "\n" + MutationGuardRevocation);
        migration.Sql(CompleteGraphGuard());
        foreach (var table in new[] { "memberships", "membership_profiles", "membership_role_assignments" })
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
        foreach (var table in new[] { "roles", "role_permission_grants" })
        {
            migration.Sql($"""
                ALTER TABLE public.{table} ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public.{table} FORCE ROW LEVEL SECURITY;
                CREATE POLICY {table}_isolation ON public.{table}
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
                """);
        }
        foreach (var table in new[] { "memberships", "membership_profiles", "membership_role_assignments", "roles", "role_permission_grants" })
        {
            migration.Sql($"""
                REVOKE ALL ON public.{table} FROM PUBLIC, learnstack_app, learnstack_platform, learnstack_outbox_admin;
                CREATE TRIGGER tg_{table}_mutation_guard BEFORE INSERT OR UPDATE OR DELETE ON public.{table}
                    FOR EACH ROW EXECUTE FUNCTION public.fn_identity_contextual_mutation_guard();
                CREATE CONSTRAINT TRIGGER tg_{table}_complete_graph AFTER INSERT OR UPDATE OR DELETE ON public.{table}
                    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.fn_identity_complete_graph_guard();
                """);
        }
        migration.Sql("""
            GRANT SELECT, INSERT, UPDATE ON public.memberships, public.membership_profiles, public.roles TO learnstack_app;
            GRANT SELECT, INSERT, DELETE ON public.membership_role_assignments, public.role_permission_grants TO learnstack_app;
            CREATE TRIGGER tg_memberships_admission BEFORE INSERT OR UPDATE ON public.memberships
                FOR EACH ROW EXECUTE FUNCTION public.fn_identity_membership_admission_guard();
            CREATE TRIGGER tg_membership_role_assignments_admission BEFORE INSERT ON public.membership_role_assignments
                FOR EACH ROW EXECUTE FUNCTION public.fn_identity_membership_admission_guard();
            """);
    }

    public static void Remove(MigrationBuilder migration)
    {
        ArgumentNullException.ThrowIfNull(migration);
        foreach (var table in new[] { "memberships", "membership_profiles", "membership_role_assignments", "roles", "role_permission_grants" })
            migration.Sql($"DROP TRIGGER tg_{table}_complete_graph ON public.{table}; DROP TRIGGER tg_{table}_mutation_guard ON public.{table};");
        migration.Sql("""
            DROP TRIGGER tg_memberships_admission ON public.memberships;
            DROP TRIGGER tg_membership_role_assignments_admission ON public.membership_role_assignments;
            DROP FUNCTION public.fn_identity_complete_graph_guard();
            DROP FUNCTION public.fn_identity_contextual_mutation_guard();
            DROP FUNCTION public.fn_identity_membership_admission_guard();
            DROP FUNCTION public.fn_identity_admin_admission_state(uuid, uuid);
            DROP FUNCTION public.fn_identity_assert_tenant_fence(uuid, boolean);
            DROP FUNCTION public.fn_identity_lock_held(text, uuid, boolean);
            DROP FUNCTION public.fn_identity_contextual_lock_key(text, uuid);
            """);
    }

    internal static void CorrectParentMirrorSqlState(MigrationBuilder migration)
    {
        ArgumentNullException.ThrowIfNull(migration);
        migration.Sql("CREATE OR REPLACE " + MutationGuardSql(correctParentMirror: true));
    }

    internal static void RestoreLegacyParentMirrorSqlState(MigrationBuilder migration)
    {
        ArgumentNullException.ThrowIfNull(migration);
        migration.Sql("CREATE OR REPLACE " + MutationGuardSql(correctParentMirror: false));
    }

    private static string MutationGuardSql(bool correctParentMirror) => MutationGuardDefinition
        .Replace("__PARENT_MIRROR_CHECK__", correctParentMirror ? ParentMirrorCheck : LegacyParentMirrorCheck,
            StringComparison.Ordinal)
        .Replace("__ROLE_ADMIN_KEY__", IdentityFoundationManifest.RoleAdmin, StringComparison.Ordinal);

    private static string CompleteGraphGuard()
    {
        var manifest = IdentityFoundationManifest.Version1;
        return $$"""
            ALTER TABLE public.roles ADD CONSTRAINT ck_roles_finite_template
                CHECK ((built_in_key,permission_scope) IN ({{manifest.RolePairsSqlValues}}));
            ALTER TABLE public.role_permission_grants ADD CONSTRAINT ck_role_permission_grants_registry
                CHECK ((permission_scope,permission_key) IN ({{manifest.RegistrySqlValues}}));
            CREATE FUNCTION public.fn_identity_complete_graph_guard() RETURNS trigger
                LANGUAGE plpgsql VOLATILE SECURITY INVOKER SET search_path = pg_catalog
            AS $guard$
            DECLARE target_tenant uuid; target_member uuid; target_role uuid; has_history boolean;
            BEGIN
                target_tenant := CASE WHEN TG_OP = 'DELETE' THEN OLD.tenant_id ELSE NEW.tenant_id END;
                PERFORM public.fn_identity_assert_tenant_fence(target_tenant,false);
                IF TG_TABLE_NAME IN ('memberships','membership_profiles','membership_role_assignments') THEN
                    IF TG_TABLE_NAME = 'memberships' THEN
                        target_member := CASE WHEN TG_OP = 'DELETE' THEN OLD.id ELSE NEW.id END;
                    ELSE
                        target_member := CASE WHEN TG_OP = 'DELETE' THEN OLD.membership_id ELSE NEW.membership_id END;
                    END IF;
                    IF NOT EXISTS (SELECT 1 FROM public.memberships AS member
                        JOIN public.membership_profiles AS profile ON profile.tenant_id = member.tenant_id AND profile.membership_id = member.id
                        WHERE member.tenant_id = target_tenant AND member.id = target_member
                          AND profile.organization_id IS NOT DISTINCT FROM member.organization_id) THEN
                        RAISE EXCEPTION 'identity graph is invalid' USING ERRCODE = 'P0304';
                    END IF;
                END IF;
                SELECT EXISTS (SELECT 1 FROM public.memberships WHERE tenant_id = target_tenant) INTO has_history;
                IF NOT has_history AND TG_TABLE_NAME IN ('roles','role_permission_grants') THEN
                    IF TG_TABLE_NAME = 'roles' THEN target_role := NEW.id; ELSE target_role := NEW.role_id; END IF;
                    IF TG_OP <> 'INSERT' OR NOT EXISTS (SELECT 1 FROM public.roles AS role
                        WHERE role.tenant_id = target_tenant AND role.id = target_role
                          AND role.xmin = pg_current_xact_id()::text::xid AND role.deleted_at IS NULL AND role.deleted_by IS NULL
                          AND NOT EXISTS (
                            (SELECT expected.scope, expected.permission_key FROM (VALUES {{manifest.GrantSqlValues}}) AS expected(built_in_key,scope,permission_key)
                             WHERE expected.built_in_key = role.built_in_key AND expected.scope = role.permission_scope
                             EXCEPT SELECT grant_row.permission_scope, grant_row.permission_key FROM public.role_permission_grants AS grant_row
                             WHERE grant_row.tenant_id = target_tenant AND grant_row.role_id = target_role)
                            UNION ALL
                            (SELECT grant_row.permission_scope, grant_row.permission_key FROM public.role_permission_grants AS grant_row
                             WHERE grant_row.tenant_id = target_tenant AND grant_row.role_id = target_role
                             EXCEPT SELECT expected.scope, expected.permission_key FROM (VALUES {{manifest.GrantSqlValues}}) AS expected(built_in_key,scope,permission_key)
                             WHERE expected.built_in_key = role.built_in_key AND expected.scope = role.permission_scope))) THEN
                        RAISE EXCEPTION 'identity graph is invalid' USING ERRCODE = 'P0304', CONSTRAINT = 'identity_initial_template';
                    END IF;
                    RETURN NULL;
                END IF;
                IF NOT EXISTS (SELECT 1 FROM public.memberships AS member
                    JOIN public.membership_role_assignments AS assignment
                      ON assignment.tenant_id = member.tenant_id AND assignment.membership_id = member.id
                    JOIN public.roles AS role ON role.tenant_id = assignment.tenant_id AND role.id = assignment.role_id
                    WHERE member.tenant_id = target_tenant AND member.organization_id IS NULL
                      AND member.status = 'Active' AND member.deleted_at IS NULL AND member.deleted_by IS NULL
                      AND role.built_in_key = 'TenantAdmin' AND role.permission_scope = 'Tenant'
                      AND role.deleted_at IS NULL AND role.deleted_by IS NULL
                      AND NOT EXISTS (SELECT 1 FROM (VALUES {{manifest.KernelSqlValues}}) AS kernel(scope,permission_key)
                        WHERE NOT EXISTS (SELECT 1 FROM public.role_permission_grants AS grant_row
                            WHERE grant_row.tenant_id = role.tenant_id AND grant_row.role_id = role.id
                              AND grant_row.permission_scope = kernel.scope AND grant_row.permission_key = kernel.permission_key))
                      AND public.fn_identity_admin_admission_state(member.user_id,target_tenant)) THEN
                    RAISE EXCEPTION 'identity administration is unavailable' USING ERRCODE = 'P0303';
                END IF;
                RETURN NULL;
            END;
            $guard$;
            COMMENT ON FUNCTION public.fn_identity_complete_graph_guard() IS 'Identity foundation v{{manifest.Version}} {{manifest.Fingerprint}}';
            REVOKE ALL ON FUNCTION public.fn_identity_complete_graph_guard() FROM PUBLIC, learnstack_app, learnstack_platform, learnstack_outbox_admin;
            """;
    }

    private const string Guards = """
        CREATE FUNCTION public.fn_identity_contextual_lock_key(kind text, target uuid) RETURNS bigint
            LANGUAGE sql IMMUTABLE STRICT SECURITY INVOKER SET search_path = pg_catalog
        AS $$
            SELECT ('x' || substr(encode(sha256(
                int4send(octet_length(convert_to('learnstack.identity.lock.v1','UTF8')))
                || convert_to('learnstack.identity.lock.v1','UTF8')
                || int4send(octet_length(convert_to(kind,'UTF8'))) || convert_to(kind,'UTF8')
                || int4send(16) || uuid_send(target)), 'hex'),1,16))::bit(64)::bigint
            WHERE kind IN ('user','tenant');
        $$;
        REVOKE ALL ON FUNCTION public.fn_identity_contextual_lock_key(text, uuid) FROM PUBLIC;
        GRANT EXECUTE ON FUNCTION public.fn_identity_contextual_lock_key(text, uuid) TO learnstack_app;

        CREATE FUNCTION public.fn_identity_lock_held(kind text, target uuid, exclusive_required boolean) RETURNS boolean
            LANGUAGE sql VOLATILE STRICT SECURITY INVOKER SET search_path = pg_catalog
        AS $$
            SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_locks AS held
                WHERE held.pid = pg_catalog.pg_backend_pid()
                  AND held.database = (SELECT oid FROM pg_catalog.pg_database WHERE datname = pg_catalog.current_database())
                  AND held.locktype = 'advisory' AND held.granted
                  AND (held.mode = 'ExclusiveLock' OR (NOT exclusive_required AND held.mode = 'ShareLock'))
                  AND held.objsubid = 1
                  AND ((held.classid::bigint << 32) | held.objid::bigint) = public.fn_identity_contextual_lock_key(kind,target));
        $$;
        REVOKE ALL ON FUNCTION public.fn_identity_lock_held(text, uuid, boolean) FROM PUBLIC;
        GRANT EXECUTE ON FUNCTION public.fn_identity_lock_held(text, uuid, boolean) TO learnstack_app;

        CREATE FUNCTION public.fn_identity_assert_tenant_fence(target uuid, exclusive_required boolean) RETURNS void
            LANGUAGE plpgsql VOLATILE SECURITY INVOKER SET search_path = pg_catalog
        AS $$
        BEGIN
            IF current_setting('transaction_isolation') <> 'read committed'
               OR target IS DISTINCT FROM NULLIF(current_setting('app.tenant_id',true),'')::uuid
               OR public.fn_identity_lock_held('tenant',target,exclusive_required) IS NOT TRUE THEN
                RAISE EXCEPTION 'identity mutation plan is unavailable' USING ERRCODE = 'P0301', CONSTRAINT = 'identity_tenant_fence';
            END IF;
        END;
        $$;
        REVOKE ALL ON FUNCTION public.fn_identity_assert_tenant_fence(uuid, boolean) FROM PUBLIC;
        GRANT EXECUTE ON FUNCTION public.fn_identity_assert_tenant_fence(uuid, boolean) TO learnstack_app;

        CREATE FUNCTION public.fn_identity_membership_admission_guard() RETURNS trigger
            LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = pg_catalog
        AS $$
        DECLARE target_user uuid; target_tenant uuid; eligible boolean;
        BEGIN
            IF TG_TABLE_SCHEMA <> 'public' THEN
                RAISE EXCEPTION 'identity mutation plan is unavailable' USING ERRCODE = 'P0301';
            END IF;
            IF TG_TABLE_NAME = 'memberships' AND TG_OP IN ('INSERT','UPDATE') THEN
                IF TG_OP = 'UPDATE' AND NOT (OLD.status = 'Suspended' AND NEW.status = 'Active') THEN RETURN NEW; END IF;
                target_user := NEW.user_id;
                target_tenant := NEW.tenant_id;
            ELSIF TG_TABLE_NAME = 'membership_role_assignments' AND TG_OP = 'INSERT' THEN
                SELECT member.user_id, member.tenant_id INTO target_user, target_tenant
                FROM public.memberships AS member
                WHERE member.tenant_id = NEW.tenant_id AND member.id = NEW.membership_id;
                IF NOT FOUND THEN
                    RAISE EXCEPTION 'identity membership target is unavailable' USING ERRCODE = 'P0302';
                END IF;
            ELSE
                RAISE EXCEPTION 'identity mutation plan is unavailable' USING ERRCODE = 'P0301';
            END IF;
            PERFORM public.fn_identity_assert_tenant_fence(target_tenant, false);
            IF public.fn_identity_lock_held('user',target_user,true) IS NOT TRUE THEN
                RAISE EXCEPTION 'identity mutation plan is unavailable' USING ERRCODE = 'P0301', CONSTRAINT = 'identity_user_fence';
            END IF;
            SELECT person.status = 'Active' AND person.deleted_at IS NULL AND person.deleted_by IS NULL
                INTO eligible FROM public.users AS person WHERE person.id = target_user;
            IF eligible IS NOT TRUE THEN
                RAISE EXCEPTION 'identity membership target is unavailable' USING ERRCODE = 'P0302';
            END IF;
            RETURN NEW;
        END;
        $$;
        ALTER FUNCTION public.fn_identity_membership_admission_guard() OWNER TO learnstack_migration;
        REVOKE ALL ON FUNCTION public.fn_identity_membership_admission_guard() FROM PUBLIC, learnstack_app, learnstack_platform, learnstack_outbox_admin;

        CREATE FUNCTION public.fn_identity_admin_admission_state(target_user uuid, target_tenant uuid) RETURNS boolean
            LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = pg_catalog
        AS $$
        DECLARE eligible boolean;
        BEGIN
            IF pg_catalog.pg_trigger_depth() < 1 THEN RETURN false; END IF;
            PERFORM public.fn_identity_assert_tenant_fence(target_tenant, false);
            IF NOT EXISTS (SELECT 1 FROM public.memberships AS member
                JOIN public.membership_role_assignments AS assignment
                  ON assignment.tenant_id = member.tenant_id AND assignment.membership_id = member.id
                JOIN public.roles AS role ON role.tenant_id = assignment.tenant_id AND role.id = assignment.role_id
                WHERE member.tenant_id = target_tenant AND member.user_id = target_user
                  AND member.organization_id IS NULL AND member.status = 'Active' AND member.deleted_at IS NULL
                  AND role.built_in_key = 'TenantAdmin' AND role.permission_scope = 'Tenant' AND role.deleted_at IS NULL) THEN
                RETURN false;
            END IF;
            SELECT person.status = 'Active' AND person.deleted_at IS NULL AND person.deleted_by IS NULL
                INTO eligible FROM public.users AS person WHERE person.id = target_user;
            RETURN COALESCE(eligible,false);
        END;
        $$;
        ALTER FUNCTION public.fn_identity_admin_admission_state(uuid, uuid) OWNER TO learnstack_migration;
        REVOKE ALL ON FUNCTION public.fn_identity_admin_admission_state(uuid, uuid) FROM PUBLIC, learnstack_platform, learnstack_outbox_admin;
        GRANT EXECUTE ON FUNCTION public.fn_identity_admin_admission_state(uuid, uuid) TO learnstack_app;
        """;

    private const string MutationGuardDefinition = """
        FUNCTION public.fn_identity_contextual_mutation_guard() RETURNS trigger
            LANGUAGE plpgsql VOLATILE SECURITY INVOKER SET search_path = pg_catalog
        AS $$
        DECLARE target_tenant uuid; target_member uuid; target_role uuid; parent_org uuid;
            parent_status text; role_scope text; role_key text; require_exclusive boolean := false;
            has_history boolean; parent_fresh boolean;
        BEGIN
            target_tenant := CASE WHEN TG_OP = 'DELETE' THEN OLD.tenant_id ELSE NEW.tenant_id END;
            IF TG_OP = 'UPDATE' AND NEW.tenant_id IS DISTINCT FROM OLD.tenant_id THEN
                RAISE EXCEPTION 'identity graph is invalid' USING ERRCODE = 'P0304';
            END IF;
            IF TG_TABLE_NAME = 'memberships' THEN
                IF TG_OP = 'DELETE' OR (TG_OP = 'INSERT' AND (NEW.status <> 'Active' OR NEW.deleted_at IS NOT NULL OR NEW.deleted_by IS NOT NULL)) THEN
                    RAISE EXCEPTION 'identity graph is invalid' USING ERRCODE = 'P0304';
                END IF;
                IF TG_OP = 'UPDATE' AND (NEW.id IS DISTINCT FROM OLD.id OR NEW.user_id IS DISTINCT FROM OLD.user_id
                    OR OLD.status = 'Revoked' OR NEW.status NOT IN ('Active','Suspended','Revoked')) THEN
                    RAISE EXCEPTION 'identity graph is invalid' USING ERRCODE = 'P0304';
                END IF;
                IF TG_OP = 'INSERT' THEN
                    require_exclusive := NOT EXISTS (SELECT 1 FROM public.memberships WHERE tenant_id = target_tenant);
                ELSIF NEW.status IS DISTINCT FROM OLD.status OR NEW.deleted_at IS DISTINCT FROM OLD.deleted_at THEN
                    SELECT EXISTS (SELECT 1 FROM public.membership_role_assignments AS assignment
                        JOIN public.roles AS role ON role.tenant_id = assignment.tenant_id AND role.id = assignment.role_id
                        WHERE assignment.tenant_id = target_tenant AND assignment.membership_id = OLD.id
                          AND OLD.organization_id IS NULL AND role.built_in_key = 'TenantAdmin' AND role.permission_scope = 'Tenant') INTO require_exclusive;
                END IF;
            ELSIF TG_TABLE_NAME IN ('membership_profiles','membership_role_assignments') THEN
                target_member := CASE WHEN TG_OP = 'DELETE' THEN OLD.membership_id ELSE NEW.membership_id END;
                IF TG_OP = 'UPDATE' AND NEW.membership_id IS DISTINCT FROM OLD.membership_id THEN
                    RAISE EXCEPTION 'identity graph is invalid' USING ERRCODE = 'P0304';
                END IF;
                SELECT member.organization_id, member.status INTO parent_org, parent_status
                    FROM public.memberships AS member WHERE member.tenant_id = target_tenant AND member.id = target_member FOR KEY SHARE;
        __PARENT_MIRROR_CHECK__
                IF TG_TABLE_NAME = 'membership_profiles' AND TG_OP = 'DELETE' THEN
                    RAISE EXCEPTION 'identity graph is invalid' USING ERRCODE = 'P0304';
                ELSIF TG_TABLE_NAME = 'membership_role_assignments' THEN
                    IF TG_OP = 'UPDATE' THEN RAISE EXCEPTION 'identity graph is invalid' USING ERRCODE = 'P0304'; END IF;
                    target_role := CASE WHEN TG_OP = 'DELETE' THEN OLD.role_id ELSE NEW.role_id END;
                    SELECT role.permission_scope, role.built_in_key INTO role_scope, role_key FROM public.roles AS role
                        WHERE role.tenant_id = target_tenant AND role.id = target_role FOR KEY SHARE;
                    IF NOT FOUND OR (parent_org IS NULL AND role_scope <> 'Tenant') OR (parent_org IS NOT NULL AND role_scope <> 'Organization') THEN
                        RAISE EXCEPTION 'identity graph is invalid' USING ERRCODE = 'P0304';
                    END IF;
                    require_exclusive := parent_org IS NULL AND role_key = 'TenantAdmin';
                END IF;
            ELSIF TG_TABLE_NAME IN ('roles','role_permission_grants') THEN
                IF NULLIF(current_setting('app.organization_id',true),'') IS NOT NULL THEN
                    RAISE EXCEPTION 'identity graph is invalid' USING ERRCODE = 'P0304';
                END IF;
                SELECT EXISTS (SELECT 1 FROM public.memberships WHERE tenant_id = target_tenant) INTO has_history;
                IF TG_TABLE_NAME = 'roles' THEN
                    IF TG_OP = 'DELETE' OR (TG_OP = 'UPDATE' AND (NEW.id IS DISTINCT FROM OLD.id
                       OR NEW.built_in_key IS DISTINCT FROM OLD.built_in_key OR NEW.permission_scope IS DISTINCT FROM OLD.permission_scope)) THEN
                        RAISE EXCEPTION 'identity graph is invalid' USING ERRCODE = 'P0304';
                    END IF;
                    IF NOT has_history AND TG_OP <> 'INSERT' THEN
                        RAISE EXCEPTION 'identity graph is invalid' USING ERRCODE = 'P0304';
                    END IF;
                    require_exclusive := has_history AND NEW.built_in_key = 'TenantAdmin';
                ELSE
                    IF TG_OP = 'UPDATE' THEN RAISE EXCEPTION 'identity graph is invalid' USING ERRCODE = 'P0304'; END IF;
                    target_role := CASE WHEN TG_OP = 'DELETE' THEN OLD.role_id ELSE NEW.role_id END;
                    SELECT role.permission_scope, role.built_in_key, role.xmin = pg_current_xact_id()::text::xid
                        INTO role_scope, role_key, parent_fresh FROM public.roles AS role
                        WHERE role.tenant_id = target_tenant AND role.id = target_role FOR KEY SHARE;
                    IF NOT FOUND OR role_scope IS DISTINCT FROM (CASE WHEN TG_OP = 'DELETE' THEN OLD.permission_scope ELSE NEW.permission_scope END)
                        OR (NOT has_history AND (TG_OP <> 'INSERT' OR NOT parent_fresh))
                        OR (TG_OP = 'INSERT' AND NEW.permission_key = '__ROLE_ADMIN_KEY__' AND role_key <> 'TenantAdmin') THEN
                        RAISE EXCEPTION 'identity graph is invalid' USING ERRCODE = 'P0304';
                    END IF;
                    require_exclusive := has_history AND role_key = 'TenantAdmin';
                END IF;
            ELSE
                RAISE EXCEPTION 'identity mutation plan is unavailable' USING ERRCODE = 'P0301';
            END IF;
            PERFORM public.fn_identity_assert_tenant_fence(target_tenant,require_exclusive);
            IF TG_OP = 'DELETE' THEN RETURN OLD; END IF;
            RETURN NEW;
        END;
        $$;
        """;

    private const string MutationGuardRevocation =
        "REVOKE ALL ON FUNCTION public.fn_identity_contextual_mutation_guard() FROM PUBLIC, learnstack_app, learnstack_platform, learnstack_outbox_admin;";

    private const string LegacyParentMirrorCheck = """
                IF NOT FOUND OR parent_org IS DISTINCT FROM (CASE WHEN TG_OP = 'DELETE' THEN OLD.organization_id ELSE NEW.organization_id END)
                    OR (TG_OP <> 'DELETE' AND parent_status = 'Revoked') THEN
                    RAISE EXCEPTION 'identity graph is invalid' USING ERRCODE = 'P0304';
                END IF;
        """;

    private const string ParentMirrorCheck = """
                IF NOT FOUND OR parent_org IS DISTINCT FROM (CASE WHEN TG_OP = 'DELETE' THEN OLD.organization_id ELSE NEW.organization_id END) THEN
                    RAISE EXCEPTION 'identity graph is invalid' USING ERRCODE = '23514', CONSTRAINT = 'membership_parent_scope';
                END IF;
                IF TG_OP <> 'DELETE' AND parent_status = 'Revoked' THEN
                    RAISE EXCEPTION 'identity graph is invalid' USING ERRCODE = 'P0304';
                END IF;
        """;
}
