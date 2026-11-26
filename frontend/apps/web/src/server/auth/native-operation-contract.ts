import type {
  AdministrationServerSdk,
  CustomFieldInput,
  CustomFieldPatch,
  ConsentGrant,
  FederationConfigurationInput,
  ConsentWithdrawal,
  GrantInput,
  InvitationInput,
  InvitationPolicyPrecondition,
  MembershipState,
  ProfileInput,
} from '@learnstack/sdk/administration-server';

import type { NativeBinding, NativeScope } from './native-contract.js';

type Page = Readonly<{ cursor?: string; limit?: number }>;
type Write = Readonly<{ version: string; idempotencyKey: string }>;
/** Closed native commands: only the owner supplies credentials, current scope and lifetime. */
export type NativeOperation =
  | (Readonly<{
      kind:
        | 'listMemberships'
        | 'listOrganizationMemberships'
        | 'getOwnContexts'
        | 'listOrganizations'
        | 'listInvitations';
    }> &
      Page)
  | Readonly<{
      kind: 'getMembership' | 'getRole' | 'getOrganization' | 'getInvitation';
      id: string;
    }>
  | Readonly<{
      kind:
        | 'getOwnProfile'
        | 'getOwnCapabilities'
        | 'listRoles'
        | 'listInvitableRoles'
        | 'getInvitationPolicy';
    }>
  | Readonly<{ kind: 'getPermissions'; sourceReleaseVersion?: 1 | 2 | 3 }>
  | Readonly<{ kind: 'createInvitation'; invitation: InvitationInput; idempotencyKey: string }>
  | (Readonly<{ kind: 'revokeInvitation' | 'reissueInvitation'; id: string }> & Write)
  | Readonly<{
      kind: 'setInvitationLifetime';
      days: number;
      precondition: InvitationPolicyPrecondition;
      idempotencyKey: string;
    }>
  | (Readonly<{ kind: 'setMembershipProfile'; id: string; profile: ProfileInput }> & Write)
  | (Readonly<{ kind: 'setOwnProfile'; profile: ProfileInput }> & Write)
  | (Readonly<{ kind: 'replaceMembershipRoles'; id: string; roleIds: readonly string[] }> & Write)
  | (Readonly<{ kind: 'changeMembershipStatus'; id: string; status: MembershipState }> & Write)
  | (Readonly<{ kind: 'changeRoleGrants'; id: string; grants: readonly GrantInput[] }> & Write)
  | (Readonly<{
      kind: 'adoptRoleManifest';
      id: string;
      manifestVersion: number;
      manifestFingerprint: string;
    }> &
      Write)
  | Readonly<{
      kind: 'createOrganization';
      idempotencyKey: string;
      slug: string;
      displayName: string;
    }>
  | (Readonly<{ kind: 'renameOrganization'; id: string; displayName: string }> & Write)
  | (Readonly<{ kind: 'archiveOrganization'; id: string }> & Write)
  | (Readonly<{ kind: 'selectDefaultOrganization'; organizationId: string }> & Write)
  | (Readonly<{ kind: 'listCustomFieldDefinitions'; status?: 'Draft' | 'Active' | 'Deprecated' }> &
      Page)
  | (Readonly<{ kind: 'getCustomFieldDefinition' | 'listCustomFieldRevisions'; id: string }> & Page)
  | Readonly<{ kind: 'getMembershipCustomFields'; id: string }>
  | Readonly<{ kind: 'getOwnCustomFields' }>
  | Readonly<{
      kind: 'registerCustomFieldDefinition';
      input: CustomFieldInput | string;
      idempotencyKey: string;
    }>
  | (Readonly<{
      kind: 'reviseCustomFieldDefinition';
      id: string;
      input: CustomFieldInput | string;
    }> &
      Write)
  | (Readonly<{
      kind: 'publishCustomFieldDefinition';
      id: string;
      expectedIncumbentId: string | null;
      expectedIncumbentVersion: string | null;
    }> &
      Write)
  | (Readonly<{
      kind: 'retireCustomFieldDefinition' | 'discardCustomFieldDefinition';
      id: string;
    }> &
      Write)
  | (Readonly<{ kind: 'setMembershipCustomFields'; id: string; patch: CustomFieldPatch | string }> &
      Write)
  | (Readonly<{ kind: 'setOwnCustomFields'; patch: CustomFieldPatch | string }> & Write)
  | Readonly<{
      kind: 'getOwnFieldConsent';
      membershipId: string;
      definitionId: string;
      definitionVersion: number;
    }>
  | Readonly<{ kind: 'grantOwnFieldConsent'; input: ConsentGrant; idempotencyKey: string }>
  // ADR-0099: no Idempotency-Key; the connection revision converges them. No test start or record here.
  | Readonly<{ kind: 'getFederationConnection' }>
  | Readonly<{
      kind: 'configureFederation';
      input: FederationConfigurationInput;
      version: string | null;
    }>
  | Readonly<{ kind: 'rotateFederationSecret'; clientSecret: string; version: string }>
  | Readonly<{ kind: 'renameFederation'; displayName: string; version: string }>
  | Readonly<{
      kind:
        | 'activateFederation'
        | 'disableFederation'
        | 'enableFederation'
        | 'deleteFederation'
        | 'resumeFederationOperation';
      version: string;
    }>
  | RetainedConsentOperation
  | OwnDataRequestOperation
  | GroupDataRequestOperation;
