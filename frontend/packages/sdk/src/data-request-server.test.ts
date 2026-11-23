import { describe, expect, expectTypeOf, it, vi } from 'vitest';

import type { AdministrationFetch, AdministrationResult } from './administration-contract';
import {
  DATA_EXPORT_ARTIFACT_STATUSES,
  DATA_REQUEST_FAILURE_REASONS,
  DATA_REQUEST_KINDS,
  DATA_REQUEST_LAST_REFUSALS,
  DATA_REQUEST_ORIGINS,
  DATA_REQUEST_RETAINED_KINDS,
  DATA_REQUEST_STATUSES,
  createAdministrationServerSdk,
} from './administration-server';
import {
  DATA_REQUEST_ADMINISTRATION_POST_CONTRACTS,
  DATA_REQUEST_POST_CONTRACTS,
} from './data-request-contract';
import type {
  DataExportArtifact,
  OwnDataRequest,
  SubjectDataRequest,
} from './data-request-contract';
import type { components, operations, paths } from './generated/schema';
import snapshot from '../../../../backend/openapi/v1.json';

const id = '019a154b-0000-7000-8000-000000000041';
const organization = '019a154b-0000-7000-8000-0000000000a1';
const key = 'k'.repeat(43);
const ticket = 'T'.repeat(42) + 'A';
const pageInfo = { nextCursor: null, previousCursor: null, hasNext: false, hasPrevious: false };
const artifact: DataExportArtifact = {
  id: '019a154b-0000-7000-8000-000000000042',
  status: 'available',
  expiresAt: '2026-11-30T10:00:00.0000000+00:00',
  purgedAt: null,
  byteCount: 131073,
  chunkCount: 2,
  truncations: [{ collection: 'audit.entries', included: 10, total: 12 }],
  version: '9007199254740993',
  readable: true,
};
const item = (value: Partial<OwnDataRequest> = {}): OwnDataRequest => ({
  id,
  kind: 'export',
  origin: 'self',
  status: 'completed',
  createdAt: '2026-11-23T10:00:00.0000000+00:00',
  updatedAt: '2026-11-23T10:01:00.0000000+00:00',
  completedAt: '2026-11-23T10:01:00.0000000+00:00',
  lastRefusal: null,
  failureReason: null,
  retained: [],
  artifact,
  version: '3',
  executeAt: null,
  ...value,
});
const json = (body: unknown, status = 200, headers: Record<string, string> = {}) =>
  new Response(JSON.stringify(body), {
    status,
    headers: {
      'Content-Type': 'application/json; charset=utf-8',
      'Cache-Control': 'no-store',
      ...headers,
    },
  });
const problem = (status: number, code: string, errors?: Record<string, { key: string }[]>) =>
  new Response(
    JSON.stringify({
      type: 'https://errors.learnstack.dev/' + code,
      title: 'Refused',
      status,
      instance: '/api/v1/identity/me/data-requests/exports',
      code,
      messageKey: 'lockey_' + code,
      correlationId: 'request-123',
      ...(errors ? { errors } : {}),
    }),
    {
      status,
      headers: { 'Content-Type': 'application/problem+json', 'Cache-Control': 'no-store' },
    },
  );
const sdkWith = (...responses: Response[]) => {
  const transport = vi.fn<AdministrationFetch>(async () => responses.shift() ?? json({}));
  return { transport, sdk: createAdministrationServerSdk(transport) };
};
const sent = (transport: ReturnType<typeof sdkWith>['transport'], call = 0) => {
  const [path, init] = transport.mock.calls[call]!;
  return {
    path,
    method: init.method,
    headers: init.headers as Record<string, string>,
    body: init.body === undefined ? undefined : JSON.parse(String(init.body)),
  };
};

