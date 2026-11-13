using Microsoft.EntityFrameworkCore.Migrations;

namespace LearnStack.Modules.Identity.Infrastructure.Persistence;

/// <summary>
/// ADR-0099 connection registry: canonical tenant-only RLS (Database Standards § Tenant-Owned and
/// Organization-Scoped Tables), application grants without DELETE, and a guard that enforces the
/// lifecycle under the Identity tenant fence. Deleted is a terminal tombstone (ADR-0099 Amendment 1).
/// </summary>
internal static class FederationConnectionStorageDdl
{
    internal static void Install(MigrationBuilder migration)
    {
        ArgumentNullException.ThrowIfNull(migration);
        migration.Sql("""
            ALTER TABLE public.federation_connections ENABLE ROW LEVEL SECURITY;
            ALTER TABLE public.federation_connections FORCE ROW LEVEL SECURITY;
            CREATE POLICY federation_connections_isolation ON public.federation_connections
                USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
            REVOKE ALL ON public.federation_connections FROM PUBLIC, learnstack_app, learnstack_platform, learnstack_outbox_admin;
            GRANT SELECT, INSERT, UPDATE ON public.federation_connections TO learnstack_app;

            CREATE FUNCTION public.fn_identity_federation_connection_guard() RETURNS trigger
                LANGUAGE plpgsql VOLATILE SECURITY INVOKER SET search_path = pg_catalog AS $guard$
            BEGIN
                IF session_user <> 'learnstack_app' OR current_user <> 'learnstack_app'
                    OR TG_TABLE_SCHEMA <> 'public' OR TG_TABLE_NAME <> 'federation_connections'
                    OR TG_OP NOT IN ('INSERT','UPDATE')
                THEN RAISE EXCEPTION 'Federation connection mutation refused.' USING ERRCODE = '23514'; END IF;
                PERFORM public.fn_identity_assert_tenant_fence(NEW.tenant_id, false);
                IF TG_OP = 'INSERT' THEN
                    IF NEW.state <> 'Draft' OR NEW.configuration_revision <> 1 OR NEW.row_version <> 0
                        OR NEW.updated_at IS NOT NULL OR NEW.updated_by IS NOT NULL
                        OR NEW.pending_operation_kind IS DISTINCT FROM 'Configure'
                        OR NEW.pending_actor IS DISTINCT FROM NEW.created_by
                        OR NEW.pending_started_at IS DISTINCT FROM NEW.created_at
                        OR NEW.pending_desired_digest IS DISTINCT FROM NEW.desired_digest
                        OR NEW.last_provider_digest IS NOT NULL
                    THEN RAISE EXCEPTION 'Federation connection initial state refused.' USING ERRCODE = '23514'; END IF;
                    RETURN NEW;
                END IF;
                IF OLD.state = 'Deleted' THEN
                    RAISE EXCEPTION 'Federation connection tombstone is terminal.' USING ERRCODE = '23514';
                END IF;
                -- The alias, issuer or entity identifier and protocol are fixed for the connection's life.
                IF ROW(NEW.id, NEW.tenant_id, NEW.protocol, NEW.alias, NEW.issuer_or_entity_id, NEW.created_at, NEW.created_by)
                    IS DISTINCT FROM ROW(OLD.id, OLD.tenant_id, OLD.protocol, OLD.alias, OLD.issuer_or_entity_id, OLD.created_at, OLD.created_by)
                    OR NEW.row_version IS DISTINCT FROM OLD.row_version + 1
                    OR NEW.updated_at IS NULL OR NEW.updated_by IS NULL OR NOT isfinite(NEW.updated_at)
                    OR NEW.updated_at < COALESCE(OLD.updated_at, OLD.created_at)
                THEN RAISE EXCEPTION 'Federation connection immutable state refused.' USING ERRCODE = '23514'; END IF;
                IF NEW.configuration_revision IS DISTINCT FROM OLD.configuration_revision
                    AND (NEW.configuration_revision <> OLD.configuration_revision + 1 OR NEW.state <> 'Draft'
                        OR OLD.pending_operation_kind IS NOT NULL
                        OR NEW.pending_operation_kind NOT IN ('Configure','RotateSecret'))
                THEN RAISE EXCEPTION 'Federation configuration revision refused.' USING ERRCODE = '23514'; END IF;
                -- A new pending operation starts only when none is unsettled.
                IF NEW.pending_operation_id IS NOT NULL AND OLD.pending_operation_id IS NOT NULL
                    AND NEW.pending_operation_id IS DISTINCT FROM OLD.pending_operation_id
                THEN RAISE EXCEPTION 'Federation provider write is already pending.' USING ERRCODE = '23514'; END IF;
                IF NEW.state IS DISTINCT FROM OLD.state AND NOT (
                    (OLD.state = 'Draft' AND NEW.state = 'TestOpen'
                        AND OLD.pending_operation_kind = 'TestStart' AND NEW.pending_operation_id IS NULL)
                    OR (OLD.state = 'TestOpen' AND NEW.state = 'TestPassed' AND NEW.pending_operation_id IS NULL)
                    OR (OLD.state = 'TestPassed' AND NEW.state = 'Active' AND NEW.pending_operation_id IS NULL
                        AND OLD.pending_operation_id IS NULL)
                    OR (OLD.state IN ('TestOpen','TestPassed','Active','Disabled') AND NEW.state = 'Draft')
                    OR (OLD.state = 'Active' AND NEW.state = 'Disabled')
                    OR (NEW.state = 'Deleted' AND OLD.pending_operation_kind = 'Delete' AND NEW.pending_operation_id IS NULL))
                THEN RAISE EXCEPTION 'Federation connection transition refused.' USING ERRCODE = '23514'; END IF;
                RETURN NEW;
            END $guard$;
            REVOKE ALL ON FUNCTION public.fn_identity_federation_connection_guard() FROM PUBLIC;
            CREATE TRIGGER tg_federation_connections_mutation_guard BEFORE INSERT OR UPDATE ON public.federation_connections
                FOR EACH ROW EXECUTE FUNCTION public.fn_identity_federation_connection_guard();
            """);
    }
}
