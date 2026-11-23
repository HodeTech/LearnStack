// @vitest-environment node
import { execFileSync } from 'node:child_process';
import { runInNewContext } from 'node:vm';

import type { OwnDataRequest } from '@learnstack/sdk/administration-server';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { DATA_REQUEST_HISTORY_SOURCE } from './data-request-html';
import {
  DATA_REQUEST_REFUSALS,
  createNativeDataRequestHandler,
  dataRequestRefusal,
} from './native-data-request-http';
import type {
  NativeAdministrationCaller,
  NativeOperationResult,
  NativeRetainedDataRequestCaller,
} from './native-operation-contract';
import en from '../../i18n/messages/en/data.json';
import tr from '../../i18n/messages/tr/data.json';
import { bffFixture, bffHost, bffSource } from '../../test/bff-fixture';
import { snapshotRequest, SnapshotWire } from '../../test/page-response-fixture';

const id = '019a154b-0000-7000-8000-000000000041';
const second = '019a154b-0000-7000-8000-000000000042';
const organization = '019a154b-0000-7000-8000-0000000000a1';
const ticket = 'T'.repeat(42) + 'A';
const now = Date.parse('2026-11-23T12:00:00Z');
const pageInfo = { nextCursor: null, previousCursor: null, hasNext: false, hasPrevious: false };
const ok = (data: unknown, extra: Partial<{ etag: string; location: string }> = {}) =>
  ({
    kind: 'success',
    status: 200,
    data,
    etag: extra.etag ?? null,
    location: extra.location ?? null,
    replayed: false,
  }) as NativeOperationResult;
const refused = (status: number, code: string, errors?: Record<string, { key: string }[]>) =>
  ({
    kind: 'api-error',
    status,
    error: { code: 'unknown', correlationId: 'c' },
    problem: {
      type: 'https://errors.learnstack.dev/' + code,
      title: 'Refused',
      status,
      instance: '/api/v1/identity/me/data-requests',
      code,
      messageKey: 'lockey_' + code,
      correlationId: 'c',
      ...(errors ? { errors } : {}),
    },
  }) as NativeOperationResult;
const artifact = (value: Partial<NonNullable<OwnDataRequest['artifact']>> = {}) => ({
  id: second,
  status: 'available' as const,
  expiresAt: '2026-11-30T10:00:00.0000000+00:00',
  purgedAt: null,
  byteCount: 131073,
  chunkCount: 2,
  truncations: [],
  version: '9007199254740993',
  readable: true,
  ...value,
});
const request = (value: Partial<OwnDataRequest> = {}): OwnDataRequest => ({
  id,
  kind: 'export',
  origin: 'self',
  status: 'requested',
  createdAt: '2026-11-23T10:00:00.0000000+00:00',
  updatedAt: null,
  completedAt: null,
  lastRefusal: null,
  failureReason: null,
  retained: [],
  artifact: null,
  version: '3',
  executeAt: null,
  ...value,
});
const chunkBytes = (index: number) =>
  Buffer.alloc(index === 0 ? 131072 : 1, index === 0 ? 0x61 : 0x62);

/** A transport sink that copies each written buffer (the relay zeroes it after the write) and acknowledges it. */
class DownloadWire extends SnapshotWire {
  readonly bytes: Buffer[] = [];
  override write(chunk: string, ...args: unknown[]) {
    this.flushHeaders();
    if (typeof chunk === 'string') this.chunks.push(chunk);
    else this.bytes.push(Buffer.from(chunk as unknown as Buffer));
    const callback = args[args.length - 1];
    if (typeof callback === 'function') queueMicrotask(() => Reflect.apply(callback, this, []));
    return true;
  }
  override end(...args: unknown[]) {
    const callback = args[args.length - 1];
    const result = super.end(...args.filter((value) => typeof value !== 'function'));
    if (typeof callback === 'function') queueMicrotask(() => Reflect.apply(callback, this, []));
    return result;
  }
}

const closes: (() => Promise<void>)[] = [];
afterEach(async () => {
  for (const close of closes.splice(0).reverse()) await close();
});

