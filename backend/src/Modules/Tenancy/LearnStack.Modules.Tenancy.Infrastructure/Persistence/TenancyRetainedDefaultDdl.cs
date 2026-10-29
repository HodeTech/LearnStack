using Microsoft.EntityFrameworkCore.Migrations;

namespace LearnStack.Modules.Tenancy.Infrastructure.Persistence;

/// <summary>Forward correction: retained incomplete roots cannot manufacture INSERT-first initiality.</summary>
public static class TenancyRetainedDefaultDdl
{
    public static void Install(MigrationBuilder migration)
    {
        ArgumentNullException.ThrowIfNull(migration);
        migration.Sql("""
            -- The migration owner is itself subject to FORCE RLS. These DDL locks
            -- exclude concurrent table use until this transaction restores RLS and
            -- commits; no runtime connection observes an unprotected relation.
            ALTER TABLE public.tenants DISABLE ROW LEVEL SECURITY;
            ALTER TABLE public.organizations DISABLE ROW LEVEL SECURITY;
            -- An established default is not repaired or silently replaced by deployment.
            DO $preflight$
            BEGIN
                IF EXISTS (SELECT 1 FROM public.tenants AS tenant
                    WHERE tenant.deleted_at IS NULL AND tenant.status IN ('Trial','Active','Suspended')
                      AND tenant.default_organization_id IS NOT NULL
                      AND NOT EXISTS (SELECT 1 FROM public.organizations AS organization
                        WHERE organization.tenant_id=tenant.id AND organization.id=tenant.default_organization_id
                          AND organization.status='Active' AND organization.deleted_at IS NULL AND organization.deleted_by IS NULL)) THEN
                    RAISE EXCEPTION 'tenancy default graph is invalid'
                        USING ERRCODE='23514', CONSTRAINT='tenancy_default_organization';
                END IF;
            END;
            $preflight$;
            ALTER TABLE public.tenants ENABLE ROW LEVEL SECURITY;
            ALTER TABLE public.tenants FORCE ROW LEVEL SECURITY;
            ALTER TABLE public.organizations ENABLE ROW LEVEL SECURITY;
            ALTER TABLE public.organizations FORCE ROW LEVEL SECURITY;

            CREATE OR REPLACE FUNCTION public.fn_tenancy_administration_guard() RETURNS trigger
                LANGUAGE plpgsql VOLATILE SECURITY INVOKER SET search_path = pg_catalog
            AS $guard$
            DECLARE target_tenant uuid; tenant_key bigint; loses_active boolean; initial_default boolean;
            BEGIN
                IF TG_TABLE_SCHEMA<>'public' OR TG_TABLE_NAME NOT IN ('tenants','organizations') THEN
                    RAISE EXCEPTION 'tenancy administration plan is invalid' USING ERRCODE='P0311';
                END IF;
                IF TG_TABLE_NAME='tenants' THEN
                    target_tenant:=OLD.id;
                    -- ADR-0042 completes the untouched creation shape in its INSERT-first
                    -- transaction. An ordinary update of retained state cannot borrow it.
                    initial_default:=OLD.default_organization_id IS NULL
                        AND OLD.row_version=0 AND OLD.updated_at IS NULL AND OLD.updated_by IS NULL
                        AND EXISTS (SELECT 1 FROM public.tenants AS tenant
                            WHERE tenant.id=OLD.id AND tenant.xmin=pg_current_xact_id()::text::xid);
                    -- xmin proves tuple creation only after every retained NULL-default
                    -- update is fenced, including unchanged-default no-ops and stamp resets.
                    -- A retained root cannot manufacture this exception with a prior UPDATE.
                    IF NEW.default_organization_id IS NOT DISTINCT FROM OLD.default_organization_id
                        AND (OLD.default_organization_id IS NOT NULL OR initial_default) THEN RETURN NEW; END IF;
                ELSE
                    IF TG_OP='UPDATE' THEN
                        IF NEW.status IS NOT DISTINCT FROM OLD.status
                            AND NEW.deleted_at IS NOT DISTINCT FROM OLD.deleted_at
                            AND NEW.deleted_by IS NOT DISTINCT FROM OLD.deleted_by THEN RETURN NEW; END IF;
                        IF OLD.status='Archived' AND NEW.status IS DISTINCT FROM OLD.status THEN
                            RAISE EXCEPTION 'tenancy lifecycle transition is invalid' USING ERRCODE='P0312';
                        END IF;
                    END IF;
                    target_tenant:=OLD.tenant_id;
                    initial_default:=false;
                END IF;

                IF NOT initial_default THEN
                    -- Canonical bigint Tenant key, independently checked against the
                    -- SharedKernel C# vectors. No Identity SQL/function dependency.
                    tenant_key:=('x'||substr(encode(sha256(
                        int4send(octet_length(convert_to('learnstack.identity.lock.v1','UTF8')))
                        ||convert_to('learnstack.identity.lock.v1','UTF8')
                        ||int4send(6)||convert_to('tenant','UTF8')
                        ||int4send(16)||uuid_send(target_tenant)),'hex'),1,16))::bit(64)::bigint;
                    IF current_setting('transaction_isolation')<>'read committed'
                        OR target_tenant IS DISTINCT FROM NULLIF(current_setting('app.tenant_id',true),'')::uuid
                        OR NOT EXISTS (SELECT 1 FROM pg_catalog.pg_locks AS held
                            WHERE held.pid=pg_catalog.pg_backend_pid()
                              AND held.database=(SELECT oid FROM pg_catalog.pg_database WHERE datname=pg_catalog.current_database())
                              AND held.locktype='advisory' AND held.granted AND held.mode='ExclusiveLock' AND held.objsubid=1
                              AND ((held.classid::bigint<<32)|held.objid::bigint)=tenant_key) THEN
                        RAISE EXCEPTION 'tenancy administration plan is invalid' USING ERRCODE='P0311';
                    END IF;
                END IF;

                IF TG_TABLE_NAME='tenants' THEN
                    -- Retained incomplete state may remain NULL after a properly fenced
                    -- update; the creation exception cannot be forged without that fence.
                    IF NEW.default_organization_id IS NOT DISTINCT FROM OLD.default_organization_id THEN RETURN NEW; END IF;
                    IF NEW.default_organization_id IS NULL OR NOT EXISTS (
                        SELECT 1 FROM public.organizations AS organization
                        WHERE organization.tenant_id=target_tenant AND organization.id=NEW.default_organization_id
                          AND organization.status='Active' AND organization.deleted_at IS NULL AND organization.deleted_by IS NULL) THEN
                        RAISE EXCEPTION 'tenancy default graph is invalid' USING ERRCODE='P0312';
                    END IF;
                    RETURN NEW;
                END IF;

                loses_active:=OLD.status='Active' AND OLD.deleted_at IS NULL AND OLD.deleted_by IS NULL;
                IF TG_OP='UPDATE' THEN
                    loses_active:=loses_active AND (NEW.status<>'Active' OR NEW.deleted_at IS NOT NULL OR NEW.deleted_by IS NOT NULL);
                END IF;
                IF EXISTS (SELECT 1 FROM public.tenants AS tenant
                    WHERE tenant.id=target_tenant AND tenant.deleted_at IS NULL AND tenant.status IN ('Trial','Active','Suspended')
                      AND tenant.default_organization_id=OLD.id)
                    AND (TG_OP='DELETE' OR NEW.status<>'Active' OR NEW.deleted_at IS NOT NULL OR NEW.deleted_by IS NOT NULL) THEN
                    RAISE EXCEPTION 'tenancy default graph is invalid' USING ERRCODE='P0312';
                END IF;
                IF loses_active AND NOT EXISTS (SELECT 1 FROM public.organizations AS organization
                    WHERE organization.tenant_id=target_tenant AND organization.id<>OLD.id
                      AND organization.status='Active' AND organization.deleted_at IS NULL AND organization.deleted_by IS NULL) THEN
                    RAISE EXCEPTION 'tenancy lifecycle transition is invalid' USING ERRCODE='P0312';
                END IF;
                IF TG_OP='DELETE' THEN RETURN OLD; END IF;
                RETURN NEW;
            END;
            $guard$;
            REVOKE ALL ON FUNCTION public.fn_tenancy_administration_guard()
                FROM PUBLIC, learnstack_app, learnstack_platform, learnstack_outbox_admin;
            """);
    }
}
