using Microsoft.EntityFrameworkCore.Migrations;

namespace LearnStack.Infrastructure.Audit;

/// <summary>ADR-0072's exact migration-only witness installer; it owns all global audit SQL.</summary>
public static class IdentityCreationWitnessInstaller
{
    public static void Install(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.Sql(InstallSql);
    }

    public static void Remove(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.Sql("""
            DROP TRIGGER IF EXISTS tg_users_creation_audit ON public.users;
            DROP TRIGGER IF EXISTS tg_audit_log_identity_creation ON public.audit_log;
            DROP TRIGGER IF EXISTS tg_audit_log_identity_insert ON public.audit_log;
            DROP FUNCTION IF EXISTS public.fn_identity_user_creation_audit_guard();
            DROP FUNCTION IF EXISTS public.fn_identity_audit_creation_guard();
            DROP FUNCTION IF EXISTS public.fn_identity_audit_insert_guard();
            DROP FUNCTION IF EXISTS public.fn_identity_audit_evidence(uuid,timestamptz,uuid,bytea,uuid,text,text);
            ALTER POLICY audit_log_isolation ON public.audit_log
                USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
                  AND (organization_id IS NULL OR organization_id = NULLIF(current_setting('app.organization_id', true), '')::uuid
                    OR current_setting('app.scope', true) = 'tenant'))
                WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
                  AND ((organization_id IS NULL AND NULLIF(current_setting('app.organization_id', true), '') IS NULL)
                    OR organization_id = NULLIF(current_setting('app.organization_id', true), '')::uuid));
            """);
    }