async function fixture(mode: 'session' | 'entry', page: readonly OwnDataRequest[] = []) {
  const own = vi.fn<
    (operation: Parameters<NativeAdministrationCaller>[2]) => NativeOperationResult
  >((operation) => {
    switch (operation.kind) {
      case 'listOwnDataRequests':
        return ok({ items: page, pageInfo });
      case 'mintOwnDataExportTicket':
        return ok({ ticket, expiresAt: '2026-11-23T12:15:00Z', chunkCount: 2, byteCount: 131073 });
      case 'readOwnDataExportChunk':
        return ok({
          index: operation.index,
          count: 2,
          bytes: chunkBytes(operation.index).toString('base64'),
        });
      case 'createOwnDataExport':
        return ok({ id, kind: 'export', status: 'requested', version: '0' });
      default:
        return ok({ version: '4' });
    }
  });
  const retained = vi.fn<NativeRetainedDataRequestCaller>(async (_token, _binding, operation) => {
    switch (operation.kind) {
      case 'listRetainedDataRequestScopes':
        return ok({
          items: [
            { tenantId: id, organizationId: null },
            { tenantId: id, organizationId: organization },
          ],
          pageInfo,
        });
      case 'listRetainedDataRequests':
        return ok({ items: page, pageInfo });
      case 'mintRetainedDataExportTicket':
        return ok({ ticket, expiresAt: '2026-11-23T12:15:00Z', chunkCount: 2, byteCount: 131073 });
      case 'readRetainedDataExportChunk':
        return ok({
          index: operation.index,
          count: 2,
          bytes: chunkBytes(operation.index).toString('base64'),
        });
      default:
        return ok({ version: '4' });
    }
  });
  const f = bffFixture({ retainedDataRequest: retained });
  const original = f.administration.getMockImplementation()!;
  f.administration.mockImplementation(async (token, binding, operation, signal) =>
    [
      'listOwnDataRequests',
      'createOwnDataExport',
      'createOwnDataErasure',
      'createOwnAccountClosure',
      'cancelOwnDataRequest',
      'revokeOwnDataExport',
      'mintOwnDataExportTicket',
      'readOwnDataExportChunk',
    ].includes(operation.kind)
      ? own(operation)
      : original(token, binding, operation, signal),
  );
  const owner = mode === 'session' ? await f.login() : await f.enter();
  const csrf =
    mode === 'session'
      ? f.store.peek((owner as Awaited<ReturnType<typeof f.login>>).binding)!.csrfToken
      : (owner as Awaited<ReturnType<typeof f.enter>>).finished.entry.csrfToken;
  f.account.mockClear();
  f.administration.mockClear();
  own.mockClear();
  const handler = createNativeDataRequestHandler({
    catalogues: { en, tr },
    store: f.store,
    responseLease: f.requestRuntime.nativeDataRequestResponse,
    now: () => now,
  });
  closes.push(async () => {
    await f.requestRuntime.stop();
    await f.store.stop();
    f.publicAdmission.shutdown();
  });
  const run = async (
    method: string,
    target: string,
    form?: Record<string, string> | URLSearchParams,
    options: { origin?: string; headers?: Record<string, string>; wire?: SnapshotWire } = {},
  ) => {
    const incoming = snapshotRequest();
    incoming.method = method;
    incoming.url = target;
    incoming.headers.cookie = owner.cookie;
    Object.assign(incoming.headers, options.headers ?? {});
    if (form) {
      incoming.headers.origin = options.origin ?? 'https://' + bffHost;
      incoming.headers['content-type'] = 'application/x-www-form-urlencoded';
      incoming.complete = true;
      incoming.push(
        (form instanceof URLSearchParams ? form : new URLSearchParams(form)).toString(),
      );
      incoming.push(null);
    }
    const binding = { host: bffHost, peer: bffSource, method, target };
    const wire = options.wire ?? new SnapshotWire();
    await f.publicAdmission.run(binding, incoming, wire.native, () =>
      f.requestRuntime.run(binding, incoming, wire.native, () =>
        handler(binding, incoming, wire.native),
      ),
    );
    wire.drainCallbacks();
    for (let turn = 0; turn < 16; ++turn) await Promise.resolve();
    return Object.assign(wire, { html: wire.chunks.join('') });
  };
  return { ...f, own, retained, run, csrf, owner };
}
const count = (html: string, pattern: RegExp) => [...html.matchAll(pattern)].length;
/** The renderer escapes every printed value; authored copy is compared as it appears in the document. */
const shown = (text: string) => text.replaceAll('&', '&amp;').replaceAll("'", '&#39;');
const formKey = (html: string) =>
  /name="idempotencyKey" value="([A-Za-z0-9_-]{43})"/.exec(html)![1]!;

