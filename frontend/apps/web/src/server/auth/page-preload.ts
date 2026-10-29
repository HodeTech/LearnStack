import type {
  MembershipDetail,
  MembershipPage,
  OwnCapabilities,
  OwnMembershipProfile,
  PermissionRegistry,
  RoleDetail,
  RoleDirectory,
} from '@learnstack/sdk/administration-server';

import type { NativeOperation, NativeOperationResult } from './native-operation-contract.js';
import type {
  NativePageSnapshot,
  PageCapabilities,
  PageFailure,
  PageMember,
  PageMemberDetail,
  PageProfile,
  PageRole,
  PageView,
} from './page-contract.js';
import { bffDocumentQuery } from './page-query.js';
import { BffSessionError } from './session-contract.js';
import type { NativeReturnPath } from './session-contract.js';

export type NativeDocumentPlan = Readonly<{
  target: NativeReturnPath;
  kind: 'studio' | 'portal' | 'members' | 'member' | 'roles' | 'role' | 'profile';
  id?: string;
  cursor?: string;
  limit: number;
}>;

export function nativeDocumentPlan(target: string, flight: boolean): NativeDocumentPlan {
  const parsed = bffDocumentQuery(target, flight);
  const path = parsed.target;
  const kind =
    path === '/studio'
      ? 'studio'
      : path === '/portal'
        ? 'portal'
        : path === '/portal/profile'
          ? 'profile'
          : path === '/studio/members'
            ? 'members'
            : path === '/studio/roles'
              ? 'roles'
              : path.startsWith('/studio/members/')
                ? 'member'
                : 'role';
  return Object.freeze({
    target: path,
    kind,
    ...(kind === 'member' || kind === 'role' ? { id: path.split('/')[3]! } : {}),
    ...(parsed.cursor ? { cursor: parsed.cursor } : {}),
    limit: parsed.limit ?? 20,
  });
}

class PageReadFailure extends Error {
  constructor(
    readonly reason: PageFailure,
    readonly status: 400 | 403 | 404 | 429 | 503,
    readonly retryAfterSeconds: number | null = null,
  ) {
    super('Protected page read unavailable');
  }
}

function refused(result: NativeOperationResult): never {
  if (result.kind === 'cancelled') throw new BffSessionError('cancelled');
  if (result.kind === 'api-error') {
    if (result.status === 403) throw new PageReadFailure('denied', 403);
    if (result.status === 404) throw new PageReadFailure('not_found', 404);
    if (result.status === 400) throw new PageReadFailure('invalid_cursor', 400);
    if (result.status === 429) {
      const retry = result.error.code === 'rate_limited' ? result.error.retryAfter : undefined;
      throw new PageReadFailure(
        'busy',
        429,
        retry !== undefined && Number.isInteger(retry) && retry >= 1 && retry <= 86_400
          ? retry
          : null,
      );
    }
  }
  throw new PageReadFailure('unavailable', 503);
}

