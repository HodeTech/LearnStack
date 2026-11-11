import type {
  CustomFieldDefinition,
  CustomFieldPage,
  MembershipCustomFields,
  ConsentHistory,
  ConsentScopes,
  LocalizedText,
  FormLabel,
} from '@learnstack/sdk/administration-server';

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
  canManageCustomFields: boolean;
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
export type PageInvitation = Readonly<{
  invitationId: string;
  email: string;
  organizationScoped: boolean;
  inCurrentScope: boolean;
  offeredRole: PageRole | null;
  roleVersion: string;
  version: string;
  status: 'Pending' | 'Accepted' | 'Revoked' | 'Expired';
  expiresAt: string;
  deliveryStatus: 'Queued' | 'SubmissionAccepted' | 'Failed' | 'Unresolved' | 'Unavailable';
  acceptanceDisposition: 'created' | 'already_member' | null;
  canReissue: boolean;
  canRevoke: boolean;
}>;
export type PageInvitationPolicy = Readonly<{
  days: number;
  present: boolean;
  version: string | null;
  canEdit: boolean;
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
      customFields?: MembershipCustomFields;
      selectedFieldId?: string | null;
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
  | Readonly<{
      kind: 'profile';
      canWrite: boolean;
      customFields?: MembershipCustomFields;
      consents?: ConsentHistory;
      consentHistoryIncomplete?: boolean;
      selectedFieldId?: string | null;
    }>
  | Readonly<{
      kind: 'invitations';
      items: readonly PageInvitation[];
      roles: readonly PageRole[];
      endCursor: string | null;
      hasNextPage: boolean;
      limit: number;
    }>
  | Readonly<{ kind: 'invitation'; invitation: PageInvitation }>
  | Readonly<{ kind: 'invitation_policy'; policy: PageInvitationPolicy }>
  | Readonly<{ kind: 'custom_fields'; definitions: CustomFieldPage; limit: number }>
  | Readonly<{
      kind: 'custom_field';
      definition: CustomFieldDefinition;
      revisions: CustomFieldPage;
      expectedIncumbentId: string | null;
      expectedIncumbentVersion: string | null;
    }>;

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
      reason: 'denied' | 'stale' | 'in_progress' | 'key_reuse' | 'unavailable' | 'validation';
      invalidFields?: readonly (
        | 'displayName'
        | 'preferredLocale'
        | 'email'
        | 'offeredRole'
        | 'days'
      )[];
      retryable: boolean;
      customFieldControls?: readonly Readonly<{
        index: number;
        schema: unknown;
        labels: LocalizedText;
        formLabels: readonly FormLabel[] | null;
      }>[];
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

export type NativeConsentSnapshot =
  | Readonly<{
      kind: 'consents';
      scopes: ConsentScopes;
      history: ConsentHistory | null;
      membershipId: string | null;
      csrfToken: string;
      formKeys: readonly string[];
    }>
  | Readonly<{ kind: 'consent_result'; status: number }>;
