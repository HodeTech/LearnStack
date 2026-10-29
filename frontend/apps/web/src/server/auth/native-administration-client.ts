import { createAdministrationServerSdk } from '@learnstack/sdk/administration-server';

import { createNativeApiTransport } from './native-api-transport.js';
import type { NativeApiConfiguration } from './native-api-transport.js';
import type {
  NativeAdministrationCaller,
  NativeOperationResult,
} from './native-operation-contract.js';

/** One explicit dispatcher over the generated SDK's 21 operations; no generic HTTP capability escapes. */
export function createNativeAdministrationCaller(
  configuration: NativeApiConfiguration,
  transport: typeof fetch,
): NativeAdministrationCaller {
  const request = createNativeApiTransport(configuration, transport);
  return async (token, binding, operation, signal) => {
    if (signal.aborted) return { kind: 'cancelled' };
    let failure: unknown;
    const sdk = createAdministrationServerSdk(async (path, init) => {
      try {
        return await request(token, binding, binding, path, init, signal);
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
        result = await sdk.getPermissions(read);
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
