import { createAdministrationServerSdk } from '@learnstack/sdk/administration-server';

import { createNativeApiTransport } from './native-api-transport.js';
import type { NativeApiConfiguration } from './native-api-transport.js';
import type { NativeBinding, NativeScope } from './native-contract.js';
import type {
  NativeAdministrationCaller,
  NativeRetainedConsentCaller,
  NativeOperationResult,
  NativeOperation,
} from './native-operation-contract.js';

/** One explicit dispatcher over the generated SDK's finite operations; no generic HTTP capability escapes. */
export function createNativeAdministrationCaller(
  configuration: NativeApiConfiguration,
  transport: typeof fetch,
) {
  return createCaller(configuration, transport) satisfies NativeAdministrationCaller;
}
export function createNativeRetainedConsentCaller(
  configuration: NativeApiConfiguration,
  transport: typeof fetch,
): NativeRetainedConsentCaller {
  return createCaller(configuration, transport);
}
function createCaller(configuration: NativeApiConfiguration, transport: typeof fetch) {
  const request = createNativeApiTransport(configuration, transport);
  return async (
    token: string,
    binding: NativeBinding & Partial<NativeScope>,
    operation: NativeOperation,
    signal: AbortSignal,
  ): Promise<NativeOperationResult> => {
    if (signal.aborted) return { kind: 'cancelled' };
    let failure: unknown;
    const sdk = createAdministrationServerSdk(async (path, init) => {
      try {
        return await request(
          token,
          binding,
          ['listOwnConsentScopes', 'listOwnConsentHistory', 'withdrawOwnFieldConsent'].includes(
            operation.kind,
          )
            ? null
            : (binding as NativeBinding & NativeScope),
          path,
          init,
          signal,
        );
      } catch (error) {
        failure = error;
        throw error;
      }
    });
    const read = { signal };
    const write = (version: string, idempotencyKey: string) => ({
      version,
      idempotencyKey,
      signal,
    });
    let result: NativeOperationResult;
    switch (operation.kind) {
      case 'listCustomFieldDefinitions':
        result = await sdk.listCustomFieldDefinitions({
          ...read,
          cursor: operation.cursor,
          limit: operation.limit,
          status: operation.status,
        });
        break;
      case 'getCustomFieldDefinition':
        result = await sdk.getCustomFieldDefinition(operation.id, read);
        break;
      case 'listCustomFieldRevisions':
        result = await sdk.listCustomFieldRevisions(operation.id, {
          ...read,
          cursor: operation.cursor,
          limit: operation.limit,
        });
        break;
      case 'getMembershipCustomFields':
        result = await sdk.getMembershipCustomFields(operation.id, read);
        break;
      case 'getOwnCustomFields':
        result = await sdk.getOwnCustomFields(read);
        break;
      case 'registerCustomFieldDefinition':
        result = await sdk.registerCustomFieldDefinition(operation.input, {
          ...read,
          idempotencyKey: operation.idempotencyKey,
        });
        break;
      case 'reviseCustomFieldDefinition':
        result = await sdk.reviseCustomFieldDefinition(
          operation.id,
          operation.input,
          write(operation.version, operation.idempotencyKey),
        );
        break;
      case 'publishCustomFieldDefinition':
        result = await sdk.publishCustomFieldDefinition(
          operation.id,
          operation.expectedIncumbentId,
          operation.expectedIncumbentVersion,
          write(operation.version, operation.idempotencyKey),
        );
        break;
      case 'retireCustomFieldDefinition':
        result = await sdk.retireCustomFieldDefinition(
          operation.id,
          write(operation.version, operation.idempotencyKey),
        );
        break;
      case 'discardCustomFieldDefinition':
        result = await sdk.discardCustomFieldDefinition(
          operation.id,
          write(operation.version, operation.idempotencyKey),
        );
        break;
      case 'setMembershipCustomFields':
        result = await sdk.setMembershipCustomFields(
          operation.id,
          operation.patch,
          write(operation.version, operation.idempotencyKey),
        );
        break;
      case 'setOwnCustomFields':
        result = await sdk.setOwnCustomFields(
          operation.patch,
          write(operation.version, operation.idempotencyKey),
        );
        break;
      case 'getOwnFieldConsent':
        result = await sdk.getOwnFieldConsent(
          operation.membershipId,
          operation.definitionId,
          operation.definitionVersion,
          read,
        );
        break;
      case 'listOwnConsentScopes':
        result = await sdk.listOwnConsentScopes({
          ...read,
          cursor: operation.cursor,
          limit: operation.limit,
        });
        break;
      case 'listOwnConsentHistory':
        result = await sdk.listOwnConsentHistory(operation.membershipId, {
          ...read,
          cursor: operation.cursor,
          limit: operation.limit,
        });
        break;
      case 'grantOwnFieldConsent':
        result = await sdk.grantOwnFieldConsent(operation.input, {
          ...read,
          idempotencyKey: operation.idempotencyKey,
        });
        break;
      case 'withdrawOwnFieldConsent':
        result = await sdk.withdrawOwnFieldConsent(operation.membershipId, operation.input, {
          ...read,
          idempotencyKey: operation.idempotencyKey,
        });
        break;

      case 'listMemberships':
        result = await sdk.listMemberships({
          ...read,
          cursor: operation.cursor,
          limit: operation.limit,
        });
        break;
      case 'listOrganizationMemberships':
        result = await sdk.listOrganizationMemberships({
          ...read,
          cursor: operation.cursor,
          limit: operation.limit,
        });
        break;
      case 'listInvitations':
        result = await sdk.listInvitations({
          ...read,
          cursor: operation.cursor,
          limit: operation.limit,
        });
        break;
      case 'getInvitation':
        result = await sdk.getInvitation(operation.id, read);
        break;
      case 'listInvitableRoles':
        result = await sdk.listInvitableRoles(read);
        break;
      case 'getInvitationPolicy':
        result = await sdk.getInvitationPolicy(read);
        break;
      case 'createInvitation':
        result = await sdk.createInvitation(operation.invitation, {
          idempotencyKey: operation.idempotencyKey,
          signal,
        });
        break;
      case 'revokeInvitation':
        result = await sdk.revokeInvitation(
          operation.id,
          write(operation.version, operation.idempotencyKey),
        );
        break;
      case 'reissueInvitation':
        result = await sdk.reissueInvitation(
          operation.id,
          write(operation.version, operation.idempotencyKey),
        );
        break;
      case 'setInvitationLifetime':
        result = await sdk.setInvitationLifetime(operation.days, {
          precondition: operation.precondition,
          idempotencyKey: operation.idempotencyKey,
          signal,
        });
        break;
      case 'getMembership':
        result = await sdk.getMembership(operation.id, read);
        break;
      case 'getOwnProfile':
        result = await sdk.getOwnProfile(read);
        break;
      case 'getOwnContexts':
        result = await sdk.getOwnContexts({
          ...read,
          cursor: operation.cursor,
          limit: operation.limit,
        });
        break;
      case 'getOwnCapabilities':
        result = await sdk.getOwnCapabilities(read);
        break;
      case 'listRoles':
        result = await sdk.listRoles(read);
        break;
      case 'getRole':
        result = await sdk.getRole(operation.id, read);
        break;
      case 'getPermissions':
        result = await sdk.getPermissions({
          ...read,
          sourceReleaseVersion: operation.sourceReleaseVersion,
        });
        break;
      case 'listOrganizations':
        result = await sdk.listOrganizations({
          ...read,
          cursor: operation.cursor,
          limit: operation.limit,
        });
        break;
      case 'getOrganization':
        result = await sdk.getOrganization(operation.id, read);
        break;
      case 'setMembershipProfile':
        result = await sdk.setMembershipProfile(
          operation.id,
          operation.profile,
          write(operation.version, operation.idempotencyKey),
        );
        break;
      case 'setOwnProfile':
        result = await sdk.setOwnProfile(
          operation.profile,
          write(operation.version, operation.idempotencyKey),
        );
        break;
      case 'replaceMembershipRoles':
        result = await sdk.replaceMembershipRoles(
          operation.id,
          operation.roleIds,
          write(operation.version, operation.idempotencyKey),
        );
        break;
      case 'changeMembershipStatus':
        result = await sdk.changeMembershipStatus(
          operation.id,
          operation.status,
          write(operation.version, operation.idempotencyKey),
        );
        break;
      case 'changeRoleGrants':
        result = await sdk.changeRoleGrants(
          operation.id,
          operation.grants,
          write(operation.version, operation.idempotencyKey),
        );
        break;
      case 'adoptRoleManifest':
        result = await sdk.adoptRoleManifest(
          operation.id,
          operation.manifestVersion,
          operation.manifestFingerprint,
          write(operation.version, operation.idempotencyKey),
        );
        break;
      case 'createOrganization':
        result = await sdk.createOrganization(
          { slug: operation.slug, displayName: operation.displayName },
          { idempotencyKey: operation.idempotencyKey, signal },
        );
        break;
      case 'renameOrganization':
        result = await sdk.renameOrganization(
          operation.id,
          operation.displayName,
          write(operation.version, operation.idempotencyKey),
        );
        break;
      case 'archiveOrganization':
        result = await sdk.archiveOrganization(
          operation.id,
          write(operation.version, operation.idempotencyKey),
        );
        break;
      case 'selectDefaultOrganization':
        result = await sdk.selectDefaultOrganization(
          operation.organizationId,
          write(operation.version, operation.idempotencyKey),
        );
        break;
      default:
        return { kind: 'invalid-request' };
    }
    if (signal.aborted) return { kind: 'cancelled' };
    if (failure) throw failure;
    return result;
  };
}