    private const string InstallSql = """
        DO $$ BEGIN
            IF to_regclass('public.audit_log') IS NULL OR to_regclass('public.users') IS NULL THEN
                RAISE EXCEPTION 'Identity creation requires the existing Audit and User schema.' USING ERRCODE='23514';
            END IF;
        END $$;

        ALTER POLICY audit_log_isolation ON public.audit_log
        USING (
            (tenant_id = NULLIF(current_setting('app.tenant_id',true),'')::uuid
             AND (organization_id IS NULL OR organization_id = NULLIF(current_setting('app.organization_id',true),'')::uuid
                  OR current_setting('app.scope',true)='tenant'))
            OR (current_user='learnstack_migration'
                AND tenant_id='00000000-0000-7000-8000-000000000002'::uuid
                AND organization_id IS NULL AND module='identity' AND operation='identity.user.create'
                AND operation_class='Must' AND operation_type='SecurityEvent' AND entity_type='User'
                AND outcome IN ('success','failed','indeterminate')
                AND metadata->>'identity_source' IN ('identity-bootstrap','identity-seed-adoption')
                AND metadata->>'identity_authority_id'=NULLIF(current_setting('app.identity_authority_id',true),'')
                AND metadata->>'identity_subject_key'=NULLIF(current_setting('app.identity_subject_key',true),'')))
        WITH CHECK (
            (tenant_id = NULLIF(current_setting('app.tenant_id',true),'')::uuid
             AND ((organization_id IS NULL AND NULLIF(current_setting('app.organization_id',true),'') IS NULL)
                  OR organization_id = NULLIF(current_setting('app.organization_id',true),'')::uuid))
            OR (current_user='learnstack_app'
                AND tenant_id='00000000-0000-7000-8000-000000000002'::uuid
                AND organization_id IS NULL AND module='identity' AND operation='identity.user.create'
                AND operation_class='Must' AND operation_type='SecurityEvent' AND entity_type='User'
                AND outcome IN ('success','failed','indeterminate')
                AND metadata->>'identity_source' IN ('identity-bootstrap','identity-seed-adoption')
                AND metadata->>'identity_authority_id'=NULLIF(current_setting('app.identity_authority_id',true),'')
                AND metadata->>'identity_subject_key'=NULLIF(current_setting('app.identity_subject_key',true),'')));

        CREATE FUNCTION public.fn_identity_audit_insert_guard() RETURNS trigger
        LANGUAGE plpgsql SECURITY INVOKER SET search_path=pg_catalog AS $$
        BEGIN
            IF NEW.operation='identity.user.create'
               OR (NEW.tenant_id='00000000-0000-7000-8000-000000000002'::uuid AND current_user='learnstack_app') THEN
                IF current_user<>'learnstack_app'
                   OR NEW.tenant_id<>'00000000-0000-7000-8000-000000000002'::uuid
                   OR NEW.organization_id IS NOT NULL OR NEW.module<>'identity'
                   OR NEW.operation<>'identity.user.create' OR NEW.operation_class<>'Must'
                   OR NEW.operation_type<>'SecurityEvent' OR NEW.entity_type IS DISTINCT FROM 'User'
                   OR NEW.entity_id IS NULL OR NEW.entity_id !~ '^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$'
                   OR NEW.outcome NOT IN ('success','failed','indeterminate')
                   OR NEW.reason IS NOT NULL OR NEW.actor_email IS NOT NULL OR NEW.ip_address IS NOT NULL OR NEW.user_agent IS NOT NULL
                   OR NEW.metadata IS NULL OR jsonb_typeof(NEW.metadata)<>'object'
                   OR (NEW.metadata - ARRAY['identity_authority_id','identity_subject_key','identity_source'])<>'{}'::jsonb
                   OR NEW.metadata->>'identity_authority_id' IS DISTINCT FROM NULLIF(current_setting('app.identity_authority_id',true),'')
                   OR NEW.metadata->>'identity_subject_key' IS DISTINCT FROM NULLIF(current_setting('app.identity_subject_key',true),'')
                   OR COALESCE(NEW.metadata->>'identity_authority_id','') !~ '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
                   OR COALESCE(NEW.metadata->>'identity_subject_key','') !~ '^[0-9a-f]{64}$'
                   OR NOT COALESCE((NEW.metadata->>'identity_source'='identity-bootstrap' AND NEW.actor_user_id IS NULL)
                       OR (NEW.metadata->>'identity_source'='identity-seed-adoption'
                           AND NEW.actor_user_id='00000000-0000-7000-8000-000000000001'::uuid),false) THEN
                    RAISE EXCEPTION 'Global identity audit attribution is invalid.' USING ERRCODE='23514';
                END IF;
            END IF;
            RETURN NEW;
        END $$;
        REVOKE ALL ON FUNCTION public.fn_identity_audit_insert_guard() FROM PUBLIC;
        CREATE TRIGGER tg_audit_log_identity_insert BEFORE INSERT ON public.audit_log
            FOR EACH ROW EXECUTE FUNCTION public.fn_identity_audit_insert_guard();

        CREATE FUNCTION public.fn_identity_user_creation_audit_guard() RETURNS trigger
        LANGUAGE plpgsql SECURITY DEFINER VOLATILE SET search_path=pg_catalog AS $$
        BEGIN
            IF TG_TABLE_SCHEMA<>'public' OR TG_TABLE_NAME<>'users' OR TG_OP<>'INSERT'
               OR NEW.authority_id::text IS DISTINCT FROM NULLIF(current_setting('app.identity_authority_id',true),'')
               OR encode(NEW.subject_key,'hex') IS DISTINCT FROM NULLIF(current_setting('app.identity_subject_key',true),'')
               OR NOT EXISTS (SELECT 1 FROM public.audit_log a
                    WHERE a.id=NEW.creation_audit_id AND a.timestamp=NEW.creation_audit_timestamp
                      AND a.tenant_id='00000000-0000-7000-8000-000000000002'::uuid AND a.organization_id IS NULL
                      AND a.module='identity' AND a.operation='identity.user.create'
                      AND a.operation_class='Must' AND a.operation_type='SecurityEvent'
                      AND a.entity_type='User' AND a.entity_id=NEW.id::text AND a.outcome='success'
                      AND a.metadata->>'identity_authority_id'=NEW.authority_id::text
                      AND a.metadata->>'identity_subject_key'=encode(NEW.subject_key,'hex')
                      AND a.metadata->>'identity_source' IN ('identity-bootstrap','identity-seed-adoption')) THEN
                RAISE EXCEPTION 'User creation requires its matching durable audit.' USING ERRCODE='23514';
            END IF;
            RETURN NULL;
        END $$;
        REVOKE ALL ON FUNCTION public.fn_identity_user_creation_audit_guard() FROM PUBLIC;
        CREATE CONSTRAINT TRIGGER tg_users_creation_audit AFTER INSERT ON public.users
            DEFERRABLE INITIALLY DEFERRED FOR EACH ROW
            EXECUTE FUNCTION public.fn_identity_user_creation_audit_guard();

        CREATE FUNCTION public.fn_identity_audit_creation_guard() RETURNS trigger
        LANGUAGE plpgsql SECURITY DEFINER VOLATILE SET search_path=pg_catalog AS $$
        BEGIN
            IF NEW.operation<>'identity.user.create' THEN RETURN NULL; END IF;
            IF NEW.metadata->>'identity_authority_id' IS DISTINCT FROM NULLIF(current_setting('app.identity_authority_id',true),'')
               OR NEW.metadata->>'identity_subject_key' IS DISTINCT FROM NULLIF(current_setting('app.identity_subject_key',true),'')
               OR (NEW.outcome='success' AND NOT EXISTS (SELECT 1 FROM public.users u
                    WHERE u.id::text=NEW.entity_id AND u.creation_audit_id=NEW.id
                      AND u.creation_audit_timestamp=NEW.timestamp
                      AND u.authority_id::text=NEW.metadata->>'identity_authority_id'
                      AND encode(u.subject_key,'hex')=NEW.metadata->>'identity_subject_key')) THEN
                RAISE EXCEPTION 'Global creation audit requires its exact creation witness.' USING ERRCODE='23514';
            END IF;
            RETURN NULL;
        END $$;
        REVOKE ALL ON FUNCTION public.fn_identity_audit_creation_guard() FROM PUBLIC;
        CREATE CONSTRAINT TRIGGER tg_audit_log_identity_creation AFTER INSERT ON public.audit_log
            DEFERRABLE INITIALLY DEFERRED FOR EACH ROW
            EXECUTE FUNCTION public.fn_identity_audit_creation_guard();

        CREATE FUNCTION public.fn_identity_audit_evidence(p_id uuid,p_timestamp timestamptz,
            p_authority_id uuid,p_subject_key bytea,p_target_id uuid,p_source text,p_outcome text)
        RETURNS boolean LANGUAGE plpgsql SECURITY DEFINER VOLATILE SET search_path=pg_catalog AS $$
        BEGIN
            IF p_authority_id::text IS DISTINCT FROM NULLIF(current_setting('app.identity_authority_id',true),'')
               OR encode(p_subject_key,'hex') IS DISTINCT FROM NULLIF(current_setting('app.identity_subject_key',true),'')
               OR octet_length(p_subject_key) IS DISTINCT FROM 32
               OR p_source IS NULL OR p_source NOT IN ('identity-bootstrap','identity-seed-adoption')
               OR p_outcome IS NULL OR p_outcome NOT IN ('success','failed','indeterminate') THEN RETURN false; END IF;
            RETURN EXISTS (SELECT 1 FROM public.audit_log a
                WHERE a.id=p_id AND a.timestamp=p_timestamp
                  AND a.tenant_id='00000000-0000-7000-8000-000000000002'::uuid AND a.organization_id IS NULL
                  AND a.module='identity' AND a.operation='identity.user.create'
                  AND a.operation_class='Must' AND a.operation_type='SecurityEvent'
                  AND a.entity_type='User' AND a.entity_id=p_target_id::text AND a.outcome=p_outcome
                  AND a.metadata->>'identity_authority_id'=p_authority_id::text
                  AND a.metadata->>'identity_subject_key'=encode(p_subject_key,'hex')
                  AND a.metadata->>'identity_source'=p_source
                  AND ((p_source='identity-bootstrap' AND a.actor_user_id IS NULL)
                    OR (p_source='identity-seed-adoption' AND a.actor_user_id='00000000-0000-7000-8000-000000000001'::uuid))
                  AND (p_outcome<>'success' OR EXISTS (SELECT 1 FROM public.users u
                    WHERE u.id=p_target_id AND u.authority_id=p_authority_id AND u.subject_key=p_subject_key
                      AND u.creation_audit_id=p_id AND u.creation_audit_timestamp=p_timestamp)));
        END $$;
        REVOKE ALL ON FUNCTION public.fn_identity_audit_evidence(uuid,timestamptz,uuid,bytea,uuid,text,text) FROM PUBLIC;
        GRANT EXECUTE ON FUNCTION public.fn_identity_audit_evidence(uuid,timestamptz,uuid,bytea,uuid,text,text) TO learnstack_app;
        """;
}