describe('the person’s own data-request page in a live session', () => {
  it('lists every closed state with its next step, dates and only the permitted actions', async () => {
    const items: OwnDataRequest[] = [
      request({ id: '019a154b-0000-7000-8000-000000000001' }),
      request({
        id: '019a154b-0000-7000-8000-000000000002',
        status: 'completed',
        completedAt: '2026-11-22T10:00:00Z',
        updatedAt: '2026-11-22T10:00:00Z',
        artifact: artifact({
          truncations: [{ collection: 'audit.entries', included: 5, total: 9 }],
        }),
      }),
      request({
        id: '019a154b-0000-7000-8000-000000000003',
        status: 'completed',
        completedAt: '2026-11-01T10:00:00Z',
        updatedAt: '2026-11-01T10:00:00Z',
        artifact: artifact({ status: 'expired', readable: false }),
      }),
      request({
        id: '019a154b-0000-7000-8000-000000000004',
        kind: 'erasure',
        status: 'scheduled',
        executeAt: '2026-11-24T10:00:00Z',
      }),
      request({
        id: '019a154b-0000-7000-8000-000000000005',
        kind: 'erasure',
        status: 'scheduled',
        executeAt: '2026-11-23T11:00:00Z',
      }),
      request({
        id: '019a154b-0000-7000-8000-000000000006',
        kind: 'erasure',
        status: 'awaiting_privileged_step',
        origin: 'administrator',
        executeAt: '2026-11-20T10:00:00Z',
        retained: [{ kind: 'consent_evidence', count: 2 }],
      }),
      request({
        id: '019a154b-0000-7000-8000-000000000007',
        kind: 'erasure',
        status: 'completed',
        completedAt: '2026-11-21T10:00:00Z',
        retained: [{ kind: 'audit_actor_reference', count: 12 }],
      }),
      request({
        id: '019a154b-0000-7000-8000-000000000008',
        kind: 'erasure',
        status: 'blocked',
        lastRefusal: 'blocked_hold',
        updatedAt: '2026-11-19T10:00:00Z',
      }),
      request({
        id: '019a154b-0000-7000-8000-000000000009',
        status: 'failed',
        failureReason: 'execution_failed',
        updatedAt: '2026-11-18T10:00:00Z',
      }),
      request({
        id: '019a154b-0000-7000-8000-000000000010',
        status: 'cancelled',
        updatedAt: '2026-11-17T10:00:00Z',
      }),
      request({
        id: '019a154b-0000-7000-8000-000000000011',
        kind: 'closure',
        status: 'scheduled',
        executeAt: '2026-11-23T11:00:00Z',
        lastRefusal: 'blocked_last_administrator',
      }),
      request({
        id: '019a154b-0000-7000-8000-000000000012',
        kind: 'erasure',
        origin: 'closure_fanout',
        status: 'fulfilling',
      }),
    ];
    const f = await fixture('session', items);
    const wire = await f.run('GET', '/account/data');
    expect(wire.statusCode).toBe(200);
    const html = wire.html;
    expect(count(html, /<h1>/g)).toBe(1);
    expect(html).toContain('data-data-request-view="session"');
    for (const sentence of [
      en.next.export_requested,
      en.next.export_ready,
      en.next.export_gone,
      en.next.erasure_scheduled,
      en.next.erasure_due,
      en.next.erasure_awaiting,
      en.next.erasure_completed,
      en.next.blocked,
      en.blocked_reason.blocked_hold,
      en.next.failed,
      en.next.cancelled,
      en.next.closure_scheduled,
      en.next.erasure_fulfilling,
      en.failure.execution_failed,
      en.last_refusal.blocked_last_administrator,
      en.list.by_administrator,
      en.list.by_closure,
      en.retained.partial,
      en.retained.pending,
      en.artifact.collection_audit_entries,
    ])
      expect(html).toContain(shown(sentence));
    expect(html).toContain('Status: ' + en.status.due);
    expect(html).toContain('Status: ' + en.status.awaiting_privileged_step);
    // Cancellable: the requested export, the erasure still in its window, the scheduled closure.
    expect(count(html, /action="\/api\/account\/data-requests\/cancel"/g)).toBe(3);
    expect(count(html, /action="\/api\/account\/data-requests\/download"/g)).toBe(1);
    expect(count(html, /action="\/api\/account\/data-requests\/revoke"/g)).toBe(1);
    expect(html).toContain('name="version" value="9007199254740993"');
    // An active export, erasure and closure each hide their filing control.
    expect(html).not.toContain('action="/api/account/data-requests/export"');
    expect(html).toContain(shown(en.export.active));
    expect(html).toContain(shown(en.erasure.active));
    expect(html).toContain(shown(en.closure.active));
    expect(html).not.toContain('<table');
    expect(html).not.toContain(ticket);
    expect(f.own.mock.calls.map(([operation]) => operation)).toEqual([
      { kind: 'listOwnDataRequests', limit: 20 },
    ]);
    // The live session's own context selects the group; the page never names one.
    expect(f.administration.mock.calls[0]![1]).toMatchObject({ tenantId: f.scope.tenantId });
    expect(f.requestRuntime.snapshotCounts()).toEqual({ active: 0, owners: 0, bytes: 0 });
  });

  it('offers every filing on an empty page and serves Turkish for a Turkish browser', async () => {
    const f = await fixture('session');
    const english = (await f.run('GET', '/account/data')).html;
    expect(english).toContain(shown(en.list.empty));
    expect(english).toContain('action="/api/account/data-requests/export"');
    expect(english).toContain('href="/account/data?step=erasure"');
    expect(english).toContain('href="/account/data?step=closure"');
    const turkish = await f.run('GET', '/account/data', undefined, {
      headers: { 'accept-language': 'tr-TR,tr;q=0.9' },
    });
    expect(turkish.html).toContain('<html lang="tr"');
    expect(turkish.html).toContain(shown(tr.list.empty));
  });

  it('files with a fresh form key and answers with a redirect to the fixed page', async () => {
    const f = await fixture('session');
    const key = formKey((await f.run('GET', '/account/data')).html);
    const wire = await f.run('POST', '/api/account/data-requests/export', {
      csrfToken: f.csrf,
      idempotencyKey: key,
    });
    expect(wire.statusCode).toBe(303);
    expect(wire.headers.get('location')).toBe('/account/data?result=export');
    expect(f.own).toHaveBeenLastCalledWith({ kind: 'createOwnDataExport', idempotencyKey: key });
    const done = await f.run('GET', '/account/data?result=export');
    expect(done.html).toContain('<div class="notice" role="status">');
    expect(done.html).toContain('<title>' + en.result.title + ' – ' + en.common.title);
  });

  it('renders the erasure and closure confirmations and refuses a filing without them', async () => {
    const f = await fixture('session');
    const erasure = (await f.run('GET', '/account/data?step=erasure')).html;
    expect(erasure).toContain('<fieldset><legend>' + en.erasure.legend + '</legend>');
    expect(erasure).toContain('name="confirm" value="erase" required');
    for (const text of [en.erasure.kept_uncounted, en.erasure.kept_partial, en.erasure.timing])
      expect(erasure).toContain(shown(text));
    const closure = (await f.run('GET', '/account/data?step=closure')).html;
    for (const text of [
      en.closure.warning_everywhere,
      en.closure.warning_email,
      en.closure.warning_stops,
      en.closure.warning_no_status,
    ])
      expect(closure).toContain(shown(text));
    expect(count(closure, /type="checkbox"[^>]*required/g)).toBe(2);
    const key = formKey(closure);
    for (const [path, fields] of [
      ['/api/account/data-requests/erasure', {}],
      ['/api/account/data-requests/erasure', { confirm: 'yes' }],
      ['/api/account/data-requests/closure', { confirm: 'close' }],
      ['/api/account/data-requests/closure', { acknowledge: 'no_status' }],
    ] as const) {
      const wire = await f.run('POST', path, { csrfToken: f.csrf, idempotencyKey: key, ...fields });
      expect(wire.statusCode).toBe(400);
      expect(wire.html).toContain(shown(en.error.confirmation));
    }
    expect(f.own).not.toHaveBeenCalledWith(
      expect.objectContaining({ kind: 'createOwnDataErasure' }),
    );
    const filed = await f.run('POST', '/api/account/data-requests/closure', {
      csrfToken: f.csrf,
      idempotencyKey: key,
      confirm: 'close',
      acknowledge: 'no_status',
    });
    expect(filed.headers.get('location')).toBe('/account/data?result=closure');
    expect(f.own).toHaveBeenLastCalledWith({
      kind: 'createOwnAccountClosure',
      idempotencyKey: key,
    });
  });

  it('refuses hostile forms before any operation', async () => {
    const f = await fixture('session');
    const key = 'k'.repeat(43);
    const valid = { csrfToken: f.csrf, idempotencyKey: key, requestId: id, version: '3' };
    const cases: [Record<string, string> | URLSearchParams, number, string?][] = [
      [{ ...valid, csrfToken: 'x'.repeat(43) }, 403],
      [{ ...valid, requestId: 'not-a-uuid' }, 400],
      [{ ...valid, requestId: '019a154b-0000-4000-8000-000000000041' }, 400],
      [{ ...valid, version: '03' }, 400],
      [{ ...valid, version: '9223372036854775808' }, 400],
      [{ ...valid, idempotencyKey: 'short' }, 400],
      [{ ...valid, group: 'tenant' }, 400],
      [{ ...valid, tenantId: id }, 400],
      [new URLSearchParams([...Object.entries(valid), ['requestId', second]]), 400],
    ];
    for (const [form, status] of cases)
      expect((await f.run('POST', '/api/account/data-requests/cancel', form)).statusCode).toBe(
        status,
      );
    expect(
      (
        await f.run('POST', '/api/account/data-requests/cancel', valid, {
          origin: 'https://evil.test',
        })
      ).statusCode,
    ).toBe(400);
    expect((await f.run('GET', '/account/data?group=tenant')).statusCode).toBe(400);
    expect((await f.run('GET', '/account/data?step=everything')).statusCode).toBe(400);
    expect((await f.run('GET', '/account/data?result=export&result=export')).statusCode).toBe(400);
    expect((await f.run('GET', '/api/account/data-requests/cancel')).statusCode).toBe(404);
    expect(f.own).not.toHaveBeenCalled();
    const cancelled = await f.run('POST', '/api/account/data-requests/cancel', valid);
    expect(cancelled.headers.get('location')).toBe('/account/data?result=cancelled');
    expect(f.own).toHaveBeenCalledExactlyOnceWith({
      kind: 'cancelOwnDataRequest',
      id,
      version: '3',
      idempotencyKey: key,
    });
  });

  it('maps every refusal to one plain sentence and keeps an unknown reason generic', async () => {
    const f = await fixture('session');
    const form = { csrfToken: f.csrf, idempotencyKey: 'k'.repeat(43) };
    for (const [reason, key] of Object.entries(DATA_REQUEST_REFUSALS)) {
      f.own.mockReturnValueOnce(refused(409, 'business_rule_violation', { $: [{ key: reason }] }));
      const wire = await f.run('POST', '/api/account/data-requests/export', form);
      expect(wire.statusCode).toBe(409);
      const [group, name] = key.split('.') as ['refusal', keyof typeof en.refusal];
      expect(group).toBe('refusal');
      expect(wire.html).toContain(shown(en.refusal[name]));
      expect(wire.html).toContain('role="alert"');
      expect(wire.html).not.toContain(reason);
    }
    f.own.mockReturnValueOnce(
      refused(409, 'business_rule_violation', { $: [{ key: 'lockey_future_reason' }] }),
    );
    expect((await f.run('POST', '/api/account/data-requests/export', form)).html).toContain(
      en.error.conflict,
    );
    f.own.mockReturnValueOnce({ kind: 'transport-error' });
    const unknown = await f.run('POST', '/api/account/data-requests/export', form);
    expect(unknown.statusCode).toBe(503);
    expect(unknown.html).toContain(shown(en.error.unconfirmed));
  });

  it('seals the API continuation to the owner and refuses a forged one before any read', async () => {
    const f = await fixture('session');
    f.own.mockReturnValueOnce(
      ok({ items: [], pageInfo: { ...pageInfo, nextCursor: 'raw-api-cursor', hasNext: true } }),
    );
    const html = (await f.run('GET', '/account/data')).html;
    const next = /href="\/account\/data\?cursor=([A-Za-z0-9_-]+)"/.exec(html)![1]!;
    expect(html).not.toContain('raw-api-cursor');
    await f.run('GET', '/account/data?cursor=' + next);
    expect(f.own).toHaveBeenLastCalledWith({
      kind: 'listOwnDataRequests',
      limit: 20,
      cursor: 'raw-api-cursor',
    });
    f.own.mockClear();
    expect(
      (await f.run('GET', '/account/data?cursor=' + next.slice(0, -2) + 'AA')).statusCode,
    ).toBe(400);
    expect(f.own).not.toHaveBeenCalled();
  });

  it('keeps HEAD and prefetch passive, refuses a bearer and shows sign-in without an owner', async () => {
    const f = await fixture('session');
    expect((await f.run('HEAD', '/account/data')).chunks).toEqual([]);
    expect(
      (await f.run('GET', '/account/data', undefined, { headers: { 'sec-purpose': 'prefetch' } }))
        .html,
    ).toBe('');
    expect(
      (await f.run('GET', '/account/data', undefined, { headers: { authorization: 'Bearer x' } }))
        .statusCode,
    ).toBe(400);
    expect(f.own).not.toHaveBeenCalled();
    const anonymous = await f.run('GET', '/account/data', undefined, {
      headers: { cookie: '' },
    });
    expect(anonymous.statusCode).toBe(200);
    expect(anonymous.html).toContain('href="/api/auth/signin"');
    const anonymousPost = await f.run(
      'POST',
      '/api/account/data-requests/export',
      { csrfToken: f.csrf, idempotencyKey: 'k'.repeat(43) },
      { headers: { cookie: '' } },
    );
    expect(anonymousPost.statusCode).toBe(401);
    expect(f.own).not.toHaveBeenCalled();
  });
});

