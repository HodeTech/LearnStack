import { createAuthenticatedServerSdk } from '@learnstack/sdk/authenticated-server';

import { createNativeApiTransport } from './native-api-transport.js';
import type { NativeApiConfiguration } from './native-api-transport.js';
import { BffProviderError } from './provider-error.js';
import type { SessionDependencies } from './session-contract.js';

/** One AccountSite capability, always bound to the owner's API-admitted native scope. */
export function createAccountSiteCaller(
  configuration: NativeApiConfiguration,
  transport: typeof fetch,
): SessionDependencies['account'] {
  const request = createNativeApiTransport(configuration, transport);
  return async (accessToken, binding, caller) => {
    const scope = Object.freeze({
      tenantId: binding.tenantId,
      organizationId: binding.organizationId,
    });
    let failure: unknown;
    const sdk = createAuthenticatedServerSdk(async (path, init) => {
      try {
        if (path !== '/api/v1/account/site' || init.method !== 'GET')
          throw new BffProviderError('configuration');
        return await request(accessToken, binding, scope, path, init, caller);
      } catch (error) {
        failure = error;
        throw error;
      }
    });
    const result = await sdk.getAccountSite({ signal: caller });
    if (caller.aborted) throw new BffProviderError('cancelled');
    if (failure) throw failure;
    // AccountSite alone has the separate identity-refusal rule; administration
    // 401/403/404 responses never change ownership through this caller.
    if (result.kind !== 'success')
      throw new BffProviderError(
        result.kind === 'api-error' && [403, 404].includes(result.status)
          ? 'identity'
          : result.kind === 'api-error'
            ? 'provider_refused'
            : result.kind === 'transport-error'
              ? 'transport'
              : 'response',
      );
    if (
      result.data.tenantId !== scope.tenantId ||
      result.data.organizationId !== scope.organizationId
    )
      throw new BffProviderError('identity');
    return Object.freeze({
      actorId: result.data.actorId.toLowerCase(),
      tenantId: result.data.tenantId,
      organizationId: result.data.organizationId,
      displayName: result.data.site.displayName,
    });
  };
}
