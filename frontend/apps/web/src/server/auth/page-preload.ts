import type {
  CustomFieldDefinition,
  CustomFieldPage,
  MembershipCustomFields,
  ConsentHistory,
  ConsentScopes,
  FederationDetail,
  Invitation,
  InvitationPage,
  InvitationPolicy,
  InvitableRoles,
  MembershipDetail,
  MembershipPage,
  OwnMembershipProfile,
  PermissionRegistry,
  RoleDetail,
  RoleDirectory,
  SubjectDataRequest,
  SubjectDataRequestPage,
} from '@learnstack/sdk/administration-server';

import type { NativeOperation, NativeOperationResult } from './native-operation-contract.js';
import type {
  DataRequestAdministrationResult,
  NativePageSnapshot,
  PageCapabilities,
  PageDataRequest,
  PageDataRequestSubject,
  PageFailure,
  PageInvitation,
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
  kind:
    | 'studio'
    | 'portal'
    | 'members'
    | 'member'
    | 'roles'
    | 'role'
    | 'profile'
    | 'invitations'
    | 'invitation'
    | 'invitation_policy'
    | 'custom_fields'
    | 'custom_field'
    | 'federation'
    | 'data_requests'
    | 'data_request';
  id?: string;
  cursor?: string;
  limit: number;
  selectedFieldId?: string | null;
  consentCursor?: string;
  /** The closed outcome a data-request action redirected with; only the request page reads it. */
  result?: DataRequestAdministrationResult | null;
}>;
/** ADR-0106 § 6: the data-request list resolves one subject per item, so its page size is fixed. */
const DATA_REQUEST_PAGE = 20;
/** The member page checks this member's requests among the newest of the group, one page at most. */
const MEMBER_DATA_REQUEST_PAGE = 100;
/** A page keeps at most this many of a request's frozen Membership identifiers (the API sends up to 256). */
const KEPT_MEMBERSHIPS = 20;
/** Subject reads run at most this many at a time, so a list neither bursts nor serializes twenty round trips. */
const SUBJECT_READS = 4;