describe('the retained own-scope entry', () => {
  it('lists the retained groups, views one by POST and files there without a live session', async () => {
    const f = await fixture('entry', [request()]);
    const chooser = await f.run('GET', '/account/data');
    expect(chooser.html).toContain('data-data-request-view="entry_scopes"');
    expect(count(chooser.html, /action="\/api\/auth\/entry\/data-requests"/g)).toBe(2);
    expect(chooser.html).toContain(shown(en.scope.entry_tenant));
    expect(chooser.html).toContain(en.scope.entry_organization + ' 1');
    expect(chooser.html).toContain(shown(en.scope.entry_names));
    const view = await f.run('POST', '/api/auth/entry/data-requests', {
      csrfToken: f.csrf,
      group: organization,
    });
    expect(view.statusCode).toBe(200);
    expect(view.html).toContain('data-data-request-view="entry"');
    expect(view.html).toContain('name="group" value="' + organization + '"');
    expect(view.html).toContain('action="/api/auth/entry/data-requests/cancel"');
    const filed = await f.run('POST', '/api/auth/entry/data-requests/erasure', {
      csrfToken: f.csrf,
      idempotencyKey: 'k'.repeat(43),
      group: 'tenant',
      confirm: 'erase',
    });
    expect(filed.headers.get('location')).toBe('/account/data?group=tenant&result=erasure');
    expect(f.retained.mock.calls.map(([, , operation]) => operation)).toEqual([
      { kind: 'listRetainedDataRequestScopes', limit: 20 },
      { kind: 'listRetainedDataRequests', group: organization, limit: 20 },
      { kind: 'createRetainedDataErasure', group: 'tenant', idempotencyKey: 'k'.repeat(43) },
    ]);
    // The retained path never performs a scoped live-session operation.
    expect(f.administration).not.toHaveBeenCalled();
    expect(f.native.self).not.toHaveBeenCalled();
  });

  it('states when the entry ends, restores the same group and returns a refused confirmation to its step', async () => {
    const f = await fixture('entry', [request()]);
    const ends = (f.owner as Awaited<ReturnType<typeof f.enter>>).finished.entry.expiresAt;
    const chooser = await f.run('GET', '/account/data');
    const view = await f.run('POST', '/api/auth/entry/data-requests', {
      csrfToken: f.csrf,
      group: organization,
    });
    // The page states the very bound its response is pinned to: the entry's own expiry.
    for (const html of [chooser.html, view.html])
      expect(html).toContain(
        '<dt>' +
          en.scope.entry_time_until +
          '</dt><dd><time datetime="' +
          new Date(ends).toISOString(),
      );
    expect(chooser.html).toContain("history.replaceState(null,'','/account/data')");
    expect(view.html).toContain(
      "history.replaceState(null,'','/account/data?group=" + organization + "')",
    );
    const refused = await f.run('POST', '/api/auth/entry/data-requests/erasure', {
      csrfToken: f.csrf,
      idempotencyKey: 'k'.repeat(43),
      group: organization,
    });
    expect(refused.statusCode).toBe(400);
    expect(refused.html).toContain(
      'href="/account/data?group=' + organization + '&amp;step=erasure"',
    );
    expect(f.retained).not.toHaveBeenCalledWith(
      expect.anything(),
      expect.anything(),
      expect.objectContaining({ kind: 'createRetainedDataErasure' }),
      expect.anything(),
    );
  });

  it('refuses an invalid group, a session-only route and a missing CSRF proof', async () => {
    const f = await fixture('entry');
    for (const [form, status] of [
      [{ csrfToken: f.csrf, group: 'Tenant' }, 400],
      [{ csrfToken: f.csrf }, 400],
      [{ csrfToken: 'x'.repeat(43), group: 'tenant' }, 403],
    ] as const)
      expect((await f.run('POST', '/api/auth/entry/data-requests', form)).statusCode).toBe(status);
    // The entry owner is not a live session: the session's routes refuse it as signed out.
    expect(
      (
        await f.run('POST', '/api/account/data-requests/export', {
          csrfToken: f.csrf,
          idempotencyKey: 'k'.repeat(43),
        })
      ).statusCode,
    ).toBe(401);
    expect((await f.run('GET', '/account/data?step=erasure')).statusCode).toBe(400);
    expect(f.retained).not.toHaveBeenCalled();
  });
});

