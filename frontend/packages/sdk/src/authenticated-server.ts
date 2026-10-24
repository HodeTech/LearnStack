import type { ApiResult } from './api-result.js';
import type { AccountSite } from './authenticated-contract.js';
import { appError, isSuccess, parseProblem } from './response-policy.js';

export type AuthenticatedFetch = (path: string, init: RequestInit) => Promise<Response>;
export type AuthenticatedServerSdk = {
  getAccountSite(options?: Readonly<{ signal?: AbortSignal }>): Promise<ApiResult<AccountSite>>;
};
const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
function identifier(value: unknown) {
  return (
    typeof value === 'string' &&
    uuid.test(value) &&
    value !== '00000000-0000-0000-0000-000000000000'
  );
}
export function isAccountSite(value: unknown): value is AccountSite {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return false;
  const object = value as Record<string, unknown>;
  return (
    identifier(object.actorId) &&
    identifier(object.tenantId) &&
    (object.organizationId === null || identifier(object.organizationId)) &&
    isSuccess('GetPublicSite', object.site)
  );
}

/** One exact authenticated GET. Credentials and trusted hop options belong to its injected caller. */
export function createAuthenticatedServerSdk(
  transport: AuthenticatedFetch,
): AuthenticatedServerSdk {
  return Object.freeze({
    async getAccountSite(
      options?: Readonly<{ signal?: AbortSignal }>,
    ): Promise<ApiResult<AccountSite>> {
      const signal = options?.signal;
      if (signal?.aborted) return { kind: 'cancelled' };
      let response: Response;
      try {
        response = await transport('/api/v1/account/site', {
          method: 'GET',
          cache: 'no-store',
          headers: { Accept: 'application/json' },
          signal,
        });
      } catch {
        return { kind: signal?.aborted ? 'cancelled' : 'transport-error' };
      }
      if (signal?.aborted) return { kind: 'cancelled' };
      if (response.status !== 200 && response.status < 400)
        return { kind: 'invalid-response', reason: 'unexpected-status' };
      let body: unknown;
      try {
        body = await response.json();
      } catch (error) {
        if (signal?.aborted) return { kind: 'cancelled' };
        return error instanceof SyntaxError
          ? { kind: 'invalid-response', reason: 'malformed-json' }
          : { kind: 'transport-error' };
      }
      if (signal?.aborted) return { kind: 'cancelled' };
      if (response.status === 200)
        return isAccountSite(body)
          ? { kind: 'success', status: 200, data: body }
          : { kind: 'invalid-response', reason: 'invalid-success' };
      const problem = parseProblem(body, response.status);
      return problem
        ? {
            kind: 'api-error',
            status: response.status,
            problem,
            error: appError(problem, response.headers.get('Retry-After')),
          }
        : { kind: 'invalid-response', reason: 'invalid-problem' };
    },
  });
}
