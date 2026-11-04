// @vitest-environment node
import { createRequire } from 'node:module';
import { runInNewContext } from 'node:vm';

import { afterEach, describe, expect, it, vi } from 'vitest';

import { bffFixture, bffHost, bffRequest } from './bff-fixture';
import { BFF_HISTORY_SOURCE } from '../components/auth/history';
import { renderBffDocument } from '../server/auth/html';
import type { BffHttpRequest } from '../server/auth/http-input';
import { createNativeAdministrationCaller } from '../server/auth/native-administration-client';

const { JSDOM } = createRequire(import.meta.url)('jsdom') as {
  JSDOM: new (html: string) => { window: { document: Document; close(): void } };
};
const invitationId = '019a154b-0000-7000-8000-000000000044';
const roleId = '019a154b-0000-7000-8000-000000000022';
const version = '9223372036854775807';
const key = 'deliberate-invitation-key';
const email = 'Person+training@example.test';
const createPath = '/api/studio/invitation-create';
const detailPath = '/studio/invitations/' + invitationId;
const location = '/api/v1/identity/invitations/' + invitationId;
const configuration = {
  apiOrigin: 'http://127.0.0.1:5100',
  secret: 'synthetic-invitation-http-hop-'.padEnd(48, 'x'),
  nativeSourceSecret: Buffer.from(Array.from({ length: 32 }, (_, index) => index + 31)).toString(
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
function receipt(created = false, replayed = false) {
  return json({ version }, created ? 201 : 200, {
    ETag: '"' + version + '"',
    ...(created ? { Location: location } : {}),
    ...(replayed ? { 'Idempotency-Replayed': 'true' } : {}),
  });
}
function problem(status: number, code: string) {
  return json(
    {
      type: 'about:blank',
      status,
      title: 'private-upstream-title',
      instance: location,
      code,
      messageKey: 'lockey_' + code,
      correlationId: 'request-invitation-proof',
    },
    status,
  );
}
async function fixture() {
  const f = bffFixture();
  fixtures.push(f);
  const active = await f.login();
  const original = f.administration.getMockImplementation()!;
  const transport = vi.fn<typeof fetch>(async () => receipt(true));
  const caller = createNativeAdministrationCaller(configuration, transport);
  f.administration.mockImplementation((...args) =>
    args[2].kind === 'getOwnProfile' ? original(...args) : caller(...args),
  );
  const csrfToken = active.finished.session.csrfToken;
  const common = { csrfToken, idempotencyKey: key };
  const create = { ...common, email, offeredRole: roleId + '.' + version };
  const post = async (path: string, fields: Record<string, string>) => {
    const response = await f.http.handle(
      bffRequest(path, {
        method: 'POST',
        cookie: active.cookie,
        form: new URLSearchParams(fields).toString(),
      }),
      (page) => renderBffDocument(site, page),
    );
    expect(response).not.toBeNull();
    return response!;
  };
  return { ...f, active, transport, common, create, post };
}
async function document(response: Response) {
  expect(response.headers.get('cache-control')).toBe('no-store');
  expect(response.headers.get('content-type')).toBe('text/html; charset=utf-8');
  const html = await response.text();
  for (const canary of [
    'private-access-canary',
    'private-refresh-canary',
    'private-upstream-title',
    'private-transport-canary',
  ])
    expect(html).not.toContain(canary);
  const dom = new JSDOM(html);
  documents.push(dom);
  expect(dom.window.document.querySelector('img, [onerror], [autofocus]')).toBeNull();
  expect(dom.window.document.querySelectorAll('script')).toHaveLength(1);
  expect(dom.window.document.querySelector('#bff-history')?.textContent).toBe(BFF_HISTORY_SOURCE);
  return dom.window.document;
}
function submittedFields(form: HTMLFormElement) {
  return Object.fromEntries(
    [...form.querySelectorAll<HTMLInputElement>('input')].map((input) => [input.name, input.value]),
  );
}
afterEach(async () => {
  for (const dom of documents.splice(0)) dom.window.close();
  await Promise.all(fixtures.splice(0).flatMap((f) => [f.store.stop(), f.requestRuntime.stop()]));
  vi.restoreAllMocks();
});

describe('native invitation administrator HTTP and actual SDK dispatch', () => {
  it('creates and deliberately replays using the same exact request and validated fixed detail redirect', async () => {
    const f = await fixture();
    for (const replayed of [false, true]) {
      f.transport.mockImplementationOnce(async () => receipt(true, replayed));
      const response = await f.post(createPath, f.create);
      expect(response.status).toBe(303);
      expect(response.headers.get('location')).toBe(detailPath);
      expect(response.headers.getSetCookie()).toEqual([]);
      expect(response.headers.get('cache-control')).toBe('no-store');
    }
    expect(f.transport).toHaveBeenCalledTimes(2);
    for (const [url, init] of f.transport.mock.calls) {
      expect(url).toBe(configuration.apiOrigin + '/api/v1/identity/invitations');
      expect(init?.method).toBe('POST');
      expect(JSON.parse(String(init?.body))).toEqual({ email, roleId, roleVersion: version });
      const headers = new Headers(init?.headers);
      expect(headers.get('Idempotency-Key')).toBe(key);
      expect(headers.has('If-Match')).toBe(false);
      expect(headers.has('If-None-Match')).toBe(false);
      expect(headers.get('X-LearnStack-Native-Tenant-Id')).toBe(f.scope.tenantId);
      expect(headers.get('X-LearnStack-Host')).toBe(bffHost);
      expect(headers.has('X-LearnStack-Native-Organization-Id')).toBe(false);
      expect(headers.has('Cookie')).toBe(false);
    }
    expect(f.store.peek(f.active.binding)).not.toBeNull();
  });

  it.each(['revoke', 'reissue'] as const)(
    'posts %s once with empty JSON and the exact current version',
    async (action) => {
      const f = await fixture();
      f.transport.mockImplementationOnce(async () => receipt());
      const response = await f.post('/api/studio/invitation-' + action, {
        ...f.common,
        version,
        invitationId,
      });
      expect(response.status).toBe(303);
      expect(response.headers.get('location')).toBe(detailPath);
      expect(f.transport).toHaveBeenCalledTimes(1);
      const [url, init] = f.transport.mock.calls[0]!;
      expect(url).toBe(
        configuration.apiOrigin + location + (action === 'revoke' ? '/revocations' : '/reissues'),
      );
      expect(init?.method).toBe('POST');
      expect(init?.body).toBe('{}');
      expect(new Headers(init?.headers).get('If-Match')).toBe('"' + version + '"');
      expect(new Headers(init?.headers).get('Idempotency-Key')).toBe(key);
    },
  );

  it.each([false, true])(
    'preserves absent versus stored-zero policy on the actual HTTP transport (present=%s)',
    async (present) => {
      const f = await fixture();
      f.transport.mockImplementationOnce(async () => receipt(false, true));
      const fields: Record<string, string> = {
        ...f.common,
        presence: present ? 'present' : 'absent',
        days: '21',
      };
      if (present) fields.version = '0';
      const response = await f.post('/api/studio/invitation-policy', fields);
      expect(response.status).toBe(303);
      expect(response.headers.get('location')).toBe('/studio/invitation-policy');
      expect(f.transport).toHaveBeenCalledTimes(1);
      const [url, init] = f.transport.mock.calls[0]!;
      expect(url).toBe(configuration.apiOrigin + '/api/v1/tenancy/settings/invitation-policy');
      expect(init?.method).toBe('PUT');
      expect(init?.body).toBe('{"days":21}');
      const headers = new Headers(init?.headers);
      expect(headers.get('If-Match')).toBe(present ? '"0"' : null);
      expect(headers.get('If-None-Match')).toBe(present ? null : '*');
      expect(headers.get('Idempotency-Key')).toBe(key);
    },
  );

  it.each(['transport', 'wrong-location'] as const)(
    'retains only the exact create request for a deliberate retry after %s uncertainty',
    async (failure) => {
      const f = await fixture();
      f.transport.mockImplementationOnce(async () => {
        if (failure === 'transport') throw new Error('private-transport-canary');
        return json({ version }, 201, {
          ETag: '"' + version + '"',
          Location: 'https://foreign.example/invitations/' + invitationId,
        });
      });
      const response = await f.post(createPath, f.create);
      expect(response.status).toBe(503);
      expect(response.headers.get('location')).toBeNull();
      expect(response.headers.getSetCookie()).toEqual([]);
      const rendered = await document(response);
      const retry = rendered.querySelector<HTMLFormElement>('form[action="' + createPath + '"]')!;
      expect(retry?.method).toBe('post');
      expect(submittedFields(retry)).toEqual(f.create);
      expect(rendered.querySelector('a[href="/studio/invitations"]')).not.toBeNull();
      expect(f.transport).toHaveBeenCalledTimes(1);
      const originalRequest = f.transport.mock.calls[0]![1]!;
      f.transport.mockImplementationOnce(async () => receipt(true, true));
      expect((await f.post(createPath, submittedFields(retry))).status).toBe(303);
      expect(f.transport).toHaveBeenCalledTimes(2);
      expect(f.transport.mock.calls[1]![1]!.body).toBe(originalRequest.body);
      expect(new Headers(f.transport.mock.calls[1]![1]!.headers).get('Idempotency-Key')).toBe(key);
    },
  );

  it.each(['concurrency_conflict', 'idempotency_key_reuse'] as const)(
    'shows escaped edits and fresh GET without a resubmit on %s',
    async (code) => {
      const f = await fixture();
      f.transport.mockImplementationOnce(async () => problem(409, code));
      const response = await f.post(createPath, f.create);
      expect(response.status).toBe(409);
      const rendered = await document(response);
      expect(rendered.querySelector('form')).toBeNull();
      expect(rendered.querySelector<HTMLTextAreaElement>('#mutation-edit-email')?.value).toBe(
        email,
      );
      expect(
        rendered.querySelector<HTMLTextAreaElement>('#mutation-edit-offeredRole')?.value,
      ).not.toContain(roleId);
      expect(rendered.querySelector('a[href="/studio/invitations"]')).not.toBeNull();
      expect(f.transport).toHaveBeenCalledTimes(1);
      expect(f.store.peek(f.active.binding)).not.toBeNull();
    },
  );

  it.each([403, 404])(
    'masks refused creation details while preserving the broader live session (%s)',
    async (status) => {
      const f = await fixture();
      f.transport.mockImplementationOnce(async () =>
        problem(status, status === 403 ? 'forbidden' : 'not_found'),
      );
      const response = await f.post(createPath, f.create);
      expect(response.status).toBe(status);
      const rendered = await document(response);
      expect(rendered.querySelector('form, textarea, input[name="email"]')).toBeNull();
      expect(rendered.body.textContent).not.toContain(email);
      expect(response.headers.getSetCookie()).toEqual([]);
      expect(f.store.peek(f.active.binding)).not.toBeNull();
      expect(f.transport).toHaveBeenCalledTimes(1);
    },
  );

  it.each(['', 'person..name@example.test', '<unsafe>@example.test'])(
    'retains invalid email edits without API mutation or same-key retry (%s)',
    async (invalidEmail) => {
      const f = await fixture();
      const response = await f.post(createPath, { ...f.create, email: invalidEmail });
      expect(response.status).toBe(400);
      const rendered = await document(response);
      expect(rendered.querySelector('[role="alert"]')).not.toBeNull();
      expect(rendered.querySelector('form')).toBeNull();
      expect(rendered.querySelector<HTMLTextAreaElement>('#mutation-edit-email')?.value).toBe(
        invalidEmail,
      );
      expect(rendered.querySelector('unsafe')).toBeNull();
      expect(rendered.querySelector('a[href="/studio/invitations"]')).not.toBeNull();
      expect(f.transport).not.toHaveBeenCalled();
    },
  );

  it('recovers invalid policy days without profile controls or rewriting presence', async () => {
    const f = await fixture();
    const response = await f.post('/api/studio/invitation-policy', {
      ...f.common,
      presence: 'absent',
      days: '31',
    });
    expect(response.status).toBe(400);
    const rendered = await document(response);
    expect(rendered.querySelector('[role="alert"]')).not.toBeNull();
    expect(rendered.querySelector<HTMLTextAreaElement>('#mutation-edit-days')?.value).toBe('31');
    expect(rendered.querySelector<HTMLTextAreaElement>('#mutation-edit-presence')?.value).toContain(
      'default lifetime is 14 days',
    );
    expect(
      rendered.querySelector('form, input[name="displayName"], select[name="preferredLocale"]'),
    ).toBeNull();
    expect(rendered.querySelector('a[href="/studio/invitation-policy"]')).not.toBeNull();
    expect(f.transport).not.toHaveBeenCalled();
  });

  it.each([
    'absent-origin',
    'null-origin',
    'foreign-origin',
    'stale-csrf',
    'duplicate-role',
    'foreign-selector',
    'extra-query',
    'oversize',
  ] as const)('refuses %s before administrator transport', async (kind) => {
    const f = await fixture();
    const fields = new URLSearchParams(f.create);
    if (kind === 'stale-csrf') fields.set('csrfToken', 'B'.repeat(43));
    if (kind === 'duplicate-role') fields.append('offeredRole', roleId + '.0');
    if (kind === 'foreign-selector') fields.set('organizationId', invitationId);
    if (kind === 'oversize') fields.set('email', 'x'.repeat(16 * 1024));
    const request: BffHttpRequest = bffRequest(
      createPath + (kind === 'extra-query' ? '?tenantId=' + invitationId : ''),
      {
        method: 'POST',
        cookie: f.active.cookie,
        form: fields.toString(),
      },
    );
    if (kind === 'absent-origin') request.headers.delete('origin');
    if (kind === 'null-origin') request.headers.set('origin', 'null');
    if (kind === 'foreign-origin') request.headers.set('origin', 'https://foreign.example');
    if (kind === 'extra-query') {
      // Native response admission rejects this non-census POST before protocol execution.
      await expect(
        f.http.handle(request, (page) => renderBffDocument(site, page)),
      ).rejects.toThrow();
    } else {
      const response = await f.http.handle(request, (page) => renderBffDocument(site, page));
      expect([400, 403]).toContain(response?.status);
    }
    expect(f.transport).not.toHaveBeenCalled();
    expect(f.store.peek(f.active.binding)).not.toBeNull();
  });

  it.each([
    [createPath, '/studio/invitations'],
    ['/api/studio/invitation-reissue', detailPath],
    ['/api/studio/invitation-policy', '/studio/invitation-policy'],
  ] as const)(
    'restores %s recovery by fresh fixed GET without replaying the POST',
    async (action, expected) => {
      const f = await fixture();
      f.transport.mockImplementationOnce(async () => problem(409, 'concurrency_conflict'));
      const fields =
        action === createPath
          ? f.create
          : action.endsWith('policy')
            ? { ...f.common, presence: 'absent', days: '14' }
            : { ...f.common, invitationId, version };
      const rendered = await document(await f.post(action, fields));
      expect(rendered.documentElement.getAttribute('data-bff-history-recovery')).toBe(expected);
      const handlers = new Map<string, (event: { persisted?: boolean }) => void>();
      const replace = vi.fn();
      const reload = vi.fn();
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
        location: { replace, reload },
      });
      handlers.get('pagehide')!({});
      handlers.get('pageshow')!({ persisted: true });
      expect(style.visibility).toBe('hidden');
      expect(replace).toHaveBeenCalledWith(expected);
      expect(reload).not.toHaveBeenCalled();
      expect(f.transport).toHaveBeenCalledTimes(1);
    },
  );
});