describe('subject data-request wrappers (ADR-0106 § 9, ADR-0107 § 4)', () => {
  it('owns exactly the person-facing POST operations of the committed OpenAPI document', () => {
    const owned = Object.entries(DATA_REQUEST_POST_CONTRACTS);
    const documented = Object.entries(
      snapshot.paths as Record<string, { post?: { operationId: string } }>,
    ).flatMap(([path, item]) =>
      item.post &&
      (path.startsWith('/api/v1/identity/me/data-requests') ||
        path.startsWith('/api/v1/identity/data-request-entry'))
        ? [[path, item.post.operationId] as const]
        : [],
    );
    expect(Object.isFrozen(DATA_REQUEST_POST_CONTRACTS)).toBe(true);
    expect(owned.sort()).toEqual([...documented].sort());
    expect(new Set(owned.map(([, operation]) => operation)).size).toBe(owned.length);
    expectTypeOf<
      paths['/api/v1/identity/me/data-requests/{id}/artifact/chunks']['post']
    >().toEqualTypeOf<operations['ReadOwnDataExportChunk']>();
    expectTypeOf<
      operations['CreateOwnDataExport']['responses'][202]['content']['application/json']
    >().toEqualTypeOf<components['schemas']['DataRequestAcceptedResponse']>();
    expectTypeOf<operations['MintRetainedDataExportTicket']['requestBody']>().toEqualTypeOf<{
      content: { 'application/json': components['schemas']['RetainedDataRequestTargetInput'] };
    }>();
    expectTypeOf<keyof OwnDataRequest>().toEqualTypeOf<
      | 'id'
      | 'kind'
      | 'origin'
      | 'status'
      | 'createdAt'
      | 'updatedAt'
      | 'completedAt'
      | 'lastRefusal'
      | 'failureReason'
      | 'retained'
      | 'artifact'
      | 'version'
      | 'executeAt'
    >();
    expectTypeOf<
      Extract<AdministrationResult<unknown, 202>, { kind: 'success' }>['status']
    >().toEqualTypeOf<202>();
  });

  it('reads the own page and receipt with the exact path, query, ETag and no-store', async () => {
    const page = { items: [item()], pageInfo: { ...pageInfo, nextCursor: 'n', hasNext: true } };
    const { sdk, transport } = sdkWith(json(page), json(item(), 200, { ETag: '"3"' }));
    expect((await sdk.listOwnDataRequests({ limit: 20, cursor: 'c-1' })).kind).toBe('success');
    expect(sent(transport)).toMatchObject({
      path: '/api/v1/identity/me/data-requests?cursor=c-1&limit=20',
      method: 'GET',
    });
    expect(sent(transport).headers).not.toHaveProperty('Idempotency-Key');
    expect((await sdk.getOwnDataRequest(id)).kind).toBe('success');
    expect(sent(transport, 1).path).toBe('/api/v1/identity/me/data-requests/' + id);
    for (const bad of [
      json(item(), 200, { ETag: '"4"' }),
      json(item()),
      json({ ...item(), id: artifact.id }, 200, { ETag: '"3"' }),
      new Response(JSON.stringify(item()), {
        headers: { 'Content-Type': 'application/json', ETag: '"3"' },
      }),
    ]) {
      const { sdk: other } = sdkWith(bad);
      expect((await other.getOwnDataRequest(id)).kind).toBe('invalid-response');
    }
  });

  it('files own requests keyed, answering 202 with the exact Location and ETag', async () => {
    for (const [method, kind, status, path] of [
      ['createOwnDataExport', 'export', 'requested', 'exports'],
      ['createOwnDataErasure', 'erasure', 'scheduled', 'erasures'],
      ['createOwnAccountClosure', 'closure', 'scheduled', 'closures'],
    ] as const) {
      const accepted = { id, kind, status, version: '0' };
      const location = { ETag: '"0"', Location: '/api/v1/identity/me/data-requests/' + id };
      const { sdk, transport } = sdkWith(json(accepted, 202, location));
      const result = await sdk[method]({ idempotencyKey: key });
      expect(result).toMatchObject({ kind: 'success', status: 202, location: location.Location });
      expect(sent(transport)).toEqual({
        path: '/api/v1/identity/me/data-requests/' + path,
        method: 'POST',
        headers: {
          Accept: 'application/json',
          'Content-Type': 'application/json',
          'Idempotency-Key': key,
        },
        body: {},
      });
      for (const bad of [
        json(accepted, 200, location),
        json(accepted, 202, { ETag: '"0"' }),
        json(accepted, 202, {
          ...location,
          Location: '/api/v1/identity/me/data-requests/' + organization,
        }),
        json({ ...accepted, kind: kind === 'export' ? 'erasure' : 'export' }, 202, location),
        json({ ...accepted, status: 'completed' }, 202, location),
      ]) {
        const { sdk: other } = sdkWith(bad);
        expect((await other[method]({ idempotencyKey: key })).kind).toBe('invalid-response');
      }
      const { sdk: replay } = sdkWith(
        json(accepted, 202, { ...location, 'Idempotency-Replayed': 'true' }),
      );
      expect(await replay[method]({ idempotencyKey: key })).toMatchObject({ replayed: true });
    }
  });

  it('cancels and revokes with If-Match and a key; tickets and chunks carry neither', async () => {
    const receipt = (version: string) => json({ version }, 200, { ETag: '"' + version + '"' });
    const { sdk, transport } = sdkWith(
      receipt('4'),
      receipt('9007199254740994'),
      json({ ticket, expiresAt: artifact.expiresAt, chunkCount: 2, byteCount: 131073 }),
      json({ index: 1, count: 2, bytes: 'AA==' }),
    );
    await sdk.cancelOwnDataRequest(id, { version: '3', idempotencyKey: key });
    await sdk.revokeOwnDataExport(id, { version: artifact.version, idempotencyKey: key });
    expect((await sdk.mintOwnDataExportTicket(id)).kind).toBe('success');
    expect((await sdk.readOwnDataExportChunk(id, ticket, 1)).kind).toBe('success');
    expect(sent(transport, 0)).toMatchObject({
      path: '/api/v1/identity/me/data-requests/' + id + '/cancellations',
      headers: { 'If-Match': '"3"', 'Idempotency-Key': key },
      body: {},
    });
    expect(sent(transport, 1)).toMatchObject({
      path: '/api/v1/identity/me/data-requests/' + id + '/artifact/revocations',
      headers: { 'If-Match': '"' + artifact.version + '"' },
    });
    for (const call of [2, 3]) {
      expect(sent(transport, call).headers).not.toHaveProperty('If-Match');
      expect(sent(transport, call).headers).not.toHaveProperty('Idempotency-Key');
    }
    expect(sent(transport, 2)).toMatchObject({
      path: '/api/v1/identity/me/data-requests/' + id + '/artifact/tickets',
      body: {},
    });
    expect(sent(transport, 3)).toMatchObject({
      path: '/api/v1/identity/me/data-requests/' + id + '/artifact/chunks',
      body: { ticket, index: 1 },
    });
  });

  it('acts in one retained group named only by the group query value', async () => {
    const { sdk, transport } = sdkWith(
      json({ items: [{ tenantId: id, organizationId: null }], pageInfo }),
      json({ items: [item()], pageInfo }),
      json({ id, kind: 'erasure', status: 'scheduled', version: '0' }, 202, { ETag: '"0"' }),
      json({ version: '4' }, 200, { ETag: '"4"' }),
      json({ ticket, expiresAt: artifact.expiresAt, chunkCount: 2, byteCount: 131073 }),
      json({ index: 0, count: 2, bytes: 'AA==' }),
    );
    expect((await sdk.listRetainedDataRequestScopes({ limit: 20 })).kind).toBe('success');
    expect((await sdk.listRetainedDataRequests(organization, { limit: 5 })).kind).toBe('success');
    expect((await sdk.createRetainedDataErasure('tenant', { idempotencyKey: key })).kind).toBe(
      'success',
    );
    expect(
      (await sdk.cancelRetainedDataRequest('tenant', id, { version: '3', idempotencyKey: key }))
        .kind,
    ).toBe('success');
    expect((await sdk.mintRetainedDataExportTicket('tenant', id)).kind).toBe('success');
    expect((await sdk.readRetainedDataExportChunk('tenant', id, ticket, 0)).kind).toBe('success');
    expect(transport.mock.calls.map(([path]) => path)).toEqual([
      '/api/v1/identity/data-request-entry/scopes?limit=20',
      '/api/v1/identity/data-request-entry/requests?group=' + organization + '&limit=5',
      '/api/v1/identity/data-request-entry/erasures?group=tenant',
      '/api/v1/identity/data-request-entry/cancellations?group=tenant',
      '/api/v1/identity/data-request-entry/tickets?group=tenant',
      '/api/v1/identity/data-request-entry/chunks?group=tenant',
    ]);
    expect(sent(transport, 3).body).toEqual({ requestId: id });
    expect(sent(transport, 4).body).toEqual({ requestId: id });
    expect(sent(transport, 5).body).toEqual({ requestId: id, ticket, index: 0 });
    // A retained creation has no per-request route, so a Location is a contract violation.
    const { sdk: located } = sdkWith(
      json({ id, kind: 'export', status: 'requested', version: '0' }, 202, {
        ETag: '"0"',
        Location: '/api/v1/identity/me/data-requests/' + id,
      }),
    );
    expect((await located.createRetainedDataExport('tenant', { idempotencyKey: key })).kind).toBe(
      'invalid-response',
    );
  });

  it('refuses hostile input before any network call', async () => {
    const { sdk, transport } = sdkWith();
    const write = { version: '3', idempotencyKey: key };
    for (const attempt of [
      () => sdk.getOwnDataRequest('not-a-uuid'),
      () => sdk.getOwnDataRequest('019a154b-0000-4000-8000-000000000041'),
      () => sdk.listOwnDataRequests({ limit: 101 }),
      () => sdk.listOwnDataRequests({ limit: 0 }),
      () => sdk.listOwnDataRequests({ cursor: 'x'.repeat(1025) }),
      () => sdk.listOwnDataRequests({ cursor: 'a b' }),
      () => sdk.listRetainedDataRequests('tenant', { limit: 21 }),
      () => sdk.listRetainedDataRequestScopes({ cursor: 'x'.repeat(37) }),
      () => sdk.listRetainedDataRequests('Tenant'),
      () => sdk.listRetainedDataRequests('tenant&group=tenant'),
      () => sdk.createOwnDataExport({ idempotencyKey: 'short' }),
      () => sdk.createRetainedDataExport('../exports', { idempotencyKey: key }),
      () => sdk.cancelOwnDataRequest(id, { ...write, version: '01' }),
      () => sdk.cancelOwnDataRequest(id, { ...write, version: '9223372036854775808' }),
      () => sdk.revokeRetainedDataExport('tenant', 'x', write),
      () => sdk.readOwnDataExportChunk(id, ticket, 512),
      () => sdk.readOwnDataExportChunk(id, ticket, -1),
      () => sdk.readOwnDataExportChunk(id, ticket, 1.5),
      () => sdk.readOwnDataExportChunk(id, 'T'.repeat(43), 0),
      () => sdk.readOwnDataExportChunk(id, ticket + 'A', 0),
      () => sdk.readRetainedDataExportChunk('tenant', id, 'not a ticket', 0),
      () => sdk.mintRetainedDataExportTicket('tenant', 'x'),
    ])
      expect(await attempt()).toEqual({ kind: 'invalid-request' });
    expect(transport).not.toHaveBeenCalled();
  });

  it('validates the closed receipt, ticket and chunk shapes', async () => {
    const page = (value: unknown) => json({ items: [value], pageInfo });
    for (const bad of [
      { ...item(), status: 'unknown' },
      { ...item(), extra: true },
      { ...item(), executeAt: '2026-11-24T10:00:00Z' },
      item({ kind: 'erasure' }),
      item({ status: 'requested' }),
      item({
        retained: [
          { kind: 'request_receipt', count: 1 },
          { kind: 'request_receipt', count: 2 },
        ],
      }),
      item({ retained: [{ kind: 'everything', count: 1 } as never] }),
      item({ retained: [{ kind: 'request_receipt', count: -1 }] }),
      item({ artifact: { ...artifact, chunkCount: 3 } }),
      item({ artifact: { ...artifact, readable: true, status: 'revoked' } }),
      item({
        artifact: { ...artifact, truncations: [{ collection: 'entries', included: 1, total: 2 }] },
      }),
      item({
        artifact: {
          ...artifact,
          truncations: [{ collection: 'audit.entries', included: 3, total: 2 }],
        },
      }),
      item({ createdAt: '2026-11-23 10:00' }),
      item({ version: '01' }),
    ]) {
      const { sdk } = sdkWith(page(bad));
      expect((await sdk.listOwnDataRequests()).kind).toBe('invalid-response');
    }
    const { sdk: duplicate } = sdkWith(json({ items: [item(), item()], pageInfo }));
    expect((await duplicate.listOwnDataRequests()).kind).toBe('invalid-response');
    const { sdk: paging } = sdkWith(json({ items: [], pageInfo: { ...pageInfo, hasNext: true } }));
    expect((await paging.listOwnDataRequests()).kind).toBe('invalid-response');
    for (const bad of [
      { ticket: 'T'.repeat(43), expiresAt: artifact.expiresAt, chunkCount: 2, byteCount: 131073 },
      { ticket, expiresAt: artifact.expiresAt, chunkCount: 1, byteCount: 131073 },
      { ticket, expiresAt: artifact.expiresAt, chunkCount: 513, byteCount: 67108865 },
      { ticket, expiresAt: 'later', chunkCount: 2, byteCount: 131073 },
    ]) {
      const { sdk } = sdkWith(json(bad));
      expect((await sdk.mintOwnDataExportTicket(id)).kind).toBe('invalid-response');
    }
    for (const bad of [
      { index: 2, count: 2, bytes: 'AA==' },
      { index: 0, count: 2, bytes: 'AB==' },
      { index: 0, count: 2, bytes: 'AA=' },
      { index: 0, count: 2, bytes: 'A'.repeat(174768) },
      { index: 0, count: 2, bytes: 'AA==', extra: 1 },
    ]) {
      const { sdk } = sdkWith(json(bad));
      expect((await sdk.readOwnDataExportChunk(id, ticket, 0)).kind).toBe('invalid-response');
    }
    const { sdk: large } = sdkWith(json({ index: 0, count: 2, bytes: 'A'.repeat(300 * 1024) }));
    expect(await large.readOwnDataExportChunk(id, ticket, 0)).toEqual({
      kind: 'invalid-response',
      reason: 'invalid-success',
    });
  });

  it('labels a body that breaks off as a transport failure and only an undecodable body as malformed', async () => {
    const headers = { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' };
    let pulls = 0;
    const broken = new Response(
      new ReadableStream<Uint8Array>({
        pull(controller) {
          if (pulls++ === 0) controller.enqueue(new TextEncoder().encode('{"index":0,'));
          else controller.error(new TypeError('terminated'));
        },
      }),
      { headers },
    );
    const { sdk } = sdkWith(broken);
    expect(await sdk.readOwnDataExportChunk(id, ticket, 0)).toEqual({ kind: 'transport-error' });
    for (const body of ['{"index":0,', new Uint8Array([0x7b, 0x22, 0xc3, 0x28, 0x22, 0x7d])]) {
      const { sdk: garbled } = sdkWith(new Response(body, { headers }));
      expect(await garbled.readOwnDataExportChunk(id, ticket, 0)).toEqual({
        kind: 'invalid-response',
        reason: 'malformed-json',
      });
    }
  });

  it('keeps the API refusal reason for the person-facing sentence', async () => {
    const { sdk } = sdkWith(
      problem(409, 'business_rule_violation', {
        artifact: [{ key: 'lockey_data_export_ticket_limit' }],
      }),
    );
    const result = await sdk.mintOwnDataExportTicket(id);
    expect(result.kind).toBe('api-error');
    if (result.kind !== 'api-error') throw new Error('Expected refusal');
    expect(result.status).toBe(409);
    expect(result.problem.errors).toEqual({
      artifact: [{ key: 'lockey_data_export_ticket_limit' }],
    });
  });
});

type Sdk = ReturnType<typeof createAdministrationServerSdk>;
type OpenApiOperation = {
  operationId?: string;
  parameters?: { in: string; name: string; required?: boolean }[];
  requestBody?: { content: Record<string, { schema: { $ref: string } }> };
};
const documented = Object.entries(
  snapshot.paths as unknown as Record<string, Record<string, OpenApiOperation>>,
).flatMap(([path, value]) =>
  /^\/api\/v1\/identity\/(?:me\/data-requests|data-request-entry|data-requests)(?:\/|$)/.test(path)
    ? Object.entries(value).map(([method, operation]) => ({
        path,
        method: method.toUpperCase(),
        operation,
      }))
    : [],
);
const schemas = snapshot.components.schemas as unknown as Record<
  string,
  { properties?: Record<string, { enum?: (string | null)[] }> }
>;
const own = '/api/v1/identity/me/data-requests';
const entry = '/api/v1/identity/data-request-entry';
const administered = '/api/v1/identity/data-requests';
const member = '019a154b-0000-7000-8000-000000000011';
const subject = (value: Partial<SubjectDataRequest> = {}): SubjectDataRequest => ({
  ...item({ origin: 'administrator' }),
  requestedByYou: true,
  membershipIds: [member],
  ...value,
});
const accepted = (kind: 'export' | 'erasure' | 'closure', located?: string) =>
  json(
    { id, kind, status: kind === 'export' ? 'requested' : 'scheduled', version: '0' },
    202,
    located === undefined ? { ETag: '"0"' } : { ETag: '"0"', Location: located + '/' + id },
  );
const receipt = () => json({ version: '4' }, 200, { ETag: '"4"' });
const ticketBody = () =>
  json({ ticket, expiresAt: artifact.expiresAt, chunkCount: 2, byteCount: 131073 });
const chunkBody = () => json({ index: 1, count: 2, bytes: 'AA==' });
const guarded = { 'Idempotency-Key': key, 'If-Match': '"3"' };
const write = { version: '3', idempotencyKey: key };
type Row = Readonly<{
  /** The SDK member under test, and the OpenAPI operation it must be. */
  name: string;
  operationId: string;
  call: (sdk: Sdk) => Promise<{ kind: string }>;
  method: 'GET' | 'POST';
  path: string;
  /** The headers beyond `Accept` and, for a POST, `Content-Type`. */
  headers?: Record<string, string>;
  body?: unknown;
  response: () => Response;
}>;
/** All 24 wrappers, each with the exact request it must put on the wire. */
const WRAPPERS: readonly Row[] = [
  {
    name: 'listOwnDataRequests',
    operationId: 'ListOwnDataRequests',
    call: (sdk) => sdk.listOwnDataRequests({ cursor: 'c-1', limit: 20 }),
    method: 'GET',
    path: own + '?cursor=c-1&limit=20',
    response: () => json({ items: [item()], pageInfo }),
  },
  {
    name: 'getOwnDataRequest',
    operationId: 'GetOwnDataRequest',
    call: (sdk) => sdk.getOwnDataRequest(id),
    method: 'GET',
    path: own + '/' + id,
    response: () => json(item(), 200, { ETag: '"3"' }),
  },
  {
    name: 'createOwnDataExport',
    operationId: 'CreateOwnDataExport',
    call: (sdk) => sdk.createOwnDataExport({ idempotencyKey: key }),
    method: 'POST',
    path: own + '/exports',
    headers: { 'Idempotency-Key': key },
    body: {},
    response: () => accepted('export', own),
  },
  {
    name: 'createOwnDataErasure',
    operationId: 'CreateOwnDataErasure',
    call: (sdk) => sdk.createOwnDataErasure({ idempotencyKey: key }),
    method: 'POST',
    path: own + '/erasures',
    headers: { 'Idempotency-Key': key },
    body: {},
    response: () => accepted('erasure', own),
  },
  {
    name: 'createOwnAccountClosure',
    operationId: 'CreateOwnAccountClosure',
    call: (sdk) => sdk.createOwnAccountClosure({ idempotencyKey: key }),
    method: 'POST',
    path: own + '/closures',
    headers: { 'Idempotency-Key': key },
    body: {},
    response: () => accepted('closure', own),
  },
  {
    name: 'cancelOwnDataRequest',
    operationId: 'CancelOwnDataRequest',
    call: (sdk) => sdk.cancelOwnDataRequest(id, write),
    method: 'POST',
    path: own + '/' + id + '/cancellations',
    headers: guarded,
    body: {},
    response: receipt,
  },
  {
    name: 'revokeOwnDataExport',
    operationId: 'RevokeOwnDataExport',
    call: (sdk) => sdk.revokeOwnDataExport(id, write),
    method: 'POST',
    path: own + '/' + id + '/artifact/revocations',
    headers: guarded,
    body: {},
    response: receipt,
  },
  {
    name: 'mintOwnDataExportTicket',
    operationId: 'MintOwnDataExportTicket',
    call: (sdk) => sdk.mintOwnDataExportTicket(id),
    method: 'POST',
    path: own + '/' + id + '/artifact/tickets',
    body: {},
    response: ticketBody,
  },
  {
    name: 'readOwnDataExportChunk',
    operationId: 'ReadOwnDataExportChunk',
    call: (sdk) => sdk.readOwnDataExportChunk(id, ticket, 1),
    method: 'POST',
    path: own + '/' + id + '/artifact/chunks',
    body: { ticket, index: 1 },
    response: chunkBody,
  },
  {
    name: 'listRetainedDataRequestScopes',
    operationId: 'ListRetainedDataRequestScopes',
    call: (sdk) => sdk.listRetainedDataRequestScopes({ cursor: 'c-1', limit: 20 }),
    method: 'GET',
    path: entry + '/scopes?cursor=c-1&limit=20',
    response: () => json({ items: [{ tenantId: id, organizationId: null }], pageInfo }),
  },
  {
    name: 'listRetainedDataRequests',
    operationId: 'ListRetainedDataRequests',
    call: (sdk) => sdk.listRetainedDataRequests(organization, { cursor: 'c-1', limit: 20 }),
    method: 'GET',
    path: entry + '/requests?group=' + organization + '&cursor=c-1&limit=20',
    response: () => json({ items: [item()], pageInfo }),
  },
  {
    name: 'createRetainedDataExport',
    operationId: 'CreateRetainedDataExport',
    call: (sdk) => sdk.createRetainedDataExport('tenant', { idempotencyKey: key }),
    method: 'POST',
    path: entry + '/exports?group=tenant',
    headers: { 'Idempotency-Key': key },
    body: {},
    response: () => accepted('export'),
  },
  {
    name: 'createRetainedDataErasure',
    operationId: 'CreateRetainedDataErasure',
    call: (sdk) => sdk.createRetainedDataErasure(organization, { idempotencyKey: key }),
    method: 'POST',
    path: entry + '/erasures?group=' + organization,
    headers: { 'Idempotency-Key': key },
    body: {},
    response: () => accepted('erasure'),
  },
  {
    name: 'createRetainedAccountClosure',
    operationId: 'CreateRetainedAccountClosure',
    call: (sdk) => sdk.createRetainedAccountClosure('tenant', { idempotencyKey: key }),
    method: 'POST',
    path: entry + '/closures?group=tenant',
    headers: { 'Idempotency-Key': key },
    body: {},
    response: () => accepted('closure'),
  },
  {
    name: 'cancelRetainedDataRequest',
    operationId: 'CancelRetainedDataRequest',
    call: (sdk) => sdk.cancelRetainedDataRequest('tenant', id, write),
    method: 'POST',
    path: entry + '/cancellations?group=tenant',
    headers: guarded,
    body: { requestId: id },
    response: receipt,
  },
  {
    name: 'revokeRetainedDataExport',
    operationId: 'RevokeRetainedDataExport',
    call: (sdk) => sdk.revokeRetainedDataExport(organization, id, write),
    method: 'POST',
    path: entry + '/revocations?group=' + organization,
    headers: guarded,
    body: { requestId: id },
    response: receipt,
  },
  {
    name: 'mintRetainedDataExportTicket',
    operationId: 'MintRetainedDataExportTicket',
    call: (sdk) => sdk.mintRetainedDataExportTicket('tenant', id),
    method: 'POST',
    path: entry + '/tickets?group=tenant',
    body: { requestId: id },
    response: ticketBody,
  },
  {
    name: 'readRetainedDataExportChunk',
    operationId: 'ReadRetainedDataExportChunk',
    call: (sdk) => sdk.readRetainedDataExportChunk('tenant', id, ticket, 1),
    method: 'POST',
    path: entry + '/chunks?group=tenant',
    body: { requestId: id, ticket, index: 1 },
    response: chunkBody,
  },
  {
    name: 'listDataRequests',
    operationId: 'ListDataRequests',
    call: (sdk) => sdk.listDataRequests({ cursor: 'c-1', limit: 20 }),
    method: 'GET',
    path: administered + '?cursor=c-1&limit=20',
    response: () => json({ items: [subject()], pageInfo }),
  },
  {
    name: 'getDataRequest',
    operationId: 'GetDataRequest',
    call: (sdk) => sdk.getDataRequest(id),
    method: 'GET',
    path: administered + '/' + id,
    response: () => json(subject(), 200, { ETag: '"3"' }),
  },
  {
    name: 'createDataExportForSubject',
    operationId: 'CreateDataExportForSubject',
    call: (sdk) => sdk.createDataExportForSubject(member, { idempotencyKey: key }),
    method: 'POST',
    path: administered + '/exports',
    headers: { 'Idempotency-Key': key },
    body: { membershipId: member },
    response: () => accepted('export', administered),
  },
  {
    name: 'createDataErasureForSubject',
    operationId: 'CreateDataErasureForSubject',
    call: (sdk) => sdk.createDataErasureForSubject(member, { idempotencyKey: key }),
    method: 'POST',
    path: administered + '/erasures',
    headers: { 'Idempotency-Key': key },
    body: { membershipId: member },
    response: () => accepted('erasure', administered),
  },
  {
    name: 'cancelDataRequest',
    operationId: 'CancelDataRequest',
    call: (sdk) => sdk.cancelDataRequest(id, write),
    method: 'POST',
    path: administered + '/' + id + '/cancellations',
    headers: guarded,
    body: {},
    response: receipt,
  },
  {
    name: 'revokeDataExport',
    operationId: 'RevokeDataExport',
    call: (sdk) => sdk.revokeDataExport(id, write),
    method: 'POST',
    path: administered + '/' + id + '/artifact/revocations',
    headers: guarded,
    body: {},
    response: receipt,
  },
];

describe('every data-request wrapper puts its documented request on the wire', () => {
  it('covers exactly the 24 documented operations, each once', () => {
    expect(WRAPPERS).toHaveLength(24);
    expect(documented).toHaveLength(24);
    expect(WRAPPERS.map((row) => row.operationId).sort()).toEqual(
      documented.map(({ operation }) => operation.operationId).sort(),
    );
    expect(new Set(WRAPPERS.map((row) => row.name)).size).toBe(24);
    const { sdk } = sdkWith();
    for (const row of WRAPPERS)
      expect(typeof (sdk as Record<string, unknown>)[row.name]).toBe('function');
    const posts = Object.entries({
      ...DATA_REQUEST_POST_CONTRACTS,
      ...DATA_REQUEST_ADMINISTRATION_POST_CONTRACTS,
    });
    expect(posts).toHaveLength(18);
    expect(
      WRAPPERS.filter((row) => row.method === 'POST')
        .map((row) => row.operationId)
        .sort(),
    ).toEqual(posts.map(([, operation]) => operation).sort());
  });

  it.each(WRAPPERS.map((row) => [row.name, row] as const))(
    '%s sends the method, path, query, headers and body of its OpenAPI operation',
    async (_name, row) => {
      const { sdk, transport } = sdkWith(row.response());
      expect((await row.call(sdk)).kind).toBe('success');
      expect(transport).toHaveBeenCalledOnce();
      const call = sent(transport);
      expect(call.method).toBe(row.method);
      expect(call.path).toBe(row.path);
      expect(call.headers).toEqual({
        Accept: 'application/json',
        ...(row.method === 'POST' ? { 'Content-Type': 'application/json' } : {}),
        ...row.headers,
      });
      expect(call.body).toEqual(row.body);
      // The same request, read back against the committed OpenAPI document.
      const doc = documented.find(({ operation }) => operation.operationId === row.operationId)!;
      const [pathname, search = ''] = call.path.split('?');
      expect(pathname!.replaceAll(id, '{id}')).toBe(doc.path);
      expect(row.method).toBe(doc.method);
      const parameters = doc.operation.parameters ?? [];
      const names = (place: string) =>
        parameters.filter(({ in: where }) => where === place).map(({ name }) => name);
      expect(
        Object.keys(call.headers)
          .filter((name) => !['Accept', 'Content-Type'].includes(name))
          .sort(),
      ).toEqual(names('header').sort());
      const query = [...new URLSearchParams(search).keys()];
      for (const name of query) expect(names('query')).toContain(name);
      for (const { name, required } of parameters.filter(({ in: where }) => where === 'query'))
        if (required) expect(query).toContain(name);
      expect(names('path').length).toBe(pathname!.includes(id) ? 1 : 0);
      if (row.method === 'POST') {
        const schema = doc.operation
          .requestBody!.content['application/json']!.schema.$ref.split('/')
          .pop()!;
        expect(Object.keys(call.body as object).sort()).toEqual(
          Object.keys(schemas[schema]!.properties ?? {}).sort(),
        );
      }
    },
  );

  it('keeps every cancellation and revocation wrapper on its own route', async () => {
    for (const row of WRAPPERS.filter((value) => /^(?:cancel|revoke)/.test(value.name))) {
      const cancel = row.name.startsWith('cancel');
      const { sdk, transport } = sdkWith(row.response());
      expect((await row.call(sdk)).kind).toBe('success');
      const [pathname] = sent(transport).path.split('?');
      expect(pathname!.endsWith(cancel ? '/cancellations' : '/revocations')).toBe(true);
      expect(pathname!.endsWith(cancel ? '/revocations' : '/cancellations')).toBe(false);
      // A per-request revocation names the artifact; a cancellation never does, and the retained route carries none.
      expect(pathname!.includes('/artifact/')).toBe(!cancel && !pathname!.startsWith(entry));
    }
  });
});

describe('the closed data-request vocabularies', () => {
  const enumOf = (schema: string, property: string) =>
    schemas[schema]!.properties![property]!.enum!;
  it('equal the OpenAPI enums, value for value', () => {
    const sorted = (values: readonly (string | null)[]) => [...values].sort();
    expect(sorted(DATA_REQUEST_KINDS)).toEqual(sorted(enumOf('OwnDataRequestResponse', 'kind')));
    expect(sorted(DATA_REQUEST_ORIGINS)).toEqual(
      sorted(enumOf('OwnDataRequestResponse', 'origin')),
    );
    expect(sorted(DATA_REQUEST_STATUSES)).toEqual(
      sorted(enumOf('OwnDataRequestResponse', 'status')),
    );
    expect(sorted([...DATA_REQUEST_LAST_REFUSALS, null])).toEqual(
      sorted(enumOf('OwnDataRequestResponse', 'lastRefusal')),
    );
    expect(sorted([...DATA_REQUEST_FAILURE_REASONS, null])).toEqual(
      sorted(enumOf('OwnDataRequestResponse', 'failureReason')),
    );
    expect(sorted(DATA_EXPORT_ARTIFACT_STATUSES)).toEqual(
      sorted(enumOf('DataExportArtifactResponse', 'status')),
    );
    expect(sorted(DATA_REQUEST_RETAINED_KINDS)).toEqual(
      sorted(enumOf('DataRequestRetainedResponse', 'kind')),
    );
    // The administrator's receipt names the same vocabularies.
    for (const property of ['kind', 'origin', 'status'])
      expect(enumOf('DataRequestResponse', property)).toEqual(
        enumOf('OwnDataRequestResponse', property),
      );
  });

  it('admit a receipt carrying every value of every closed vocabulary', async () => {
    const instant = '2026-11-23T10:00:00.0000000+00:00';
    const receipts: OwnDataRequest[] = [
      ...DATA_REQUEST_KINDS.map((kind, index) =>
        item({
          id: '019a154b-0000-7000-8000-0000000001' + String(index).padStart(2, '0'),
          kind,
          origin: 'self',
          status: 'scheduled',
          completedAt: null,
          executeAt: kind === 'export' ? null : instant,
          artifact: null,
        }),
      ),
      ...DATA_REQUEST_ORIGINS.map((origin, index) =>
        item({
          id: '019a154b-0000-7000-8000-0000000002' + String(index).padStart(2, '0'),
          kind: 'erasure',
          origin,
          status: 'scheduled',
          completedAt: null,
          executeAt: instant,
          artifact: null,
        }),
      ),
      ...DATA_REQUEST_STATUSES.map((status, index) =>
        item({
          id: '019a154b-0000-7000-8000-0000000003' + String(index).padStart(2, '0'),
          kind: 'erasure',
          status,
          completedAt: status === 'completed' ? instant : null,
          executeAt: null,
          artifact: null,
        }),
      ),
      ...DATA_EXPORT_ARTIFACT_STATUSES.map((status, index) =>
        item({
          id: '019a154b-0000-7000-8000-0000000004' + String(index).padStart(2, '0'),
          artifact: {
            ...artifact,
            status,
            readable: status === 'available',
            purgedAt: status === 'purged' ? instant : null,
          },
        }),
      ),
      ...DATA_REQUEST_LAST_REFUSALS.map((lastRefusal, index) =>
        item({
          id: '019a154b-0000-7000-8000-0000000005' + String(index).padStart(2, '0'),
          kind: 'erasure',
          status: 'blocked',
          completedAt: null,
          executeAt: null,
          artifact: null,
          lastRefusal,
        }),
      ),
      ...DATA_REQUEST_FAILURE_REASONS.map((failureReason, index) =>
        item({
          id: '019a154b-0000-7000-8000-0000000006' + String(index).padStart(2, '0'),
          status: 'failed',
          completedAt: null,
          artifact: null,
          failureReason,
        }),
      ),
      item({
        id: '019a154b-0000-7000-8000-000000000700',
        kind: 'erasure',
        status: 'completed',
        executeAt: null,
        artifact: null,
        retained: DATA_REQUEST_RETAINED_KINDS.map((kind, index) => ({ kind, count: index })),
      }),
    ];
    // One page holds at most 100 receipts; each value is read back exactly as the API sent it.
    expect(receipts.length).toBeLessThanOrEqual(100);
    const { sdk } = sdkWith(json({ items: receipts, pageInfo }));
    const result = await sdk.listOwnDataRequests({ limit: 100 });
    expect(result.kind).toBe('success');
    if (result.kind !== 'success') return;
    expect(result.data.items).toEqual(receipts);
    // The same single receipt, one value at a time, so a refused value names itself.
    for (const receipt of receipts) {
      const { sdk: single } = sdkWith(json(receipt, 200, { ETag: '"' + receipt.version + '"' }));
      expect(await single.getOwnDataRequest(receipt.id), JSON.stringify(receipt)).toMatchObject({
        kind: 'success',
      });
    }
  });
});

describe('the data-request response gate', () => {
  it('refuses a success that is not application/json, with or without no-store', async () => {
    for (const type of [
      'text/plain',
      'text/html; charset=utf-8',
      'application/problem+json',
      'application/jsonx',
    ]) {
      const { sdk } = sdkWith(
        new Response(JSON.stringify(item()), {
          headers: { 'Content-Type': type, 'Cache-Control': 'no-store', ETag: '"3"' },
        }),
      );
      expect(await sdk.getOwnDataRequest(id), type).toEqual({
        kind: 'invalid-response',
        reason: 'invalid-success',
      });
    }
    const { sdk: missing } = sdkWith(
      new Response(JSON.stringify(item()), {
        headers: { 'Cache-Control': 'no-store', ETag: '"3"' },
      }),
    );
    expect((await missing.getOwnDataRequest(id)).kind).toBe('invalid-response');
  });

  it('refuses an Idempotency-Replayed marker on a call that carries no key', async () => {
    const replay = { 'Idempotency-Replayed': 'true' };
    const calls: [string, Response, (sdk: Sdk) => Promise<{ kind: string }>][] = [
      ['a list', json({ items: [], pageInfo }, 200, replay), (sdk) => sdk.listOwnDataRequests()],
      ['a read', json(item(), 200, { ETag: '"3"', ...replay }), (sdk) => sdk.getOwnDataRequest(id)],
      ['a ticket', ticketResponseWith(replay), (sdk) => sdk.mintOwnDataExportTicket(id)],
      [
        'a chunk',
        json({ index: 0, count: 2, bytes: 'AA==' }, 200, replay),
        (sdk) => sdk.readOwnDataExportChunk(id, ticket, 0),
      ],
      [
        'a retained ticket',
        ticketResponseWith(replay),
        (sdk) => sdk.mintRetainedDataExportTicket('tenant', id),
      ],
    ];
    for (const [name, response, call] of calls) {
      const { sdk } = sdkWith(response);
      expect((await call(sdk)).kind, name).toBe('invalid-response');
    }
    // A keyed call may be replayed, and only with the literal value `true`.
    const { sdk: keyed } = sdkWith(
      json({ version: '4' }, 200, { ETag: '"4"', 'Idempotency-Replayed': 'false' }),
    );
    expect((await keyed.cancelOwnDataRequest(id, write)).kind).toBe('invalid-response');
  });

  it('refuses a page that names a previous page and a page that disagrees with itself', async () => {
    for (const bad of [
      { ...pageInfo, previousCursor: 'p-1' },
      { ...pageInfo, previousCursor: 'p-1', hasPrevious: true },
      { ...pageInfo, hasPrevious: true },
      { ...pageInfo, nextCursor: 'n-1' },
    ]) {
      const { sdk } = sdkWith(json({ items: [item()], pageInfo: bad }));
      expect((await sdk.listOwnDataRequests()).kind).toBe('invalid-response');
      const { sdk: scopes } = sdkWith(json({ items: [], pageInfo: bad }));
      expect((await scopes.listRetainedDataRequestScopes()).kind).toBe('invalid-response');
    }
  });

  it('leaves a chunk position to the download relay: any well-formed index is a valid chunk', async () => {
    // The relay (and the route test) refuse a chunk whose index is not the one asked for; the SDK validates shape.
    const { sdk } = sdkWith(json({ index: 1, count: 2, bytes: 'AA==' }));
    expect(await sdk.readOwnDataExportChunk(id, ticket, 0)).toMatchObject({
      kind: 'success',
      data: { index: 1, count: 2 },
    });
  });
});

function ticketResponseWith(headers: Record<string, string>) {
  return json(
    { ticket, expiresAt: artifact.expiresAt, chunkCount: 2, byteCount: 131073 },
    200,
    headers,
  );
}
