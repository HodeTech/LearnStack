using System.Globalization;
using LearnStack.Modules.Tenancy.Application.Contracts.Seeding;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using Microsoft.EntityFrameworkCore.Migrations;

namespace LearnStack.Modules.Tenancy.Infrastructure.Persistence;

/// <summary>Invoker-only guard for the fixed monotonic development-origin adoption.</summary>
public static class SeedAdministrationOriginDdl
{
    public static void Install(MigrationBuilder migration)
    {
        ArgumentNullException.ThrowIfNull(migration);
        // The finite SQL registry and application declaration share their authored pins.
        // Lock inspection uses the existing key family without an Identity table/function dependency.
        var declarations = string.Join(",", SeedAdministrationOriginManifest.All.Select(declaration =>
            $"('{declaration.TenantId.Value:D}'::uuid,'{declaration.TenantSlug}','{declaration.Host}',"
            + $"{IdentityAdvisoryLockKey.ForTenant(declaration.TenantId).ToString(CultureInfo.InvariantCulture)}::bigint)"));
        migration.Sql($$"""
            ALTER TABLE public.tenants ADD CONSTRAINT ck_tenants_adopted_seed_origin_version
                CHECK (adopted_seed_origin_version IN (0,1));

            CREATE FUNCTION public.fn_tenancy_seed_administration_origin_guard() RETURNS trigger
                LANGUAGE plpgsql VOLATILE SECURITY INVOKER SET search_path = pg_catalog
            AS $guard$
            DECLARE declared_slug text; declared_host text; declared_lock bigint; mapping_host text;
            BEGIN
                IF TG_TABLE_SCHEMA <> 'public' OR TG_TABLE_NAME <> 'tenants' THEN
                    RAISE EXCEPTION 'seed administration origin is invalid'
                        USING ERRCODE='23514', CONSTRAINT='ck_tenants_adopted_seed_origin_version';
                END IF;
                IF TG_OP='INSERT' THEN
                    IF NEW.adopted_seed_origin_version IS DISTINCT FROM 0 THEN
                        RAISE EXCEPTION 'seed administration origin is invalid'
                            USING ERRCODE='23514', CONSTRAINT='ck_tenants_adopted_seed_origin_version';
                    END IF;
                    RETURN NEW;
                END IF;
                IF NEW.adopted_seed_origin_version IS NOT DISTINCT FROM OLD.adopted_seed_origin_version THEN
                    RETURN NEW;
                END IF;

                SELECT declaration.slug,declaration.host,declaration.lock_key
                    INTO declared_slug,declared_host,declared_lock
                    FROM (VALUES {{declarations}}) AS declaration(tenant_id,slug,host,lock_key)
                    WHERE declaration.tenant_id=OLD.id;
                IF NOT FOUND OR OLD.adopted_seed_origin_version<>0 OR NEW.adopted_seed_origin_version<>1
                    OR NEW.id IS DISTINCT FROM OLD.id OR OLD.slug IS DISTINCT FROM declared_slug
                    OR NEW.slug IS DISTINCT FROM OLD.slug OR NEW.display_name IS DISTINCT FROM OLD.display_name
                    OR NEW.status IS DISTINCT FROM OLD.status OR OLD.status NOT IN ('Trial','Active','Suspended')
                    OR OLD.default_organization_id IS NULL
                    OR NEW.default_organization_id IS DISTINCT FROM OLD.default_organization_id
                    OR OLD.created_by<>'{{UserId.SystemActor.Value:D}}'::uuid
                    OR OLD.created_at<='0001-01-01 00:00:00+00'::timestamptz OR NOT isfinite(OLD.created_at)
                    OR NEW.created_at IS DISTINCT FROM OLD.created_at OR NEW.created_by IS DISTINCT FROM OLD.created_by
                    OR OLD.deleted_at IS NOT NULL OR OLD.deleted_by IS NOT NULL
                    OR NEW.deleted_at IS NOT NULL OR NEW.deleted_by IS NOT NULL
                    OR OLD.row_version<1 OR NEW.row_version<>OLD.row_version+1
                    OR NEW.updated_by IS DISTINCT FROM '{{UserId.SystemActor.Value:D}}'::uuid
                    OR NEW.updated_at IS NULL OR NEW.updated_at<='0001-01-01 00:00:00+00'::timestamptz
                    OR NOT isfinite(NEW.updated_at)
                    OR current_setting('transaction_isolation')<>'read committed'
                    OR OLD.id IS DISTINCT FROM NULLIF(current_setting('app.tenant_id',true),'')::uuid
                    OR NULLIF(current_setting('app.organization_id',true),'') IS NOT NULL THEN
                    RAISE EXCEPTION 'seed administration origin is invalid'
                        USING ERRCODE='23514', CONSTRAINT='ck_tenants_adopted_seed_origin_version';
                END IF;
                IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_locks AS held
                    WHERE held.pid=pg_catalog.pg_backend_pid()
                        AND held.database=(SELECT oid FROM pg_catalog.pg_database WHERE datname=pg_catalog.current_database())
                        AND held.locktype='advisory' AND held.granted AND held.mode='ExclusiveLock' AND held.objsubid=1
                        AND ((held.classid::bigint << 32) | held.objid::bigint)=declared_lock)
                    OR NOT EXISTS (SELECT 1 FROM public.organizations AS organization
                        WHERE organization.tenant_id=OLD.id AND organization.id=OLD.default_organization_id
                            AND organization.status='Active' AND organization.deleted_at IS NULL AND organization.deleted_by IS NULL)
                    OR EXISTS (SELECT 1 FROM public.tenant_locales WHERE tenant_id=OLD.id AND is_default AND NOT is_enabled)
                    OR (EXISTS (SELECT 1 FROM public.tenant_locales WHERE tenant_id=OLD.id AND is_enabled)
                        AND (SELECT count(*) FROM public.tenant_locales WHERE tenant_id=OLD.id AND is_default)<>1) THEN
                    RAISE EXCEPTION 'seed administration origin is invalid'
                        USING ERRCODE='23514', CONSTRAINT='ck_tenants_adopted_seed_origin_version';
                END IF;

                -- RLS keeps a foreign host invisible. Exact scope/flags are required;
                -- the row lock survives marker update and the central audit commit.
                SELECT mapping.host INTO mapping_host FROM public.platform_host_to_tenant AS mapping
                    WHERE mapping.host=declared_host AND mapping.tenant_id=OLD.id
                        AND mapping.organization_id IS NULL AND mapping.is_active AND mapping.is_publicly_live
                    FOR UPDATE;
                IF NOT FOUND THEN
                    RAISE EXCEPTION 'seed administration origin is invalid'
                        USING ERRCODE='23514', CONSTRAINT='ck_tenants_adopted_seed_origin_version';
                END IF;
                RETURN NEW;
            END;
            $guard$;
            REVOKE ALL ON FUNCTION public.fn_tenancy_seed_administration_origin_guard()
                FROM PUBLIC, learnstack_app, learnstack_platform, learnstack_outbox_admin;
            CREATE TRIGGER tg_tenants_seed_administration_origin_guard BEFORE INSERT OR UPDATE ON public.tenants
                FOR EACH ROW EXECUTE FUNCTION public.fn_tenancy_seed_administration_origin_guard();
            CREATE CONSTRAINT TRIGGER tg_tenants_seed_administration_origin_complete AFTER UPDATE ON public.tenants
                DEFERRABLE INITIALLY DEFERRED
                FOR EACH ROW EXECUTE FUNCTION public.fn_tenancy_seed_administration_origin_guard();
            """);
    }

