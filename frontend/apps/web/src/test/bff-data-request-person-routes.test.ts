// @vitest-environment node
import { afterEach, describe, expect, it, vi } from 'vitest';

import {
  DownloadWire,
  artifact,
  chunkBytes,
  closeDataRequestFixtures,
  configuration,
  fixture,
  id,
  organization,
  pageInfo,
  request,
  second,
  shown,
  ticket,
} from './data-request-person-fixture';
import en from '../i18n/messages/en/data.json';

/**
 * The person's own data-request routes through the real session store, the real native callers, the real SDK and the
 * real native transport, with only `fetch` faked: every request the API would receive, and every response it could
 * send, crosses the SDK's validators and the BFF's Location, ETag and no-store rules (ADR-0106 §§ 9-11, ADR-0107 § 4).
 */
afterEach(closeDataRequestFixtures);

const own = '/api/v1/identity/me/data-requests';
const entry = '/api/v1/identity/data-request-entry';
const key = 'person-route-key'.padEnd(43, 'k');
const json = (body: unknown, status = 200, headers: Record<string, string> = {}) =>
  new Response(JSON.stringify(body), {
    status,
    headers: {
      'content-type': status < 400 ? 'application/json' : 'application/problem+json',
      'cache-control': 'no-store',
      ...headers,
    },
  });
const accepted = (kind: 'export' | 'erasure' | 'closure', headers: Record<string, string> = {}) =>
  json({ id, kind, status: kind === 'export' ? 'requested' : 'scheduled', version: '0' }, 202, {
    ETag: '"0"',
    ...headers,
  });
const receipt = (version = '4') => json({ version }, 200, { ETag: '"' + version + '"' });
const ticketBody = () =>
  json({
    ticket,
    expiresAt: '2026-11-23T12:15:00.0000000+00:00',
    chunkCount: 2,
    byteCount: 131073,
  });
const chunkBody = (index: number, shown = index) =>
  json({ index: shown, count: 2, bytes: chunkBytes(index).toString('base64') });

/** A fake API: each "METHOD path" answers one response factory, and anything else is recorded and refused. */
function api() {
  const routes = new Map<string, () => Response>();
  const unexpected: string[] = [];
  const transport = vi.fn<typeof fetch>(async (input, init) => {
    const url = new URL(String(input));
    const label = (init?.method ?? 'GET') + ' ' + url.pathname;
    const answer = routes.get(label);
    if (!answer) {
      unexpected.push(label);
      return json({ title: 'unexpected' }, 500);
    }
    return answer();
  });
  return {
    transport,
    unexpected,
    on: (method: string, path: string, answer: () => Response) =>
      routes.set(method + ' ' + path, answer),
  };
}
const sent = (transport: ReturnType<typeof api>['transport'], call = 0) => {
  const [url, init] = transport.mock.calls[call]!;
  const parsed = new URL(String(url));
  return {
    origin: parsed.origin,
    path: parsed.pathname + parsed.search,
    method: init?.method,
    body: init?.body === undefined ? undefined : (JSON.parse(String(init.body)) as unknown),
    headers: new Headers(init?.headers),
    init,
  };
};
const paths = (transport: ReturnType<typeof api>['transport']) =>
  transport.mock.calls.map(
    (_, call) => sent(transport, call).method + ' ' + sent(transport, call).path,
  );

