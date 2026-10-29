// @vitest-environment node
import type {
  MembershipDetail,
  PermissionRegistry,
  RoleDetail,
} from '@learnstack/sdk/administration-server';
import { describe, expect, it, vi } from 'vitest';

import type { NativeOperation, NativeOperationResult } from './native-operation-contract';
import type { NativePageSnapshot, PageCapabilities } from './page-contract';
import { nativeDocumentPlan, preloadPage } from './page-preload';
import { BffSessionError } from './session-contract';

const memberId = '019a154b-0000-7000-8000-000000000012';
const roleId = '019a154b-0000-7000-8000-000000000013';
const organizationId = '019a154b-0000-7000-8000-000000000014';
const actor = '019a154b-0000-7000-8000-000000000011';
const capabilities: PageCapabilities = {
  canReadTenantMembers: true,
  canReadOrganizationMembers: true,
  canWriteMemberProfiles: true,
  canManageMemberships: true,
  canReadRoles: true,
  canChangeRoleGrants: true,
  canAdoptRoleRelease: true,
  canWriteOwnProfile: true,
  canManageOrganizations: true,
};
const member: MembershipDetail = {
  membershipId: memberId,
  userId: actor,
  organizationId: null,
  status: 'Active',
  displayName: 'Target member',
  preferredLocale: 'en',
  roleIds: [roleId],
  version: '7',
};
const role: RoleDetail = {
  roleId,
  builtInKey: 'TenantAdmin',
  capabilityScope: 'Tenant',
  version: '8',
  grants: [{ scope: 'Tenant', key: 'identity.membership.admin' }],
  adoptedReleaseVersion: 1,
  adoptedReleaseFingerprint: 'a'.repeat(64),
};
const common: Omit<NativePageSnapshot, 'capabilities' | 'ownProfile' | 'profileState' | 'outcome'> =
  {
    target: '/studio',
    site: {
      displayName: 'School',
      defaultLocale: 'en',
      enabledLocales: ['en', 'tr'],
      theme: null,
      showPlatformAttribution: true,
    },
    session: {
      state: 'authenticated',
      displayName: 'School',
      expiresAt: 1800000300000,
      limitedByAbsolute: false,
      csrfToken: 'A'.repeat(43),
    },
    scopeKind: 'tenant',
    formKeys: ['owned-form-key'],
  };
function success<const T extends Extract<NativeOperationResult, { kind: 'success' }>['data']>(
  data: T,
) {
  return {
    kind: 'success' as const,
    status: 200 as const,
    data,
    etag: null,
    location: null,
    replayed: false,
  };
}

function refused(status: number): NativeOperationResult {
  return {
    kind: 'api-error',
    status,
    error: { code: 'not_found' },
    problem: {
      status,
      type: 'about:blank',
      title: 'Unavailable',
      instance: '/owned',
      code: 'not_found',
      messageKey: 'lockey_not_found',
      correlationId: actor,
    },
  };
}
function fixture(overrides: Partial<Record<NativeOperation['kind'], NativeOperationResult>> = {}) {
  const results: Partial<Record<NativeOperation['kind'], NativeOperationResult>> = {
    getOwnCapabilities: success(capabilities),
    getOwnProfile: success({ displayName: 'Current actor', preferredLocale: 'tr', version: '3' }),
    getMembership: success(member),
    listRoles: success({ items: [role] }),
    getRole: success(role),
    getPermissions: success<PermissionRegistry>({
      items: [
        {
          scope: 'Tenant',
          key: 'identity.membership.admin',
          labelKey: 'lockey_membership_admin',
          canGrant: true,
        },
      ],
      release: { version: 2, fingerprint: 'b'.repeat(64), tenantAdminDelta: [] },
    }),
    listMemberships: success({
      items: [{ ...member, status: 'Active' }],
      pageInfo: {
        nextCursor: 'private-api-cursor',
        previousCursor: null,
        hasNext: true,
        hasPrevious: false,
      },
    }),
    listOrganizationMemberships: success({
      items: [{ ...member, organizationId, status: 'Active' }],
      pageInfo: { nextCursor: null, previousCursor: null, hasNext: false, hasPrevious: false },
    }),
    ...overrides,
  };
  const read = vi.fn(
    async (operation: NativeOperation): Promise<NativeOperationResult> =>
      results[operation.kind] ?? { kind: 'invalid-request' },
  );
  const wrap = vi.fn(() => 'opaque-browser-cursor');
  return {
    read,
    wrap,
    calls: () => read.mock.calls.map(([operation]) => operation.kind),
    load: (target: string, currentOrganization: string | null = null) => {
      const plan = nativeDocumentPlan(target, false);
      return preloadPage(
        plan,
        {
          ...common,
          target: plan.target,
          scopeKind: currentOrganization === null ? 'tenant' : 'organization',
        },
        currentOrganization,
        read,
        wrap,
      );
    },
  };
}

