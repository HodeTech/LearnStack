// @vitest-environment node
import { afterEach, describe, expect, it, vi } from 'vitest';

import { createNativeAdministrationCaller } from './native-administration-client';
import { NATIVE_API_MAX_BYTES } from './native-api-transport';
import type { NativeOperation } from './native-operation-contract';
import {
  createNativeSessionApi,
  nativeContextPage,
  nativeSubjectKey,
} from './native-session-client';
import type { ProviderIdentity } from './provider';

const authorityId = '019a1929-5450-7bbb-8df1-516e8e002501';
const actorId = '019a154b-0000-7000-8000-000000000011';
const tenantId = '019a154b-0000-7000-8000-000000000012';
const organizationId = '019a154b-0000-7000-8000-000000000013';
const configuration = {
  apiOrigin: 'http://127.0.0.1:5100',
  secret: 'private-hop-'.padEnd(48, 'x'),
  nativeSourceSecret: Buffer.from(Array.from({ length: 32 }, (_, index) => index + 1)).toString(
    'base64url',
  ),
  authorityId,
  issuer: 'https://identity.example/realms/learnstack',
};
const binding = { host: 'institution.example.test:3000', source: '203.0.113.19' };
const scope = { ...binding, tenantId, organizationId: null };
const identity: ProviderIdentity = {
  issuer: configuration.issuer,
  subject: 'opaque-subject',
  actorId: null,
  tenantId: null,
  organizationId: null,
  displayLabel: 'Account label',
};
const association = {
  authorityId,
  actorId,
  subjectKey: 'e766989ed59e5e7cc8dcaed58d7d9ca36a53d3d6320cbd4b11da510075b9b61e',
};
const active = { state: 'active', ...association };
const signal = () => new AbortController().signal;
const json = (value: unknown, status = 200, headers: Record<string, string> = {}) =>
  new Response(JSON.stringify(value), {
    status,
    headers: {
      'Content-Type': status < 400 ? 'application/json' : 'application/problem+json',
      ...headers,
    },
  });
const problem = (status = 403) => ({
  type: 'https://errors.learnstack.dev/forbidden',
  title: 'Refused',
  status,
  instance: '/api/v1/identity/self',
  code: 'forbidden',
  messageKey: 'lockey_forbidden',
  correlationId: 'request-123',
});
function deferred<T>() {
  let resolve!: (value: T | PromiseLike<T>) => void;
  const promise = new Promise<T>((done) => {
    resolve = done;
  });
  return { promise, resolve };
}
afterEach(() => vi.restoreAllMocks());

describe('native context page projection', () => {
  const item = {
    tenantId,
    organizationId,
    tenantName: 'Institution',
    organizationName: 'Branch',
    entryOrigin: 'https://institution.example.test:3000',
    canUseCurrentHost: true,
  };
  const pageInfo = { nextCursor: null, previousCursor: null, hasNext: false, hasPrevious: false };
  it('accepts twenty choices, copies their exact fields and freezes the detached navigation result', () => {
    const items = Array.from({ length: 20 }, (_, index) => ({
      ...item,
      organizationId: '019a154b-0000-7000-8000-' + String(index + 100).padStart(12, '0'),
    }));
    const result = nativeContextPage({ items, pageInfo }, 20);
    expect(result.items).toEqual(items);
    expect(result).toMatchObject({ endCursor: null, hasNextPage: false });
    expect(Object.isFrozen(result)).toBe(true);
    expect(Object.isFrozen(result.items)).toBe(true);
    expect(result.items.every(Object.isFrozen)).toBe(true);
    items[0]!.tenantName = 'changed after parsing';
    expect(result.items[0]!.tenantName).toBe('Institution');
    expect(() => nativeContextPage({ items: [...items, item], pageInfo }, 20)).toThrow(
      'BFF provider: response',
    );
    expect(() => nativeContextPage({ items, pageInfo }, 19)).toThrow('BFF provider: response');
  });
  it('preserves an opaque scoped continuation without interpreting it as global membership authority', () => {
    const nextCursor = Buffer.from(
      JSON.stringify({ v: 1, scope: 'a'.repeat(64), id: organizationId }),
    ).toString('base64url');
    expect(
      nativeContextPage(
        { items: [item], pageInfo: { ...pageInfo, nextCursor, hasNext: true } },
        20,
      ),
    ).toEqual({ items: [item], endCursor: nextCursor, hasNextPage: true });
  });
  it.each([
    { version: '1' },
    { items: [item], pageInfo, actorId },
    { items: [{ ...item, subjectKey: association.subjectKey }], pageInfo },
    { items: [{ ...item, tenantId: '00000000-0000-7000-8000-000000000002' }], pageInfo },
    { items: [{ ...item, entryOrigin: 'https://institution.example.test/path' }], pageInfo },
    { items: [{ ...item, entryOrigin: 'https://user:pass@institution.example.test' }], pageInfo },
    { items: [{ ...item, canUseCurrentHost: 'true' }], pageInfo },
    { items: [item], pageInfo: { ...pageInfo, hasNext: true } },
    { items: [item], pageInfo: { ...pageInfo, nextCursor: 'YQ' } },
  ])('refuses unrelated SDK data or malformed navigation without projecting it', (value) => {
    expect(() => nativeContextPage(value, 20)).toThrow('BFF provider: response');
  });
  it.each([0, 21, 1.5, Number.NaN, Number.POSITIVE_INFINITY])(
    'refuses invalid native page ceiling %s',
    (limit) => {
      expect(() => nativeContextPage({ items: [], pageInfo }, limit)).toThrow(
        'BFF provider: configuration',
      );
    },
  );
});

