using Microsoft.EntityFrameworkCore.Migrations;

namespace LearnStack.Modules.Identity.Infrastructure.Persistence;

/// <summary>
/// The confined definer that resolves an operator-named provider subject to an Active User inside a
/// tenant transaction (ADR-0102 § 5).
/// </summary>
/// <remarks>
/// <c>users</c> is under forced row security keyed by identity settings that every tenant announcement
/// blanks, so a tenant transaction cannot read it. This function runs as the migration owner, whose
/// policy branch sees the table, and takes the stored authority and the derived subject key as
/// arguments. It refuses any session but the application role, any transaction that is not an ordinary
/// read-write tenant one, any authority other than the stored one, and any key that is not 32 bytes;
/// it answers the id of an Active, undeleted user or null. It reads nothing else and writes nothing.
/// </remarks>
internal static class OperatorSubjectResolverDdl
{
    internal static void Add(MigrationBuilder migration) => migration.Sql("""
        CREATE FUNCTION public.fn_identity_operator_resolve_user(selected_authority uuid, selected_subject_key bytea)
            RETURNS uuid LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $resolve$
        DECLARE resolved_user uuid;
        BEGIN
            IF session_user <> 'learnstack_app'
                OR NULLIF(current_setting('app.tenant_id',true),'') IS NULL
                OR current_setting('transaction_read_only') <> 'off'
                OR current_setting('transaction_isolation') <> 'read committed'
                OR selected_authority IS NULL OR selected_subject_key IS NULL OR octet_length(selected_subject_key) <> 32
                OR NOT EXISTS (SELECT FROM public.identity_authority a WHERE a.id=1 AND a.authority_id=selected_authority)
            THEN RAISE EXCEPTION 'Operator subject resolution refused.' USING ERRCODE='23514'; END IF;
            SELECT u.id INTO resolved_user FROM public.users u
                WHERE u.authority_id=selected_authority AND u.subject_key=selected_subject_key
                    AND u.status='Active' AND u.deleted_at IS NULL;
            RETURN resolved_user;
        END $resolve$;
        REVOKE ALL ON FUNCTION public.fn_identity_operator_resolve_user(uuid,bytea) FROM PUBLIC;
        GRANT EXECUTE ON FUNCTION public.fn_identity_operator_resolve_user(uuid,bytea) TO learnstack_app;
        """);
}
