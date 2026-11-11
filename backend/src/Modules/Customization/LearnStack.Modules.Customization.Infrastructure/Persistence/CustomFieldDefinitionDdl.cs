using Microsoft.EntityFrameworkCore.Migrations;

namespace LearnStack.Modules.Customization.Infrastructure.Persistence;

/// <summary>New table's isolation, retained revisions and immutable publication backstops.</summary>
public static class CustomFieldDefinitionDdl
{
    public static void Install(MigrationBuilder migration)
    {
        ArgumentNullException.ThrowIfNull(migration);
        migration.Sql("""
            ALTER TABLE public.tenant_custom_field_defs ENABLE ROW LEVEL SECURITY;
            ALTER TABLE public.tenant_custom_field_defs FORCE ROW LEVEL SECURITY;
            CREATE POLICY tenant_custom_field_defs_isolation ON public.tenant_custom_field_defs
                USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
            REVOKE ALL ON public.tenant_custom_field_defs FROM PUBLIC, learnstack_app, learnstack_platform, learnstack_outbox_admin;
            GRANT SELECT, INSERT, UPDATE ON public.tenant_custom_field_defs TO learnstack_app;

            CREATE FUNCTION public.fn_custom_field_definition_guard() RETURNS trigger
                LANGUAGE plpgsql VOLATILE SECURITY INVOKER SET search_path = pg_catalog
            AS $guard$
            BEGIN
                IF TG_TABLE_SCHEMA <> 'public' OR TG_TABLE_NAME <> 'tenant_custom_field_defs' OR TG_OP = 'DELETE' THEN
                    RAISE EXCEPTION 'custom field definition is invalid' USING ERRCODE='P0304', CONSTRAINT='custom_field_definition';
                END IF;
                PERFORM public.fn_identity_assert_tenant_fence(NEW.tenant_id, true);
                IF NULLIF(current_setting('app.organization_id',true),'') IS NOT NULL THEN
                    RAISE EXCEPTION 'custom field definition is invalid' USING ERRCODE='P0304', CONSTRAINT='custom_field_definition';
                END IF;
                IF TG_OP = 'INSERT' THEN
                    IF NEW.status <> 'Draft' OR NEW.deleted_at IS NOT NULL OR NEW.deleted_by IS NOT NULL THEN
                        RAISE EXCEPTION 'custom field definition is invalid' USING ERRCODE='P0304', CONSTRAINT='custom_field_definition';
                    END IF;
                ELSE
                    IF (NEW.id,NEW.tenant_id,NEW.target_entity,NEW.key,NEW.schema_version,NEW.created_at,NEW.created_by)
                        IS DISTINCT FROM (OLD.id,OLD.tenant_id,OLD.target_entity,OLD.key,OLD.schema_version,OLD.created_at,OLD.created_by)
                        OR OLD.deleted_at IS NOT NULL OR OLD.deleted_by IS NOT NULL
                        OR NEW.row_version <> OLD.row_version + 1
                        OR (NEW.deleted_at IS NULL) <> (NEW.deleted_by IS NULL)
                        OR NEW.deleted_at IS NOT NULL AND (OLD.status <> 'Draft' OR NEW.status <> 'Draft')
                        OR OLD.status = 'Deprecated'
                        OR OLD.status = 'Active' AND NEW.status <> 'Deprecated'
                        OR OLD.status = 'Draft' AND NEW.status NOT IN ('Draft','Active')
                        OR OLD.status <> 'Draft' AND (NEW.json_schema,NEW.labels_json,NEW.pii_category,NEW.is_required,NEW.sort,
                            NEW.purpose_key,NEW.notices_json,NEW.maximum_value_bytes,NEW.form_labels_json)
                            IS DISTINCT FROM (OLD.json_schema,OLD.labels_json,OLD.pii_category,OLD.is_required,OLD.sort,
                            OLD.purpose_key,OLD.notices_json,OLD.maximum_value_bytes,OLD.form_labels_json)
                        OR OLD.status = 'Draft' AND NEW.status = 'Active' AND (NEW.json_schema,NEW.labels_json,NEW.pii_category,NEW.is_required,NEW.sort,
                            NEW.purpose_key,NEW.notices_json,NEW.maximum_value_bytes,NEW.form_labels_json)
                            IS DISTINCT FROM (OLD.json_schema,OLD.labels_json,OLD.pii_category,OLD.is_required,OLD.sort,
                            OLD.purpose_key,OLD.notices_json,OLD.maximum_value_bytes,OLD.form_labels_json) THEN
                        RAISE EXCEPTION 'custom field definition is invalid' USING ERRCODE='P0304', CONSTRAINT='custom_field_definition';
                    END IF;
                END IF;
                RETURN NEW;
            END;
            $guard$;
            ALTER FUNCTION public.fn_custom_field_definition_guard() OWNER TO learnstack_migration;
            REVOKE ALL ON FUNCTION public.fn_custom_field_definition_guard() FROM PUBLIC, learnstack_app, learnstack_platform, learnstack_outbox_admin;
            CREATE TRIGGER tg_tenant_custom_field_defs_guard BEFORE INSERT OR UPDATE OR DELETE ON public.tenant_custom_field_defs
                FOR EACH ROW EXECUTE FUNCTION public.fn_custom_field_definition_guard();
            """);
    }

    public static void Remove(MigrationBuilder migration)
    {
        ArgumentNullException.ThrowIfNull(migration);
        migration.Sql("""
            DROP TRIGGER tg_tenant_custom_field_defs_guard ON public.tenant_custom_field_defs;
            DROP FUNCTION public.fn_custom_field_definition_guard();
            """);
    }
}