/**
 * ADR-0106 §§ 3, 6, 13: an administrator's data requests in the session context's group. The subject is named only
 * by a Membership of that group; no ticket or chunk operation exists, because an administrator never receives a bundle.
 */
export type GroupDataRequestOperation =
  | (Readonly<{ kind: 'listDataRequests' }> & Page)
  | Readonly<{ kind: 'getDataRequest'; id: string }>
  | Readonly<{
      kind: 'createDataExportForSubject' | 'createDataErasureForSubject';
      membershipId: string;
      idempotencyKey: string;
    }>
  | (Readonly<{ kind: 'cancelDataRequest' | 'revokeDataExport'; id: string }> & Write);
/**
 * ADR-0106 § 9, ADR-0107 § 4: the person's own data requests in the live session's context group. The ticket of a
 * chunk read is a sensitive cursor: it exists only between the download relay and this owner, never in a page or log.
 */
export type OwnDataRequestOperation =
  | (Readonly<{ kind: 'listOwnDataRequests' }> & Page)
  | Readonly<{
      kind: 'createOwnDataExport' | 'createOwnDataErasure' | 'createOwnAccountClosure';
      idempotencyKey: string;
    }>
  | (Readonly<{ kind: 'cancelOwnDataRequest' | 'revokeOwnDataExport'; id: string }> & Write)
  | Readonly<{ kind: 'mintOwnDataExportTicket'; id: string }>
  | Readonly<{ kind: 'readOwnDataExportChunk'; id: string; ticket: string; index: number }>;
/** The closed retained-entry set (ADR-0106 § 9): discovery and the actions in one named group. */
export type RetainedDataRequestOperation =
  | (Readonly<{ kind: 'listRetainedDataRequestScopes' }> & Page)
  | (Readonly<{ kind: 'listRetainedDataRequests'; group: string }> & Page)
  | Readonly<{
      kind:
        | 'createRetainedDataExport'
        | 'createRetainedDataErasure'
        | 'createRetainedAccountClosure';
      group: string;
      idempotencyKey: string;
    }>
  | (Readonly<{
      kind: 'cancelRetainedDataRequest' | 'revokeRetainedDataExport';
      group: string;
      id: string;
    }> &
      Write)
  | Readonly<{ kind: 'mintRetainedDataExportTicket'; group: string; id: string }>
  | Readonly<{
      kind: 'readRetainedDataExportChunk';
      group: string;
      id: string;
      ticket: string;
      index: number;
    }>;
export type NativeRetainedDataRequestCaller = (
  token: string,
  binding: NativeBinding,
  operation: RetainedDataRequestOperation,
  signal: AbortSignal,
) => Promise<NativeOperationResult>;
export type RetainedConsentOperation =
  | (Readonly<{ kind: 'listOwnConsentScopes' }> & Page)
  | (Readonly<{ kind: 'listOwnConsentHistory'; membershipId: string }> & Page)
  | Readonly<{
      kind: 'withdrawOwnFieldConsent';
      membershipId: string;
      input: ConsentWithdrawal;
      idempotencyKey: string;
    }>;
export type NativeRetainedConsentCaller = (
  token: string,
  binding: NativeBinding,
  operation: RetainedConsentOperation,
  signal: AbortSignal,
) => Promise<NativeOperationResult>;

export type NativeOperationResult = Awaited<
  ReturnType<AdministrationServerSdk[keyof AdministrationServerSdk]>
>;
export type NativeAdministrationCaller = (
  token: string,
  binding: NativeBinding & NativeScope,
  operation: NativeOperation,
  signal: AbortSignal,
) => Promise<NativeOperationResult>;
