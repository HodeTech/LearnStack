using LearnStack.Modules.Identity.Domain;
using Microsoft.EntityFrameworkCore.Migrations;

namespace LearnStack.Modules.Identity.Infrastructure.Persistence;

/// <summary>Forward-only finite registry and retained Role-release guards; no grant backfill.</summary>
public static class RoleReleaseDdl
{
    public static void Install(MigrationBuilder migration)
    {
        ArgumentNullException.ThrowIfNull(migration);
        var initial = IdentityFoundationManifest.Version1;
        var release = IdentityRoleRelease.Version2;
        migration.Sql($$"""
            ALTER TABLE public.role_permission_grants DROP CONSTRAINT ck_role_permission_grants_registry;
            ALTER TABLE public.role_permission_grants ADD CONSTRAINT ck_role_permission_grants_registry
                CHECK ((permission_scope,permission_key) IN ({{release.RegistrySqlValues}}));
            ALTER TABLE public.roles ADD CONSTRAINT ck_roles_adopted_release
                CHECK ((adopted_release_version=1 AND adopted_release_fingerprint='{{initial.Fingerprint}}')
                    OR (adopted_release_version={{release.Version}} AND adopted_release_fingerprint='{{release.Fingerprint}}'
                        AND built_in_key='TenantAdmin' AND permission_scope='Tenant'));

            CREATE FUNCTION public.fn_identity_role_release_guard() RETURNS trigger
                LANGUAGE plpgsql VOLATILE SECURITY INVOKER SET search_path = pg_catalog
            AS $guard$
            DECLARE target_tenant uuid; target_role uuid; role_key text;
            BEGIN
                IF TG_TABLE_SCHEMA <> 'public' THEN
                    RAISE EXCEPTION 'identity graph is invalid' USING ERRCODE='P0304', CONSTRAINT='identity_role_release';
                END IF;
                IF TG_TABLE_NAME='roles' AND TG_WHEN='AFTER' THEN
                    IF NEW.adopted_release_version IS DISTINCT FROM OLD.adopted_release_version
                        AND EXISTS (SELECT 1 FROM (VALUES {{release.TenantAdminDeltaSqlValues}}) AS delta(scope,permission_key)
                            WHERE NOT EXISTS (SELECT 1 FROM public.role_permission_grants AS grant_row
                                WHERE grant_row.tenant_id=NEW.tenant_id AND grant_row.role_id=NEW.id
                                    AND grant_row.permission_scope=delta.scope AND grant_row.permission_key=delta.permission_key)) THEN
                        RAISE EXCEPTION 'identity graph is invalid' USING ERRCODE='P0304', CONSTRAINT='identity_role_release';
                    END IF;
                    RETURN NULL;
                ELSIF TG_TABLE_NAME='roles' THEN
                    IF TG_OP='INSERT' THEN
                        IF NEW.adopted_release_version<>1 OR NEW.adopted_release_fingerprint<>'{{initial.Fingerprint}}' THEN
                            RAISE EXCEPTION 'identity graph is invalid' USING ERRCODE='P0304', CONSTRAINT='identity_role_release';
                        END IF;
                    ELSIF NEW.adopted_release_version IS DISTINCT FROM OLD.adopted_release_version
                        OR NEW.adopted_release_fingerprint IS DISTINCT FROM OLD.adopted_release_fingerprint THEN
                        IF OLD.adopted_release_version<>1 OR OLD.adopted_release_fingerprint<>'{{initial.Fingerprint}}'
                            OR NEW.adopted_release_version<>{{release.Version}} OR NEW.adopted_release_fingerprint<>'{{release.Fingerprint}}'
                            OR NEW.built_in_key<>'TenantAdmin' OR NEW.permission_scope<>'Tenant'
                            OR OLD.deleted_at IS NOT NULL OR OLD.deleted_by IS NOT NULL
                            OR NEW.deleted_at IS NOT NULL OR NEW.deleted_by IS NOT NULL
                            OR NEW.row_version<>OLD.row_version+1
                            OR NULLIF(current_setting('app.organization_id',true),'') IS NOT NULL THEN
                            RAISE EXCEPTION 'identity graph is invalid' USING ERRCODE='P0304', CONSTRAINT='identity_role_release';
                        END IF;
                        PERFORM public.fn_identity_assert_tenant_fence(NEW.tenant_id,true);
                    END IF;
                    RETURN NEW;
                ELSIF TG_TABLE_NAME='role_permission_grants' THEN
                    target_tenant := CASE WHEN TG_OP='DELETE' THEN OLD.tenant_id ELSE NEW.tenant_id END;
                    target_role := CASE WHEN TG_OP='DELETE' THEN OLD.role_id ELSE NEW.role_id END;
                    -- Acquire after the canonical tenant family, before any grant
                    -- mutation. KEY SHARE would permit a conflicting non-key UPDATE.
                    PERFORM public.fn_identity_assert_tenant_fence(target_tenant,false);
                    SELECT role.built_in_key INTO role_key FROM public.roles AS role
                        WHERE role.tenant_id=target_tenant AND role.id=target_role FOR NO KEY UPDATE;
                    IF NOT FOUND THEN
                        RAISE EXCEPTION 'identity graph is invalid' USING ERRCODE='P0304', CONSTRAINT='identity_role_release';
                    END IF;
                    PERFORM public.fn_identity_assert_tenant_fence(target_tenant,role_key='TenantAdmin'
                        AND EXISTS (SELECT 1 FROM public.memberships WHERE tenant_id=target_tenant));
                    IF TG_OP='DELETE' THEN RETURN OLD; END IF;
                    RETURN NEW;
                END IF;
                RAISE EXCEPTION 'identity graph is invalid' USING ERRCODE='P0304', CONSTRAINT='identity_role_release';
            END;
            $guard$;
            COMMENT ON FUNCTION public.fn_identity_role_release_guard() IS 'Identity Role release v{{release.Version}} {{release.Fingerprint}}';
            REVOKE ALL ON FUNCTION public.fn_identity_role_release_guard() FROM PUBLIC, learnstack_app, learnstack_platform, learnstack_outbox_admin;
            CREATE TRIGGER tg_roles_release_guard BEFORE INSERT OR UPDATE ON public.roles
                FOR EACH ROW EXECUTE FUNCTION public.fn_identity_role_release_guard();
            CREATE CONSTRAINT TRIGGER tg_roles_release_complete AFTER UPDATE ON public.roles
                DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.fn_identity_role_release_guard();
            CREATE TRIGGER tg_role_permission_grants_root_lock BEFORE INSERT OR DELETE ON public.role_permission_grants
                FOR EACH ROW EXECUTE FUNCTION public.fn_identity_role_release_guard();
            """);
    }
}