/** Finite native reads share their caller's held revision, scope and deadline. */
export async function preloadPage(
  plan: NativeDocumentPlan,
  base: Omit<NativePageSnapshot, 'capabilities' | 'ownProfile' | 'profileState' | 'outcome'>,
  organizationId: string | null,
  read: (operation: NativeOperation) => Promise<NativeOperationResult>,
  wrapCursor: (cursor: string) => string,
): Promise<NativePageSnapshot> {
  let capabilities: PageCapabilities | null = null;
  let ownProfile: PageProfile | null = null;
  let profileState: NativePageSnapshot['profileState'] = 'unavailable';
  const data = async <T>(operation: NativeOperation): Promise<T> => {
    const result = await read(operation);
    if (result.kind !== 'success') return refused(result);
    return result.data as T;
  };
  const projectMember = (
    member: Pick<
      MembershipDetail,
      'membershipId' | 'displayName' | 'preferredLocale' | 'status' | 'organizationId'
    >,
  ): PageMember => ({
    membershipId: member.membershipId,
    displayName: member.displayName,
    preferredLocale: member.preferredLocale,
    status: member.status,
    inCurrentScope: member.organizationId === organizationId,
    organizationScoped: member.organizationId !== null,
  });
  const roleChoice = (role: RoleDirectory['items'][number]): PageRole => ({
    roleId: role.roleId,
    builtInKey: role.builtInKey,
    capabilityScope: role.capabilityScope,
    version: role.version,
  });
  try {
    capabilities = await data<OwnCapabilities>({ kind: 'getOwnCapabilities' });
    const profile = await read({ kind: 'getOwnProfile' });
    if (profile.kind === 'success') {
      const value = profile.data as OwnMembershipProfile;
      ownProfile = {
        displayName: value.displayName,
        preferredLocale: value.preferredLocale,
        version: value.version,
      };
      profileState = 'available';
    } else if (profile.kind === 'api-error' && profile.status === 404) {
      // Derived tenant-wide coverage can admit a branch without its own profile.
      profileState = 'missing';
    } else refused(profile);

    let view: PageView;
    switch (plan.kind) {
      case 'studio':
      case 'portal':
        view = { kind: plan.kind };
        break;
      case 'profile':
        view = {
          kind: 'profile',
          canWrite: capabilities.canWriteOwnProfile && ownProfile !== null,
        };
        break;
      case 'members': {
        if (!capabilities.canReadTenantMembers && !capabilities.canReadOrganizationMembers)
          throw new PageReadFailure('denied', 403);
        const page = await data<MembershipPage>({
          kind: capabilities.canReadTenantMembers
            ? 'listMemberships'
            : 'listOrganizationMemberships',
          ...(plan.cursor ? { cursor: plan.cursor } : {}),
          limit: plan.limit,
        });
        view = {
          kind: 'members',
          items: page.items.map(projectMember),
          endCursor:
            page.pageInfo.nextCursor === null ? null : wrapCursor(page.pageInfo.nextCursor),
          hasNextPage: page.pageInfo.hasNext,
          limit: plan.limit,
        };
        break;
      }
      case 'member': {
        if (!capabilities.canReadTenantMembers && !capabilities.canReadOrganizationMembers)
          throw new PageReadFailure('denied', 403);
        const value = await data<MembershipDetail>({ kind: 'getMembership', id: plan.id! });
        const exact = value.organizationId === organizationId;
        const roles = capabilities.canReadRoles
          ? (await data<RoleDirectory>({ kind: 'listRoles' })).items.map(roleChoice)
          : null;
        const member: PageMemberDetail = {
          ...projectMember(value),
          version: value.version,
          roleIds: [...value.roleIds],
          canWriteProfile:
            exact && value.status === 'Active' && capabilities.canWriteMemberProfiles,
          canChangeStatus: exact && value.status !== 'Revoked' && capabilities.canManageMemberships,
          canReplaceRoles:
            exact &&
            value.status !== 'Revoked' &&
            capabilities.canManageMemberships &&
            roles !== null,
        };
        view = { kind: 'member', member, roles };
        break;
      }
      case 'roles':
        if (!capabilities.canReadRoles) throw new PageReadFailure('denied', 403);
        view = {
          kind: 'roles',
          roles: (await data<RoleDirectory>({ kind: 'listRoles' })).items.map(roleChoice),
        };
        break;
      case 'role': {
        if (!capabilities.canReadRoles) throw new PageReadFailure('denied', 403);
        const role = await data<RoleDetail>({ kind: 'getRole', id: plan.id! });
        const registry = await data<PermissionRegistry>({ kind: 'getPermissions' });
        view = {
          kind: 'role',
          role: {
            ...roleChoice(role),
            grants: role.grants.map(({ scope, key }) => ({ scope, key })),
            adoptedReleaseVersion: role.adoptedReleaseVersion,
            adoptedReleaseFingerprint: role.adoptedReleaseFingerprint,
          },
          permissions: registry.items.map(({ scope, key, labelKey, canGrant }) => ({
            scope,
            key,
            labelKey,
            canGrant,
          })),
          release: {
            version: registry.release.version,
            fingerprint: registry.release.fingerprint,
            tenantAdminDelta: registry.release.tenantAdminDelta.map(({ scope, key }) => ({
              scope,
              key,
            })),
          },
          canChangeGrants: capabilities.canChangeRoleGrants && organizationId === null,
          canAdoptRelease:
            capabilities.canAdoptRoleRelease &&
            organizationId === null &&
            role.builtInKey === 'TenantAdmin' &&
            role.capabilityScope === 'Tenant' &&
            role.adoptedReleaseVersion < registry.release.version,
        };
        break;
      }
    }
    return { ...base, capabilities, ownProfile, profileState, outcome: { kind: 'ready', view } };
  } catch (error) {
    if (
      !(error instanceof PageReadFailure) &&
      !(error instanceof BffSessionError && error.reason === 'response')
    )
      throw error;
    return {
      ...base,
      capabilities,
      ownProfile,
      profileState,
      outcome: {
        kind: 'failure',
        reason: error instanceof PageReadFailure ? error.reason : 'unavailable',
        status: error instanceof PageReadFailure ? error.status : 503,
        retryAfterSeconds: error instanceof PageReadFailure ? error.retryAfterSeconds : null,
      },
    };
  }
}
