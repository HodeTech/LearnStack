using Microsoft.EntityFrameworkCore.Migrations;

namespace LearnStack.Modules.Tenancy.Infrastructure.Persistence;

/// <summary>Database isolation and immutable-history backstops for consent evidence.</summary>
public static class MembershipFieldConsentDdl
{
    public static void Install(MigrationBuilder migration)
    {
        ArgumentNullException.ThrowIfNull(migration);
        migration.Sql("""
            ALTER TABLE public.membership_field_consents ENABLE ROW LEVEL SECURITY;
            ALTER TABLE public.membership_field_consents FORCE ROW LEVEL SECURITY;
            -- One permissive policy includes the confined definer owner's SELECT
            -- exception. WITH CHECK admits only ordinary app writes.
            CREATE POLICY membership_field_consents_isolation ON public.membership_field_consents
              USING (current_user = 'learnstack_migration' OR
                (current_user = 'learnstack_app'
                 AND tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
                 AND (organization_id IS NULL OR organization_id = NULLIF(current_setting('app.organization_id', true), '')::uuid
                   OR current_setting('app.scope', true) = 'tenant')))
              WITH CHECK (current_user = 'learnstack_app'
                AND tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
                AND ((organization_id IS NULL AND NULLIF(current_setting('app.organization_id', true), '') IS NULL)
                  OR organization_id = NULLIF(current_setting('app.organization_id', true), '')::uuid));
            CREATE POLICY membership_field_consents_org_write_guard ON public.membership_field_consents
              AS RESTRICTIVE FOR UPDATE
              USING ((organization_id IS NULL AND NULLIF(current_setting('app.organization_id', true), '') IS NULL)
                OR organization_id = NULLIF(current_setting('app.organization_id', true), '')::uuid);
            CREATE POLICY membership_field_consents_org_delete_guard ON public.membership_field_consents
              AS RESTRICTIVE FOR DELETE
              USING ((organization_id IS NULL AND NULLIF(current_setting('app.organization_id', true), '') IS NULL)
                OR organization_id = NULLIF(current_setting('app.organization_id', true), '')::uuid);
            REVOKE ALL ON public.membership_field_consents FROM PUBLIC, learnstack_app, learnstack_platform, learnstack_outbox_admin;
            GRANT SELECT, INSERT ON public.membership_field_consents TO learnstack_app;

            CREATE FUNCTION public.fn_tenancy_field_consent_guard() RETURNS trigger
              LANGUAGE plpgsql SECURITY INVOKER SET search_path = pg_catalog AS $guard$
            DECLARE prior record; latest_grant timestamptz;
            BEGIN
              IF TG_TABLE_SCHEMA <> 'public' OR TG_TABLE_NAME <> 'membership_field_consents' THEN
                RAISE EXCEPTION 'consent trigger target is invalid' USING ERRCODE='P0321';
              END IF;
              IF TG_OP <> 'INSERT' THEN
                RAISE EXCEPTION 'consent evidence is immutable' USING ERRCODE='P0320';
              END IF;
              IF NEW.tenant_id IS NULL OR NEW.tenant_id IN (
                   '00000000-0000-0000-0000-000000000000'::uuid,
                   '00000000-0000-7000-8000-000000000002'::uuid)
                OR NEW.subject_user_id IS NULL OR NEW.subject_user_id IN (
                   '00000000-0000-0000-0000-000000000000'::uuid,
                   '00000000-0000-7000-8000-000000000001'::uuid)
                OR NEW.actor_user_id IS DISTINCT FROM NEW.subject_user_id THEN
                RAISE EXCEPTION 'consent subject is invalid' USING ERRCODE='P0321';
              END IF;
              PERFORM public.fn_identity_assert_tenant_fence(NEW.tenant_id,true);
              SELECT c.id, c.sequence, c.state, c.notice_locale, c.notice_hash INTO prior
                FROM public.membership_field_consents c
                WHERE c.tenant_id=NEW.tenant_id AND c.organization_id IS NOT DISTINCT FROM NEW.organization_id
                  AND c.membership_id=NEW.membership_id AND c.subject_user_id=NEW.subject_user_id
                  AND c.definition_id=NEW.definition_id AND c.definition_version=NEW.definition_version
                  AND c.purpose_key=NEW.purpose_key ORDER BY c.sequence DESC LIMIT 1;
              IF NOT FOUND THEN
                IF NEW.sequence<>1 OR NEW.predecessor_sequence<>0 OR NEW.predecessor_id IS NOT NULL OR NEW.state<>'Granted' THEN
                  RAISE EXCEPTION 'consent predecessor is invalid' USING ERRCODE='P0322';
                END IF;
              ELSE
                IF NEW.sequence<>prior.sequence+1 OR NEW.predecessor_sequence<>prior.sequence
                  OR NEW.predecessor_id IS DISTINCT FROM prior.id OR NEW.state=prior.state
                  OR (NEW.state='Withdrawn' AND (NEW.notice_locale IS DISTINCT FROM prior.notice_locale
                    OR NEW.notice_hash IS DISTINCT FROM prior.notice_hash)) THEN
                  RAISE EXCEPTION 'consent predecessor is invalid' USING ERRCODE='P0322';
                END IF;
              END IF;
              -- The database clock owns durable cooldown evidence, independent of client clocks.
              NEW.created_at := clock_timestamp();
              IF NEW.state='Granted' THEN
                SELECT c.created_at INTO latest_grant FROM public.membership_field_consents c
                  WHERE c.tenant_id=NEW.tenant_id AND c.organization_id IS NOT DISTINCT FROM NEW.organization_id
                    AND c.membership_id=NEW.membership_id AND c.subject_user_id=NEW.subject_user_id
                    AND c.definition_id=NEW.definition_id AND c.definition_version=NEW.definition_version
                    AND c.purpose_key=NEW.purpose_key AND c.state='Granted'
                  ORDER BY c.sequence DESC LIMIT 1;
                IF latest_grant IS NOT NULL AND NEW.created_at < latest_grant + interval '60 seconds' THEN
                  RAISE EXCEPTION 'consent grant cooldown' USING ERRCODE='P0323';
                END IF;
              END IF;
              RETURN NEW;
            END $guard$;
            REVOKE ALL ON FUNCTION public.fn_tenancy_field_consent_guard() FROM PUBLIC, learnstack_app, learnstack_platform, learnstack_outbox_admin;
            CREATE TRIGGER tg_membership_field_consents_organization_id_immutable BEFORE UPDATE ON public.membership_field_consents
              FOR EACH ROW EXECUTE FUNCTION public.fn_organization_id_immutable();
            CREATE TRIGGER tg_membership_field_consents_insert BEFORE INSERT ON public.membership_field_consents
              FOR EACH ROW EXECUTE FUNCTION public.fn_tenancy_field_consent_guard();
            CREATE TRIGGER tg_membership_field_consents_immutable BEFORE UPDATE OR DELETE OR TRUNCATE ON public.membership_field_consents
              FOR EACH STATEMENT EXECUTE FUNCTION public.fn_tenancy_field_consent_guard();

            CREATE FUNCTION public.fn_tenancy_own_field_consent_scope(
              p_actor uuid, p_host_tenant uuid, p_host_organization uuid, p_mode text,
              p_membership_id uuid, p_after_membership uuid, p_limit integer)
              RETURNS TABLE(membership_id uuid, tenant_id uuid, organization_id uuid)
              LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = pg_catalog AS $scope$
            BEGIN
              IF p_actor IS NULL OR p_host_tenant IS NULL OR p_actor='00000000-0000-0000-0000-000000000000'::uuid
                OR p_actor='00000000-0000-7000-8000-000000000001'::uuid
                OR p_host_tenant IN ('00000000-0000-0000-0000-000000000000'::uuid,
                    '00000000-0000-7000-8000-000000000002'::uuid)
                OR p_host_organization='00000000-0000-0000-0000-000000000000'::uuid
                OR p_membership_id='00000000-0000-0000-0000-000000000000'::uuid
                OR p_after_membership='00000000-0000-0000-0000-000000000000'::uuid
                OR p_limit IS NULL OR p_limit<1 OR p_limit>20
                OR p_mode IS NULL OR p_mode NOT IN ('list','select')
                OR (p_mode='list' AND p_membership_id IS NOT NULL)
                OR (p_mode='select' AND (p_membership_id IS NULL OR p_after_membership IS NOT NULL OR p_limit<>1)) THEN
                RAISE EXCEPTION 'consent scope request is invalid' USING ERRCODE='P0324';
              END IF;
              RETURN QUERY SELECT DISTINCT c.membership_id, c.tenant_id, c.organization_id
                FROM public.membership_field_consents c
                WHERE c.subject_user_id=p_actor AND c.tenant_id=p_host_tenant
                  AND (p_host_organization IS NULL OR c.organization_id=p_host_organization)
                  AND ((p_mode='select' AND c.membership_id=p_membership_id)
                    OR (p_mode='list' AND (p_after_membership IS NULL OR c.membership_id>p_after_membership)))
                ORDER BY c.membership_id LIMIT p_limit;
            END $scope$;
            ALTER FUNCTION public.fn_tenancy_own_field_consent_scope(uuid,uuid,uuid,text,uuid,uuid,integer) OWNER TO learnstack_migration;
            REVOKE ALL ON FUNCTION public.fn_tenancy_own_field_consent_scope(uuid,uuid,uuid,text,uuid,uuid,integer) FROM PUBLIC;
            GRANT EXECUTE ON FUNCTION public.fn_tenancy_own_field_consent_scope(uuid,uuid,uuid,text,uuid,uuid,integer) TO learnstack_app;
            """);
    }

    public static void Remove(MigrationBuilder migration)
    {
        ArgumentNullException.ThrowIfNull(migration);
        migration.Sql("""
            DROP FUNCTION IF EXISTS public.fn_tenancy_own_field_consent_scope(uuid,uuid,uuid,text,uuid,uuid,integer);
            DROP TRIGGER IF EXISTS tg_membership_field_consents_organization_id_immutable ON public.membership_field_consents;
            DROP TRIGGER IF EXISTS tg_membership_field_consents_insert ON public.membership_field_consents;
            DROP TRIGGER IF EXISTS tg_membership_field_consents_immutable ON public.membership_field_consents;
            DROP FUNCTION IF EXISTS public.fn_tenancy_field_consent_guard();
            DROP POLICY IF EXISTS membership_field_consents_org_write_guard ON public.membership_field_consents;
            DROP POLICY IF EXISTS membership_field_consents_org_delete_guard ON public.membership_field_consents;
            DROP POLICY IF EXISTS membership_field_consents_isolation ON public.membership_field_consents;
            """);
    }
}
