import type {
  CustomFieldDefinition,
  CustomFieldPage,
  MembershipCustomFields,
  ConsentHistory,
  ConsentScopes,
  LocalizedText,
  FormLabel,
  FederationConnection,
  OwnDataRequestPage,
  RetainedDataRequestScopePage,
  SubjectDataRequest,
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
  canManageFederation: boolean;
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
/**
 * ADR-0099 § Sign-in exposure and § User experience: the nonsecret connection detail, the provider readback verdict and
 * the values the administrator pastes into the IdP, all derived server-side from the alias and the realm issuer.
 */
export type PageFederation = Readonly<{
  connection: FederationConnection | null;
  provider: 'not_read' | 'matches' | 'drift' | 'unavailable';
  values: Readonly<{ redirectUri: string; entityId: string; descriptorUrl: string }> | null;
}>;
/**
 * ADR-0106 § 6: who a request concerns, exactly as the member list shows a member; never a role or a User. The
 * Membership is the one whose read named the person, so it has a member page of its own.
 */
export type PageDataRequestSubject = Readonly<{
  membershipId: string;
  displayName: string | null;
  status: 'Active' | 'Suspended' | 'Revoked';
  organizationScoped: boolean;
}>;
/**
 * ADR-0106 §§ 3, 6, 13: an administrator's receipt of a request of the context's group, for one page: status and
 * counts, never content. At most 20 of the frozen Membership identifiers are kept, with their count. The subject is
 * the member-list projection of a Membership of the snapshot that could be read; `revoked` when every Membership of
 * the snapshot is known to be Revoked, so none was read (no member page exists); `null` when no read named it.
 */
export type PageDataRequest = Readonly<
  Omit<SubjectDataRequest, 'membershipIds'> & {
    membershipIds: readonly string[];
    membershipCount: number;
    subject: PageDataRequestSubject | 'revoked' | null;
  }
>;
/** The closed outcome a Studio data-request action reports on its request's page after the 303. */
export type DataRequestAdministrationResult = 'export' | 'erasure' | 'cancelled' | 'revoked';
/**
 * The closed refusals the administrator's data-request routes answer in `errors.$` or `errors.artifact` (409
 * `business_rule_violation`), without their `lockey_` prefix; `unknown` is a reason this release does not name.
 */
export type DataRequestAdministrationRefusal =
  | 'request_already_active'
  | 'blocked_hold'
  | 'blocked_last_administrator'
  | 'subject_export_available'
  | 'subject_membership_current'
  | 'subject_is_requester'
  | 'subject_not_admitted'
  | 'snapshot_too_large'
  | 'data_export_unavailable'
  | 'data_request_window_ended'
  | 'data_request_not_cancellable'
  | 'unknown';
export type PageView =
  | Readonly<{ kind: 'studio' | 'portal' }>
  | Readonly<{ kind: 'federation'; federation: PageFederation }>
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
      /**
       * This member's requests among the newest 100 of the exact current group; present only for a membership
       * administrator whose context is the member's group. `items` is null when the list could not be read, and
       * `complete` is false when older requests were not read.
       */
      dataRequests?: Readonly<{
        items: readonly PageDataRequest[] | null;
        complete: boolean;
        preparedAt: string;
      }>;
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
  | Readonly<{
      kind: 'data_requests';
      items: readonly PageDataRequest[];
      endCursor: string | null;
      hasNextPage: boolean;
      /** The instant the page was prepared: a scheduled erasure past `executeAt` reads as due. */
      preparedAt: string;
    }>
  | Readonly<{
      kind: 'data_request';
      request: PageDataRequest;
      result: DataRequestAdministrationResult | null;
      preparedAt: string;
    }>
  | Readonly<{
      /**
       * ADR-0106 § 3: the erasure filing for a Revoked Membership, which has no member page. Nothing about the
       * Membership is read (the API refuses a Revoked target's detail); `null` when the page was opened without one.
       */
      kind: 'data_request_erasure';
      membershipId: string | null;
    }>
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
      /** ADR-0099: the closed field names to correct, or the settled outcome; never a secret or provider text. */
      federation?: Readonly<{ invalid: readonly string[]; outcome: string | null }>;
      /** ADR-0106: a data-request action's closed refusal, or null for a generic outcome; never API text. */
      dataRequest?: Readonly<{ refusal: DataRequestAdministrationRefusal | null }>;
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

/** The closed outcome a data-request action reports on the fixed GET after its 303 (never a replayed POST). */
export type DataRequestResult = 'export' | 'erasure' | 'closure' | 'cancelled' | 'revoked';
/**
 * ADR-0106 § 9, ADR-0107 §§ 4, 6 and 7: the person's own data-request document. The session mode shows the live
 * context's group; the retained mode first lists the groups the entry owner retains, then one named group.
 */
export type NativeDataRequestSnapshot =
  | Readonly<{
      kind: 'data_requests';
      mode: 'session' | 'entry';
      /** The session's group, for the explanation only; null in the retained mode. */
      scope: 'tenant' | 'organization' | null;
      /** The retained group shown (`tenant` or an organization UUIDv7); null while choosing one. */
      group: string | null;
      scopes: RetainedDataRequestScopePage | null;
      requests: OwnDataRequestPage | null;
      step: 'erasure' | 'closure' | null;
      result: DataRequestResult | null;
      /** The instant the document was prepared: an erasure past `executeAt` but still scheduled is due. */
      preparedAt: string;
      /**
       * The retained mode's owner expiry: the instant the entry stops serving this page (at most five minutes from the
       * start of sign-in, never extended). Null in the session mode.
       */
      entryExpiresAt: string | null;
      csrfToken: string;
      formKeys: readonly string[];
    }>
  | Readonly<{ kind: 'data_request_result'; status: number }>;