describe('the person’s page in a live session reads and files through the real SDK', () => {
  it('lists the receipts that pass the closed validators and shows them', async () => {
    const fake = api();
    fake.on('GET', own, () =>
      json({
        items: [
          request({
            status: 'completed',
            completedAt: '2026-11-23T11:00:00.0000000+00:00',
            artifact: artifact(),
          }),
        ],
        pageInfo,
      }),
    );
    const f = await fixture('session', [], { transport: fake.transport });
    const wire = await f.run('GET', '/account/data');
    expect(wire.statusCode).toBe(200);
    expect(wire.html).toContain('action="/api/account/data-requests/download"');
    expect(paths(fake.transport)).toEqual(['GET ' + own + '?limit=20']);
    const call = sent(fake.transport);
    expect(call.path).toBe(own + '?limit=20');
    // The native transport's closed request: an owned credential and scope, never a cookie or a browser value.
    expect(call.headers.get('Authorization')).toBe('Bearer private-access-canary');
    expect(call.headers.get('X-LearnStack-Native-Tenant-Id')).toBe(f.scope.tenantId);
    expect(call.headers.get('X-LearnStack-Host')).toBe('institution.example.test:3000');
    expect(call.headers.has('Cookie')).toBe(false);
    expect(call.init).toMatchObject({ cache: 'no-store', credentials: 'omit', redirect: 'error' });
    expect(fake.unexpected).toEqual([]);
  });

  it('answers a receipt the SDK refuses with the unavailable sentence, never with its content', async () => {
    for (const bad of [
      { ...request(), status: 'unknown' },
      { ...request(), extra: 'private-extra-canary' },
      request({ origin: 'someone_else' as never }),
      request({ artifact: { ...artifact(), status: 'lost' } as never, kind: 'export' }),
    ]) {
      const fake = api();
      fake.on('GET', own, () => json({ items: [bad], pageInfo }));
      const f = await fixture('session', [], { transport: fake.transport });
      const wire = await f.run('GET', '/account/data');
      expect(wire.statusCode).toBe(503);
      expect(wire.html).toContain(shown(en.error.unavailable));
      expect(wire.html).not.toContain('private-extra-canary');
      expect(wire.html).not.toContain('someone_else');
    }
  });

  it.each([
    ['export', 'exports', 'export'],
    ['erasure', 'erasures', 'erasure'],
    ['closure', 'closures', 'closure'],
  ] as const)(
    'files a %s at its own route with its key, an empty body and no precondition',
    async (action, route, kind) => {
      const fake = api();
      fake.on('POST', own + '/' + route, () => accepted(kind, { Location: own + '/' + id }));
      const f = await fixture('session', [], { transport: fake.transport });
      const wire = await f.run('POST', '/api/account/data-requests/' + action, {
        csrfToken: f.csrf,
        idempotencyKey: key,
        ...(action === 'erasure' ? { confirm: 'erase' } : {}),
        ...(action === 'closure' ? { confirm: 'close', acknowledge: 'no_status' } : {}),
      });
      expect(wire.statusCode).toBe(303);
      expect(wire.headers.get('location')).toBe('/account/data?result=' + kind);
      expect(paths(fake.transport)).toEqual(['POST ' + own + '/' + route]);
      const call = sent(fake.transport);
      expect(call.body).toEqual({});
      expect(call.headers.get('Idempotency-Key')).toBe(key);
      expect(call.headers.has('If-Match')).toBe(false);
      expect(call.headers.get('X-LearnStack-Native-Tenant-Id')).toBe(f.scope.tenantId);
      expect(fake.unexpected).toEqual([]);
    },
  );

  it('answers a creation whose Location names another request as unconfirmed, with no redirect', async () => {
    const fake = api();
    fake.on('POST', own + '/exports', () => accepted('export', { Location: own + '/' + second }));
    const f = await fixture('session', [], { transport: fake.transport });
    const wire = await f.run('POST', '/api/account/data-requests/export', {
      csrfToken: f.csrf,
      idempotencyKey: key,
    });
    expect(wire.statusCode).toBe(503);
    expect(wire.headers.has('location')).toBe(false);
    expect(wire.html).toContain(shown(en.error.unconfirmed));
  });

  it.each([
    ['cancel', '/cancellations', '3', 'cancelled'],
    ['revoke', '/artifact/revocations', '9007199254740993', 'revoked'],
  ] as const)(
    '%s posts once at the exact version with its key, never at another request’s route',
    async (action, suffix, version, result) => {
      const fake = api();
      fake.on('POST', own + '/' + id + suffix, () => receipt());
      const f = await fixture('session', [], { transport: fake.transport });
      const wire = await f.run('POST', '/api/account/data-requests/' + action, {
        csrfToken: f.csrf,
        idempotencyKey: key,
        requestId: id,
        version,
      });
      expect(wire.statusCode).toBe(303);
      expect(wire.headers.get('location')).toBe('/account/data?result=' + result);
      expect(paths(fake.transport)).toEqual(['POST ' + own + '/' + id + suffix]);
      const call = sent(fake.transport);
      expect(call.body).toEqual({});
      expect(call.headers.get('If-Match')).toBe('"' + version + '"');
      expect(call.headers.get('Idempotency-Key')).toBe(key);
      expect(fake.unexpected).toEqual([]);
    },
  );

  it('answers a stale version with the stale sentence and the same key left for a deliberate retry', async () => {
    const fake = api();
    fake.on('POST', own + '/' + id + '/cancellations', () =>
      json(
        {
          type: 'https://errors.learnstack.dev/concurrency_conflict',
          title: 'Refused',
          status: 409,
          instance: own + '/' + id + '/cancellations',
          code: 'concurrency_conflict',
          messageKey: 'lockey_concurrency_conflict',
          correlationId: 'c',
        },
        409,
      ),
    );
    const f = await fixture('session', [], { transport: fake.transport });
    const wire = await f.run('POST', '/api/account/data-requests/cancel', {
      csrfToken: f.csrf,
      idempotencyKey: key,
      requestId: id,
      version: '3',
    });
    expect(wire.statusCode).toBe(409);
    expect(wire.html).toContain(shown(en.error.stale));
    expect(wire.headers.has('location')).toBe(false);
  });

  it('streams a two-chunk bundle: ticket, then chunk 0 and chunk 1 in order, as one attachment', async () => {
    const fake = api();
    fake.on('POST', own + '/' + id + '/artifact/tickets', ticketBody);
    let index = 0;
    fake.on('POST', own + '/' + id + '/artifact/chunks', () => chunkBody(index++));
    const f = await fixture('session', [], { transport: fake.transport });
    const wire = new DownloadWire();
    await f.run(
      'POST',
      '/api/account/data-requests/download',
      { csrfToken: f.csrf, requestId: id },
      { wire },
    );
    expect(wire.statusCode).toBe(200);
    expect(wire.headers.get('content-length')).toBe('131073');
    expect(wire.headers.get('content-disposition')).toBe('attachment; filename="data-export.json"');
    expect(Buffer.concat(wire.bytes).equals(Buffer.concat([chunkBytes(0), chunkBytes(1)]))).toBe(
      true,
    );
    expect(wire.writableEnded).toBe(true);
    expect(wire.destroyed).toBe(false);
    expect(JSON.stringify([...wire.headers])).not.toContain(ticket);
    expect(paths(fake.transport)).toEqual([
      'POST ' + own + '/' + id + '/artifact/tickets',
      'POST ' + own + '/' + id + '/artifact/chunks',
      'POST ' + own + '/' + id + '/artifact/chunks',
    ]);
    // The ticket is a body value of the chunk reads only; neither read carries a key or a precondition.
    expect(sent(fake.transport, 0).body).toEqual({});
    expect(sent(fake.transport, 1).body).toEqual({ ticket, index: 0 });
    expect(sent(fake.transport, 2).body).toEqual({ ticket, index: 1 });
    for (const call of [0, 1, 2]) {
      expect(sent(fake.transport, call).headers.has('Idempotency-Key')).toBe(false);
      expect(sent(fake.transport, call).headers.has('If-Match')).toBe(false);
      expect(sent(fake.transport, call).path).not.toContain(ticket);
    }
    expect(fake.unexpected).toEqual([]);
  });

  it('refuses a chunk that names another position before any byte reaches the browser', async () => {
    const fake = api();
    fake.on('POST', own + '/' + id + '/artifact/tickets', ticketBody);
    // The SDK takes a well-formed chunk of any position; the relay owns the order.
    fake.on('POST', own + '/' + id + '/artifact/chunks', () => chunkBody(0, 1));
    const f = await fixture('session', [], { transport: fake.transport });
    const wire = new DownloadWire();
    const response = await f.run(
      'POST',
      '/api/account/data-requests/download',
      { csrfToken: f.csrf, requestId: id },
      { wire },
    );
    expect(wire.statusCode).toBe(503);
    expect(wire.headers.has('content-disposition')).toBe(false);
    expect(wire.bytes).toHaveLength(0);
    expect(response.html).toContain(shown(en.error.download));
    expect(response.html).not.toContain(ticket);
  });

  it('answers a ticket the API refuses with that sentence and reads no chunk', async () => {
    const fake = api();
    fake.on('POST', own + '/' + id + '/artifact/tickets', () =>
      json(
        {
          type: 'https://errors.learnstack.dev/business_rule_violation',
          title: 'Refused',
          status: 409,
          instance: own + '/' + id + '/artifact/tickets',
          code: 'business_rule_violation',
          messageKey: 'lockey_business_rule_violation',
          correlationId: 'c',
          errors: { artifact: [{ key: 'lockey_data_export_ticket_limit' }] },
        },
        409,
      ),
    );
    const f = await fixture('session', [], { transport: fake.transport });
    const wire = await f.run('POST', '/api/account/data-requests/download', {
      csrfToken: f.csrf,
      requestId: id,
    });
    expect(wire.statusCode).toBe(409);
    expect(wire.html).toContain(shown(en.refusal.data_export_ticket_limit));
    expect(paths(fake.transport)).toEqual(['POST ' + own + '/' + id + '/artifact/tickets']);
  });
});

