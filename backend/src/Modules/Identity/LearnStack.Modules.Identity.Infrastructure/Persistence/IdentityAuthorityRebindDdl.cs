using LearnStack.Infrastructure.Audit;
using Microsoft.EntityFrameworkCore.Migrations;

namespace LearnStack.Modules.Identity.Infrastructure.Persistence;

/// <summary>
/// The storage the offline issuer-rebind tool needs and nothing else (ADR-0104 § 2, § 7, § 8).
/// </summary>
/// <remarks>
/// <para>
/// <c>learnstack_identity_rebind</c> is a login role without <c>BYPASSRLS</c> that no runtime host
/// holds. It reads <c>identity_authority</c>, may change its <c>issuer</c> column and no other, reads
/// the four <c>users</c> columns the continuity proof compares, and inserts <c>audit_log</c> rows that
/// one policy arm limits to the platform sentinel, the identity module and the two rebind operations; that
/// grant and arm are installed by <see cref="IdentityRebindAuditInstaller"/>.
/// </para>
/// <para>
/// One permissive policy per command: the read policy gains the role, a third policy admits its
/// update, the <c>users</c> and <c>audit_log</c> isolation policies each gain one arm, and a second
/// permissive policy is never added. The trigger refuses any change of <c>id</c> or
/// <c>authority_id</c> for every role, so the stable key the subject keys hash cannot move; the issuer
/// grammar stays in the tool and in qualification, not in SQL.
/// </para>
/// </remarks>
internal static class IdentityAuthorityRebindDdl
{
    internal static void Add(MigrationBuilder migration)
    {
        migration.Sql("""
        GRANT SELECT ON public.identity_authority TO learnstack_identity_rebind;
        GRANT UPDATE (issuer) ON public.identity_authority TO learnstack_identity_rebind;
        GRANT SELECT (authority_id, subject_key, provider_subject, status) ON public.users TO learnstack_identity_rebind;

        ALTER POLICY identity_authority_read ON public.identity_authority
            USING (current_user IN ('learnstack_app','learnstack_migration','learnstack_identity_rebind'));
        CREATE POLICY identity_authority_rebind ON public.identity_authority FOR UPDATE
            USING (current_user='learnstack_identity_rebind' AND id=1)
            WITH CHECK (current_user='learnstack_identity_rebind' AND id=1);

        ALTER POLICY users_isolation ON public.users
            USING ((current_user='learnstack_app'
                    AND authority_id::text=NULLIF(current_setting('app.identity_authority_id',true),'')
                    AND encode(subject_key,'hex')=NULLIF(current_setting('app.identity_subject_key',true),''))
                   OR current_user='learnstack_migration'
                   OR current_user='learnstack_identity_rebind');

        CREATE FUNCTION public.fn_identity_authority_immutable_key() RETURNS trigger
            LANGUAGE plpgsql SECURITY INVOKER SET search_path=pg_catalog AS $$
        BEGIN
            IF NEW.id IS DISTINCT FROM OLD.id OR NEW.authority_id IS DISTINCT FROM OLD.authority_id THEN
                RAISE EXCEPTION 'The identity authority key cannot change.' USING ERRCODE='23514';
            END IF;
            RETURN NEW;
        END $$;
        REVOKE ALL ON FUNCTION public.fn_identity_authority_immutable_key() FROM PUBLIC;
        CREATE TRIGGER tg_identity_authority_immutable_key BEFORE UPDATE ON public.identity_authority
            FOR EACH ROW EXECUTE FUNCTION public.fn_identity_authority_immutable_key();
        """);

        // The audit_log grant and the one policy arm belong to the central audit installer: no module names the table.
        IdentityRebindAuditInstaller.Install(migration);
    }
}
