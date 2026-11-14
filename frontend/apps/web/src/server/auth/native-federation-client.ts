import { FEDERATION_ALIAS } from './federation-alias.js';
import { canonicalUuid, createNativeApiTransport, exactFields } from './native-api-transport.js';
import type { NativeApiConfiguration } from './native-api-transport.js';
import { NativeApiError } from './native-contract.js';
import type { NativeBinding, NativeScope } from './native-contract.js';
import { nativePublicSdk } from './native-invitation-http.js';
import type { FederationSignInOption, NativeFederationTestApi } from './session-contract.js';

const version = (value: unknown): value is string =>
  typeof value === 'string' && /^(0|[1-9][0-9]{0,18})$/.test(value);
const outcomes = [
  'applied',
  'failed',
  'uncertain',
  'pending',
  'drift',
  'recorded',
  'expired',
  'superseded',
] as const;

/**
 * ADR-0099 Amendment 6 (S-B): the BFF-only federation test owner. It is not part of the Studio administration
 * dispatcher, so no browser form can reach it. `start` reads the connection and begins its test window with the
 * administrator's token; the attempt identifier it returns stays in the session owner. `record` reports the closed
 * result with the federation_test access token, which alone carries the `federation.test_record` scope.
 */
export function createNativeFederationTestApi(
  configuration: NativeApiConfiguration,
  transport: typeof fetch,
): NativeFederationTestApi {
  const request = createNativeApiTransport(configuration, transport);
  async function call(
    token: string,
    binding: NativeBinding & NativeScope,
    path: string,
    init: RequestInit,
    signal: AbortSignal,
  ): Promise<unknown> {
    const scope = Object.freeze({
      tenantId: binding.tenantId,
      organizationId: binding.organizationId,
    });
    const response = await request(token, binding, scope, path, init, signal);
    let value: unknown;
    try {
      value = JSON.parse(
        new TextDecoder('utf-8', { fatal: true }).decode(await response.arrayBuffer()),
      ) as unknown;
    } catch {
      throw new NativeApiError(signal.aborted ? 'cancelled' : 'response');
    }
    if (signal.aborted) throw new NativeApiError('cancelled');
    // Problem bodies are never surfaced; only the closed status classifies the refusal.
    if (response.status >= 400) throw new NativeApiError('provider_refused', response.status);
    if (response.status !== 200) throw new NativeApiError('response');
    return value;
  }
  function receipt(value: unknown) {
    if (
      !exactFields(value, ['connectionId', 'version', 'outcome', 'reason', 'testAttemptId']) ||
      !canonicalUuid(value.connectionId) ||
      !version(value.version) ||
      !outcomes.includes(value.outcome as (typeof outcomes)[number]) ||
      !(value.reason === null || typeof value.reason === 'string') ||
      !(value.testAttemptId === null || canonicalUuid(value.testAttemptId))
    )
      throw new NativeApiError('response');
    return value as Readonly<{
      connectionId: string;
      version: string;
      outcome: (typeof outcomes)[number];
      testAttemptId: string | null;
    }>;
  }
  const json = (body: unknown, current: string): RequestInit => ({
    method: 'POST',
    body: JSON.stringify(body),
    headers: { 'Content-Type': 'application/json', 'If-Match': '"' + current + '"' },
  });
  return Object.freeze({
    async start(token, binding, signal) {
      const detail = await call(
        token,
        binding,
        '/api/v1/identity/federation',
        { method: 'GET' },
        signal,
      );
      if (!exactFields(detail, ['connection', 'provider']) || typeof detail.provider !== 'string')
        throw new NativeApiError('response');
      const connection = detail.connection as Record<string, unknown> | null;
      if (
        connection === null ||
        typeof connection !== 'object' ||
        typeof connection.alias !== 'string' ||
        !FEDERATION_ALIAS.test(connection.alias) ||
        !version(connection.version) ||
        !canonicalUuid(connection.connectionId)
      )
        throw new NativeApiError(connection === null ? 'provider_refused' : 'response', 404);
      // Only a Draft connection begins a window; anything else is a conflict the page explains.
      if (connection.status !== 'draft') throw new NativeApiError('provider_refused', 409);
      const started = receipt(
        await call(
          token,
          binding,
          '/api/v1/identity/federation/test-starts',
          json({}, connection.version),
          signal,
        ),
      );
      if (
        started.outcome !== 'applied' ||
        started.testAttemptId === null ||
        started.connectionId !== connection.connectionId
      )
        throw new NativeApiError('provider_refused', 409);
      return Object.freeze({
        alias: connection.alias,
        attemptId: started.testAttemptId,
        version: started.version,
        connectionId: started.connectionId,
      });
    },
    async record(token, binding, input, signal) {
      if (
        !exactFields(input, ['version', 'attemptId', 'passed']) ||
        !version(input.version) ||
        !canonicalUuid(input.attemptId) ||
        typeof input.passed !== 'boolean'
      )
        throw new NativeApiError('configuration');
      const recorded = receipt(
        await call(
          token,
          binding,
          '/api/v1/identity/federation/test-results',
          json({ testAttemptId: input.attemptId, passed: input.passed }, input.version),
          signal,
        ),
      );
      return Object.freeze({ outcome: recorded.outcome });
    },
  });
}

/**
 * ADR-0099 § Sign-in exposure: the anonymous, host-bound public read, credential-free. Any refusal, malformed body or
 * transport failure is null, which hides only the federation option.
 */
export function createPublicSignInReader(
  configuration: Readonly<{ apiOrigin: string; secret: string }>,
  transport: typeof fetch,
) {
  return async (
    binding: NativeBinding,
    signal: AbortSignal,
  ): Promise<FederationSignInOption | null> => {
    const result = await nativePublicSdk(
      configuration,
      { host: binding.host, peer: binding.source, method: 'GET', target: '/api/auth/signin' },
      signal,
      transport,
      '/api/v1/public/sign-in',
    ).getSignIn({ signal });
    if (result.kind !== 'success') return null;
    const federation = (result.data as { federation?: unknown }).federation;
    if (
      !federation ||
      typeof federation !== 'object' ||
      !exactFields(federation, ['alias', 'displayName']) ||
      typeof federation.alias !== 'string' ||
      !FEDERATION_ALIAS.test(federation.alias) ||
      typeof federation.displayName !== 'string'
    )
      return null;
    return Object.freeze({ alias: federation.alias, displayName: federation.displayName });
  };
}
