import type {
  AdministrationFetch,
  AdministrationReadOptions,
  AdministrationResult,
} from './administration-contract.js';
import { object, version } from './administration-policy.js';
import type {
  FederationConfigurationInput,
  FederationConfigureOptions,
  FederationDetail,
  FederationReceipt,
  FederationWriteOptions,
} from './federation-contract.js';
import { appError, parseProblem } from './response-policy.js';
export type * from './federation-contract.js';

const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;
const digest = (value: unknown) => typeof value === 'string' && /^[0-9a-f]{64}$/.test(value);
const instant = (value: unknown) =>
  typeof value === 'string' && value.length <= 40 && !Number.isNaN(Date.parse(value));
const bounded = (value: unknown, max: number, nullable = false) =>
  (nullable && value === null) ||
  (typeof value === 'string' && value.length > 0 && value.length <= max);
const exact = (value: unknown, keys: readonly string[]): value is Record<string, unknown> =>
  object(value) &&
  Object.keys(value).length === keys.length &&
  keys.every((key) => Object.hasOwn(value, key));
const oneOf = (value: unknown, choices: readonly string[]) =>
  typeof value === 'string' && choices.includes(value);

function connection(value: unknown): boolean {
  if (
    !exact(value, [
      'connectionId',
      'protocol',
      'alias',
      'displayName',
      'issuerOrEntityId',
      'status',
      'source',
      'clientId',
      'configurationRevision',
      'configurationDigest',
      'desiredDigest',
      'lastProviderDigest',
      'pending',
      'testExpiresAt',
      'version',
    ])
  )
    return false;
  const pending = value.pending;
  return (
    typeof value.connectionId === 'string' &&
    uuid.test(value.connectionId) &&
    oneOf(value.protocol, ['oidc', 'saml']) &&
    typeof value.alias === 'string' &&
    /^lsf-[0-9a-f]{32}$/.test(value.alias) &&
    bounded(value.displayName, 80) &&
    bounded(value.issuerOrEntityId, 2048) &&
    oneOf(value.status, ['draft', 'test_open', 'test_passed', 'active', 'disabled']) &&
    oneOf(value.source, ['metadata_url', 'discovery_url', 'manual']) &&
    bounded(value.clientId, 255, true) &&
    version(value.configurationRevision) &&
    digest(value.configurationDigest) &&
    digest(value.desiredDigest) &&
    (value.lastProviderDigest === null || digest(value.lastProviderDigest)) &&
    (pending === null ||
      (exact(pending, ['kind', 'startedAt', 'quietUntil']) &&
        oneOf(pending.kind, [
          'configure',
          'rotate_secret',
          'rename',
          'test_start',
          'disable',
          'delete',
        ]) &&
        instant(pending.startedAt) &&
        instant(pending.quietUntil))) &&
    (value.testExpiresAt === null || instant(value.testExpiresAt)) &&
    version(value.version)
  );
}

function receipt(value: unknown): boolean {
  return (
    exact(value, ['connectionId', 'version', 'outcome', 'reason', 'testAttemptId']) &&
    typeof value.connectionId === 'string' &&
    uuid.test(value.connectionId) &&
    version(value.version) &&
    oneOf(value.outcome, [
      'applied',
      'failed',
      'uncertain',
      'pending',
      'drift',
      'recorded',
      'expired',
      'superseded',
    ]) &&
    (value.reason === null ||
      oneOf(value.reason, [
        'provider_refused',
        'drift',
        'owner_mismatch',
        'egress_refused',
        'resubmit_required',
        'provider_unavailable',
        'egress_unavailable',
      ])) &&
    // The attempt identifier belongs to the BFF's server-side test owner, never to these operations.
    value.testAttemptId === null
  );
}

const configureFields = [
  'protocol',
  'displayName',
  'source',
  'documentUrl',
  'issuerOrEntityId',
  'authorizationEndpoint',
  'tokenEndpoint',
  'jwksEndpoint',
  'userInfoEndpoint',
  'singleSignOnEndpoint',
  'signingCertificates',
  'clientId',
  'clientAuthentication',
  'clientSecret',
] as const;
function configuration(input: FederationConfigurationInput): boolean {
  const url = (value: unknown) =>
    value === null ||
    (typeof value === 'string' && value.length <= 2048 && value.startsWith('https://'));
  return (
    exact(input, configureFields) &&
    oneOf(input.protocol, ['oidc', 'saml']) &&
    bounded(input.displayName, 80, true) &&
    oneOf(input.source, ['metadata_url', 'discovery_url', 'manual']) &&
    url(input.documentUrl) &&
    bounded(input.issuerOrEntityId, 2048, true) &&
    url(input.authorizationEndpoint) &&
    url(input.tokenEndpoint) &&
    url(input.jwksEndpoint) &&
    url(input.userInfoEndpoint) &&
    url(input.singleSignOnEndpoint) &&
    Array.isArray(input.signingCertificates) &&
    input.signingCertificates.length <= 2 &&
    input.signingCertificates.every((pem) => bounded(pem, 8192)) &&
    bounded(input.clientId, 255, true) &&
    (input.clientAuthentication === null ||
      oneOf(input.clientAuthentication, ['client_secret_basic', 'client_secret_post'])) &&
    (input.clientSecret === null || bounded(input.clientSecret, 1024))
  );
}