describe('native global entry and scope client', () => {
  it.each([
    ['opaque-subject', association.subjectKey],
    ['  I/ı/🙂  ', '27681d98c78fb11e3cb9b4b61a0388e7916a5dfd861f99a40c1b5b63f8043d9b'],
  ])('matches the independent backend digest vector for %s', (subject, expected) => {
    expect(nativeSubjectKey(authorityId, subject)).toBe(expected);
  });
  it.each(['', ' ', 'x'.repeat(256), 'a\0b', 'a\ud800b', 'a\udc00b'])(
    'rejects invalid exact subject before hashing',
    (subject) => {
      expect(() => nativeSubjectKey(authorityId, subject)).toThrow('BFF provider: identity');
    },
  );
  it('checks configured issuer, authority, exact subject digest and optional compatibility actor independently', () => {
    const api = createNativeSessionApi(configuration, vi.fn());
    expect(() => api.verifyAssociation(identity, association)).not.toThrow();
    expect(() => api.verifyAssociation({ ...identity, actorId }, association)).not.toThrow();
    for (const wrong of [
      { ...identity, issuer: 'https://other.example/realms/learnstack' },
      { ...identity, subject: 'Opaque-subject' },
      { ...identity, subject: ' opaque-subject' },
      { ...identity, actorId: tenantId },
    ])
      expect(() => api.verifyAssociation(wrong, association)).toThrow('BFF provider: identity');
    for (const wrong of [
      { ...association, authorityId: tenantId },
      { ...association, subjectKey: '0'.repeat(64) },
      { ...association, actorId: '00000000-0000-7000-8000-000000000001' },
    ])
      expect(() => api.verifyAssociation(identity, wrong)).toThrow('BFF provider: identity');
  });
  it('uses exact global routes without native scope/assertion/idempotency selectors', async () => {
    const transport = vi.fn<typeof fetch>(async () => json(active));
    const api = createNativeSessionApi(configuration, transport);
    expect(await api.self('held-access', scope, signal())).toEqual({
      state: 'active',
      association,
    });
    expect(await api.bootstrap('held-access', scope, signal())).toEqual(association);
    for (const [index, path] of ['/identity/self', '/identity/self/bootstrap'].entries()) {
      const [url, init] = transport.mock.calls[index]!;
      expect(url).toBe(configuration.apiOrigin + '/api/v1' + path);
      expect(init).toMatchObject({
        method: index ? 'POST' : 'GET',
        cache: 'no-store',
        credentials: 'omit',
        redirect: 'error',
      });
      expect(init!.body).toBe(index ? '{}' : undefined);
      const headers = new Headers(init!.headers);
      expect(headers.get('Authorization')).toBe('Bearer held-access');
      expect(headers.get('X-LearnStack-Hop-Secret')).toBe(configuration.secret);
      expect(headers.get('X-LearnStack-Native-Session-Secret')).toBe(
        configuration.nativeSourceSecret,
      );
      expect(headers.get('X-LearnStack-Host')).toBe(binding.host);
      expect(headers.get('X-LearnStack-Visitor-Address')).toBe(binding.source);
      for (const name of [
        'X-LearnStack-Native-Tenant-Id',
        'X-LearnStack-Native-Organization-Id',
        'X-Tenant-Id',
        'Idempotency-Key',
        'Cookie',
      ])
        expect(headers.has(name)).toBe(false);
    }
  });
  it('keeps missing distinct from active and validates new-bootstrap Location without automatic retry', async () => {
    const transport = vi
      .fn<typeof fetch>()
      .mockResolvedValueOnce(json({ state: 'missing' }))
      .mockResolvedValueOnce(json(problem(503), 503, { 'Retry-After': '10' }))
      .mockResolvedValueOnce(json(active, 201, { Location: '/api/v1/identity/self' }));
    const api = createNativeSessionApi(configuration, transport);
    expect(await api.self('token', binding, signal())).toEqual({ state: 'missing' });
    await expect(api.bootstrap('token', binding, signal())).rejects.toMatchObject({
      reason: 'provider_refused',
      status: 503,
      retryAfterSeconds: undefined,
    });
    expect(transport).toHaveBeenCalledTimes(2);
    expect(await api.bootstrap('token', binding, signal())).toEqual(association);
    expect(transport).toHaveBeenCalledTimes(3);
  });
  it('uses exact scoped empty-JSON admission and refuses a mismatched scope', async () => {
    const transport = vi.fn<typeof fetch>(async () =>
      json({ ...association, tenantId, organizationId }),
    );
    const api = createNativeSessionApi(configuration, transport);
    expect(await api.admit('token', { ...scope, organizationId }, signal())).toEqual({
      ...association,
      tenantId,
      organizationId,
    });
    const [url, init] = transport.mock.calls[0]!;
    expect(url).toBe(configuration.apiOrigin + '/api/v1/identity/session-scope/admissions');
    expect(init!.method).toBe('POST');
    expect(init!.body).toBe('{}');
    const headers = new Headers(init!.headers);
    expect(headers.get('X-LearnStack-Native-Tenant-Id')).toBe(tenantId);
    expect(headers.get('X-LearnStack-Native-Organization-Id')).toBe(organizationId);
    expect(headers.has('Idempotency-Key')).toBe(false);
    await expect(api.admit('token', scope, signal())).rejects.toMatchObject({ reason: 'response' });
  });
  it('flattens the actual forward-only page and clamps the native request to twenty items', async () => {
    const endCursor = Buffer.from(
      authorityId + ':' + association.subjectKey + ':' + actorId,
    ).toString('base64url');
    const item = {
      tenantId,
      organizationId: null,
      tenantName: 'Institution',
      organizationName: null,
      entryOrigin: 'https://institution.example.test:3000',
      canUseCurrentHost: true,
    };
    const transport = vi.fn<typeof fetch>(async () =>
      json({
        items: [item],
        pageInfo: {
          nextCursor: endCursor,
          previousCursor: null,
          hasNext: true,
          hasPrevious: false,
        },
      }),
    );
    const api = createNativeSessionApi(configuration, transport);
    expect(await api.contexts('token', binding, { limit: 100 }, signal())).toEqual({
      items: [item],
      endCursor,
      hasNextPage: true,
    });
    expect(transport.mock.calls[0]![0]).toBe(
      configuration.apiOrigin + '/api/v1/identity/self/contexts?limit=20',
    );
    expect(Object.isFrozen((await api.contexts('token', binding, {}, signal())).items)).toBe(true);
  });
  it.each([
    { state: 'missing', actorId },
    { ...active, subjectKey: 'A'.repeat(64) },
    { ...active, actorId: tenantId.toUpperCase() },
    { ...active, private: 'canary' },
    { state: 'closed' },
  ])('rejects malformed or broadened self data', async (value) => {
    await expect(
      createNativeSessionApi(configuration, async () => json(value)).self(
        'token',
        binding,
        signal(),
      ),
    ).rejects.toMatchObject({ reason: 'response' });
  });
  it.each(['uniform', 'noncanonical', 'shared-hop'])(
    'rejects an unqualified native secret: %s',
    (kind) => {
      const nativeSourceSecret =
        kind === 'uniform'
          ? Buffer.alloc(32, 1).toString('base64url')
          : kind === 'shared-hop'
            ? configuration.nativeSourceSecret
            : configuration.nativeSourceSecret + '=';
      expect(() =>
        createNativeSessionApi(
          {
            ...configuration,
            nativeSourceSecret,
            secret: kind === 'shared-hop' ? nativeSourceSecret : configuration.secret,
          },
          vi.fn(),
        ),
      ).toThrow('BFF provider: configuration');
    },
  );
  it.each(['with space', 'with\r\nheader', 'with\0null', 'with\ttab', 'with🙂unicode'])(
    'refuses an invalid held token without transport or including it in the error',
    async (token) => {
      const transport = vi.fn<typeof fetch>();
      await expect(
        createNativeSessionApi(configuration, transport).self(token, binding, signal()),
      ).rejects.toMatchObject({ reason: 'configuration', message: 'BFF provider: configuration' });
      expect(transport).not.toHaveBeenCalled();
    },
  );
  it.each([
    { nextCursor: null, previousCursor: null, hasNext: true, hasPrevious: false },
    { nextCursor: 'YQ', previousCursor: null, hasNext: false, hasPrevious: false },
    { nextCursor: null, previousCursor: 'YQ', hasNext: false, hasPrevious: true },
    { nextCursor: 'YQ=', previousCursor: null, hasNext: true, hasPrevious: false },
  ])('refuses contradictory or noncanonical context pagination', async (pageInfo) => {
    await expect(
      createNativeSessionApi(configuration, async () => json({ items: [], pageInfo })).contexts(
        'token',
        binding,
        {},
        signal(),
      ),
    ).rejects.toMatchObject({ reason: 'response' });
  });
  it('refuses invalid UTF-8 instead of replacing bytes before parsing identity', async () => {
    const bytes = Buffer.concat([
      Buffer.from('{"state":"'),
      Buffer.from([0xc3, 0x28]),
      Buffer.from('"}'),
    ]);
    await expect(
      createNativeSessionApi(
        configuration,
        async () =>
          new Response(bytes, {
            headers: { 'Content-Type': 'application/json' },
          }),
      ).self('token', binding, signal()),
    ).rejects.toMatchObject({ reason: 'response' });
  });
  it.each([
    ['30', 30],
    ['86400', 86400],
    ['0', undefined],
    ['86401', undefined],
    ['0001', undefined],
    ['tomorrow', undefined],
  ] as const)('exposes only bounded canonical 429 retry delay %s', async (header, expected) => {
    await expect(
      createNativeSessionApi(configuration, async () =>
        json(problem(429), 429, {
          'Retry-After': header,
        }),
      ).bootstrap('token', binding, signal()),
    ).rejects.toMatchObject({
      reason: 'provider_refused',
      status: 429,
      retryAfterSeconds: expected,
    });
  });
  it('waits for oversize response cancellation to actually finish before rejecting', async () => {
    const entered = deferred<void>();
    const cleanup = deferred<void>();
    const body = new ReadableStream<Uint8Array>({
      start(controller) {
        controller.enqueue(new Uint8Array(NATIVE_API_MAX_BYTES + 1));
      },
      cancel() {
        entered.resolve();
        return cleanup.promise;
      },
    });
    const api = createNativeSessionApi(
      configuration,
      async () => new Response(body, { headers: { 'Content-Type': 'application/json' } }),
    );
    let settled = false;
    const result = api.self('token', binding, signal()).finally(() => {
      settled = true;
    });
    const rejected = expect(result).rejects.toMatchObject({ reason: 'response' });
    await entered.promise;
    expect(settled).toBe(false);
    cleanup.resolve();
    await rejected;
    expect(body.locked).toBe(false);
  });
  it('keeps abort-insensitive transport owned until it returns and joins body cancellation', async () => {
    const delivered = deferred<Response>();
    const cleanup = deferred<void>();
    const entered = deferred<void>();
    const controller = new AbortController();
    const api = createNativeSessionApi(configuration, async () => delivered.promise);
    let settled = false;
    const result = api.self('token', binding, controller.signal).finally(() => {
      settled = true;
    });
    const rejected = expect(result).rejects.toMatchObject({ reason: 'cancelled' });
    controller.abort();
    await Promise.resolve();
    expect(settled).toBe(false);
    delivered.resolve(
      new Response(
        new ReadableStream({
          cancel() {
            entered.resolve();
            return cleanup.promise;
          },
        }),
        { headers: { 'Content-Type': 'application/json' } },
      ),
    );
    await entered.promise;
    expect(settled).toBe(false);
    cleanup.resolve();
    await rejected;
  });
  it('uses the same bounded cleanup when the operation deadline aborts a pending body read', async () => {
    const deadline = new AbortController();
    vi.spyOn(AbortSignal, 'timeout').mockReturnValue(deadline.signal);
    const entered = deferred<void>();
    const cancelled = deferred<void>();
    const cleanup = deferred<void>();
    const body = new ReadableStream<Uint8Array>({
      pull() {
        entered.resolve();
      },
      cancel() {
        cancelled.resolve();
        return cleanup.promise;
      },
    });
    const api = createNativeSessionApi(
      configuration,
      async () => new Response(body, { headers: { 'Content-Type': 'application/json' } }),
    );
    let settled = false;
    const result = api.self('token', binding, signal()).finally(() => {
      settled = true;
    });
    const rejected = expect(result).rejects.toMatchObject({ reason: 'deadline' });
    await entered.promise;
    deadline.abort();
    await cancelled.promise;
    expect(settled).toBe(false);
    cleanup.resolve();
    await rejected;
  });
});

