using Microsoft.EntityFrameworkCore.Migrations;

namespace LearnStack.Modules.Identity.Infrastructure.Persistence;

/// <summary>P03-3's three confined functions and nonpersonal authority budget; no tenant/audit writer.</summary>
internal static class IdentityEntryStorageDdl
{
    internal static void Install(MigrationBuilder migration) => migration.Sql("""
        -- Freeze User INSERT while installing the initial count and its guard.
        -- Otherwise an old writer could commit between the census and trigger.
        LOCK TABLE public.users IN SHARE ROW EXCLUSIVE MODE;
        CREATE TABLE public.identity_creation_capacity (
            authority_id uuid NOT NULL,
            minute_window timestamptz NOT NULL,
            minute_attempts integer NOT NULL DEFAULT 0,
            day_window timestamptz NOT NULL,
            day_attempts integer NOT NULL DEFAULT 0,
            allocated_users bigint NOT NULL,
            CONSTRAINT pk_identity_creation_capacity PRIMARY KEY (authority_id),
            CONSTRAINT fk_identity_creation_capacity_authority FOREIGN KEY (authority_id)
                REFERENCES public.identity_authority(authority_id) ON DELETE RESTRICT,
            CONSTRAINT ck_identity_creation_capacity_counts CHECK (
                minute_attempts BETWEEN 0 AND 10 AND day_attempts BETWEEN 0 AND 100 AND allocated_users >= 0)
        );
        ALTER TABLE public.identity_creation_capacity ENABLE ROW LEVEL SECURITY;
        ALTER TABLE public.identity_creation_capacity FORCE ROW LEVEL SECURITY;
        CREATE POLICY identity_creation_capacity_owner ON public.identity_creation_capacity
            USING (current_user='learnstack_migration')
            WITH CHECK (current_user='learnstack_migration');
        REVOKE ALL ON public.identity_creation_capacity FROM PUBLIC,learnstack_app,learnstack_platform,learnstack_outbox_admin;
        INSERT INTO public.identity_creation_capacity
            (authority_id, minute_window, day_window, allocated_users)
            SELECT a.authority_id,
                date_trunc('minute', clock_timestamp() AT TIME ZONE 'UTC') AT TIME ZONE 'UTC',
                date_trunc('day', clock_timestamp() AT TIME ZONE 'UTC') AT TIME ZONE 'UTC',
                (SELECT count(*) FROM public.users u WHERE u.authority_id=a.authority_id)
            FROM public.identity_authority a;

        CREATE FUNCTION public.fn_identity_creation_attempt_admission()
            RETURNS TABLE (admitted boolean, retry_after_seconds integer)
            LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $attempt$
        DECLARE
            selected_authority uuid := NULLIF(current_setting('app.identity_authority_id',true),'')::uuid;
            selected_subject text := NULLIF(current_setting('app.identity_subject_key',true),'');
            capacity public.identity_creation_capacity%ROWTYPE;
            instant timestamptz;
            minute_start timestamptz;
            day_start timestamptz;
            next_window timestamptz;
        BEGIN
            IF session_user <> 'learnstack_app' OR selected_authority IS NULL
                OR selected_subject IS NULL OR selected_subject !~ '^[0-9a-f]{64}$'
                OR NULLIF(current_setting('app.tenant_id',true),'') IS NOT NULL
                OR current_setting('transaction_isolation') <> 'read committed'
                OR current_setting('transaction_read_only') <> 'off'
            THEN RAISE EXCEPTION 'Identity creation admission refused.' USING ERRCODE='23514'; END IF;
            SELECT * INTO capacity FROM public.identity_creation_capacity
                WHERE authority_id=selected_authority FOR UPDATE;
            IF NOT FOUND THEN
                RAISE EXCEPTION 'Identity creation admission unavailable.' USING ERRCODE='23514';
            END IF;
            instant := clock_timestamp();
            minute_start := date_trunc('minute',instant AT TIME ZONE 'UTC') AT TIME ZONE 'UTC';
            day_start := date_trunc('day',instant AT TIME ZONE 'UTC') AT TIME ZONE 'UTC';
            IF minute_start > capacity.minute_window THEN
                capacity.minute_window := minute_start; capacity.minute_attempts := 0;
            END IF;
            IF day_start > capacity.day_window THEN
                capacity.day_window := day_start; capacity.day_attempts := 0;
            END IF;
            IF capacity.minute_attempts >= 10 OR capacity.day_attempts >= 100 THEN
                IF capacity.minute_attempts >= 10 THEN
                    next_window := capacity.minute_window + interval '1 minute';
                END IF;
                IF capacity.day_attempts >= 100 THEN
                    next_window := GREATEST(next_window,capacity.day_window + interval '1 day');
                END IF;
                RETURN QUERY SELECT false,
                    LEAST(86400,GREATEST(1,ceil(extract(epoch FROM next_window-instant))))::integer;
                RETURN;
            END IF;
            UPDATE public.identity_creation_capacity SET
                minute_window=capacity.minute_window, minute_attempts=capacity.minute_attempts+1,
                day_window=capacity.day_window, day_attempts=capacity.day_attempts+1
                WHERE authority_id=selected_authority;
            RETURN QUERY SELECT true,NULL::integer;
        END $attempt$;
        REVOKE ALL ON FUNCTION public.fn_identity_creation_attempt_admission() FROM PUBLIC;
        GRANT EXECUTE ON FUNCTION public.fn_identity_creation_attempt_admission() TO learnstack_app;

        CREATE FUNCTION public.fn_identity_user_capacity_guard() RETURNS trigger
            LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $capacity$
        DECLARE allocated bigint;
        BEGIN
            IF TG_TABLE_SCHEMA <> 'public' OR TG_TABLE_NAME <> 'users' OR TG_OP <> 'INSERT'
                OR TG_WHEN NOT IN ('BEFORE','AFTER') OR session_user <> 'learnstack_app'
                OR NEW.authority_id::text IS DISTINCT FROM NULLIF(current_setting('app.identity_authority_id',true),'')
                OR encode(NEW.subject_key,'hex') IS DISTINCT FROM NULLIF(current_setting('app.identity_subject_key',true),'')
                OR NULLIF(current_setting('app.tenant_id',true),'') IS NOT NULL
                OR current_setting('transaction_isolation') <> 'read committed'
            THEN RAISE EXCEPTION 'Identity allocation refused.' USING ERRCODE='23514'; END IF;
            SELECT allocated_users INTO allocated FROM public.identity_creation_capacity
                WHERE authority_id=NEW.authority_id FOR UPDATE;
            IF NOT FOUND OR allocated >= 10000 THEN
                RAISE EXCEPTION 'Identity allocation unavailable.' USING ERRCODE='23514',
                    CONSTRAINT='ck_identity_creation_capacity';
            END IF;
            -- BEFORE retains the fence/check. Only an actual INSERT reaches
            -- AFTER: ON CONFLICT DO NOTHING and suppressed rows allocate nothing.
            IF TG_WHEN='AFTER' THEN
                UPDATE public.identity_creation_capacity SET allocated_users=allocated_users+1
                    WHERE authority_id=NEW.authority_id;
            END IF;
            RETURN NEW;
        END $capacity$;
        REVOKE ALL ON FUNCTION public.fn_identity_user_capacity_guard() FROM PUBLIC;
        CREATE TRIGGER tg_users_creation_capacity BEFORE INSERT ON public.users
            FOR EACH ROW EXECUTE FUNCTION public.fn_identity_user_capacity_guard();
        CREATE TRIGGER tg_users_creation_capacity_record AFTER INSERT ON public.users
            FOR EACH ROW EXECUTE FUNCTION public.fn_identity_user_capacity_guard();

        ALTER POLICY memberships_isolation ON public.memberships
            USING ((tenant_id = NULLIF(current_setting('app.tenant_id',true),'')::uuid
                AND (organization_id IS NULL
                    OR organization_id = NULLIF(current_setting('app.organization_id',true),'')::uuid
                    OR NULLIF(current_setting('app.scope',true),'') = 'tenant'))
                OR (current_user='learnstack_migration' AND status='Active' AND deleted_at IS NULL
                    AND EXISTS (SELECT FROM public.users u WHERE u.id=public.memberships.user_id
                        AND u.authority_id::text=NULLIF(current_setting('app.identity_authority_id',true),'')
                        AND encode(u.subject_key,'hex')=NULLIF(current_setting('app.identity_subject_key',true),'')
                        AND u.status='Active')))
            WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id',true),'')::uuid
                AND ((organization_id IS NULL AND NULLIF(current_setting('app.organization_id',true),'') IS NULL)
                    OR organization_id = NULLIF(current_setting('app.organization_id',true),'')::uuid));

        CREATE FUNCTION public.fn_identity_self_membership_scopes(after_membership_id uuid,page_limit integer)
            RETURNS TABLE (membership_id uuid,tenant_id uuid,organization_id uuid)
            LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $scopes$
        DECLARE
            selected_authority text := NULLIF(current_setting('app.identity_authority_id',true),'');
            selected_subject text := NULLIF(current_setting('app.identity_subject_key',true),'');
        BEGIN
            IF session_user <> 'learnstack_app' OR selected_authority IS NULL
                OR selected_subject IS NULL OR selected_subject !~ '^[0-9a-f]{64}$'
                OR NULLIF(current_setting('app.tenant_id',true),'') IS NOT NULL
                OR current_setting('transaction_read_only') <> 'on'
                OR current_setting('transaction_isolation') <> 'read committed'
                OR page_limit IS NULL OR page_limit NOT BETWEEN 1 AND 100
            THEN RAISE EXCEPTION 'Identity self discovery refused.' USING ERRCODE='23514'; END IF;
            RETURN QUERY SELECT m.id,m.tenant_id,m.organization_id
                FROM public.users u JOIN public.memberships m ON m.user_id=u.id
                WHERE u.authority_id::text=selected_authority AND encode(u.subject_key,'hex')=selected_subject
                    AND u.status='Active' AND m.status='Active' AND m.deleted_at IS NULL
                    AND (after_membership_id IS NULL OR m.id>after_membership_id)
                ORDER BY m.id LIMIT page_limit;
        END $scopes$;
        REVOKE ALL ON FUNCTION public.fn_identity_self_membership_scopes(uuid,integer) FROM PUBLIC;
        GRANT EXECUTE ON FUNCTION public.fn_identity_self_membership_scopes(uuid,integer) TO learnstack_app;
        """);

    internal static void Remove(MigrationBuilder migration) => migration.Sql("""
        DROP FUNCTION public.fn_identity_self_membership_scopes(uuid,integer);
        ALTER POLICY memberships_isolation ON public.memberships
            USING (tenant_id = NULLIF(current_setting('app.tenant_id',true),'')::uuid
                AND (organization_id IS NULL
                    OR organization_id = NULLIF(current_setting('app.organization_id',true),'')::uuid
                    OR NULLIF(current_setting('app.scope',true),'') = 'tenant'))
            WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id',true),'')::uuid
                AND ((organization_id IS NULL AND NULLIF(current_setting('app.organization_id',true),'') IS NULL)
                    OR organization_id = NULLIF(current_setting('app.organization_id',true),'')::uuid));
        DROP TRIGGER tg_users_creation_capacity ON public.users;
        DROP TRIGGER tg_users_creation_capacity_record ON public.users;
        DROP FUNCTION public.fn_identity_user_capacity_guard();
        DROP FUNCTION public.fn_identity_creation_attempt_admission();
        DROP TABLE public.identity_creation_capacity;
        """);
}
