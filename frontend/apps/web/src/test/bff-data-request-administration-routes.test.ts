// @vitest-environment node
import { createRequire } from 'node:module';
import { runInNewContext } from 'node:vm';

import { afterEach, describe, expect, it, vi } from 'vitest';

import { bffFixture, bffHost, bffRequest } from './bff-fixture';
import { BFF_HISTORY_SOURCE } from '../components/auth/history';
import { renderBffDocument } from '../server/auth/html';
import type { BffHttpRequest } from '../server/auth/http-input';
import type { BffPage } from '../server/auth/http-protocol';
import { createNativeAdministrationCaller } from '../server/auth/native-administration-client';

const { JSDOM } = createRequire(import.meta.url)('jsdom') as {
  JSDOM: new (html: string) => { window: { document: Document; close(): void } };
};
const requestId = '019a154b-0000-7000-8000-000000000041';
const membershipId = '019a154b-0000-7000-8000-000000000012';
const version = '9223372036854775807';
const key = 'deliberate-data-request-key';
const exportPath = '/api/studio/data-request-export';
const erasurePath = '/api/studio/data-request-erasure';
const cancelPath = '/api/studio/data-request-cancel';
const revokePath = '/api/studio/data-request-revoke';
const memberPage = '/studio/members/' + membershipId;
const requestPage = '/studio/data-requests/' + requestId;
const location = '/api/v1/identity/data-requests/' + requestId;
const configuration = {
  apiOrigin: 'http://127.0.0.1:5100',
  secret: 'synthetic-data-request-hop-'.padEnd(48, 'x'),
  nativeSourceSecret: Buffer.from(Array.from({ length: 32 }, (_, index) => index + 41)).toString(
    'base64url',
  ),
};
const site = {
  displayName: 'Institution',
  defaultLocale: 'en',
  enabledLocales: ['en', 'tr'],
  theme: null,
  showPlatformAttribution: true,
};
const fixtures: ReturnType<typeof bffFixture>[] = [];
const documents: InstanceType<typeof JSDOM>[] = [];
function json(body: unknown, status = 200, headers: Record<string, string> = {}) {
  return new Response(JSON.stringify(body), {
    status,
    headers: {
      'content-type': status < 400 ? 'application/json' : 'application/problem+json',
      'cache-control': 'no-store',
      ...headers,
    },
  });
}
const accepted = (kind: 'export' | 'erasure', where = location) =>
  json(
    { id: requestId, kind, status: kind === 'export' ? 'requested' : 'scheduled', version: '0' },
    202,
    {
      ETag: '"0"',
      Location: where,
    },
  );
const receipt = () => json({ version: '4' }, 200, { ETag: '"4"' });
function problem(status: number, code: string, errors?: Record<string, { key: string }[]>) {
  return json(
    {
      type: 'about:blank',
      status,
      title: 'private-upstream-title',
      instance: location,
      code,
      messageKey: 'lockey_' + code,
      correlationId: 'request-data-request-proof',
      ...(errors ? { errors } : {}),
    },
    status,
  );
}
async function fixture() {
  const f = bffFixture();
  fixtures.push(f);
  const active = await f.login();
  const original = f.administration.getMockImplementation()!;
  const transport = vi.fn<typeof fetch>(async () => accepted('export'));
  const caller = createNativeAdministrationCaller(configuration, transport);
  f.administration.mockImplementation((...args) =>
    args[2].kind === 'getOwnProfile' ? original(...args) : caller(...args),
  );
  const csrfToken = active.finished.session.csrfToken;
  const create = { csrfToken, idempotencyKey: key, membershipId };
  const write = { csrfToken, idempotencyKey: key, version, requestId };
  // The protocol's own recovery decision, independently of how the renderer then shows it.
  const pages: BffPage[] = [];
  const post = async (path: string, fields: Record<string, string>) => {
    const response = await f.http.handle(
      bffRequest(path, {
        method: 'POST',
        cookie: active.cookie,
        form: new URLSearchParams(fields).toString(),
      }),
      (page) => {
        pages.push(page);
        return renderBffDocument(site, page);
      },
    );
    expect(response).not.toBeNull();
    return response!;
  };
  return { ...f, active, transport, create, write, post, pages };
}
async function document(response: Response) {
  expect(response.headers.get('cache-control')).toBe('no-store');
  expect(response.headers.get('content-type')).toBe('text/html; charset=utf-8');
  const html = await response.text();
  for (const canary of ['private-upstream-title', 'private-transport-canary', 'lockey_'])
    expect(html).not.toContain(canary);
  const dom = new JSDOM(html);
  documents.push(dom);
  expect(dom.window.document.querySelectorAll('script')).toHaveLength(1);
  expect(dom.window.document.querySelector('#bff-history')?.textContent).toBe(BFF_HISTORY_SOURCE);
  return dom.window.document;
}
const sent = (f: Awaited<ReturnType<typeof fixture>>, call = 0) => {
  const [url, init] = f.transport.mock.calls[call]!;
  const headers = new Headers(init?.headers);
  return { url: String(url), method: init?.method, body: String(init?.body), headers };
};
afterEach(async () => {
  for (const dom of documents.splice(0)) dom.window.close();
  await Promise.all(fixtures.splice(0).flatMap((f) => [f.store.stop(), f.requestRuntime.stop()]));
  vi.restoreAllMocks();
});

