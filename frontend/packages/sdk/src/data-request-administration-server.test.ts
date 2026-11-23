import { describe, expect, expectTypeOf, it, vi } from 'vitest';

import type { AdministrationFetch } from './administration-contract';
import { DATA_REQUEST_ORIGINS, createAdministrationServerSdk } from './administration-server';
import { DATA_REQUEST_ADMINISTRATION_POST_CONTRACTS } from './data-request-contract';
import type { DataExportArtifact, SubjectDataRequest } from './data-request-contract';
import type { components, operations, paths } from './generated/schema';
import snapshot from '../../../../backend/openapi/v1.json';

const id = '019a154b-0000-7000-8000-000000000041';
const member = '019a154b-0000-7000-8000-000000000011';
const other = '019a154b-0000-7000-8000-000000000012';
const key = 'k'.repeat(43);
const pageInfo = { nextCursor: null, previousCursor: null, hasNext: false, hasPrevious: false };
const artifact: DataExportArtifact = {
  id: '019a154b-0000-7000-8000-000000000042',
  status: 'available',
  expiresAt: '2026-11-30T10:00:00.0000000+00:00',
  purgedAt: null,
  byteCount: 2048,
  chunkCount: 1,
  truncations: [],
  version: '9007199254740993',
  readable: true,
};
const item = (value: Partial<SubjectDataRequest> = {}): SubjectDataRequest => ({
  id,
  kind: 'export',
  origin: 'administrator',
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
  requestedByYou: true,
  membershipIds: [member],
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

describe('administrator data-request wrappers (ADR-0106 §§ 3, 6, 10, 13; ADR-0107 § 4)', () => {
  it('owns exactly the administrator POST operations of the committed OpenAPI document', () => {
    const owned = Object.entries(DATA_REQUEST_ADMINISTRATION_POST_CONTRACTS);
    const documented = Object.entries(
      snapshot.paths as Record<string, { post?: { operationId: string } }>,
    ).flatMap(([path, value]) =>
      value.post && path.startsWith('/api/v1/identity/data-requests')
        ? [[path, value.post.operationId] as const]
        : [],
    );
    expect(Object.isFrozen(DATA_REQUEST_ADMINISTRATION_POST_CONTRACTS)).toBe(true);
    expect(owned.sort()).toEqual([...documented].sort());
    expectTypeOf<
      paths['/api/v1/identity/data-requests/{id}/artifact/revocations']['post']
    >().toEqualTypeOf<operations['RevokeDataExport']>();
    expectTypeOf<operations['CreateDataErasureForSubject']['requestBody']>().toEqualTypeOf<{
      content: { 'application/json': components['schemas']['DataExportForSubjectInput'] };
    }>();
    expectTypeOf<
      Exclude<keyof SubjectDataRequest, keyof components['schemas']['OwnDataRequestResponse']>
    >().toEqualTypeOf<'requestedByYou' | 'membershipIds'>();
  });

  it('lists and reads with the exact path, the request ETag and no key', async () => {
    const page = {
      items: [item(), item({ id: artifact.id, kind: 'erasure', artifact: null })],
      pageInfo: { ...pageInfo, nextCursor: 'n-1', hasNext: true },
    };
    const { sdk, transport } = sdkWith(json(page), json(item(), 200, { ETag: '"3"' }));
    expect((await sdk.listDataRequests({ cursor: 'c-1', limit: 20 })).kind).toBe('success');
    expect(sent(transport)).toMatchObject({
      path: '/api/v1/identity/data-requests?cursor=c-1&limit=20',
      method: 'GET',
    });
    expect(sent(transport).headers).not.toHaveProperty('Idempotency-Key');
    expect((await sdk.getDataRequest(id)).kind).toBe('success');
    expect(sent(transport, 1)).toMatchObject({
      path: '/api/v1/identity/data-requests/' + id,
      method: 'GET',
    });
    for (const bad of [
      json(item(), 200, { ETag: '"4"' }),
      json(item()),
      json(item({ id: artifact.id }), 200, { ETag: '"3"' }),
    ]) {
      const { sdk: refused } = sdkWith(bad);
      expect((await refused.getDataRequest(id)).kind).toBe('invalid-response');
    }
  });

  it('admits a receipt at the Membership snapshot bound and one of every origin', async () => {
    const ids = (count: number) =>
      Array.from(
        { length: count },
        (_, index) => member.slice(0, -3) + String(index).padStart(3, '0'),
      );
    // The bound is inclusive: 256 distinct Memberships are valid, one more is refused above.
    const { sdk: bound } = sdkWith(json({ items: [item({ membershipIds: ids(256) })], pageInfo }));
    expect(await bound.listDataRequests()).toMatchObject({ kind: 'success' });
    const { sdk: one } = sdkWith(json({ items: [item({ membershipIds: ids(1) })], pageInfo }));
    expect(await one.listDataRequests()).toMatchObject({ kind: 'success' });
    const receipts = DATA_REQUEST_ORIGINS.map((origin, index) =>
      item({
        id: '019a154b-0000-7000-8000-0000000009' + String(index).padStart(2, '0'),
        kind: 'erasure',
        origin,
        status: 'scheduled',
        completedAt: null,
        executeAt: '2026-11-24T10:00:00Z',
        artifact: null,
      }),
    );
    const { sdk: origins } = sdkWith(json({ items: receipts, pageInfo }));
    const result = await origins.listDataRequests();
    expect(result).toMatchObject({ kind: 'success' });
    if (result.kind === 'success') expect(result.data.items).toEqual(receipts);
  });

  it('refuses a receipt an administrator never reads', async () => {
    for (const bad of [
      item({ kind: 'closure', artifact: null, executeAt: '2026-11-24T10:00:00Z' }),
      item({ membershipIds: [] }),
      item({ membershipIds: [member, member] }),
      item({ membershipIds: ['019a154b-0000-4000-8000-000000000011'] }),
      item({
        membershipIds: Array.from(
          { length: 257 },
          (_, index) => member.slice(0, -3) + String(index).padStart(3, '0'),
        ),
      }),
      { ...item(), requestedByYou: 'yes' },
      (({ requestedByYou: _, ...rest }) => rest)(item()),
      { ...item(), subjectUserId: member },
    ]) {
      const { sdk } = sdkWith(json({ items: [bad], pageInfo }));
      expect((await sdk.listDataRequests()).kind).toBe('invalid-response');
    }
    const { sdk: duplicate } = sdkWith(json({ items: [item(), item()], pageInfo }));
    expect((await duplicate.listDataRequests()).kind).toBe('invalid-response');
  });

  it.each([
    ['createDataExportForSubject', 'export', 'requested', 'exports'],
    ['createDataErasureForSubject', 'erasure', 'scheduled', 'erasures'],
  ] as const)(
    'files %s for a Membership keyed, answering 202 with the exact Location',
    async (method, kind, status, path) => {
      const accepted = { id, kind, status, version: '0' };
      const location = { ETag: '"0"', Location: '/api/v1/identity/data-requests/' + id };
      const { sdk, transport } = sdkWith(json(accepted, 202, location));
      expect(await sdk[method](member, { idempotencyKey: key })).toMatchObject({
        kind: 'success',
        status: 202,
        location: location.Location,
      });
      expect(sent(transport)).toEqual({
        path: '/api/v1/identity/data-requests/' + path,
        method: 'POST',
        headers: {
          Accept: 'application/json',
          'Content-Type': 'application/json',
          'Idempotency-Key': key,
        },
        body: { membershipId: member },
      });
      for (const bad of [
        json(accepted, 202, { ETag: '"0"' }),
        json(accepted, 202, { ...location, Location: '/api/v1/identity/me/data-requests/' + id }),
        json(accepted, 202, { ...location, Location: '/api/v1/identity/data-requests/' + other }),
        json({ ...accepted, kind: kind === 'export' ? 'erasure' : 'export' }, 202, location),
        json(accepted, 200, location),
      ]) {
        const { sdk: refused } = sdkWith(bad);
        expect((await refused[method](member, { idempotencyKey: key })).kind).toBe(
          'invalid-response',
        );
      }
      const { sdk: replay } = sdkWith(
        json(accepted, 202, { ...location, 'Idempotency-Replayed': 'true' }),
      );
      expect(await replay[method](member, { idempotencyKey: key })).toMatchObject({
        replayed: true,
      });
    },
  );

  it('cancels at the request version and revokes at the artifact version, keyed', async () => {
    const receipt = (version: string) => json({ version }, 200, { ETag: '"' + version + '"' });
    const { sdk, transport } = sdkWith(receipt('4'), receipt('9007199254740994'));
    expect((await sdk.cancelDataRequest(id, { version: '3', idempotencyKey: key })).kind).toBe(
      'success',
    );
    expect(
      (await sdk.revokeDataExport(id, { version: artifact.version, idempotencyKey: key })).kind,
    ).toBe('success');
    expect(sent(transport, 0)).toEqual({
      path: '/api/v1/identity/data-requests/' + id + '/cancellations',
      method: 'POST',
      headers: {
        Accept: 'application/json',
        'Content-Type': 'application/json',
        'Idempotency-Key': key,
        'If-Match': '"3"',
      },
      body: {},
    });
    expect(sent(transport, 1)).toMatchObject({
      path: '/api/v1/identity/data-requests/' + id + '/artifact/revocations',
      headers: { 'If-Match': '"' + artifact.version + '"', 'Idempotency-Key': key },
      body: {},
    });
  });

  it('refuses hostile input before any network call', async () => {
    const { sdk, transport } = sdkWith();
    const write = { version: '3', idempotencyKey: key };
    for (const attempt of [
      () => sdk.getDataRequest('not-a-uuid'),
      () => sdk.getDataRequest('019a154b-0000-4000-8000-000000000041'),
      () => sdk.listDataRequests({ limit: 101 }),
      () => sdk.listDataRequests({ limit: 0 }),
      () => sdk.listDataRequests({ cursor: 'x'.repeat(1025) }),
      () => sdk.listDataRequests({ cursor: 'a b' }),
      () => sdk.createDataExportForSubject('../exports', { idempotencyKey: key }),
      () => sdk.createDataErasureForSubject(member.toUpperCase(), { idempotencyKey: key }),
      () => sdk.createDataExportForSubject(member, { idempotencyKey: 'short' }),
      () => sdk.cancelDataRequest(id, { ...write, version: '01' }),
      () => sdk.cancelDataRequest(id, { ...write, version: '9223372036854775808' }),
      () => sdk.cancelDataRequest('x', write),
      () => sdk.revokeDataExport(id, { ...write, idempotencyKey: 'k k k k k' }),
    ])
      expect(await attempt()).toEqual({ kind: 'invalid-request' });
    expect(transport).not.toHaveBeenCalled();
  });

  it('keeps the API refusal reason for the administrator sentence', async () => {
    const { sdk } = sdkWith(
      new Response(
        JSON.stringify({
          type: 'https://errors.learnstack.dev/business_rule_violation',
          title: 'Refused',
          status: 409,
          instance: '/api/v1/identity/data-requests/erasures',
          code: 'business_rule_violation',
          messageKey: 'lockey_business_rule_violation',
          correlationId: 'request-123',
          errors: { $: [{ key: 'lockey_subject_membership_current' }] },
        }),
        {
          status: 409,
          headers: { 'Content-Type': 'application/problem+json', 'Cache-Control': 'no-store' },
        },
      ),
    );
    const result = await sdk.createDataErasureForSubject(member, { idempotencyKey: key });
    if (result.kind !== 'api-error') throw new Error('Expected refusal');
    expect(result.status).toBe(409);
    expect(result.problem.code).toBe('business_rule_violation');
    expect(result.problem.errors).toEqual({ $: [{ key: 'lockey_subject_membership_current' }] });
  });
});
