import type { EntryProjection, NativeReturnPath } from './session-contract.js';
import type { SessionProjection } from './session-projection.js';

/** Closed, credential-free views. Only native preparation constructs these values. */
export type PageSite = Readonly<{
  displayName: string;
  defaultLocale: string;
  enabledLocales: readonly string[];
  theme: Readonly<{
    primary: string;
    background: string;
    foreground: string;
    muted: string;
  }> | null;
  showPlatformAttribution: boolean;
}>;
export type PageCapabilities = Readonly<{
  canReadTenantMembers: boolean;
  canReadOrganizationMembers: boolean;
  canWriteMemberProfiles: boolean;
  canManageMemberships: boolean;
  canReadRoles: boolean;
  canChangeRoleGrants: boolean;
  canAdoptRoleRelease: boolean;
  canWriteOwnProfile: boolean;
  canManageOrganizations: boolean;
}>;
export type PageProfile = Readonly<{
  displayName: string | null;
  preferredLocale: string | null;
  version: string;
}>;
export type PageFailure = 'denied' | 'not_found' | 'invalid_cursor' | 'unavailable' | 'busy';
export type PageGrant = Readonly<{ scope: 'Tenant' | 'Organization'; key: string }>;
export type PageRole = Readonly<{
  roleId: string;
  builtInKey:
    | 'TenantAdmin'
    | 'OrgAdmin'
    | 'Instructor'
    | 'OrgInstructor'
    | 'Learner'
    | 'Guardian'
    | 'Editor';
  capabilityScope: 'Tenant' | 'Organization';
  version: string;
}>;
export type PageRoleDetail = PageRole &
  Readonly<{
    grants: readonly PageGrant[];
    adoptedReleaseVersion: number;
    adoptedReleaseFingerprint: string;
  }>;
export type PagePermission = PageGrant & Readonly<{ labelKey: string; canGrant: boolean }>;
export type PageRelease = Readonly<{
  version: number;
  fingerprint: string;
  tenantAdminDelta: readonly PageGrant[];
}>;
export type PageMember = Readonly<{
  membershipId: string;
  displayName: string | null;
  preferredLocale: string | null;
  status: 'Active' | 'Suspended' | 'Revoked';
  inCurrentScope: boolean;
  organizationScoped: boolean;
}>;
export type PageMemberDetail = PageMember &
  Readonly<{
    version: string;
    roleIds: readonly string[];
    canWriteProfile: boolean;
    canChangeStatus: boolean;
    canReplaceRoles: boolean;
  }>;
export type PageView =
  | Readonly<{ kind: 'studio' | 'portal' }>
  | Readonly<{
      kind: 'members';
      items: readonly PageMember[];
      endCursor: string | null;
      hasNextPage: boolean;
      limit: number;
    }>
  | Readonly<{
      kind: 'member';
      member: PageMemberDetail;
      roles: readonly PageRole[] | null;
    }>
  | Readonly<{ kind: 'roles'; roles: readonly PageRole[] }>
  | Readonly<{
      kind: 'role';
      role: PageRoleDetail;
      permissions: readonly PagePermission[];
      release: PageRelease;
      canChangeGrants: boolean;
      canAdoptRelease: boolean;
    }>
  | Readonly<{ kind: 'profile'; canWrite: boolean }>;

export type NativePageSnapshot = Readonly<{
  target: NativeReturnPath;
  site: PageSite;
  session: SessionProjection;
  scopeKind: 'tenant' | 'organization';
  capabilities: PageCapabilities | null;
  ownProfile: PageProfile | null;
  profileState: 'available' | 'missing' | 'unavailable';
  /** Independent unused identities for the finite forms in this one document. */
  formKeys: readonly string[];
  outcome:
    | Readonly<{ kind: 'ready'; view: PageView }>
    | Readonly<{
        kind: 'failure';
        reason: PageFailure;
        status: 400 | 403 | 404 | 429 | 503;
        retryAfterSeconds: number | null;
      }>;
}>;

export type ScopeChoices = Readonly<{
  session: SessionProjection;
  expiresAt: number;
  contexts: Readonly<{
    items: readonly Readonly<{
      tenantId: string;
      organizationId: string | null;
      tenantName: string;
      organizationName: string | null;
      entryOrigin: string;
      canUseCurrentHost: boolean;
      current: boolean;
    }>[];
    endCursor: string | null;
    hasNextPage: boolean;
  }>;
}>;
/** Private native action recovery. Its body shares the actual response snapshot lifetime. */
export type NativeActionPage = Readonly<
  | {
      kind: 'mutation_error';
      action: string;
      destination: string;
      fields: readonly (readonly [string, string])[];
      reason: 'denied' | 'stale' | 'in_progress' | 'key_reuse' | 'unavailable';
      retryable: boolean;
      roleLabels?: readonly Pick<PageRole, 'roleId' | 'builtInKey' | 'capabilityScope'>[];
    }
  | { kind: 'account_change'; csrfToken: string }
>;
export type NativeEntrySnapshot = Readonly<{
  site: PageSite;
  locale: string;
  view:
    | NativeActionPage
    | Readonly<{ kind: 'entry'; entry: EntryProjection | null; cursor: string | null }>
    | Readonly<{ kind: 'scopes'; selection: ScopeChoices; cursor: string | null }>
    | Readonly<{
        kind: 'failure';
        reason: PageFailure | 'reauthenticate' | 'creation_disabled';
        /** A validated NativeApiError status (400–599), or a fixed local refusal. */
        status?: number;
        code?: string;
        retryAfterSeconds: number | null;
        returnPath: NativeReturnPath;
      }>;
}>;
