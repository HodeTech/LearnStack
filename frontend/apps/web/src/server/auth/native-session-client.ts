import { createHash } from 'node:crypto';

import {
  createNativeApiTransport,
  canonicalUuid,
  exactFields,
  storableText,
  wireObject,
} from './native-api-transport.js';
import type { NativeApiConfiguration } from './native-api-transport.js';
import { NativeApiError } from './native-contract.js';
import type {
  Association,
  NativeBinding,
  NativeScope,
  NativeSessionApi,
  SelfContext,
  SelfContexts,
} from './native-contract.js';

type Configuration = NativeApiConfiguration & Readonly<{ authorityId: string; issuer: string }>;
const digest = (value: unknown): value is string =>
  typeof value === 'string' && /^[0-9a-f]{64}$/.test(value);
const actor = (value: unknown): value is string =>
  canonicalUuid(value) &&
  /^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/.test(value) &&
  value !== '00000000-0000-7000-8000-000000000001';
function cursor(value: unknown): value is string {
  return (
    typeof value === 'string' &&
    value.length > 0 &&
    value.length <= 1024 &&
    /^[A-Za-z0-9_-]+$/.test(value) &&
    Buffer.from(value, 'base64url').toString('base64url') === value
  );
}
function association(value: Record<string, unknown>): Association {
  if (!canonicalUuid(value.authorityId) || !digest(value.subjectKey) || !actor(value.actorId))
    throw new NativeApiError('response');
  return Object.freeze({
    authorityId: value.authorityId,
    subjectKey: value.subjectKey,
    actorId: value.actorId,
  });
}

/** Exact navigation wire projection; choices and opaque cursors remain suggestions, not admission. */
export function nativeContextPage(value: unknown, limit: number): SelfContexts {
  if (!Number.isInteger(limit) || limit < 1 || limit > 20)
    throw new NativeApiError('configuration');
  if (
    !exactFields(value, ['items', 'pageInfo']) ||
    !Array.isArray(value.items) ||
    value.items.length > limit ||
    !exactFields(value.pageInfo, ['nextCursor', 'previousCursor', 'hasNext', 'hasPrevious']) ||
    value.pageInfo.previousCursor !== null ||
    value.pageInfo.hasPrevious !== false ||
    typeof value.pageInfo.hasNext !== 'boolean'
  )
    throw new NativeApiError('response');
  const endCursor = value.pageInfo.nextCursor;
  if ((endCursor !== null && !cursor(endCursor)) || value.pageInfo.hasNext !== (endCursor !== null))
    throw new NativeApiError('response');
  const items: SelfContext[] = value.items.map((item: unknown) => {
    if (
      !exactFields(item, [
        'tenantId',
        'organizationId',
        'tenantName',
        'organizationName',
        'entryOrigin',
        'canUseCurrentHost',
      ]) ||
      !canonicalUuid(item.tenantId) ||
      item.tenantId === '00000000-0000-7000-8000-000000000002' ||
      !(item.organizationId === null || canonicalUuid(item.organizationId)) ||
      !storableText(item.tenantName, 200) ||
      !(item.organizationName === null || storableText(item.organizationName, 200)) ||
      typeof item.entryOrigin !== 'string' ||
      typeof item.canUseCurrentHost !== 'boolean'
    )
      throw new NativeApiError('response');
    let origin: URL;
    try {
      origin = new URL(item.entryOrigin);
    } catch {
      throw new NativeApiError('response');
    }
    if (origin.protocol !== 'https:' || origin.origin !== item.entryOrigin)
      throw new NativeApiError('response');
    return Object.freeze({
      tenantId: item.tenantId,
      organizationId: item.organizationId,
      tenantName: item.tenantName,
      organizationName: item.organizationName,
      entryOrigin: item.entryOrigin,
      canUseCurrentHost: item.canUseCurrentHost,
    });
  });
  return Object.freeze({
    items: Object.freeze(items),
    endCursor,
    hasNextPage: value.pageInfo.hasNext,
  });
}

/** Same unsigned big-endian framing and RFC UUID bytes as IdentitySubjectKey.Compute. */
export function nativeSubjectKey(authorityId: string, subject: string): string {
  if (!canonicalUuid(authorityId) || !storableText(subject, 255))
    throw new NativeApiError('identity');
  const hash = createHash('sha256');
  for (const bytes of [
    Buffer.from('learnstack.identity.subject.v1', 'utf8'),
    Buffer.from(authorityId.replaceAll('-', ''), 'hex'),
    Buffer.from(subject, 'utf8'),
  ]) {
    const length = Buffer.alloc(4);
    length.writeUInt32BE(bytes.length);
    hash.update(length);
    hash.update(bytes);
  }
  return hash.digest('hex');
}