describe('the retained entry reads and files through the real SDK, selector-free', () => {
  const group = 'tenant';
  const tail = '?group=' + group;
  it('lists the groups and a group’s requests with no tenant or organization header', async () => {
    const fake = api();
    fake.on('GET', entry + '/scopes', () =>
      json({
        items: [
          { tenantId: id, organizationId: null },
          { tenantId: id, organizationId: organization },
        ],
        pageInfo,
      }),
    );
    fake.on('GET', entry + '/requests', () => json({ items: [request()], pageInfo }));
    const f = await fixture('entry', [], { transport: fake.transport });
    expect((await f.run('GET', '/account/data')).html).toContain(
      'data-data-request-view="entry_scopes"',
    );
    expect((await f.run('GET', '/account/data?group=' + organization)).html).toContain(
      'data-data-request-view="entry"',
    );
    expect(
      fake.transport.mock.calls.map(
        ([url]) => new URL(String(url)).pathname + new URL(String(url)).search,
      ),
    ).toEqual([
      entry + '/scopes?limit=20',
      entry + '/requests?group=' + organization + '&limit=20',
    ]);
    for (const call of [0, 1]) {
      const headers = sent(fake.transport, call).headers;
      expect(headers.has('X-LearnStack-Native-Tenant-Id')).toBe(false);
      expect(headers.has('X-LearnStack-Native-Organization-Id')).toBe(false);
      expect(headers.get('X-LearnStack-Native-Session-Secret')).toBe(
        configuration.nativeSourceSecret,
      );
    }
    // The retained path never performs a scoped session operation.
    expect(f.administration).not.toHaveBeenCalled();
    expect(fake.unexpected).toEqual([]);
  });

  it.each([
    ['export', 'exports', 'export'],
    ['erasure', 'erasures', 'erasure'],
    ['closure', 'closures', 'closure'],
  ] as const)('files a retained %s in its group, with no Location', async (action, route, kind) => {
    const fake = api();
    fake.on('POST', entry + '/' + route, () => accepted(kind));
    const f = await fixture('entry', [], { transport: fake.transport });
    const wire = await f.run('POST', '/api/auth/entry/data-requests/' + action, {
      csrfToken: f.csrf,
      idempotencyKey: key,
      group,
      ...(action === 'erasure' ? { confirm: 'erase' } : {}),
      ...(action === 'closure' ? { confirm: 'close', acknowledge: 'no_status' } : {}),
    });
    expect(wire.statusCode).toBe(303);
    expect(wire.headers.get('location')).toBe('/account/data?group=' + group + '&result=' + kind);
    expect(paths(fake.transport)).toEqual(['POST ' + entry + '/' + route + tail]);
    expect(sent(fake.transport).body).toEqual({});
    expect(sent(fake.transport).headers.get('Idempotency-Key')).toBe(key);
    expect(fake.unexpected).toEqual([]);
  });

  it('answers a retained creation that carries a Location as unconfirmed', async () => {
    const fake = api();
    fake.on('POST', entry + '/exports', () => accepted('export', { Location: own + '/' + id }));
    const f = await fixture('entry', [], { transport: fake.transport });
    const wire = await f.run('POST', '/api/auth/entry/data-requests/export', {
      csrfToken: f.csrf,
      idempotencyKey: key,
      group,
    });
    expect(wire.statusCode).toBe(503);
    expect(wire.headers.has('location')).toBe(false);
    expect(wire.html).toContain(shown(en.error.unconfirmed));
  });

  it.each([
    ['cancel', '/cancellations', '3', 'cancelled'],
    ['revoke', '/revocations', '9007199254740993', 'revoked'],
  ] as const)(
    'retained %s names its request in the body, its group in the query and its version in If-Match',
    async (action, suffix, version, result) => {
      const fake = api();
      fake.on('POST', entry + suffix, () => receipt());
      const f = await fixture('entry', [], { transport: fake.transport });
      const wire = await f.run('POST', '/api/auth/entry/data-requests/' + action, {
        csrfToken: f.csrf,
        idempotencyKey: key,
        group: organization,
        requestId: id,
        version,
      });
      expect(wire.statusCode).toBe(303);
      expect(wire.headers.get('location')).toBe(
        '/account/data?group=' + organization + '&result=' + result,
      );
      expect(paths(fake.transport)).toEqual(['POST ' + entry + suffix + '?group=' + organization]);
      const call = sent(fake.transport);
      expect(call.body).toEqual({ requestId: id });
      expect(call.headers.get('If-Match')).toBe('"' + version + '"');
      expect(call.headers.get('Idempotency-Key')).toBe(key);
      expect(fake.unexpected).toEqual([]);
    },
  );

  it('streams a two-chunk retained bundle with the group in every query', async () => {
    const fake = api();
    fake.on('POST', entry + '/tickets', ticketBody);
    let index = 0;
    fake.on('POST', entry + '/chunks', () => chunkBody(index++));
    const f = await fixture('entry', [], { transport: fake.transport });
    const wire = new DownloadWire();
    await f.run(
      'POST',
      '/api/auth/entry/data-requests/download',
      { csrfToken: f.csrf, requestId: id, group: organization },
      { wire },
    );
    expect(wire.statusCode).toBe(200);
    expect(Buffer.concat(wire.bytes).equals(Buffer.concat([chunkBytes(0), chunkBytes(1)]))).toBe(
      true,
    );
    expect(wire.writableEnded).toBe(true);
    expect(paths(fake.transport)).toEqual([
      'POST ' + entry + '/tickets?group=' + organization,
      'POST ' + entry + '/chunks?group=' + organization,
      'POST ' + entry + '/chunks?group=' + organization,
    ]);
    expect(sent(fake.transport, 0).body).toEqual({ requestId: id });
    expect(sent(fake.transport, 1).body).toEqual({ requestId: id, ticket, index: 0 });
    expect(sent(fake.transport, 2).body).toEqual({ requestId: id, ticket, index: 1 });
    expect(fake.unexpected).toEqual([]);
  });
});
