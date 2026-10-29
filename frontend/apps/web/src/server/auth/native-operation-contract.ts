import type {
  AdministrationServerSdk,
  GrantInput,
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
        | 'listOrganizations';
    }> &
      Page)
  | Readonly<{ kind: 'getMembership' | 'getRole' | 'getOrganization'; id: string }>
  | Readonly<{ kind: 'getOwnProfile' | 'getOwnCapabilities' | 'listRoles' | 'getPermissions' }>
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
  | (Readonly<{ kind: 'selectDefaultOrganization'; organizationId: string }> & Write);

export type NativeOperationResult = Awaited<
  ReturnType<AdministrationServerSdk[keyof AdministrationServerSdk]>
>;
export type NativeAdministrationCaller = (
  token: string,
  binding: NativeBinding & NativeScope,
  operation: NativeOperation,
  signal: AbortSignal,
) => Promise<NativeOperationResult>;
