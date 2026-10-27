namespace LearnStack.Tests.Integration.Database;

public sealed partial class EducationStructureTests
{
    // Independent whole-body expectation for the combined contextual guard. A
    // fragment match would miss an early return or a branch that bypasses the
    // parent lookup. This fixture is not obtained from the production installer.
    private static bool IsIdentityParentGuardBody(string body) =>
        NormalizeFunction(body) == NormalizeFunction(IdentityParentGuardBody);

    private const string IdentityParentGuardBody = """
        DECLARE target_tenant uuid; target_member uuid; target_role uuid; parent_org uuid;
            parent_status text; role_scope text; role_key text; require_exclusive boolean := false;
            has_history boolean; parent_fresh boolean;
        BEGIN
            target_tenant := CASE WHEN TG_OP = 'DELETE' THEN OLD.tenant_id ELSE NEW.tenant_id END;
            IF TG_OP = 'UPDATE' AND NEW.tenant_id IS DISTINCT FROM OLD.tenant_id THEN
                RAISE EXCEPTION 'identity graph is invalid' USING ERRCODE = 'P0304';
            END IF;
            IF TG_TABLE_NAME = 'memberships' THEN
                IF TG_OP = 'DELETE' OR (TG_OP = 'INSERT' AND (NEW.status <> 'Active' OR NEW.deleted_at IS NOT NULL OR NEW.deleted_by IS NOT NULL)) THEN
                    RAISE EXCEPTION 'identity graph is invalid' USING ERRCODE = 'P0304';
                END IF;
                IF TG_OP = 'UPDATE' AND (NEW.id IS DISTINCT FROM OLD.id OR NEW.user_id IS DISTINCT FROM OLD.user_id
                    OR OLD.status = 'Revoked' OR NEW.status NOT IN ('Active','Suspended','Revoked')) THEN
                    RAISE EXCEPTION 'identity graph is invalid' USING ERRCODE = 'P0304';
                END IF;
                IF TG_OP = 'INSERT' THEN
                    require_exclusive := NOT EXISTS (SELECT 1 FROM public.memberships WHERE tenant_id = target_tenant);
                ELSIF NEW.status IS DISTINCT FROM OLD.status OR NEW.deleted_at IS DISTINCT FROM OLD.deleted_at THEN
                    SELECT EXISTS (SELECT 1 FROM public.membership_role_assignments AS assignment
                        JOIN public.roles AS role ON role.tenant_id = assignment.tenant_id AND role.id = assignment.role_id
                        WHERE assignment.tenant_id = target_tenant AND assignment.membership_id = OLD.id
                          AND OLD.organization_id IS NULL AND role.built_in_key = 'TenantAdmin' AND role.permission_scope = 'Tenant') INTO require_exclusive;
                END IF;
            ELSIF TG_TABLE_NAME IN ('membership_profiles','membership_role_assignments') THEN
                target_member := CASE WHEN TG_OP = 'DELETE' THEN OLD.membership_id ELSE NEW.membership_id END;
                IF TG_OP = 'UPDATE' AND NEW.membership_id IS DISTINCT FROM OLD.membership_id THEN
                    RAISE EXCEPTION 'identity graph is invalid' USING ERRCODE = 'P0304';
                END IF;
                SELECT member.organization_id, member.status INTO parent_org, parent_status
                    FROM public.memberships AS member WHERE member.tenant_id = target_tenant AND member.id = target_member FOR NO KEY UPDATE;
                IF NOT FOUND OR parent_org IS DISTINCT FROM (CASE WHEN TG_OP = 'DELETE' THEN OLD.organization_id ELSE NEW.organization_id END) THEN
                    RAISE EXCEPTION 'identity graph is invalid' USING ERRCODE = '23514', CONSTRAINT = 'membership_parent_scope';
                END IF;
                IF TG_OP <> 'DELETE' AND parent_status = 'Revoked' THEN
                    RAISE EXCEPTION 'identity graph is invalid' USING ERRCODE = 'P0304';
                END IF;
                IF TG_TABLE_NAME = 'membership_profiles' AND TG_OP = 'DELETE' THEN
                    RAISE EXCEPTION 'identity graph is invalid' USING ERRCODE = 'P0304';
                ELSIF TG_TABLE_NAME = 'membership_role_assignments' THEN
                    IF TG_OP = 'UPDATE' THEN RAISE EXCEPTION 'identity graph is invalid' USING ERRCODE = 'P0304'; END IF;
                    target_role := CASE WHEN TG_OP = 'DELETE' THEN OLD.role_id ELSE NEW.role_id END;
                    SELECT role.permission_scope, role.built_in_key INTO role_scope, role_key FROM public.roles AS role
                        WHERE role.tenant_id = target_tenant AND role.id = target_role FOR KEY SHARE;
                    IF NOT FOUND OR (parent_org IS NULL AND role_scope <> 'Tenant') OR (parent_org IS NOT NULL AND role_scope <> 'Organization') THEN
                        RAISE EXCEPTION 'identity graph is invalid' USING ERRCODE = 'P0304';
                    END IF;
                    require_exclusive := parent_org IS NULL AND role_key = 'TenantAdmin';
                END IF;
            ELSIF TG_TABLE_NAME IN ('roles','role_permission_grants') THEN
                IF NULLIF(current_setting('app.organization_id',true),'') IS NOT NULL THEN
                    RAISE EXCEPTION 'identity graph is invalid' USING ERRCODE = 'P0304';
                END IF;
                SELECT EXISTS (SELECT 1 FROM public.memberships WHERE tenant_id = target_tenant) INTO has_history;
                IF TG_TABLE_NAME = 'roles' THEN
                    IF TG_OP = 'DELETE' OR (TG_OP = 'UPDATE' AND (NEW.id IS DISTINCT FROM OLD.id
                       OR NEW.built_in_key IS DISTINCT FROM OLD.built_in_key OR NEW.permission_scope IS DISTINCT FROM OLD.permission_scope)) THEN
                        RAISE EXCEPTION 'identity graph is invalid' USING ERRCODE = 'P0304';
                    END IF;
                    IF NOT has_history AND TG_OP <> 'INSERT' THEN
                        RAISE EXCEPTION 'identity graph is invalid' USING ERRCODE = 'P0304';
                    END IF;
                    require_exclusive := has_history AND NEW.built_in_key = 'TenantAdmin';
                ELSE
                    IF TG_OP = 'UPDATE' THEN RAISE EXCEPTION 'identity graph is invalid' USING ERRCODE = 'P0304'; END IF;
                    target_role := CASE WHEN TG_OP = 'DELETE' THEN OLD.role_id ELSE NEW.role_id END;
                    SELECT role.permission_scope, role.built_in_key, role.xmin = pg_current_xact_id()::text::xid
                        INTO role_scope, role_key, parent_fresh FROM public.roles AS role
                        WHERE role.tenant_id = target_tenant AND role.id = target_role FOR KEY SHARE;
                    IF NOT FOUND OR role_scope IS DISTINCT FROM (CASE WHEN TG_OP = 'DELETE' THEN OLD.permission_scope ELSE NEW.permission_scope END)
                        OR (NOT has_history AND (TG_OP <> 'INSERT' OR NOT parent_fresh))
                        OR (TG_OP = 'INSERT' AND NEW.permission_key = 'identity.role.admin' AND role_key <> 'TenantAdmin') THEN
                        RAISE EXCEPTION 'identity graph is invalid' USING ERRCODE = 'P0304';
                    END IF;
                    require_exclusive := has_history AND role_key = 'TenantAdmin';
                END IF;
            ELSE
                RAISE EXCEPTION 'identity mutation plan is unavailable' USING ERRCODE = 'P0301';
            END IF;
            PERFORM public.fn_identity_assert_tenant_fence(target_tenant,require_exclusive);
            IF TG_OP = 'DELETE' THEN RETURN OLD; END IF;
            RETURN NEW;
        END;
        """;
}