describe('the export download relay', () => {
  it('streams the chunks in order as one no-store attachment and never shows the ticket', async () => {
    const f = await fixture('session');
    const log = vi.spyOn(console, 'log');
    const warn = vi.spyOn(console, 'warn');
    const wire = new DownloadWire();
    await f.run(
      'POST',
      '/api/account/data-requests/download',
      { csrfToken: f.csrf, requestId: id },
      { wire },
    );
    expect(wire.statusCode).toBe(200);
    expect(Object.fromEntries(wire.headers)).toMatchObject({
      'content-type': 'application/json; charset=utf-8',
      'content-disposition': 'attachment; filename="data-export.json"',
      'content-length': '131073',
      'cache-control': 'no-store',
      'x-content-type-options': 'nosniff',
    });
    const body = Buffer.concat(wire.bytes);
    expect(body.equals(Buffer.concat([chunkBytes(0), chunkBytes(1)]))).toBe(true);
    expect(wire.destroyed).toBe(false);
    expect(JSON.stringify([...wire.headers])).not.toContain(ticket);
    expect(f.own.mock.calls.map(([operation]) => operation.kind)).toEqual([
      'mintOwnDataExportTicket',
      'readOwnDataExportChunk',
      'readOwnDataExportChunk',
    ]);
    expect(log).not.toHaveBeenCalled();
    expect(warn).not.toHaveBeenCalled();
  });

  it('destroys the connection on a failed chunk after the headers, with no completed body', async () => {
    const f = await fixture('session');
    f.own.mockImplementation((operation) =>
      operation.kind === 'mintOwnDataExportTicket'
        ? ok({ ticket, expiresAt: '2026-11-23T12:15:00Z', chunkCount: 2, byteCount: 131073 })
        : operation.kind === 'readOwnDataExportChunk' && operation.index === 0
          ? ok({ index: 0, count: 2, bytes: chunkBytes(0).toString('base64') })
          : { kind: 'transport-error' },
    );
    const wire = await f.run(
      'POST',
      '/api/account/data-requests/download',
      { csrfToken: f.csrf, requestId: id },
      { wire: new DownloadWire() },
    );
    expect(wire.headersSent).toBe(true);
    expect(wire.destroyed).toBe(true);
    expect(wire.writableEnded).toBe(false);
  });

  it('answers the ticket limit and a gone file with their sentences before any byte', async () => {
    const f = await fixture('session');
    for (const [reason, sentence] of [
      ['lockey_data_export_ticket_limit', en.refusal.data_export_ticket_limit],
      ['lockey_data_export_unavailable', en.refusal.data_export_unavailable],
    ] as const) {
      f.own.mockReturnValueOnce(
        refused(409, 'business_rule_violation', { artifact: [{ key: reason }] }),
      );
      const wire = await f.run('POST', '/api/account/data-requests/download', {
        csrfToken: f.csrf,
        requestId: id,
      });
      expect(wire.statusCode).toBe(409);
      expect(wire.headers.get('content-type')).toBe('text/html; charset=utf-8');
      expect(wire.html).toContain(sentence);
    }
  });

  it('downloads through the retained chunks for an entry owner', async () => {
    const f = await fixture('entry');
    const wire = new DownloadWire();
    await f.run(
      'POST',
      '/api/auth/entry/data-requests/download',
      { csrfToken: f.csrf, requestId: id, group: 'tenant' },
      { wire },
    );
    expect(Buffer.concat(wire.bytes).length).toBe(131073);
    expect(f.retained.mock.calls.map(([, , operation]) => operation)).toEqual([
      { kind: 'mintRetainedDataExportTicket', group: 'tenant', id },
      { kind: 'readRetainedDataExportChunk', group: 'tenant', id, ticket, index: 0 },
      { kind: 'readRetainedDataExportChunk', group: 'tenant', id, ticket, index: 1 },
    ]);
  });
});

