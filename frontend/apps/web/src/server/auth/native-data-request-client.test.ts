// @vitest-environment node
import { describe, expect, it, vi } from 'vitest';

import {
  createNativeAdministrationCaller,
  createNativeRetainedDataRequestCaller,
} from './native-administration-client';
import type { RetainedDataRequestOperation } from './native-operation-contract';

const id = '019a154b-0000-7000-8000-000000000011';
const tenant = '019a154b-0000-7000-8000-0000000000ff';
const configuration = {
  apiOrigin: 'https://api.example.test',
  secret: 'private-hop-'.padEnd(48, 'x'),
  nativeSourceSecret: Buffer.from(Array.from({ length: 32 }, (_, index) => index + 1)).toString(
    'base64url',
  ),
};
const binding = { host: 'institution.example.test:3000', source: '203.0.113.19' };
const json = (value: unknown, status = 200, headers: Record<string, string> = {}) =>
  new Response(JSON.stringify(value), {
    status,
    headers: { 'content-type': 'application/json', 'cache-control': 'no-store', ...headers },
  });
const signal = () => new AbortController().signal;

describe('native data-request operation sources (ADR-0106 § 9)', () => {
  it('performs the retained set selector-free and nothing else on the retained caller', async () => {
    const transport = vi.fn<typeof fetch>(async () =>
      json({
        items: [],
        pageInfo: { nextCursor: null, previousCursor: null, hasNext: false, hasPrevious: false },
      }),
    );
    const caller = createNativeRetainedDataRequestCaller(configuration, transport);
    expect(
      (
        await caller(
          'owned-token',
          binding,
          { kind: 'listRetainedDataRequests', group: 'tenant', limit: 20 },
          signal(),
        )
      ).kind,
    ).toBe('success');
    expect(transport.mock.calls[0]![0]).toBe(
      'https://api.example.test/api/v1/identity/data-request-entry/requests?group=tenant&limit=20',
    );
    const headers = new Headers(transport.mock.calls[0]![1]!.headers);
    expect(headers.get('X-LearnStack-Native-Session-Secret')).toBe(
      configuration.nativeSourceSecret,
    );
    expect(headers.has('X-LearnStack-Native-Tenant-Id')).toBe(false);
    expect(headers.has('X-LearnStack-Native-Organization-Id')).toBe(false);
    for (const operation of [
      { kind: 'listOwnDataRequests', limit: 20 },
      { kind: 'listOwnConsentScopes' },
      { kind: 'getOwnCapabilities' },
    ])
      expect(
        await caller(
          'owned-token',
          binding,
          operation as unknown as RetainedDataRequestOperation,
          signal(),
        ),
      ).toEqual({ kind: 'invalid-request' });
    expect(transport).toHaveBeenCalledOnce();
  });

  it('keeps own operations in the session scope and refuses retained ones on the session caller', async () => {
    const transport = vi.fn<typeof fetch>(async () =>
      json({ id, kind: 'export', status: 'requested', version: '0' }, 202, {
        etag: '"0"',
        location: '/api/v1/identity/me/data-requests/' + id,
      }),
    );
    const caller = createNativeAdministrationCaller(configuration, transport);
    const scoped = { ...binding, tenantId: tenant, organizationId: null };
    const result = await caller(
      'owned-token',
      scoped,
      { kind: 'createOwnDataExport', idempotencyKey: 'k'.repeat(43) },
      signal(),
    );
    expect(result).toMatchObject({ kind: 'success', status: 202 });
    const init = transport.mock.calls[0]![1]!;
    expect(new Headers(init.headers).get('X-LearnStack-Native-Tenant-Id')).toBe(tenant);
    expect(new Headers(init.headers).get('Idempotency-Key')).toBe('k'.repeat(43));
    expect(
      await caller(
        'owned-token',
        scoped,
        { kind: 'listRetainedDataRequestScopes' } as never,
        signal(),
      ),
    ).toEqual({ kind: 'invalid-request' });
    expect(transport).toHaveBeenCalledOnce();
  });

  it('admits 202 as a success status and still refuses any other 2xx', async () => {
    const accepted = vi.fn<typeof fetch>(async () =>
      json({ id, kind: 'export', status: 'requested', version: '0' }, 202, {
        etag: '"0"',
        location: '/api/v1/identity/me/data-requests/' + id,
      }),
    );
    const caller = createNativeAdministrationCaller(configuration, accepted);
    const scoped = { ...binding, tenantId: tenant, organizationId: null };
    const own = { kind: 'createOwnDataExport', idempotencyKey: 'k'.repeat(43) } as const;
    expect((await caller('owned-token', scoped, own, signal())).kind).toBe('success');
    const other = createNativeAdministrationCaller(
      configuration,
      vi.fn<typeof fetch>(async () => json({}, 203)),
    );
    await expect(caller('owned-token', scoped, own, signal())).resolves.toBeDefined();
    await expect(other('owned-token', scoped, own, signal())).rejects.toThrow();
  });

  it("performs the administrator's group operations in the session's exact scope only", async () => {
    const organization = '019a154b-0000-7000-8000-0000000000ee';
    const receipt = {
      id,
      kind: 'export',
      origin: 'administrator',
      status: 'requested',
      createdAt: '2026-11-23T10:00:00Z',
      updatedAt: null,
      completedAt: null,
      lastRefusal: null,
      failureReason: null,
      retained: [],
      artifact: null,
      version: '0',
      executeAt: null,
      requestedByYou: true,
      membershipIds: [tenant.slice(0, -2) + '12'],
    };
    const transport = vi.fn<typeof fetch>(async (input) =>
      String(input).endsWith('/data-requests?limit=20')
        ? json({
            items: [receipt],
            pageInfo: {
              nextCursor: null,
              previousCursor: null,
              hasNext: false,
              hasPrevious: false,
            },
          })
        : json(receipt, 200, { etag: '"0"' }),
    );
    const caller = createNativeAdministrationCaller(configuration, transport);
    const scoped = { ...binding, tenantId: tenant, organizationId: organization };
    expect(
      (await caller('owned-token', scoped, { kind: 'listDataRequests', limit: 20 }, signal())).kind,
    ).toBe('success');
    expect(
      (await caller('owned-token', scoped, { kind: 'getDataRequest', id }, signal())).kind,
    ).toBe('success');
    expect(transport.mock.calls.map(([url]) => url)).toEqual([
      'https://api.example.test/api/v1/identity/data-requests?limit=20',
      'https://api.example.test/api/v1/identity/data-requests/' + id,
    ]);
    for (const [, init] of transport.mock.calls) {
      const headers = new Headers(init!.headers);
      expect(headers.get('X-LearnStack-Native-Tenant-Id')).toBe(tenant);
      expect(headers.get('X-LearnStack-Native-Organization-Id')).toBe(organization);
    }
    // The retained entry never performs an administrator operation.
    const retained = createNativeRetainedDataRequestCaller(configuration, transport);
    for (const operation of [
      { kind: 'listDataRequests', limit: 20 },
      { kind: 'createDataExportForSubject', membershipId: id, idempotencyKey: 'k'.repeat(43) },
    ])
      expect(
        await retained(
          'owned-token',
          binding,
          operation as unknown as RetainedDataRequestOperation,
          signal(),
        ),
      ).toEqual({ kind: 'invalid-request' });
    expect(transport).toHaveBeenCalledTimes(2);
  });
});
