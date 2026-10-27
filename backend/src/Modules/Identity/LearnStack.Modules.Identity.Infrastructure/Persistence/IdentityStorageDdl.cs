using Microsoft.EntityFrameworkCore.Migrations;

namespace LearnStack.Modules.Identity.Infrastructure.Persistence;

/// <summary>Exact Identity-owned storage guards; global audit installation has its separate central owner.</summary>
internal static class IdentityStorageDdl
{
    internal static void Install(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        ALTER TABLE public.identity_authority ENABLE ROW LEVEL SECURITY;
        ALTER TABLE public.identity_authority FORCE ROW LEVEL SECURITY;
        CREATE POLICY identity_authority_read ON public.identity_authority FOR SELECT
            USING (current_user IN ('learnstack_app','learnstack_migration'));
        CREATE POLICY identity_authority_install ON public.identity_authority FOR INSERT
            WITH CHECK (current_user='learnstack_migration' AND id=1);
        REVOKE ALL ON public.identity_authority FROM PUBLIC,learnstack_app,learnstack_platform,learnstack_outbox_admin;
        GRANT SELECT ON public.identity_authority TO learnstack_app;

        ALTER TABLE public.users ENABLE ROW LEVEL SECURITY;
        ALTER TABLE public.users FORCE ROW LEVEL SECURITY;
        CREATE POLICY users_isolation ON public.users
            USING ((current_user='learnstack_app'
                    AND authority_id::text=NULLIF(current_setting('app.identity_authority_id',true),'')
                    AND encode(subject_key,'hex')=NULLIF(current_setting('app.identity_subject_key',true),''))
                   OR current_user='learnstack_migration')
            WITH CHECK (current_user='learnstack_app'
                    AND authority_id::text=NULLIF(current_setting('app.identity_authority_id',true),'')
                    AND encode(subject_key,'hex')=NULLIF(current_setting('app.identity_subject_key',true),'')
                    AND status='Active' AND provider_subject IS NOT NULL
                    AND deleted_at IS NULL AND deleted_by IS NULL);
        REVOKE ALL ON public.users FROM PUBLIC,learnstack_app,learnstack_platform,learnstack_outbox_admin;
        GRANT SELECT ON public.users TO learnstack_app;
        GRANT INSERT (id,authority_id,subject_key,provider_subject,status,creation_audit_id,
            creation_audit_timestamp,created_at,created_by,row_version) ON public.users TO learnstack_app;

        CREATE FUNCTION public.fn_identity_user_immutable() RETURNS trigger
            LANGUAGE plpgsql SECURITY INVOKER SET search_path=pg_catalog AS $$
        BEGIN
            RAISE EXCEPTION 'Identity bindings have no admitted update or removal owner.' USING ERRCODE='23514';
        END $$;
        REVOKE ALL ON FUNCTION public.fn_identity_user_immutable() FROM PUBLIC;
        CREATE TRIGGER tg_users_no_update BEFORE UPDATE ON public.users
            FOR EACH ROW EXECUTE FUNCTION public.fn_identity_user_immutable();
        CREATE TRIGGER tg_users_no_delete BEFORE DELETE ON public.users
            FOR EACH ROW EXECUTE FUNCTION public.fn_identity_user_immutable();
        CREATE TRIGGER tg_users_no_truncate BEFORE TRUNCATE ON public.users
            FOR EACH STATEMENT EXECUTE FUNCTION public.fn_identity_user_immutable();
        """);

    internal static void Remove(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        DROP TRIGGER IF EXISTS tg_users_no_update ON public.users;
        DROP TRIGGER IF EXISTS tg_users_no_delete ON public.users;
        DROP TRIGGER IF EXISTS tg_users_no_truncate ON public.users;
        DROP FUNCTION IF EXISTS public.fn_identity_user_immutable();
        """);
}