describe('refusal mapping', () => {
  it('keeps statuses and the generic envelope closed', () => {
    expect(dataRequestRefusal(refused(404, 'not_found'), true)).toMatchObject({
      status: 404,
      message: 'error.not_found',
    });
    expect(dataRequestRefusal(refused(401, 'unauthorized'), false).message).toBe(
      'error.unauthorized',
    );
    expect(dataRequestRefusal(refused(403, 'forbidden'), false).message).toBe('error.forbidden');
    expect(dataRequestRefusal(refused(429, 'rate_limited'), false)).toMatchObject({
      message: 'error.busy',
      retryAfter: 30,
    });
    expect(dataRequestRefusal(refused(503, 'dependency_unavailable'), true)).toMatchObject({
      message: 'error.unavailable',
      retryAfter: 30,
    });
    expect(dataRequestRefusal(refused(500, 'unknown'), true).message).toBe('error.unconfirmed');
    expect(dataRequestRefusal(refused(409, 'concurrency_conflict'), true).message).toBe(
      'error.stale',
    );
    expect(dataRequestRefusal(refused(409, 'request_in_progress'), true).message).toBe(
      'error.in_progress',
    );
    expect(dataRequestRefusal(refused(409, 'idempotency_key_reuse'), true).message).toBe(
      'error.key_reuse',
    );
    expect(dataRequestRefusal(refused(409, 'idempotency_outcome_unavailable'), true).message).toBe(
      'error.outcome_unavailable',
    );
    expect(dataRequestRefusal({ kind: 'invalid-request' }, true).status).toBe(400);
    expect(dataRequestRefusal({ kind: 'cancelled' }, false).message).toBe('error.unavailable');
  });
});