export function nativeDocumentPlan(target: string, flight: boolean): NativeDocumentPlan {
  const parsed = bffDocumentQuery(target, flight);
  const path = parsed.target;
  let kind: NativeDocumentPlan['kind'];
  switch (path) {
    case '/studio':
      kind = 'studio';
      break;
    case '/portal':
      kind = 'portal';
      break;
    case '/portal/profile':
      kind = 'profile';
      break;
    case '/studio/members':
      kind = 'members';
      break;
    case '/studio/custom-fields':
      kind = 'custom_fields';
      break;
    case '/studio/roles':
      kind = 'roles';
      break;
    case '/studio/invitations':
      kind = 'invitations';
      break;
    case '/studio/invitation-policy':
      kind = 'invitation_policy';
      break;
    case '/studio/federation':
      kind = 'federation';
      break;
    case '/studio/data-requests':
      kind = 'data_requests';
      break;
    default:
      if (path.startsWith('/studio/members/')) kind = 'member';
      else if (path.startsWith('/studio/roles/')) kind = 'role';
      else if (path.startsWith('/studio/invitations/')) kind = 'invitation';
      else if (path.startsWith('/studio/custom-fields/')) kind = 'custom_field';
      else if (path.startsWith('/studio/data-requests/')) kind = 'data_request';
      else throw new BffSessionError('configuration');
  }
  return Object.freeze({
    target: path,
    kind,
    ...(kind === 'member' ||
    kind === 'role' ||
    kind === 'invitation' ||
    kind === 'custom_field' ||
    kind === 'data_request'
      ? { id: path.split('/')[3]! }
      : {}),
    ...(parsed.cursor ? { cursor: parsed.cursor } : {}),
    limit:
      kind === 'custom_fields' || kind === 'custom_field'
        ? 20
        : kind === 'data_requests'
          ? DATA_REQUEST_PAGE
          : (parsed.limit ?? 20),
    selectedFieldId: parsed.selectedFieldId,
    consentCursor: parsed.consentCursor,
    ...(kind === 'data_request' ? { result: parsed.result } : {}),
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
  /** The tenant realm issuer the IdP values derive from (ADR-0099); absent shows no values. */
  issuer?: string,
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
  const invitation = (item: Invitation, roles: readonly PageRole[]): PageInvitation => {
    const offeredRole = roles.find((role) => role.roleId === item.roleId) ?? null;
    const inCurrentScope = item.organizationId === organizationId;
    const pending = item.status === 'Pending' || item.status === 'Expired';
    return {
      invitationId: item.invitationId,
      email: item.email,
      organizationScoped: item.organizationId !== null,
      inCurrentScope,
      offeredRole,
      roleVersion: item.roleVersion,
      version: item.version,
      status: item.status,
      expiresAt: item.expiresAt,
      deliveryStatus: item.deliveryStatus,
      acceptanceDisposition: item.acceptanceDisposition,
      canReissue: inCurrentScope && pending && offeredRole?.version === item.roleVersion,
      canRevoke: inCurrentScope && pending,
    };
  };
  /**
   * ADR-0106 § 6: a receipt names its subject by Membership only. The first identifier is read exactly as the member
   * page reads it, and only what the member list shows is kept; a refused read leaves the subject unnamed.
   */
  const subjects = async (
    identifiers: readonly string[],
  ): Promise<ReadonlyMap<string, PageDataRequestSubject | null>> => {
    const found = new Map<string, PageDataRequestSubject | null>();
    const pending = [...new Set(identifiers)];
    let failed = false;
    const worker = async () => {
      while (!failed) {
        const next = pending.shift();
        if (next === undefined) return;
        let result: NativeOperationResult;
        try {
          result = await read({ kind: 'getMembership', id: next });
          if (result.kind === 'cancelled') throw new BffSessionError('cancelled');
        } catch (error) {
          // The first failure stops the other workers taking further identifiers.
          failed = true;
          throw error;
        }
        const value = result.kind === 'success' ? (result.data as MembershipDetail) : null;
        found.set(
          next,
          value && value.membershipId === next
            ? {
                displayName: value.displayName,
                status: value.status,
                organizationScoped: value.organizationId !== null,
              }
            : null,
        );
      }
    };
    // Every worker settles before the page does: no read outlives the preparation that started it.
    const settled = await Promise.allSettled(
      Array.from({ length: Math.min(SUBJECT_READS, pending.length) }, worker),
    );
    for (const outcome of settled) if (outcome.status === 'rejected') throw outcome.reason;
    return found;
  };
  const projectRequest = (
    item: SubjectDataRequest,
    subject: PageDataRequestSubject | null,
  ): PageDataRequest => {
    const { membershipIds, ...receipt } = item;
    return {
      ...receipt,
      membershipIds: membershipIds.slice(0, KEPT_MEMBERSHIPS),
      membershipCount: membershipIds.length,
      subject,
    };
  };
  const named = async (items: readonly SubjectDataRequest[]) => {
    const found = await subjects(items.map((item) => item.membershipIds[0]!));
    return items.map((item) => projectRequest(item, found.get(item.membershipIds[0]!) ?? null));
  };
  try {
    capabilities = await data<PageCapabilities>({ kind: 'getOwnCapabilities' });
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
      case 'profile': {
        const customFields =
          ownProfile === null
            ? undefined
            : await data<MembershipCustomFields>({ kind: 'getOwnCustomFields' });
        const scopes =
          customFields === undefined
            ? undefined
            : await data<ConsentScopes>({ kind: 'listOwnConsentScopes', limit: 20 });
        const consents =
          customFields !== undefined &&
          scopes?.items.some(
            (scope) =>
              scope.membershipId === customFields.membershipId &&
              scope.organizationId === organizationId,
          )
            ? await data<ConsentHistory>({
                kind: 'listOwnConsentHistory',
                membershipId: customFields.membershipId,
                limit: 20,
                cursor: plan.consentCursor,
              })
            : undefined;
        view = {
          kind: 'profile',
          canWrite: capabilities.canWriteOwnProfile && ownProfile !== null,
          customFields,
          consents,
          consentHistoryIncomplete: consents === undefined && scopes?.pageInfo.hasNext === true,
          selectedFieldId: plan.selectedFieldId,
        };
        break;
      }
      case 'invitations': {
        if (!capabilities.canManageMemberships) throw new PageReadFailure('denied', 403);
        const page = await data<InvitationPage>({
          kind: 'listInvitations',
          ...(plan.cursor ? { cursor: plan.cursor } : {}),
          limit: plan.limit,
        });
        const roles = (await data<InvitableRoles>({ kind: 'listInvitableRoles' })).items.map(
          roleChoice,
        );
        view = {
          kind: 'invitations',
          items: page.items.map((item) => invitation(item, roles)),
          roles,
          endCursor:
            page.pageInfo.nextCursor === null ? null : wrapCursor(page.pageInfo.nextCursor),
          hasNextPage: page.pageInfo.hasNext,
          limit: plan.limit,
        };
        break;
      }
      case 'invitation': {
        if (!capabilities.canManageMemberships) throw new PageReadFailure('denied', 403);
        const item = await data<Invitation>({ kind: 'getInvitation', id: plan.id! });
        const roles = (await data<InvitableRoles>({ kind: 'listInvitableRoles' })).items.map(
          roleChoice,
        );
        view = { kind: 'invitation', invitation: invitation(item, roles) };
        break;
      }
      case 'federation': {
        // Tenant-scope only, under the advisory capability; the API enforces every operation.
        if (organizationId !== null || !capabilities.canManageFederation)
          throw new PageReadFailure('denied', 403);
        const detail = await data<FederationDetail>({ kind: 'getFederationConnection' });
        const alias = detail.connection?.alias;
        const base =
          issuer && /^https:\/\/[^/?#]+\/realms\/[A-Za-z0-9_-]+$/.test(issuer) ? issuer : null;
        view = {
          kind: 'federation',
          federation: {
            connection: detail.connection,
            provider: detail.provider,
            values:
              base && alias && /^lsf-[0-9a-f]{32}$/.test(alias)
                ? {
                    redirectUri: base + '/broker/' + alias + '/endpoint',
                    entityId: base,
                    descriptorUrl: base + '/broker/' + alias + '/endpoint/descriptor',
                  }
                : null,
          },
        };
        break;
      }
      case 'invitation_policy': {
        if (organizationId !== null) throw new PageReadFailure('denied', 403);
        const policy = await data<InvitationPolicy>({ kind: 'getInvitationPolicy' });
        view = {
          kind: 'invitation_policy',
          policy: {
            days: policy.days,
            present: policy.present,
            version: policy.version,
            canEdit: policy.canEdit,
          },
        };
        break;
      }
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
        const customFields = await data<MembershipCustomFields>({
          kind: 'getMembershipCustomFields',
          id: plan.id!,
        });
        // ADR-0106 § 3: requests are filed in the context's group, so only an exact-scope member has them here.
        let dataRequests: Extract<PageView, { kind: 'member' }>['dataRequests'];
        if (capabilities.canManageMemberships && exact) {
          // An unavailable list hides only this section's list; the member's own page stays usable.
          const listed = await read({ kind: 'listDataRequests', limit: MEMBER_DATA_REQUEST_PAGE });
          if (listed.kind === 'cancelled') throw new BffSessionError('cancelled');
          const page = listed.kind === 'success' ? (listed.data as SubjectDataRequestPage) : null;
          const subject: PageDataRequestSubject = {
            displayName: member.displayName,
            status: member.status,
            organizationScoped: member.organizationScoped,
          };
          dataRequests = {
            items:
              page?.items
                .filter((item) => item.membershipIds.includes(member.membershipId))
                .map((item) => projectRequest(item, subject)) ?? null,
            complete: page !== null && !page.pageInfo.hasNext,
            preparedAt: new Date().toISOString(),
          };
        }
        view = {
          kind: 'member',
          member,
          roles,
          customFields,
          selectedFieldId: plan.selectedFieldId,
          ...(dataRequests ? { dataRequests } : {}),
        };
        break;
      }
      case 'data_requests': {
        if (!capabilities.canManageMemberships) throw new PageReadFailure('denied', 403);
        const page = await data<SubjectDataRequestPage>({
          kind: 'listDataRequests',
          ...(plan.cursor ? { cursor: plan.cursor } : {}),
          limit: DATA_REQUEST_PAGE,
        });
        view = {
          kind: 'data_requests',
          items: await named(page.items),
          endCursor:
            page.pageInfo.nextCursor === null ? null : wrapCursor(page.pageInfo.nextCursor),
          hasNextPage: page.pageInfo.hasNext,
          preparedAt: new Date().toISOString(),
        };
        break;
      }
      case 'data_request': {
        if (!capabilities.canManageMemberships) throw new PageReadFailure('denied', 403);
        const item = await data<SubjectDataRequest>({ kind: 'getDataRequest', id: plan.id! });
        view = {
          kind: 'data_request',
          request: (await named([item]))[0]!,
          result: plan.result ?? null,
          preparedAt: new Date().toISOString(),
        };
        break;
      }
      case 'custom_fields': {
        if (!capabilities.canManageCustomFields || organizationId !== null)
          throw new PageReadFailure('denied', 403);
        const definitions = await data<CustomFieldPage>({
          kind: 'listCustomFieldDefinitions',
          cursor: plan.cursor,
          limit: 20,
        });
        view = {
          kind: 'custom_fields',
          definitions: {
            ...definitions,
            pageInfo: {
              ...definitions.pageInfo,
              nextCursor:
                definitions.pageInfo.nextCursor === null
                  ? null
                  : wrapCursor(definitions.pageInfo.nextCursor),
            },
          },
          limit: 20,
        };
        break;
      }
      case 'custom_field': {
        if (!capabilities.canManageCustomFields || organizationId !== null)
          throw new PageReadFailure('denied', 403);
        const definition = await data<CustomFieldDefinition>({
          kind: 'getCustomFieldDefinition',
          id: plan.id!,
        });
        const revisions = await data<CustomFieldPage>({
          kind: 'listCustomFieldRevisions',
          id: plan.id!,
          cursor: plan.cursor,
          limit: 20,
        });
        const active = definition.activeRevision;
        view = {
          kind: 'custom_field',
          definition,
          revisions: {
            ...revisions,
            pageInfo: {
              ...revisions.pageInfo,
              nextCursor:
                revisions.pageInfo.nextCursor === null
                  ? null
                  : wrapCursor(revisions.pageInfo.nextCursor),
            },
          },
          expectedIncumbentId: active?.id ?? null,
          expectedIncumbentVersion: active?.version ?? null,
        };
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
        const registry = await data<PermissionRegistry>({
          kind: 'getPermissions',
          sourceReleaseVersion:
            role.adoptedReleaseVersion === 1 ? 1 : role.adoptedReleaseVersion === 2 ? 2 : 3,
        });
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
