import { describe, expect, expectTypeOf, it, vi } from 'vitest';

import type { AdministrationFetch, AdministrationResult } from './administration-contract';
import { createAdministrationServerSdk } from './administration-server';
import { DATA_REQUEST_POST_CONTRACTS } from './data-request-contract';
import type { DataExportArtifact, OwnDataRequest } from './data-request-contract';
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
