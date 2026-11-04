import type {
  NativePageSnapshot,
  PageMemberDetail,
  PageRole,
  PageView,
} from '../server/auth/page-contract';

export const uiMemberId = '019a154b-0000-7000-8000-000000000011';
export const uiRoleId = '019a154b-0000-7000-8000-000000000022';
export const uiOtherRoleId = '019a154b-0000-7000-8000-000000000033';
export const uiMember: PageMemberDetail = {
  membershipId: uiMemberId,
  displayName: '<Member>',
  preferredLocale: 'en',
  status: 'Active',
  inCurrentScope: true,
  organizationScoped: false,
  version: '9223372036854775807',
  roleIds: [uiRoleId],
  canWriteProfile: true,
  canChangeStatus: true,
  canReplaceRoles: true,
};
export const uiRole: PageRole = {
  roleId: uiRoleId,
  builtInKey: 'TenantAdmin',
  capabilityScope: 'Tenant',
  version: '9007199254740993',
};
export const uiOtherRole: PageRole = {
  roleId: uiOtherRoleId,
  builtInKey: 'Instructor',
  capabilityScope: 'Tenant',
  version: '1',
};

export function administrationSnapshot(
  view: PageView,
  overrides: Partial<NativePageSnapshot> = {},
): NativePageSnapshot {
  return {
    target:
      view.kind === 'member'
        ? (('/studio/members/' + view.member.membershipId) as `/studio/members/${string}`)
        : view.kind === 'role'
          ? (('/studio/roles/' + view.role.roleId) as `/studio/roles/${string}`)
          : view.kind === 'profile'
            ? '/portal/profile'
            : view.kind === 'members'
              ? '/studio/members'
              : view.kind === 'roles'
                ? '/studio/roles'
                : (('/' + view.kind) as '/studio' | '/portal'),
    site: {
      displayName: 'Institution',
      defaultLocale: 'en',
      enabledLocales: ['en', 'tr'],
      theme: null,
      showPlatformAttribution: true,
    },
    session: {
      state: 'authenticated',
      displayName: 'Admitted institution',
      csrfToken: 'A'.repeat(43),
      expiresAt: Date.now() + 180_000,
      limitedByAbsolute: false,
    },
    scopeKind: 'tenant',
    capabilities: {
      canReadTenantMembers: true,
      canReadOrganizationMembers: false,
      canWriteMemberProfiles: true,
      canManageMemberships: true,
      canReadRoles: true,
      canChangeRoleGrants: true,
      canAdoptRoleRelease: true,
      canWriteOwnProfile: true,
      canManageOrganizations: true,
    },
    ownProfile: {
      displayName: 'My contextual name',
      preferredLocale: 'en',
      version: '9007199254740995',
    },
    profileState: 'available',
    formKeys: Array.from(
      { length: 32 },
      (_, index) => String(index).padStart(2, '0') + 'K'.repeat(41),
    ),
    outcome: { kind: 'ready', view },
    ...overrides,
  };
}