describe('data-request private history guard', () => {
  const install = () => {
    const listeners = new Map<string, (event?: { persisted?: boolean }) => void>();
    const style = { visibility: '' };
    const replace = vi.fn();
    const reload = vi.fn();
    const replaceState = vi.fn();
    runInNewContext(DATA_REQUEST_HISTORY_SOURCE, {
      addEventListener: (name: string, callback: (event?: { persisted?: boolean }) => void) =>
        listeners.set(name, callback),
      document: { documentElement: { style } },
      history: { replaceState },
      location: { replace, reload },
    });
    return { listeners, style, replace, reload, replaceState };
  };
  it.each([false, true])(
    'veils a departing page and restores by a fixed GET (persisted=%s)',
    (persisted) => {
      const page = install();
      expect(page.replaceState).toHaveBeenCalledExactlyOnceWith(null, '', '/account/data');
      page.listeners.get('pagehide')!();
      expect(page.style.visibility).toBe('hidden');
      page.listeners.get('pageshow')!({ persisted });
      expect(page.replace).toHaveBeenCalledExactlyOnceWith('/account/data');
      expect(page.reload).not.toHaveBeenCalled();
    },
  );
  it('permits only the request nonce and installs the guard before private markup', async () => {
    const f = await fixture('session');
    const response = await f.run('GET', '/account/data');
    const nonce = /<script nonce="([A-Za-z0-9+/]{22}==)">/.exec(response.html)?.[1];
    expect(nonce).toBeDefined();
    expect(response.headers.get('content-security-policy')).toContain(
      "script-src 'nonce-" + nonce + "'",
    );
    expect(response.headers.get('cache-control')).toBe('no-store');
    expect(response.html.indexOf(DATA_REQUEST_HISTORY_SOURCE)).toBeLessThan(
      response.html.indexOf('<body>'),
    );
  });
});

it('executes all independent closed native data-request HTML observation controls', () => {
  const script = new URL('../../../scripts/test-data-request-proof-view.mjs', import.meta.url);
  const output = execFileSync(process.execPath, ['--test', script.pathname], {
    encoding: 'utf8',
    timeout: 10_000,
    maxBuffer: 65536,
    env: {
      PATH: process.env.PATH,
      NODE_ENV: 'production',
      NODE_TLS_REJECT_UNAUTHORIZED: '1',
      NEXT_TELEMETRY_DISABLED: '1',
    },
  });
  for (const [field, value] of [
    ['tests', 4],
    ['pass', 4],
    ['fail', 0],
    ['cancelled', 0],
    ['skipped', 0],
    ['todo', 0],
  ] as const)
    expect(output).toMatch(new RegExp('^# ' + field + ' ' + value + '$', 'm'));
});