describe('finite native page preparation reads and safe projections', () => {
  it.each([
    ['/studio', 'studio', []],
    ['/portal', 'portal', []],
    ['/portal/profile', 'profile', []],
    ['/studio/members', 'members', ['listMemberships']],
    ['/studio/members/' + memberId, 'member', ['getMembership', 'listRoles']],
    ['/studio/roles', 'roles', ['listRoles']],
    ['/studio/roles/' + roleId, 'role', ['getRole', 'getPermissions']],
  ] as const)('loads only the finite reads for %s', async (target, kind, calls) => {
    const test = fixture();
    const result = await test.load(target);
    expect(test.calls()).toEqual(['getOwnCapabilities', 'getOwnProfile', ...calls]);
    expect(result.outcome).toMatchObject({ kind: 'ready', view: { kind } });
    expect(result.ownProfile?.preferredLocale).toBe('tr');
    expect(JSON.stringify(result)).not.toContain(actor);
    expect(JSON.stringify(result)).not.toContain('userId');
  });

  it('uses the branch directory when only organization coverage is available and seals pagination', async () => {
    const test = fixture({
      getOwnCapabilities: success({ ...capabilities, canReadTenantMembers: false }),
    });
    await test.load('/studio/members?limit=7', organizationId);
    expect(test.calls()).toEqual([
      'getOwnCapabilities',
      'getOwnProfile',
      'listOrganizationMemberships',
    ]);
    expect(test.read).toHaveBeenLastCalledWith({ kind: 'listOrganizationMemberships', limit: 7 });
    const tenant = fixture();
    const result = await tenant.load('/studio/members');
    expect(tenant.wrap).toHaveBeenCalledExactlyOnceWith('private-api-cursor');
    expect(result.outcome).toMatchObject({
      kind: 'ready',
      view: { endCursor: 'opaque-browser-cursor', hasNextPage: true },
    });
    expect(JSON.stringify(result)).not.toContain('private-api-cursor');
  });

  it('allows independent member management without requesting inaccessible role choices', async () => {
    const test = fixture({ getOwnCapabilities: success({ ...capabilities, canReadRoles: false }) });
    const result = await test.load('/studio/members/' + memberId);
    expect(test.calls()).toEqual(['getOwnCapabilities', 'getOwnProfile', 'getMembership']);
    expect(result.outcome).toMatchObject({
      kind: 'ready',
      view: {
        roles: null,
        member: { canWriteProfile: true, canChangeStatus: true, canReplaceRoles: false },
      },
    });
  });

  it.each(['/studio/roles', '/studio/roles/' + roleId])(
    'withholds %s before any Role API when role reading is denied',
    async (target) => {
      const test = fixture({
        getOwnCapabilities: success({ ...capabilities, canReadRoles: false }),
      });
      const result = await test.load(target);
      expect(test.calls()).toEqual(['getOwnCapabilities', 'getOwnProfile']);
      expect(result.outcome).toMatchObject({ kind: 'failure', reason: 'denied', status: 403 });
    },
  );

  it('withholds member data before any directory API without current coverage', async () => {
    const test = fixture({
      getOwnCapabilities: success({
        ...capabilities,
        canReadTenantMembers: false,
        canReadOrganizationMembers: false,
      }),
    });
    const result = await test.load('/studio/members');
    expect(test.calls()).toEqual(['getOwnCapabilities', 'getOwnProfile']);
    expect(result.outcome).toMatchObject({ kind: 'failure', reason: 'denied' });
  });

  it('keeps a cross-scope detail read-only and uses own profile rather than target locale', async () => {
    const test = fixture({ getMembership: success({ ...member, organizationId }) });
    const result = await test.load('/studio/members/' + memberId);
    expect(result.ownProfile?.preferredLocale).toBe('tr');
    expect(result.outcome).toMatchObject({
      kind: 'ready',
      view: {
        member: {
          preferredLocale: 'en',
          inCurrentScope: false,
          organizationScoped: true,
          canWriteProfile: false,
          canChangeStatus: false,
          canReplaceRoles: false,
        },
      },
    });
    expect(JSON.stringify(result)).not.toContain(organizationId);
  });

  it.each(['Suspended', 'Revoked'] as const)(
    'retains %s lifecycle restrictions independently of permissions',
    async (status) => {
      const test = fixture({ getMembership: success({ ...member, status }) });
      expect((await test.load('/studio/members/' + memberId)).outcome).toMatchObject({
        kind: 'ready',
        view: {
          member: {
            canWriteProfile: false,
            canChangeStatus: status === 'Suspended',
            canReplaceRoles: status === 'Suspended',
          },
        },
      });
    },
  );

  it.each([
    [false, true, null, false, true],
    [true, false, null, true, false],
    [true, true, organizationId, false, false],
  ] as const)(
    'keeps grant editing and release adoption separate within exact held scope',
    async (change, adopt, organization, expectedChange, expectedAdopt) => {
      const test = fixture({
        getOwnCapabilities: success({
          ...capabilities,
          canChangeRoleGrants: change,
          canAdoptRoleRelease: adopt,
        }),
      });
      expect((await test.load('/studio/roles/' + roleId, organization)).outcome).toMatchObject({
        kind: 'ready',
        view: {
          canChangeGrants: expectedChange,
          canAdoptRelease: expectedAdopt,
        },
      });
    },
  );

  it('treats missing exact own profile as legitimate on a derived branch', async () => {
    const test = fixture({ getOwnProfile: refused(404) });
    const result = await test.load('/portal/profile', organizationId);
    expect(result).toMatchObject({
      ownProfile: null,
      profileState: 'missing',
      outcome: { kind: 'ready', view: { kind: 'profile', canWrite: false } },
    });
    expect(test.calls()).toEqual(['getOwnCapabilities', 'getOwnProfile']);
  });

  it.each([
    [400, 'invalid_cursor'],
    [403, 'denied'],
    [404, 'not_found'],
    [429, 'busy'],
    [503, 'unavailable'],
  ] as const)(
    'projects API %i as the closed failure %s without exposing backend metadata',
    async (status, reason) => {
      const test = fixture({ listMemberships: refused(status) });
      const result = await test.load('/studio/members');
      expect(result.outcome).toEqual({ kind: 'failure', reason, status, retryAfterSeconds: null });
      expect(JSON.stringify(result)).not.toContain(actor);
      expect(JSON.stringify(result)).not.toContain('lockey_');
    },
  );

  it('stops before optional reads when common authorization fails and preserves cancellation', async () => {
    const denied = fixture({ getOwnCapabilities: refused(403) });
    expect((await denied.load('/studio/members')).outcome.kind).toBe('failure');
    expect(denied.calls()).toEqual(['getOwnCapabilities']);
    const cancelled = fixture({ getOwnProfile: { kind: 'cancelled' } });
    await expect(cancelled.load('/studio')).rejects.toThrow(BffSessionError);
    expect(cancelled.calls()).toEqual(['getOwnCapabilities', 'getOwnProfile']);
  });

  it.each([
    [37, true, 37],
    [86_400, true, 86_400],
    [0, true, null],
    [86_401, true, null],
    [37, false, null],
  ] as const)(
    'projects only a bounded rate-limit retry hint (%i, rate-limited=%s)',
    async (retryAfter, rateLimited, expected) => {
      const failure = refused(429);
      if (failure.kind !== 'api-error') throw new Error('Expected API refusal fixture');
      const test = fixture({
        listMemberships: {
          ...failure,
          error: rateLimited ? { code: 'rate_limited', retryAfter } : { code: 'not_found' },
        },
      });
      expect((await test.load('/studio/members')).outcome).toEqual({
        kind: 'failure',
        reason: 'busy',
        status: 429,
        retryAfterSeconds: expected,
      });
    },
  );
});