const id = actorId;
const write = { version: '9007199254740993', idempotencyKey: 'deliberate-retry-key' };
const profile = { displayName: 'İpek', preferredLocale: 'tr' };
const routes: readonly {
  operation: NativeOperation;
  method: string;
  path: string;
  body?: unknown;
}[] = [
  { operation: { kind: 'listMemberships' }, method: 'GET', path: '/identity/memberships' },
  {
    operation: { kind: 'listOrganizationMemberships' },
    method: 'GET',
    path: '/identity/organization-memberships',
  },
  { operation: { kind: 'getMembership', id }, method: 'GET', path: '/identity/memberships/' + id },
  { operation: { kind: 'getOwnProfile' }, method: 'GET', path: '/identity/me/profile' },
  { operation: { kind: 'getOwnContexts' }, method: 'GET', path: '/identity/me/contexts' },
  { operation: { kind: 'getOwnCapabilities' }, method: 'GET', path: '/identity/me/capabilities' },
  { operation: { kind: 'listRoles' }, method: 'GET', path: '/identity/roles' },
  { operation: { kind: 'getRole', id }, method: 'GET', path: '/identity/roles/' + id },
  { operation: { kind: 'getPermissions' }, method: 'GET', path: '/identity/permissions' },
  { operation: { kind: 'listOrganizations' }, method: 'GET', path: '/tenancy/organizations' },
  {
    operation: { kind: 'getOrganization', id },
    method: 'GET',
    path: '/tenancy/organizations/' + id,
  },
  {
    operation: { kind: 'setMembershipProfile', id, profile, ...write },
    method: 'PUT',
    path: '/identity/memberships/' + id + '/profile',
    body: profile,
  },
  {
    operation: { kind: 'setOwnProfile', profile, ...write },
    method: 'PUT',
    path: '/identity/me/profile',
    body: profile,
  },
  {
    operation: { kind: 'replaceMembershipRoles', id, roleIds: [], ...write },
    method: 'PUT',
    path: '/identity/memberships/' + id + '/role-assignments',
    body: { roleIds: [] },
  },
  {
    operation: { kind: 'changeMembershipStatus', id, status: 'Suspended', ...write },
    method: 'PUT',
    path: '/identity/memberships/' + id + '/status',
    body: { status: 'Suspended' },
  },
  {
    operation: { kind: 'changeRoleGrants', id, grants: [], ...write },
    method: 'PUT',
    path: '/identity/roles/' + id + '/grants',
    body: { grants: [] },
  },
  {
    operation: {
      kind: 'adoptRoleManifest',
      id,
      manifestVersion: 2,
      manifestFingerprint: 'a'.repeat(64),
      ...write,
    },
    method: 'POST',
    path: '/identity/roles/' + id + '/manifest-adoptions',
    body: { manifestVersion: 2, manifestFingerprint: 'a'.repeat(64) },
  },
  {
    operation: {
      kind: 'createOrganization',
      slug: 'branch',
      displayName: 'Branch',
      idempotencyKey: write.idempotencyKey,
    },
    method: 'POST',
    path: '/tenancy/organizations',
    body: { slug: 'branch', displayName: 'Branch' },
  },
  {
    operation: { kind: 'renameOrganization', id, displayName: 'Branch', ...write },
    method: 'PUT',
    path: '/tenancy/organizations/' + id,
    body: { displayName: 'Branch' },
  },
  {
    operation: { kind: 'archiveOrganization', id, ...write },
    method: 'POST',
    path: '/tenancy/organizations/' + id + '/archive',
    body: {},
  },
  {
    operation: { kind: 'selectDefaultOrganization', organizationId, ...write },
    method: 'PUT',
    path: '/tenancy/default-organization',
    body: { organizationId },
  },
];
describe('native closed administration dispatcher', () => {
  it.each(routes)(
    'dispatches $operation.kind through its exact SDK operation',
    async ({ operation, method, path, body }) => {
      const transport = vi.fn<typeof fetch>(async () => json(problem(), 403));
      const result = await createNativeAdministrationCaller(configuration, transport)(
        'held-token',
        scope,
        operation,
        signal(),
      );
      expect(result).toMatchObject({ kind: 'api-error', status: 403 });
      expect(transport).toHaveBeenCalledTimes(1);
      const [url, init] = transport.mock.calls[0]!;
      expect(url).toBe(configuration.apiOrigin + '/api/v1' + path);
      expect(init!.method).toBe(method);
      expect(init!.body).toBe(body === undefined ? undefined : JSON.stringify(body));
      const headers = new Headers(init!.headers);
      expect(headers.get('Authorization')).toBe('Bearer held-token');
      expect(headers.get('X-LearnStack-Native-Tenant-Id')).toBe(tenantId);
      expect(headers.has('X-LearnStack-Native-Organization-Id')).toBe(false);
      expect(headers.get('If-Match')).toBe(
        'version' in operation ? '"' + write.version + '"' : null,
      );
      expect(headers.get('Idempotency-Key')).toBe(
        'idempotencyKey' in operation ? write.idempotencyKey : null,
      );
    },
  );
  it.each([401, 403, 404, 429, 503])(
    'returns administration refusal %s without making an identity decision or retrying',
    async (status) => {
      const transport = vi.fn<typeof fetch>(async () => json(problem(status), status));
      expect(
        await createNativeAdministrationCaller(configuration, transport)(
          'token',
          scope,
          { kind: 'getOwnCapabilities' },
          signal(),
        ),
      ).toMatchObject({ kind: 'api-error', status });
      expect(transport).toHaveBeenCalledTimes(1);
    },
  );
  it('refuses unknown operations and SDK-invalid IDs before any transport', async () => {
    const transport = vi.fn<typeof fetch>();
    const call = createNativeAdministrationCaller(configuration, transport);
    expect(
      await call(
        'token',
        scope,
        { kind: 'request', path: 'https://other.example' } as unknown as NativeOperation,
        signal(),
      ),
    ).toEqual({ kind: 'invalid-request' });
    expect(await call('token', scope, { kind: 'getMembership', id: '../other' }, signal())).toEqual(
      { kind: 'invalid-request' },
    );
    expect(transport).not.toHaveBeenCalled();
    expect(new Set(routes.map((route) => route.operation.kind)).size).toBe(21);
  });
  it('preserves decimal versions, Location and replay metadata for a deliberate exact retry', async () => {
    const transport = vi.fn<typeof fetch>(async () =>
      json({ version: write.version }, 201, {
        ETag: '"' + write.version + '"',
        Location: '/api/v1/tenancy/organizations/' + organizationId,
        'Idempotency-Replayed': 'true',
      }),
    );
    const result = await createNativeAdministrationCaller(configuration, transport)(
      'token',
      scope,
      {
        kind: 'createOrganization',
        slug: 'branch',
        displayName: 'Branch',
        idempotencyKey: write.idempotencyKey,
      },
      signal(),
    );
    expect(result).toMatchObject({
      kind: 'success',
      status: 201,
      data: { version: write.version },
      replayed: true,
      etag: '"' + write.version + '"',
      location: '/api/v1/tenancy/organizations/' + organizationId,
    });
  });
});