    /// <summary>Forward-only replacement; the original installer remains historical marker-1 DDL.</summary>
    public static void InstallQualifiedCompletion(MigrationBuilder migration)
    {
        ArgumentNullException.ThrowIfNull(migration);
        // The finite SQL registry and application declaration share their authored pins.
        // Lock inspection uses the existing key family without an Identity table/function dependency.
        var declarations = string.Join(",", SeedAdministrationOriginManifest.All.Select(declaration =>
            $"('{declaration.TenantId.Value:D}'::uuid,'{declaration.TenantSlug}','{declaration.Host}',"
            + $"{IdentityAdvisoryLockKey.ForTenant(declaration.TenantId).ToString(CultureInfo.InvariantCulture)}::bigint)"));
        migration.Sql($$"""
            ALTER TABLE public.tenants DROP CONSTRAINT ck_tenants_adopted_seed_origin_version;
            ALTER TABLE public.tenants ADD CONSTRAINT ck_tenants_adopted_seed_origin_version
                CHECK (adopted_seed_origin_version IN (0,1,2));

            CREATE OR REPLACE FUNCTION public.fn_tenancy_seed_administration_origin_guard() RETURNS trigger
                LANGUAGE plpgsql VOLATILE SECURITY INVOKER SET search_path = pg_catalog
            AS $guard$
            DECLARE declared_slug text; declared_host text; declared_lock bigint; mapping_host text;
            BEGIN
                IF TG_TABLE_SCHEMA <> 'public' OR TG_TABLE_NAME <> 'tenants' THEN
                    RAISE EXCEPTION 'seed administration origin is invalid'
                        USING ERRCODE='23514', CONSTRAINT='ck_tenants_adopted_seed_origin_version';
                END IF;
                IF TG_OP='INSERT' THEN
                    IF NEW.adopted_seed_origin_version IS DISTINCT FROM 0 THEN
                        RAISE EXCEPTION 'seed administration origin is invalid'
                            USING ERRCODE='23514', CONSTRAINT='ck_tenants_adopted_seed_origin_version';
                    END IF;
                    RETURN NEW;
                END IF;
                IF NEW.adopted_seed_origin_version IS NOT DISTINCT FROM OLD.adopted_seed_origin_version THEN
                    RETURN NEW;
                END IF;

                SELECT declaration.slug,declaration.host,declaration.lock_key
                    INTO declared_slug,declared_host,declared_lock
                    FROM (VALUES {{declarations}}) AS declaration(tenant_id,slug,host,lock_key)
                    WHERE declaration.tenant_id=OLD.id;
                IF NOT FOUND OR OLD.adopted_seed_origin_version NOT IN (0,1) OR NEW.adopted_seed_origin_version<>2
                    OR NEW.id IS DISTINCT FROM OLD.id OR OLD.slug IS DISTINCT FROM declared_slug
                    OR NEW.slug IS DISTINCT FROM OLD.slug OR NEW.display_name IS DISTINCT FROM OLD.display_name
                    OR NEW.status IS DISTINCT FROM OLD.status OR OLD.status NOT IN ('Trial','Active','Suspended')
                    OR OLD.default_organization_id IS NULL
                    OR NEW.default_organization_id IS DISTINCT FROM OLD.default_organization_id
                    OR OLD.created_by<>'{{UserId.SystemActor.Value:D}}'::uuid
                    OR OLD.created_at<='0001-01-01 00:00:00+00'::timestamptz OR NOT isfinite(OLD.created_at)
                    OR NEW.created_at IS DISTINCT FROM OLD.created_at OR NEW.created_by IS DISTINCT FROM OLD.created_by
                    OR OLD.deleted_at IS NOT NULL OR OLD.deleted_by IS NOT NULL
                    OR NEW.deleted_at IS NOT NULL OR NEW.deleted_by IS NOT NULL
                    OR OLD.row_version<1 OR NEW.row_version<>OLD.row_version+1
                    OR NEW.updated_by IS DISTINCT FROM '{{UserId.SystemActor.Value:D}}'::uuid
                    OR NEW.updated_at IS NULL OR NEW.updated_at<='0001-01-01 00:00:00+00'::timestamptz
                    OR NOT isfinite(NEW.updated_at)
                    OR current_setting('transaction_isolation')<>'read committed'
                    OR OLD.id IS DISTINCT FROM NULLIF(current_setting('app.tenant_id',true),'')::uuid
                    OR NULLIF(current_setting('app.organization_id',true),'') IS NOT NULL THEN
                    RAISE EXCEPTION 'seed administration origin is invalid'
                        USING ERRCODE='23514', CONSTRAINT='ck_tenants_adopted_seed_origin_version';
                END IF;
                IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_locks AS held
                    WHERE held.pid=pg_catalog.pg_backend_pid()
                        AND held.database=(SELECT oid FROM pg_catalog.pg_database WHERE datname=pg_catalog.current_database())
                        AND held.locktype='advisory' AND held.granted AND held.mode='ExclusiveLock' AND held.objsubid=1
                        AND ((held.classid::bigint << 32) | held.objid::bigint)=declared_lock)
                    OR NOT EXISTS (SELECT 1 FROM public.organizations AS organization
                        WHERE organization.tenant_id=OLD.id AND organization.id=OLD.default_organization_id
                            AND organization.status='Active' AND organization.deleted_at IS NULL AND organization.deleted_by IS NULL)
                    OR EXISTS (SELECT 1 FROM public.tenant_locales WHERE tenant_id=OLD.id AND is_default AND NOT is_enabled)
                    OR (EXISTS (SELECT 1 FROM public.tenant_locales WHERE tenant_id=OLD.id AND is_enabled)
                        AND (SELECT count(*) FROM public.tenant_locales WHERE tenant_id=OLD.id AND is_default)<>1) THEN
                    RAISE EXCEPTION 'seed administration origin is invalid'
                        USING ERRCODE='23514', CONSTRAINT='ck_tenants_adopted_seed_origin_version';
                END IF;

                -- RLS keeps a foreign host invisible. Exact scope/flags are required;
                -- the row lock survives marker update and the central audit commit.
                SELECT mapping.host INTO mapping_host FROM public.platform_host_to_tenant AS mapping
                    WHERE mapping.host=declared_host AND mapping.tenant_id=OLD.id
                        AND mapping.organization_id IS NULL AND mapping.is_active AND mapping.is_publicly_live
                    FOR UPDATE;
                IF NOT FOUND THEN
                    RAISE EXCEPTION 'seed administration origin is invalid'
                        USING ERRCODE='23514', CONSTRAINT='ck_tenants_adopted_seed_origin_version';
                END IF;
                RETURN NEW;
            END;
            $guard$;
            REVOKE ALL ON FUNCTION public.fn_tenancy_seed_administration_origin_guard()
                FROM PUBLIC, learnstack_app, learnstack_platform, learnstack_outbox_admin;
            """);
    }
}