/**
 * ADR-0099 closed federation wrappers for the Studio page. No test start or test record: those belong only to the BFF's
 * server-side test owner (ADR-0099 Amendment 6). No Idempotency-Key is ever sent.
 */
export function createFederationServerSdk(transport: AdministrationFetch) {
  async function request<T>(
    path: string,
    valid: (value: unknown) => boolean,
    options: AdministrationReadOptions | undefined,
    mutation?: Readonly<{ method: 'PUT' | 'POST'; body: unknown; version: string | null }>,
  ): Promise<AdministrationResult<T>> {
    const signal = options?.signal;
    if (signal?.aborted) return { kind: 'cancelled' };
    const headers: Record<string, string> = { Accept: 'application/json' };
    let body: string | undefined;
    if (mutation) {
      if (mutation.version !== null && !version(mutation.version))
        return { kind: 'invalid-request' };
      try {
        body = JSON.stringify(mutation.body);
      } catch {
        return { kind: 'invalid-request' };
      }
      if (new TextEncoder().encode(body).length > 16 * 1024) return { kind: 'invalid-request' };
      headers['Content-Type'] = 'application/json';
      if (mutation.version !== null) headers['If-Match'] = '"' + mutation.version + '"';
    }
    let response: Response;
    try {
      response = await transport(path, {
        method: mutation?.method ?? 'GET',
        cache: 'no-store',
        headers,
        ...(body === undefined ? {} : { body }),
        signal,
      });
    } catch {
      return { kind: signal?.aborted ? 'cancelled' : 'transport-error' };
    }
    if (response.status !== 200 && response.status < 400) {
      await response.body?.cancel().catch(() => undefined);
      return { kind: 'invalid-response', reason: 'unexpected-status' };
    }
    let value: unknown;
    try {
      const text = await response.text();
      if (new TextEncoder().encode(text).length > 256 * 1024)
        return { kind: 'invalid-response', reason: 'invalid-success' };
      value = JSON.parse(text) as unknown;
    } catch {
      return signal?.aborted
        ? { kind: 'cancelled' }
        : { kind: 'invalid-response', reason: 'malformed-json' };
    }
    if (signal?.aborted) return { kind: 'cancelled' };
    if (response.status !== 200) {
      const problem = parseProblem(value, response.status);
      if (!problem) return { kind: 'invalid-response', reason: 'invalid-problem' };
      const error = appError(problem, response.headers.get('Retry-After'));
      return {
        kind: 'api-error',
        status: response.status,
        problem,
        error: error.code === 'concurrency_conflict' ? { code: 'concurrency_conflict' } : error,
      };
    }
    const etag = response.headers.get('ETag');
    const detail = !mutation;
    const present = detail && object(value) && object(value.connection);
    if (
      response.headers.get('Content-Type')?.split(';', 1)[0]?.trim().toLowerCase() !==
        'application/json' ||
      !valid(value) ||
      (present
        ? etag !== '"' + (value as { connection: { version: string } }).connection.version + '"'
        : etag !== null) ||
      response.headers.get('Location') !== null ||
      response.headers.get('Idempotency-Replayed') !== null
    )
      return { kind: 'invalid-response', reason: 'invalid-success' };
    return {
      kind: 'success',
      status: 200,
      data: value as T,
      etag,
      location: null,
      replayed: false,
    };
  }
  const empty = (path: string, options: FederationWriteOptions) =>
    request<FederationReceipt>('/api/v1/identity/federation/' + path, receipt, options, {
      method: 'POST',
      body: {},
      version: options.version,
    });
  return Object.freeze({
    getFederationConnection: (options?: AdministrationReadOptions) =>
      request<FederationDetail>(
        '/api/v1/identity/federation',
        (value) =>
          exact(value, ['connection', 'provider']) &&
          oneOf(value.provider, ['not_read', 'matches', 'drift', 'unavailable']) &&
          (value.connection === null || connection(value.connection)),
        options,
      ),
    configureFederation: (
      input: FederationConfigurationInput,
      options: FederationConfigureOptions,
    ) =>
      configuration(input)
        ? request<FederationReceipt>(
            '/api/v1/identity/federation/configuration',
            receipt,
            options,
            {
              method: 'PUT',
              body: input,
              version: options.version,
            },
          )
        : Promise.resolve({ kind: 'invalid-request' } as const),
    rotateFederationSecret: (clientSecret: string, options: FederationWriteOptions) =>
      bounded(clientSecret, 1024)
        ? request<FederationReceipt>(
            '/api/v1/identity/federation/secret-rotations',
            receipt,
            options,
            {
              method: 'POST',
              body: { clientSecret },
              version: options.version,
            },
          )
        : Promise.resolve({ kind: 'invalid-request' } as const),
    renameFederation: (displayName: string, options: FederationWriteOptions) =>
      bounded(displayName, 80)
        ? request<FederationReceipt>('/api/v1/identity/federation/display-name', receipt, options, {
            method: 'PUT',
            body: { displayName },
            version: options.version,
          })
        : Promise.resolve({ kind: 'invalid-request' } as const),
    activateFederation: (options: FederationWriteOptions) => empty('activation', options),
    disableFederation: (options: FederationWriteOptions) => empty('disablement', options),
    enableFederation: (options: FederationWriteOptions) => empty('enablement', options),
    deleteFederation: (options: FederationWriteOptions) => empty('deletion', options),
    resumeFederationOperation: (options: FederationWriteOptions) => empty('resumption', options),
  });
}