/** Four exact native entry calls. No response creates scope or confirms a provider association on its own. */
export function createNativeSessionApi(
  configuration: Configuration,
  transport: typeof fetch,
): NativeSessionApi {
  const request = createNativeApiTransport(configuration, transport);
  const authorityId = configuration.authorityId;
  const issuer = configuration.issuer;
  let address: URL;
  try {
    address = new URL(issuer);
  } catch {
    throw new NativeApiError('configuration');
  }
  if (
    !canonicalUuid(authorityId) ||
    address.href !== issuer ||
    address.username ||
    address.password ||
    address.hash ||
    !['https:', 'http:'].includes(address.protocol)
  )
    throw new NativeApiError('configuration');

  async function read(
    token: string,
    binding: NativeBinding,
    scope: NativeScope | null,
    path: string,
    method: 'GET' | 'POST',
    signal: AbortSignal,
  ) {
    const response = await request(
      token,
      binding,
      scope,
      path,
      {
        method,
        ...(method === 'POST'
          ? { body: '{}', headers: { 'Content-Type': 'application/json' } }
          : {}),
      },
      signal,
    );
    let value: unknown;
    try {
      value = JSON.parse(
        new TextDecoder('utf-8', { fatal: true }).decode(await response.arrayBuffer()),
      ) as unknown;
    } catch {
      throw new NativeApiError(signal.aborted ? 'cancelled' : 'response');
    }
    if (signal.aborted) throw new NativeApiError('cancelled');
    if (response.status >= 400) {
      if (
        !wireObject(value) ||
        value.status !== response.status ||
        typeof value.code !== 'string' ||
        !/^[a-z][a-z0-9_]{0,63}$/.test(value.code) ||
        typeof value.messageKey !== 'string' ||
        !value.messageKey.startsWith('lockey_')
      )
        throw new NativeApiError('response');
      const retry = response.headers.get('Retry-After');
      const retryAfterSeconds =
        response.status === 429 &&
        retry !== null &&
        /^[1-9][0-9]{0,4}$/.test(retry) &&
        Number(retry) <= 86400
          ? Number(retry)
          : undefined;
      throw new NativeApiError('provider_refused', response.status, retryAfterSeconds);
    }
    if (response.headers.has('ETag') || response.headers.has('Idempotency-Replayed'))
      throw new NativeApiError('response');
    return { response, value };
  }
  function ordinary(response: Response) {
    if (response.status !== 200 || response.headers.has('Location'))
      throw new NativeApiError('response');
  }
  const api: NativeSessionApi = {
    async self(token, binding, signal) {
      const { response, value } = await read(
        token,
        binding,
        null,
        '/api/v1/identity/self',
        'GET',
        signal,
      );
      ordinary(response);
      if (exactFields(value, ['state']) && value.state === 'missing')
        return Object.freeze({ state: 'missing' as const });
      if (
        !exactFields(value, ['state', 'authorityId', 'subjectKey', 'actorId']) ||
        value.state !== 'active'
      )
        throw new NativeApiError('response');
      return Object.freeze({ state: 'active' as const, association: association(value) });
    },
    async bootstrap(token, binding, signal) {
      const { response, value } = await read(
        token,
        binding,
        null,
        '/api/v1/identity/self/bootstrap',
        'POST',
        signal,
      );
      if (
        (response.status === 200
          ? response.headers.has('Location')
          : response.status !== 201 ||
            response.headers.get('Location') !== '/api/v1/identity/self') ||
        !exactFields(value, ['state', 'authorityId', 'subjectKey', 'actorId']) ||
        value.state !== 'active'
      )
        throw new NativeApiError('response');
      return association(value);
    },
    async contexts(token, binding, page, signal) {
      if (
        (page.cursor !== undefined && !cursor(page.cursor)) ||
        (page.limit !== undefined &&
          (!Number.isInteger(page.limit) || page.limit < 1 || page.limit > 2147483647))
      )
        throw new NativeApiError('configuration');
      const limit = Math.min(page.limit ?? 20, 20);
      const query = new URLSearchParams({ limit: String(limit) });
      if (page.cursor !== undefined) query.set('cursor', page.cursor);
      const { response, value } = await read(
        token,
        binding,
        null,
        '/api/v1/identity/self/contexts?' + query.toString(),
        'GET',
        signal,
      );
      ordinary(response);
      return nativeContextPage(value, limit);
    },
    async admit(token, binding, signal) {
      const scope = Object.freeze({
        tenantId: binding.tenantId,
        organizationId: binding.organizationId,
      });
      const { response, value } = await read(
        token,
        binding,
        scope,
        '/api/v1/identity/session-scope/admissions',
        'POST',
        signal,
      );
      ordinary(response);
      if (
        !exactFields(value, [
          'actorId',
          'tenantId',
          'organizationId',
          'authorityId',
          'subjectKey',
        ]) ||
        value.tenantId !== scope.tenantId ||
        value.organizationId !== scope.organizationId
      )
        throw new NativeApiError('response');
      return Object.freeze({ ...association(value), ...scope });
    },
    verifyAssociation(identity, witness) {
      if (
        identity.issuer !== issuer ||
        witness.authorityId !== authorityId ||
        !actor(witness.actorId) ||
        !digest(witness.subjectKey) ||
        witness.subjectKey !== nativeSubjectKey(authorityId, identity.subject) ||
        (identity.actorId !== null && identity.actorId !== witness.actorId)
      )
        throw new NativeApiError('identity');
    },
  };
  return Object.freeze(api);
}
