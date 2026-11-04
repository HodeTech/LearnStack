import type { AdminCopy } from '@/i18n/administration-catalogues';
import type { PageGrant, PageRole } from '@/server/auth/page-contract';

export function roleLabel(
  role: Pick<PageRole, 'builtInKey' | 'capabilityScope'>,
  admin: AdminCopy,
) {
  const labels = {
    TenantAdmin: admin('roles.tenant_admin'),
    OrgAdmin: admin('roles.org_admin'),
    Instructor: admin('roles.instructor'),
    OrgInstructor: admin('roles.org_instructor'),
    Learner:
      role.capabilityScope === 'Tenant'
        ? admin('roles.tenant_learner')
        : admin('roles.org_learner'),
    Guardian: admin('roles.guardian'),
    Editor: admin('roles.editor'),
  };
  return labels[role.builtInKey];
}

export function permissionLabel(grant: PageGrant, admin: AdminCopy): string {
  const labels: Readonly<Record<string, string>> = {
    'Tenant:identity.membership.admin': admin('permissions.tenant_members'),
    'Organization:identity.membership.admin': admin('permissions.organization_members'),
    'Tenant:identity.role.admin': admin('permissions.roles'),
    'Tenant:identity.membership_profile.write': admin('permissions.tenant_profiles'),
    'Organization:identity.membership_profile.write': admin('permissions.organization_profiles'),
    'Tenant:tenancy.organization.admin': admin('permissions.organizations'),
    'Tenant:tenancy.locale.write': admin('permissions.locales'),
    'Tenant:tenancy.setting.write': admin('permissions.settings'),
  };
  return labels[grant.scope + ':' + grant.key] ?? admin('permissions.unavailable');
}

export function statusLabel(status: 'Active' | 'Suspended' | 'Revoked', admin: AdminCopy) {
  return {
    Active: admin('status.active'),
    Suspended: admin('status.suspended'),
    Revoked: admin('status.revoked'),
  }[status];
}

export function languageLabel(tag: string, locale: string): string {
  try {
    return new Intl.DisplayNames(locale, { type: 'language' }).of(tag) ?? tag;
  } catch {
    return tag;
  }
}
