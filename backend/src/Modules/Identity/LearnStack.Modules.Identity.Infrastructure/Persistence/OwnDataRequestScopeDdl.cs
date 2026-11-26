using Microsoft.EntityFrameworkCore.Migrations;

namespace LearnStack.Modules.Identity.Infrastructure.Persistence;

/// <summary>
/// The retained own data-request scope of ADR-0106 § 9: the confined definer
/// <c>fn_identity_own_data_request_scopes</c> and the two owner arms it reads through, one on
/// <c>memberships</c> and one on <c>data_subject_requests</c> (ADR-0107 § 8).
/// </summary>
/// <remarks>
/// <para>
/// <b>The function</b> is modelled on the consent subject's <c>fn_tenancy_own_field_consent_scope</c>: a
/// verified actor the caller normalized from its credential, an independently classified real host as the
/// ceiling, closed <c>list</c> and <c>select</c> modes, at most twenty distinct keys per call ordered by
/// organization (the tenant-wide group first), and a fixed search path, migration ownership and an exact
/// application EXECUTE grant. It returns the scope groups — a tenant and an organization or none — in the host's
/// tenant where the actor holds a Membership of any status (a Revoked tombstone included) or has any data
/// request; on an organization host only that organization's group. It returns no Membership, request, status,
/// profile or provider data, and writes nothing. It refuses, as <c>23514</c>, any session but the application
/// role, any announced tenant, organization, scope or global identity, any transaction that is not read-only
/// READ COMMITTED, the nil and reserved identifiers, and any argument outside its two modes.
/// </para>
/// <para>
/// <b>The owner arms.</b> Both tables are under forced row security keyed by the tenant announcement, which this
/// pre-context reader deliberately has not made, so the function runs as the NOBYPASSRLS migration owner and
/// each table gains one owner arm, beside its unchanged application arm and unchanged WITH CHECK. Each arm admits
/// exactly the state the function itself requires on entry: the migration owner as the current user (only a
/// definer body runs so), the application role as the session user, no tenant, organization, scope or global
/// identity announcement, and a read-only transaction. No other definer reads these tables in that state:
/// the self-discovery and creation definers require a global identity announcement, the membership guards run
/// in a tenant's write, the provider definers run for their own login role, the consent definer reads only its
/// own table, and no other definer body names <c>data_subject_requests</c> at all — the function census pins
/// every body. A row-visibility arm is not a write path: the arms are read-only by their own predicate, WITH
/// CHECK is unchanged and the mutation guards refuse every role but the application one.
/// </para>
/// </remarks>
internal static class OwnDataRequestScopeDdl
{
    internal static void Install(MigrationBuilder migration)
    {
        ArgumentNullException.ThrowIfNull(migration);
        // The Membership policy restates its three existing arms unchanged (the application arm, the provider
        // ingress arm and the global self-discovery arm, as ProviderEventStorageDdl last wrote them) and adds the
        // fourth; ALTER POLICY ... USING leaves its WITH CHECK as it is. Both owner arms are written out literally
        // and identically: they admit exactly the state the function requires on entry.
        migration.Sql("""
            ALTER POLICY memberships_isolation ON public.memberships
                USING ((tenant_id=NULLIF(current_setting('app.tenant_id',true),'')::uuid
                    AND (organization_id IS NULL OR organization_id=NULLIF(current_setting('app.organization_id',true),'')::uuid
                        OR NULLIF(current_setting('app.scope',true),'')='tenant'))
                    OR (current_user='learnstack_migration' AND session_user='learnstack_identity_ingress'
                        AND status IN ('Active','Suspended') AND deleted_at IS NULL AND deleted_by IS NULL)
                    OR (current_user='learnstack_migration' AND status='Active' AND deleted_at IS NULL
                        AND EXISTS (SELECT FROM public.users u WHERE u.id=public.memberships.user_id
                            AND u.authority_id::text=NULLIF(current_setting('app.identity_authority_id',true),'')
                            AND encode(u.subject_key,'hex')=NULLIF(current_setting('app.identity_subject_key',true),'')
                            AND u.status='Active'))
                    OR (current_user='learnstack_migration' AND session_user='learnstack_app'
                        AND NULLIF(current_setting('app.tenant_id',true),'') IS NULL
                        AND NULLIF(current_setting('app.organization_id',true),'') IS NULL
                        AND NULLIF(current_setting('app.scope',true),'') IS NULL
                        AND NULLIF(current_setting('app.identity_authority_id',true),'') IS NULL
                        AND NULLIF(current_setting('app.identity_subject_key',true),'') IS NULL
                        AND current_setting('transaction_read_only')='on'));
            ALTER POLICY data_subject_requests_isolation ON public.data_subject_requests
                USING ((tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
                        AND (organization_id IS NULL
                            OR organization_id = NULLIF(current_setting('app.organization_id', true), '')::uuid
                            OR NULLIF(current_setting('app.scope', true), '') = 'tenant'))
                    OR (current_user='learnstack_migration' AND session_user='learnstack_app'
                        AND NULLIF(current_setting('app.tenant_id',true),'') IS NULL
                        AND NULLIF(current_setting('app.organization_id',true),'') IS NULL
                        AND NULLIF(current_setting('app.scope',true),'') IS NULL
                        AND NULLIF(current_setting('app.identity_authority_id',true),'') IS NULL
                        AND NULLIF(current_setting('app.identity_subject_key',true),'') IS NULL
                        AND current_setting('transaction_read_only')='on'));
            """);
        migration.Sql(Function);
    }