describe('native administrator data-request HTTP and actual SDK dispatch (ADR-0106 §§ 3, 10, 11)', () => {
  it.each([
    [exportPath, 'export', '/exports', {}],
    [erasurePath, 'erasure', '/erasures', { confirm: 'erase' }],
  ] as const)(
    'files %s for one Membership with its key and lands on the request page',
    async (path, kind, suffix, extra) => {
      const f = await fixture();
      f.transport.mockImplementationOnce(async () => accepted(kind));
      const response = await f.post(path, { ...f.create, ...extra });
      expect(response.status).toBe(303);
      expect(response.headers.get('location')).toBe(requestPage + '?result=' + kind);
      expect(response.headers.getSetCookie()).toEqual([]);
      const call = sent(f);
      expect(call.url).toBe(configuration.apiOrigin + '/api/v1/identity/data-requests' + suffix);
      expect(call.method).toBe('POST');
      expect(JSON.parse(call.body)).toEqual({ membershipId });
      expect(call.headers.get('Idempotency-Key')).toBe(key);
      expect(call.headers.has('If-Match')).toBe(false);
      expect(call.headers.get('X-LearnStack-Native-Tenant-Id')).toBe(f.scope.tenantId);
      expect(call.headers.get('X-LearnStack-Host')).toBe(bffHost);
      expect(call.headers.has('Cookie')).toBe(false);
    },
  );

  it.each([
    [cancelPath, '/cancellations', 'cancelled'],
    [revokePath, '/artifact/revocations', 'revoked'],
  ] as const)(
    'posts %s once at the exact version with empty JSON',
    async (path, suffix, result) => {
      const f = await fixture();
      f.transport.mockImplementationOnce(async () => receipt());
      const response = await f.post(path, f.write);
      expect(response.status).toBe(303);
      expect(response.headers.get('location')).toBe(requestPage + '?result=' + result);
      const call = sent(f);
      expect(call.url).toBe(configuration.apiOrigin + location + suffix);
      expect(call.body).toBe('{}');
      expect(call.headers.get('If-Match')).toBe('"' + version + '"');
      expect(call.headers.get('Idempotency-Key')).toBe(key);
    },
  );

  it('treats a creation answered with another Location as unconfirmed, keeping the exact request for a deliberate retry', async () => {
    const f = await fixture();
    f.transport.mockImplementationOnce(async () =>
      accepted('export', '/api/v1/identity/me/data-requests/' + requestId),
    );
    const response = await f.post(exportPath, f.create);
    expect(response.status).toBe(503);
    expect(response.headers.get('location')).toBeNull();
    const rendered = await document(response);
    expect(rendered.querySelector('[role="alert"]')?.textContent).toContain(
      'The result could not be confirmed.',
    );
    const retry = rendered.querySelector<HTMLFormElement>('form[action="' + exportPath + '"]')!;
    expect(
      Object.fromEntries(
        [...retry.querySelectorAll<HTMLInputElement>('input')].map((input) => [
          input.name,
          input.value,
        ]),
      ),
    ).toEqual(f.create);
    expect(rendered.querySelector('a[href="' + memberPage + '"]')).not.toBeNull();
    f.transport.mockImplementationOnce(async () => accepted('export'));
    expect((await f.post(exportPath, f.create)).status).toBe(303);
    expect(sent(f, 1).body).toBe(sent(f, 0).body);
    expect(sent(f, 1).headers.get('Idempotency-Key')).toBe(key);
  });

  it.each([
    [
      exportPath,
      '$',
      'lockey_subject_export_available',
      'A copy of the data is already available to download.',
    ],
    [
      exportPath,
      '$',
      'lockey_request_already_active',
      'A request of this kind is already in progress for this member',
    ],
    [
      erasurePath,
      '$',
      'lockey_subject_membership_current',
      'This membership is still active or suspended.',
    ],
    [
      erasurePath,
      '$',
      'lockey_blocked_last_administrator',
      "This member is the institution's only administrator.",
    ],
    [
      exportPath,
      '$',
      'lockey_subject_is_requester',
      'Requests about your own data are made on your own data page.',
    ],
    [exportPath, '$', 'lockey_subject_not_admitted', "This person's account is being closed"],
    [cancelPath, '$', 'lockey_data_request_window_ended', 'so it can no longer be cancelled'],
    [
      cancelPath,
      '$',
      'lockey_data_request_not_cancellable',
      'This request can no longer be cancelled',
    ],
    [revokePath, 'artifact', 'lockey_data_export_unavailable', 'so there is nothing to delete'],
    [exportPath, '$', 'lockey_reason_from_the_future', 'This request was refused.'],
  ] as const)(
    'explains %s refused with %s %s in plain language and offers no retry',
    async (path, field, reason, sentence) => {
      const f = await fixture();
      f.transport.mockImplementationOnce(async () =>
        problem(409, 'business_rule_violation', { [field]: [{ key: reason }] }),
      );
      const response = await f.post(
        path,
        path === erasurePath
          ? { ...f.create, confirm: 'erase' }
          : path === exportPath
            ? f.create
            : f.write,
      );
      expect(response.status).toBe(409);
      expect(f.pages).toEqual([
        expect.objectContaining({
          kind: 'mutation_error',
          reason: 'denied',
          retryable: false,
          dataRequest: { refusal: expect.any(String) },
        }),
      ]);
      const rendered = await document(response);
      expect(rendered.querySelector('[role="alert"]')?.textContent).toContain(sentence);
      expect(rendered.querySelector('form')).toBeNull();
      const back = path === cancelPath || path === revokePath ? requestPage : memberPage;
      expect(rendered.querySelector('a[href="' + back + '"]')).not.toBeNull();
      expect(rendered.documentElement.getAttribute('data-bff-history-recovery')).toBe(back);
      expect(f.transport).toHaveBeenCalledTimes(1);
      expect(f.store.peek(f.active.binding)).not.toBeNull();
    },
  );

  it.each([
    ['concurrency_conflict', 'This item changed.'],
    ['request_in_progress', 'This request is still in progress.'],
    ['idempotency_key_reuse', 'This form was already used for a different change.'],
  ] as const)('keeps the template sentence for the generic %s outcome', async (code, sentence) => {
    const f = await fixture();
    f.transport.mockImplementationOnce(async () => problem(409, code));
    const rendered = await document(await f.post(cancelPath, f.write));
    expect(rendered.querySelector('[role="alert"]')?.textContent).toContain(sentence);
    expect(rendered.querySelector('form') !== null).toBe(code === 'request_in_progress');
  });

  it('asks again for an erasure confirmation the form did not carry, before any API work', async () => {
    const f = await fixture();
    for (const fields of [f.create, { ...f.create, confirm: 'yes' }]) {
      const response = await f.post(erasurePath, fields);
      expect(response.status).toBe(400);
      const rendered = await document(response);
      expect(rendered.querySelector('[role="alert"]')?.textContent).toBe(
        'Tick every confirmation box to go ahead.',
      );
      expect(rendered.querySelector('form')).toBeNull();
      expect(rendered.querySelector('a[href="' + memberPage + '"]')).not.toBeNull();
    }
    expect(f.transport).not.toHaveBeenCalled();
  });

  it.each([
    'absent-origin',
    'foreign-origin',
    'stale-csrf',
    'duplicate-membership',
    'foreign-selector',
    'noncanonical-membership',
  ] as const)('refuses %s before administrator transport', async (kind) => {
    const f = await fixture();
    const fields = new URLSearchParams(f.create);
    if (kind === 'stale-csrf') fields.set('csrfToken', 'B'.repeat(43));
    if (kind === 'duplicate-membership') fields.append('membershipId', requestId);
    if (kind === 'foreign-selector') fields.set('organizationId', requestId);
    if (kind === 'noncanonical-membership') fields.set('membershipId', membershipId.toUpperCase());
    const request: BffHttpRequest = bffRequest(exportPath, {
      method: 'POST',
      cookie: f.active.cookie,
      form: fields.toString(),
    });
    if (kind === 'absent-origin') request.headers.delete('origin');
    if (kind === 'foreign-origin') request.headers.set('origin', 'https://foreign.example');
    const response = await f.http.handle(request, (page) => renderBffDocument(site, page));
    expect([400, 403]).toContain(response?.status);
    expect(f.transport).not.toHaveBeenCalled();
    expect(f.store.peek(f.active.binding)).not.toBeNull();
  });

  it('restores a refusal by its fixed request page GET without replaying the POST', async () => {
    const f = await fixture();
    f.transport.mockImplementationOnce(async () => problem(409, 'concurrency_conflict'));
    const rendered = await document(await f.post(revokePath, f.write));
    const handlers = new Map<string, (event: { persisted?: boolean }) => void>();
    const replace = vi.fn();
    const style = { visibility: '' };
    runInNewContext(BFF_HISTORY_SOURCE, {
      document: {
        documentElement: {
          style,
          getAttribute: (name: string) => rendered.documentElement.getAttribute(name),
        },
      },
      addEventListener: (name: string, handler: (event: { persisted?: boolean }) => void) =>
        handlers.set(name, handler),
      location: { replace, reload: vi.fn() },
    });
    handlers.get('pagehide')!({});
    handlers.get('pageshow')!({ persisted: true });
    expect(replace).toHaveBeenCalledWith(requestPage);
    expect(f.transport).toHaveBeenCalledTimes(1);
  });
});