    private const string Function = """
        CREATE FUNCTION public.fn_identity_own_data_request_scopes(p_actor uuid, p_host_tenant uuid, p_host_organization uuid,
            p_mode text, p_organization uuid, p_after_organization uuid, p_limit integer)
            RETURNS TABLE (tenant_id uuid, organization_id uuid)
            LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $scopes$
        BEGIN
            IF (session_user <> 'learnstack_app'
                OR NULLIF(current_setting('app.tenant_id',true),'') IS NOT NULL
                OR NULLIF(current_setting('app.organization_id',true),'') IS NOT NULL
                OR NULLIF(current_setting('app.scope',true),'') IS NOT NULL
                OR NULLIF(current_setting('app.identity_authority_id',true),'') IS NOT NULL
                OR NULLIF(current_setting('app.identity_subject_key',true),'') IS NOT NULL
                OR current_setting('transaction_read_only') <> 'on'
                OR current_setting('transaction_isolation') <> 'read committed'
                OR p_actor IS NULL OR p_actor IN ('00000000-0000-0000-0000-000000000000'::uuid,'00000000-0000-7000-8000-000000000001'::uuid)
                OR p_host_tenant IS NULL
                OR p_host_tenant IN ('00000000-0000-0000-0000-000000000000'::uuid,'00000000-0000-7000-8000-000000000002'::uuid)
                OR p_host_organization IS NOT DISTINCT FROM '00000000-0000-0000-0000-000000000000'::uuid
                OR p_organization IS NOT DISTINCT FROM '00000000-0000-0000-0000-000000000000'::uuid
                OR p_mode IS NULL OR p_mode NOT IN ('list','select')
                OR p_limit IS NULL OR p_limit NOT BETWEEN 1 AND 20
                OR (p_mode = 'list' AND p_organization IS NOT NULL)
                OR (p_mode = 'select' AND (p_after_organization IS NOT NULL OR p_limit <> 1))) IS NOT FALSE
            THEN RAISE EXCEPTION 'Own data request scope discovery refused.' USING ERRCODE='23514'; END IF;
            -- The tenant-wide group sorts first under the nil key, which is also the list cursor that follows it.
            RETURN QUERY SELECT held.group_tenant, held.group_organization
                FROM (SELECT m.tenant_id, m.organization_id FROM public.memberships AS m
                        WHERE m.user_id = p_actor AND m.tenant_id = p_host_tenant
                    UNION
                    SELECT r.tenant_id, r.organization_id FROM public.data_subject_requests AS r
                        WHERE r.subject_user_id = p_actor AND r.tenant_id = p_host_tenant) AS held(group_tenant, group_organization)
                WHERE (p_host_organization IS NULL OR held.group_organization = p_host_organization)
                    AND ((p_mode = 'select' AND held.group_organization IS NOT DISTINCT FROM p_organization)
                        OR (p_mode = 'list' AND (p_after_organization IS NULL
                            OR COALESCE(held.group_organization,'00000000-0000-0000-0000-000000000000'::uuid) > p_after_organization)))
                ORDER BY COALESCE(held.group_organization,'00000000-0000-0000-0000-000000000000'::uuid)
                LIMIT p_limit;
        END $scopes$;
        ALTER FUNCTION public.fn_identity_own_data_request_scopes(uuid,uuid,uuid,text,uuid,uuid,integer) OWNER TO learnstack_migration;
        REVOKE ALL ON FUNCTION public.fn_identity_own_data_request_scopes(uuid,uuid,uuid,text,uuid,uuid,integer) FROM PUBLIC;
        GRANT EXECUTE ON FUNCTION public.fn_identity_own_data_request_scopes(uuid,uuid,uuid,text,uuid,uuid,integer) TO learnstack_app;
        """;
}
