// @vitest-environment node
import { createRequire } from 'node:module';

import { afterEach, describe, expect, it, vi } from 'vitest';

import { bffFixture, bffHost, bffOpaque, bffRequest, bffSource } from './bff-fixture';
import { BFF_COOKIES } from '../server/auth/cookies';
import { renderBffDocument } from '../server/auth/html';
import { bffAdministrationForm, bffCursor, bffPageQuery } from '../server/auth/http-forms';
import { BffSessionError } from '../server/auth/session-contract';
import type { PublicSite } from '../server/public-entry';

const { JSDOM } = createRequire(import.meta.url)('jsdom') as {
  JSDOM: new (html: string) => { window: { document: Document; close(): void } };
};
const recoverySite: PublicSite = {
  displayName: 'Institution',
  enabledLocales: ['en', 'tr'],
  defaultLocale: 'en',
  theme: null,
  showPlatformAttribution: true,
};

function deferred() {
  let release!: () => void;
  const promise = new Promise<void>((resolve) => {
    release = resolve;
  });
  return { promise, release };
}

async function expectUnknownRecovery(response: Response, fields: Record<string, string>) {
  expect(response.status).toBe(503);
  expect(response.headers.get('cache-control')).toBe('no-store');
  expect(response.headers.get('content-type')).toBe('text/html; charset=utf-8');
  expect(response.headers.get('location')).toBeNull();
  expect(response.headers.getSetCookie()).toEqual([]);
  const dom = new JSDOM(await response.text());
  try {
    const document = dom.window.document;
    const form = document.querySelector('form')!;
    expect(form?.getAttribute('action')).toBe('/api/account/profile');
    expect(form?.getAttribute('method')).toBe('post');
    expect([...form.querySelectorAll('input')].map((field) => [field.name, field.value])).toEqual(
      Object.entries(fields),
    );
    expect(document.body.textContent).toContain('The result could not be confirmed.');
    expect(document.querySelector('a[href="/portal/profile"]')).not.toBeNull();
    expect(document.querySelector('script, img, [onerror], [autofocus]')).toBeNull();
    expect(document.body.textContent).not.toContain('private-');
  } finally {
    dom.window.close();
  }
}

const fixtures: ReturnType<typeof bffFixture>[] = [];
function fixture() {
  const f = bffFixture();
  fixtures.push(f);
  return f;
}
afterEach(async () => {
  vi.restoreAllMocks();
  await Promise.all(fixtures.splice(0).map((f) => f.store.stop()));
});
const member = '019a154b-0000-7000-8000-000000000011';
const role = '019a154b-0000-7000-8000-000000000022';
const write = { version: '9223372036854775807', idempotencyKey: 'deliberate-key-123' };
const cases = [
  [
    '/api/studio/membership-profile',
    { membershipId: member, displayName: 'Name', preferredLocale: '' },
    {
      kind: 'setMembershipProfile',
      id: member,
      profile: { displayName: 'Name', preferredLocale: null },
    },
    '/studio/members/' + member,
  ],
  [
    '/api/studio/membership-roles',
    { membershipId: member, roleIds: JSON.stringify([role]) },
    { kind: 'replaceMembershipRoles', id: member, roleIds: [role] },
    '/studio/members/' + member,
  ],
  [
    '/api/studio/membership-status',
    { membershipId: member, status: 'Suspended' },
    { kind: 'changeMembershipStatus', id: member, status: 'Suspended' },
    '/studio/members/' + member,
  ],
  [
    '/api/studio/role-grants',
    { roleId: role, grants: '[{"scope":"Tenant","key":"identity.role.admin"}]' },
    {
      kind: 'changeRoleGrants',
      id: role,
      grants: [{ scope: 'Tenant', key: 'identity.role.admin' }],
    },
    '/studio/roles/' + role,
  ],
  [
    '/api/studio/role-manifest-adoption',
    { roleId: role, manifestVersion: '2', manifestFingerprint: 'a'.repeat(64) },
    {
      kind: 'adoptRoleManifest',
      id: role,
      manifestVersion: 2,
      manifestFingerprint: 'a'.repeat(64),
    },
    '/studio/roles/' + role,
  ],
  [
    '/api/account/profile',
    { displayName: '', preferredLocale: 'tr' },
    { kind: 'setOwnProfile', profile: { displayName: null, preferredLocale: 'tr' } },
    '/portal/profile',
  ],
] as const;

describe('exact native administration forms', () => {
  it.each(cases)(
    '%s invokes one closed operation with lossless version and fixed 303',
    async (path, fields, operation, destination) => {
      const f = fixture();
      const active = await f.login();
      const response = await f.http.handle(
        bffRequest(path, {
          method: 'POST',
          cookie: active.cookie,
          form: new URLSearchParams({
            csrfToken: active.finished.session.csrfToken,
            ...write,
            ...fields,
          }).toString(),
        }),
      );
      expect(response!.status).toBe(303);
      expect(response!.headers.get('location')).toBe(destination);
      expect(response!.headers.get('cache-control')).toBe('no-store');
      expect(response!.headers.getSetCookie()).toEqual([]);
      expect(f.administration).toHaveBeenCalledExactlyOnceWith(
        'private-access-canary',
        { host: bffHost, source: bffSource, traceparent: undefined, ...f.scope },
        { ...operation, ...write },
        expect.any(AbortSignal),
      );
      expect(await response!.text()).not.toContain('private-');
    },
  );
  it('a terminal status returns to the fixed member directory', () => {
    expect(
      bffAdministrationForm(
        '/api/studio/membership-status',
        new URLSearchParams({ ...write, membershipId: member, status: 'Revoked' }),
      ).destination,
    ).toBe('/studio/members');
  });
  it.each(cases)(
    '%s refuses missing, duplicate and unknown fields without API work',
    async (path, fields) => {
      const f = fixture();
      const active = await f.login();
      const body = new URLSearchParams({
        csrfToken: active.finished.session.csrfToken,
        ...write,
        ...fields,
      });
      const missing = new URLSearchParams(body);
      missing.delete(Object.keys(fields)[0]!);
      for (const form of [
        missing.toString(),
        body + '&version=1',
        body + '&tenantId=' + f.scope.tenantId,
      ]) {
        const response = await f.http.handle(
          bffRequest(path, { method: 'POST', cookie: active.cookie, form }),
        );
        expect(response!.status).toBe(400);
        expect(response!.headers.has('location')).toBe(false);
      }
      expect(f.administration).not.toHaveBeenCalled();
    },
  );
  it.each(cases)('%s requires the current session CSRF before API work', async (path, fields) => {
    const f = fixture();
    const active = await f.login();
    const response = await f.http.handle(
      bffRequest(path, {
        method: 'POST',
        cookie: active.cookie,
        form: new URLSearchParams({ csrfToken: bffOpaque(), ...write, ...fields }).toString(),
      }),
    );
    expect(response!.status).toBe(403);
    expect(f.administration).not.toHaveBeenCalled();
    expect(f.store.peek(active.binding)).not.toBeNull();
  });
  it.each(['01', '-1', '9223372036854775808', '1e3', '1.0', '', ' 1'])(
    'rejects noncanonical Int64 version %j before API work',
    (version) => {
      expect(() =>
        bffAdministrationForm(
          '/api/account/profile',
          new URLSearchParams({ ...write, version, displayName: '', preferredLocale: '' }),
        ),
      ).toThrow();
    },
  );
  it.each([
    '[{"scope":"Tenant","scope":"Tenant","key":"identity.role.admin"}]',
    '[{"scope":"Tenant","sco\\u0070e":"Tenant","key":"identity.role.admin"}]',
    '[{"scope":"Tenant","key":"identity.role.admin","other":"x"}]',
    '[{"scope":0,"key":"identity.role.admin"}]',
    '[{"scope":"Organization","key":"identity.role.admin"}]',
    '[{"scope":"Tenant","key":"foreign.permission"}]',
    '[{"scope":"Tenant"}]',
    '[{"scope":"Tenant","key":"identity.role.admin"},]',
    '[{"scope":"Tenant","key":"identity.role.admin"}] trailing',
    '[{"scope":"Tenant","key":"identity.role.admin"},{"scope":"Tenant","key":"identity.role.admin"}]',
    JSON.stringify(
      Array.from({ length: 9 }, () => ({ scope: 'Tenant', key: 'identity.role.admin' })),
    ),
  ])('refuses malformed or ambiguous finite grant array %s', (grants) => {
    expect(() =>
      bffAdministrationForm(
        '/api/studio/role-grants',
        new URLSearchParams({ ...write, roleId: role, grants }),
      ),
    ).toThrow();
  });
  it.each([
    '[1]',
    '[null]',
    '{}',
    '[[]]',
    '["' + role.toUpperCase() + '"]',
    JSON.stringify([role, role]),
    JSON.stringify(Array.from({ length: 9 }, () => role)),
  ])('refuses noncanonical or repeated role IDs %s', (roleIds) => {
    expect(() =>
      bffAdministrationForm(
        '/api/studio/membership-roles',
        new URLSearchParams({ ...write, membershipId: member, roleIds }),
      ),
    ).toThrow();
  });
  it('accepts empty full replacements and escaped JSON field spellings only once', () => {
    expect(
      bffAdministrationForm(
        '/api/studio/membership-roles',
        new URLSearchParams({ ...write, membershipId: member, roleIds: '[]' }),
      ).operation,
    ).toMatchObject({ roleIds: [] });
    expect(
      bffAdministrationForm(
        '/api/studio/role-grants',
        new URLSearchParams({
          ...write,
          roleId: role,
          grants: '[{"sco\\u0070e":"Tenant","key":"identity.role.admin"}]',
        }),
      ).operation,
    ).toMatchObject({ grants: [{ scope: 'Tenant', key: 'identity.role.admin' }] });
  });
  it.each([401, 403, 404, 429, 503])(
    'ordinary administration refusal %i preserves the live session and withholds upstream diagnostics',
    async (status) => {
      const f = fixture();
      const active = await f.login();
      f.administration.mockResolvedValueOnce({
        kind: 'api-error',
        status,
        error: { code: 'forbidden' },
        problem: { detail: 'private-upstream-canary' },
      } as never);
      const response = await f.http.handle(
        bffRequest('/api/account/profile', {
          method: 'POST',
          cookie: active.cookie,
          form: new URLSearchParams({
            csrfToken: active.finished.session.csrfToken,
            ...write,
            displayName: '',
            preferredLocale: '',
          }).toString(),
        }),
      );
      expect(response!.status).toBe(status);
      expect(await response!.text()).toBe('{"code":"forbidden"}');
      expect(response!.headers.getSetCookie()).toEqual([]);
      expect(f.store.peek(active.binding)).not.toBeNull();
      expect(f.administration).toHaveBeenCalledTimes(1);
    },
  );
  it.each(['transport-error', 'invalid-response'] as const)(
    'uncertain %s preserves exact edits/key for one deliberate retry without replaying automatically',
    async (kind) => {
      const f = fixture();
      const active = await f.login();
      f.administration.mockResolvedValueOnce(
        kind === 'transport-error' ? { kind } : { kind, reason: 'invalid-success' },
      );
      const fields = {
        csrfToken: active.finished.session.csrfToken,
        ...write,
        displayName: 'Edited name',
        preferredLocale: '',
      };
      const render = vi.fn(() => '<form>deliberate retry</form>');
      const response = await f.http.handle(
        bffRequest('/api/account/profile', {
          method: 'POST',
          cookie: active.cookie,
          form: new URLSearchParams(fields).toString(),
        }),
        render,
      );
      expect(response!.status).toBe(503);
      expect(response!.headers.get('location')).toBeNull();
      expect(render).toHaveBeenCalledWith(
        expect.objectContaining({
          kind: 'mutation_error',
          fields: Object.entries(fields),
          retryable: true,
          reason: 'unavailable',
        }),
      );
      expect(f.administration).toHaveBeenCalledTimes(1);
      expect(f.store.peek(active.binding)).not.toBeNull();
    },
  );
  it('a throwing transport retains the deliberate retry fields without publishing its exception', async () => {
    const f = fixture();
    const active = await f.login();
    f.administration.mockRejectedValueOnce(new Error('private-transport-canary'));
    const render = vi.fn(() => 'retry');
    const response = await f.http.handle(
      bffRequest('/api/account/profile', {
        method: 'POST',
        cookie: active.cookie,
        form: new URLSearchParams({
          csrfToken: active.finished.session.csrfToken,
          ...write,
          displayName: '',
          preferredLocale: '',
        }).toString(),
      }),
      render,
    );
    expect(response!.status).toBe(503);
    expect(await response!.text()).toBe('retry');
    expect(render).toHaveBeenCalledWith(expect.objectContaining({ retryable: true }));
    expect(f.administration).toHaveBeenCalledTimes(1);
  });

  it.each(['deadline', 'disconnect'] as const)(
    'distinguishes a connected POST deadline from an actual %s while retaining held API cleanup',
    async (interruption) => {
      const f = fixture();
      const active = await f.login();
      const browser = new AbortController();
      const deadline = new AbortController();
      const timeout = vi.spyOn(AbortSignal, 'timeout').mockImplementationOnce((milliseconds) => {
        expect(milliseconds).toBe(10_000);
        return deadline.signal;
      });
      const fields = {
        csrfToken: active.finished.session.csrfToken,
        ...write,
        displayName: '"><img src=x onerror=alert(1)>',
        preferredLocale: '',
      };
      const formStarted = deferred();
      const finishForm = deferred();
      const apiStarted = deferred();
      const apiAborted = deferred();
      const finishApi = deferred();
      let apiSignal: AbortSignal | undefined;
      const success = f.administration.getMockImplementation()!;
      f.administration.mockImplementationOnce(async (...args) => {
        apiSignal = args[3];
        apiSignal.addEventListener('abort', apiAborted.release, { once: true });
        apiStarted.release();
        await apiAborted.promise;
        // Actual transport/body cleanup still owns the operation after logical abort.
        await finishApi.promise;
        return success(...args);
      });
      const render = vi.fn((page: Parameters<typeof renderBffDocument>[1]) =>
        renderBffDocument(recoverySite, page),
      );
      const request = bffRequest('/api/account/profile', {
        method: 'POST',
        cookie: active.cookie,
        form: new URLSearchParams(fields).toString(),
        signal: browser.signal,
      });
      const pending = f.http.handle(
        {
          ...request,
          body: new ReadableStream({
            async pull(controller) {
              formStarted.release();
              await finishForm.promise;
              controller.enqueue(new TextEncoder().encode(new URLSearchParams(fields).toString()));
              controller.close();
            },
          }),
        },
        render,
      );
      let settled = false;
      void pending.then(() => {
        settled = true;
      });
      try {
        await formStarted.promise;
        expect(timeout).toHaveBeenNthCalledWith(1, 10_000);
        expect(f.administration).not.toHaveBeenCalled();
        expect(f.store.counts().operations).toBe(0);
        finishForm.release();
        await apiStarted.promise;
        expect(f.store.counts()).toMatchObject({ owners: 1, operations: 1 });
        (interruption === 'deadline' ? deadline : browser).abort();
        await apiAborted.promise;
        expect(browser.signal.aborted).toBe(interruption === 'disconnect');
        expect(apiSignal?.aborted).toBe(true);
        expect(settled).toBe(false);
        expect(render).not.toHaveBeenCalled();
        expect(f.store.counts()).toMatchObject({ owners: 1, operations: 1 });
        expect(f.store.peek(active.binding)).not.toBeNull();
        finishApi.release();
        const response = (await pending)!;
        expect(f.store.counts()).toMatchObject({ owners: 1, operations: 0 });
        expect(f.administration).toHaveBeenCalledTimes(1);
        expect(f.provider.refresh).not.toHaveBeenCalled();
        expect(f.store.peek(active.binding)).not.toBeNull();
        if (interruption === 'deadline') {
          await expectUnknownRecovery(response, fields);
          expect(render).toHaveBeenCalledTimes(1);
        } else {
          expect(render).not.toHaveBeenCalled();
          expect(response.status).not.toBe(303);
          expect(response.headers.get('location')).toBeNull();
          expect(response.headers.getSetCookie()).toEqual([]);
          expect(await response.text()).not.toContain(fields.idempotencyKey);
        }
      } finally {
        finishForm.release();
        browser.abort();
        finishApi.release();
        await pending;
      }
    },
  );

  it('a connected deadline after real mutation settlement still withholds success and preserves recovery', async () => {
    const f = fixture();
    const active = await f.login();
    const browser = new AbortController();
    const deadline = new AbortController();
    vi.spyOn(AbortSignal, 'timeout').mockImplementationOnce((milliseconds) => {
      expect(milliseconds).toBe(10_000);
      return deadline.signal;
    });
    const operate = f.store.operate.bind(f.store);
    vi.spyOn(f.store, 'operate').mockImplementationOnce(async (...args) => {
      const outcome = await operate(...args);
      expect(f.store.counts().operations).toBe(0);
      deadline.abort();
      return outcome;
    });
    const fields = {
      csrfToken: active.finished.session.csrfToken,
      ...write,
      displayName: 'Settled but unconfirmed <edit>',
      preferredLocale: 'tr',
    };
    const response = await f.http.handle(
      bffRequest('/api/account/profile', {
        method: 'POST',
        cookie: active.cookie,
        form: new URLSearchParams(fields).toString(),
        signal: browser.signal,
      }),
      (page) => renderBffDocument(recoverySite, page),
    );
    expect(browser.signal.aborted).toBe(false);
    expect(f.administration).toHaveBeenCalledTimes(1);
    expect(f.store.peek(active.binding)).not.toBeNull();
    await expectUnknownRecovery(response!, fields);
  });

  it('a concurrent credential refresh withholds a held mutation success but keeps exact live-owner recovery', async () => {
    const f = fixture();
    const active = await f.login();
    const apiStarted = deferred();
    const finishApi = deferred();
    const success = f.administration.getMockImplementation()!;
    f.administration.mockImplementationOnce(async (...args) => {
      apiStarted.release();
      await finishApi.promise;
      return success(...args);
    });
    const browser = new AbortController();
    const fields = {
      csrfToken: active.finished.session.csrfToken,
      ...write,
      displayName: 'Preserve across refresh <edit>',
      preferredLocale: '',
    };
    const pending = f.http.handle(
      bffRequest('/api/account/profile', {
        method: 'POST',
        cookie: active.cookie,
        form: new URLSearchParams(fields).toString(),
        signal: browser.signal,
      }),
      (page) => renderBffDocument(recoverySite, page),
    );
    try {
      await apiStarted.promise;
      expect(f.store.counts().operations).toBe(1);
      const refreshed = await f.store.admit(active.binding, bffSource, 'extend');
      expect(f.provider.refresh).toHaveBeenCalledTimes(1);
      expect(refreshed.csrfToken).toBe(active.finished.session.csrfToken);
      expect(f.store.peek(active.binding)).toEqual(refreshed);
      expect(f.store.counts().operations).toBe(1);
      finishApi.release();
      const response = (await pending)!;
      expect(browser.signal.aborted).toBe(false);
      expect(f.administration).toHaveBeenCalledTimes(1);
      expect(f.store.counts().operations).toBe(0);
      expect(f.store.peek(active.binding)).toEqual(refreshed);
      await expectUnknownRecovery(response, fields);
    } finally {
      finishApi.release();
      await pending;
    }
  });

  it.each([
    ['en', 'concurrency_conflict'],
    ['en', 'idempotency_key_reuse'],
    ['tr', 'concurrency_conflict'],
    ['tr', 'idempotency_key_reuse'],
  ] as const)(
    '%s %s preserves visible submitted edits through the real protocol renderer',
    async (locale, code) => {
      const f = fixture();
      const active = await f.login();
      f.administration.mockResolvedValueOnce({
        kind: 'api-error',
        status: 409,
        error: { code },
        problem: {
          type: 'https://errors.example.test/' + code,
          title: 'Conflict',
          status: 409,
          instance: '/api/v1/identity/me/profile',
          code,
          messageKey: 'error.' + code,
          correlationId: '019a154b-0000-7000-8000-000000000055',
        },
      });
      const fields = {
        csrfToken: active.finished.session.csrfToken,
        ...write,
        displayName: '"><img src=x onerror=alert(1)>',
        preferredLocale: 'tr',
      };
      const response = (await f.http.handle(
        bffRequest('/api/account/profile', {
          method: 'POST',
          cookie: active.cookie,
          form: new URLSearchParams(fields).toString(),
        }),
        (page) => renderBffDocument({ ...recoverySite, defaultLocale: locale }, page),
      ))!;
      expect(response.status).toBe(409);
      expect(response.headers.get('cache-control')).toBe('no-store');
      expect(response.headers.get('location')).toBeNull();
      expect(response.headers.getSetCookie()).toEqual([]);
      const dom = new JSDOM(await response.text());
      try {
        const document = dom.window.document;
        expect(document.documentElement.lang).toBe(locale);
        expect([...document.querySelectorAll('textarea')].map((field) => field.value)).toEqual([
          fields.displayName,
          fields.preferredLocale,
        ]);
        expect(document.body.textContent).toContain(
          locale === 'en' ? 'Submitted edits' : 'Gönderilen değişiklikler',
        );
        expect(document.body.textContent).not.toContain(fields.csrfToken);
        expect(document.body.textContent).not.toContain(fields.idempotencyKey);
        expect(document.body.textContent).not.toContain(fields.version);
        expect(
          document.querySelector('form, input, script, img, [onerror], [autofocus]'),
        ).toBeNull();
        expect(document.querySelector('a[href="/portal/profile"]')).not.toBeNull();
      } finally {
        dom.window.close();
      }
      expect(f.administration).toHaveBeenCalledTimes(1);
      expect(f.store.peek(active.binding)).not.toBeNull();
    },
  );
});

describe('entry and scope HTTP publication', () => {
  it('entry creation is deliberate, never a consequence of callback or discovery', async () => {
    const f = fixture();
    const entered = await f.enter();
    expect(f.native.self).not.toHaveBeenCalled();
    expect(f.native.bootstrap).not.toHaveBeenCalled();
    expect(f.account).not.toHaveBeenCalled();
    expect(f.store.peek(entered.binding)).toBeNull();
    f.native.self.mockResolvedValueOnce({ state: 'missing' });
    const read = await f.http.handle(bffRequest('/api/auth/entry', { cookie: entered.cookie }));
    expect(await read!.json()).toMatchObject({ identityState: 'missing', contexts: null });
    expect(read!.headers.getSetCookie()).toEqual([]);
    expect(f.native.bootstrap).not.toHaveBeenCalled();
    f.native.self.mockResolvedValueOnce({ state: 'missing' });
    const created = await f.http.handle(
      bffRequest('/api/auth/entry/create', {
        method: 'POST',
        cookie: entered.cookie,
        form: 'csrfToken=' + entered.finished.entry.csrfToken,
      }),
    );
    expect(created!.status).toBe(303);
    expect(created!.headers.get('location')).toBe('/api/auth/entry');
    expect(created!.headers.getSetCookie()).toEqual([]);
    expect(f.native.bootstrap).toHaveBeenCalledTimes(1);
    expect(f.account).not.toHaveBeenCalled();
  });
  it('selection requires current entry proof and fresh exact-page membership before audited admission', async () => {
    const f = fixture();
    const entered = await f.enter();
    const form = (csrf: string, tenant = f.scope.tenantId) =>
      new URLSearchParams({
        csrfToken: csrf,
        tenantId: tenant,
        cursor: 'opaque_cursor',
      }).toString();
    const invalid = await f.http.handle(
      bffRequest('/api/auth/entry/select', {
        method: 'POST',
        cookie: entered.cookie,
        form: form(bffOpaque()),
      }),
    );
    expect(invalid!.status).toBe(403);
    expect(f.native.contexts).not.toHaveBeenCalled();
    expect(f.native.admit).not.toHaveBeenCalled();
    const wrong = await f.http.handle(
      bffRequest('/api/auth/entry/select', {
        method: 'POST',
        cookie: entered.cookie,
        form: form(entered.finished.entry.csrfToken, '20000000-0000-4000-8000-000000000001'),
      }),
    );
    expect(wrong!.status).toBe(400);
    expect(f.native.admit).not.toHaveBeenCalled();
    const success = await f.http.handle(
      bffRequest('/api/auth/entry/select', {
        method: 'POST',
        cookie: entered.cookie,
        form: form(entered.finished.entry.csrfToken),
      }),
    );
    expect(success!.status).toBe(303);
    expect(f.native.admit).toHaveBeenCalledTimes(1);
    expect(f.account).toHaveBeenCalledTimes(1);
    expect(f.native.contexts).toHaveBeenLastCalledWith(
      'private-access-canary',
      expect.objectContaining({ host: bffHost }),
      { cursor: 'opaque_cursor', limit: 20 },
      expect.any(AbortSignal),
    );
    expect(
      success!.headers.getSetCookie().some((value) => value.startsWith('__Host-ls-session=')),
    ).toBe(true);
  });
  it('cross-host selection sends only canonical suggestions and no credentials or source cookie edits', async () => {
    const f = fixture();
    const entered = await f.enter({ returnPath: '/portal/profile' });
    f.native.contexts.mockResolvedValueOnce({
      items: [
        {
          ...f.scope,
          tenantName: 'Other institution',
          organizationName: null,
          entryOrigin: 'https://other.example.test:3000',
          canUseCurrentHost: false,
        },
      ],
      endCursor: null,
      hasNextPage: false,
    });
    const response = await f.http.handle(
      bffRequest('/api/auth/entry/select', {
        method: 'POST',
        cookie: entered.cookie,
        form: new URLSearchParams({
          csrfToken: entered.finished.entry.csrfToken,
          tenantId: f.scope.tenantId,
        }).toString(),
      }),
    );
    expect(response!.headers.get('location')).toBe(
      'https://other.example.test:3000/api/auth/signin?returnTo=%2Fportal%2Fprofile&contextHint=tenant',
    );
    expect(response!.headers.getSetCookie()).toEqual([]);
    expect(f.native.admit).not.toHaveBeenCalled();
    expect(f.account).not.toHaveBeenCalled();
    expect(f.store.counts().owners).toBe(1);
  });
  it('entry provider signout preserves the old session and unrelated cookies through its independent return', async () => {
    const f = fixture();
    const active = await f.login();
    const entered = await f.enter(active.binding);
    const pending = await f.store.start({
      ...active.binding,
      source: bffSource,
      returnPath: '/studio',
    });
    const loginCookie = BFF_COOKIES.login + pending.state + '=' + pending.proof;
    const response = await f.http.handle(
      bffRequest('/api/auth/signout', {
        method: 'POST',
        cookie:
          active.cookie +
          '; ' +
          BFF_COOKIES.entry +
          '=' +
          entered.finished.handle +
          '; ' +
          loginCookie,
        form: 'csrfToken=' + entered.finished.entry.csrfToken,
      }),
    );
    expect(response!.status).toBe(303);
    expect(response!.headers.get('location')).toBe('https://identity.example.test/logout');
    expect(response!.headers.get('referrer-policy')).toBe('no-referrer');
    expect(response!.headers.get('cache-control')).toBe('no-store');
    const edits = response!.headers.getSetCookie();
    expect(edits).toHaveLength(2);
    expect(edits[0]).toBe('__Host-ls-entry=; Path=/; Max-Age=0; HttpOnly; Secure; SameSite=Lax');
    expect(edits[1]).toMatch(
      /^__Host-ls-logout-[A-Za-z0-9_-]{43}=[A-Za-z0-9_-]{43}; Path=\/; Max-Age=300; HttpOnly; Secure; SameSite=Lax$/,
    );
    expect(edits.join(';')).not.toContain('__Host-ls-session=');
    expect(edits.join(';')).not.toContain('__Host-ls-browser=');
    expect(edits.join(';')).not.toContain(BFF_COOKIES.login);
    expect(f.store.peek(active.binding)).toEqual(active.finished.session);
    const proof = edits[1]!.split(';', 1)[0]!;
    const state = proof.slice(BFF_COOKIES.logout.length).split('=')[0]!;
    expect(f.provider.logoutUrl).toHaveBeenCalledExactlyOnceWith(
      'private-id-canary',
      'https://' + bffHost + '/',
      state,
    );
    const returned = await f.http.handle(
      bffRequest('/?state=' + state, { cookie: active.cookie + '; ' + proof + '; ' + loginCookie }),
    );
    expect(returned!.status).toBe(303);
    expect(returned!.headers.getSetCookie()).toEqual([
      BFF_COOKIES.logout + state + '=; Path=/; Max-Age=0; HttpOnly; Secure; SameSite=Lax',
    ]);
    expect(f.store.peek(active.binding)).toEqual(active.finished.session);
    expect(
      (
        await f.store.complete({
          host: bffHost,
          source: bffSource,
          browser: pending.browser,
          state: pending.state,
          proof: pending.proof,
          responseUrl:
            'https://' +
            bffHost +
            '/api/auth/callback/keycloak?state=' +
            pending.state +
            '&code=owned',
        })
      ).kind,
    ).toBe('entry');
  });
  it('entry logout URL failure clears only entry authority and retains an independent bounded return', async () => {
    const f = fixture();
    const active = await f.login();
    const entered = await f.enter(active.binding);
    f.provider.logoutUrl.mockImplementationOnce(() => {
      throw new Error('private-provider-logout-canary');
    });
    const response = await f.http.handle(
      bffRequest('/api/auth/signout', {
        method: 'POST',
        cookie: active.cookie + '; ' + BFF_COOKIES.entry + '=' + entered.finished.handle,
        form: 'csrfToken=' + entered.finished.entry.csrfToken,
      }),
    );
    expect(response!.status).toBe(303);
    expect(response!.headers.get('location')).toBe('/api/auth/signin');
    const edits = response!.headers.getSetCookie();
    expect(edits).toHaveLength(2);
    expect(edits[0]).toMatch(/^__Host-ls-entry=; /);
    expect(edits[1]).toMatch(/^__Host-ls-logout-[A-Za-z0-9_-]{43}=[A-Za-z0-9_-]{43}; /);
    expect(await response!.text()).not.toContain('private-');
    expect(f.store.peek(active.binding)).toEqual(active.finished.session);
    expect(f.store.counts().logoutStates).toBe(1);
  });
  it('entry logout during actual selection withholds late publication and retains the independent logout proof', async () => {
    const f = fixture();
    const active = await f.login();
    const entered = await f.enter(active.binding);
    let release!: () => void;
    const held = new Promise<void>((resolve) => {
      release = resolve;
    });
    const account = f.account.getMockImplementation()!;
    f.account.mockImplementationOnce(async () => {
      await held;
      return account();
    });
    const cookie = active.cookie + '; ' + BFF_COOKIES.entry + '=' + entered.finished.handle;
    const selecting = f.http.handle(
      bffRequest('/api/auth/entry/select', {
        method: 'POST',
        cookie,
        form: new URLSearchParams({
          csrfToken: entered.finished.entry.csrfToken,
          tenantId: f.scope.tenantId,
        }).toString(),
      }),
    );
    try {
      await vi.waitFor(() => expect(f.account).toHaveBeenCalledTimes(2));
      const logout = await f.http.handle(
        bffRequest('/api/auth/signout', {
          method: 'POST',
          cookie,
          form: 'csrfToken=' + entered.finished.entry.csrfToken,
        }),
      );
      expect(logout!.status).toBe(303);
      expect(logout!.headers.get('location')).toBe('https://identity.example.test/logout');
      expect(f.store.peek(active.binding)).toEqual(active.finished.session);
      expect(f.store.counts()).toMatchObject({ owners: 2, operations: 1, logoutStates: 1 });
      release();
      const withheld = (await selecting)!;
      expect(withheld.status).toBe(400);
      expect(await withheld.json()).toEqual({ code: 'cancelled' });
      expect(withheld.headers.getSetCookie()).toEqual([]);
      expect(withheld.headers.has('location')).toBe(false);
      expect(f.store.counts()).toMatchObject({ owners: 2, operations: 0, logoutStates: 1 });
      const proof = logout!.headers
        .getSetCookie()
        .find((value) => value.startsWith(BFF_COOKIES.logout))!
        .split(';', 1)[0]!;
      const state = proof.slice(BFF_COOKIES.logout.length).split('=')[0]!;
      const returned = await f.http.handle(
        bffRequest('/?state=' + state, { cookie: active.cookie + '; ' + proof }),
      );
      expect(returned!.status).toBe(303);
      expect(f.store.peek(active.binding)).toEqual(active.finished.session);
      expect(f.store.counts()).toMatchObject({ owners: 1, operations: 0, logoutStates: 0 });
    } finally {
      release();
      await selecting;
    }
  });
  it('same-host switching publishes a fresh handle/CSRF and a fixed shell without another provider grant', async () => {
    const f = fixture();
    const active = await f.login();
    const response = await f.http.handle(
      bffRequest('/api/auth/switch', {
        method: 'POST',
        cookie: active.cookie,
        form: new URLSearchParams({
          csrfToken: active.finished.session.csrfToken,
          tenantId: f.scope.tenantId,
        }).toString(),
      }),
    );
    expect(response!.status).toBe(303);
    expect(response!.headers.get('location')).toBe('/studio');
    expect(response!.headers.getSetCookie().join(';')).not.toContain(active.finished.handle);
    expect(f.store.peek(active.binding)).toBeNull();
    expect(f.provider.redeem).toHaveBeenCalledTimes(1);
    expect(f.native.admit).toHaveBeenCalledTimes(2);
  });
  it('different-account replacement offers explicit current-session signout, retaining both owners', async () => {
    const f = fixture();
    const active = await f.login();
    const entered = await f.enter(active.binding);
    vi.spyOn(f.store, 'select').mockRejectedValueOnce(new BffSessionError('account_change'));
    const render = vi.fn(() => 'explicit signout');
    const response = await f.http.handle(
      bffRequest('/api/auth/entry/select', {
        method: 'POST',
        cookie: active.cookie + '; ' + BFF_COOKIES.entry + '=' + entered.finished.handle,
        form: new URLSearchParams({
          csrfToken: entered.finished.entry.csrfToken,
          tenantId: f.scope.tenantId,
        }).toString(),
      }),
      render,
    );
    expect(response!.status).toBe(409);
    expect(render).toHaveBeenCalledWith({
      kind: 'account_change',
      csrfToken: active.finished.session.csrfToken,
    });
    expect(response!.headers.getSetCookie()).toEqual([]);
    expect(f.store.peek(active.binding)).not.toBeNull();
  });
  it.each(['', ' ', 'x=y', 'x'.repeat(1025)])(
    'rejects blank/noncanonical/bounded cursor %j',
    (cursor) => expect(() => bffCursor(cursor)).toThrow(),
  );
  it('bounds positive page limits and clamps valid large requests', () => {
    expect(bffPageQuery(new URLSearchParams('limit=999'))).toEqual({
      cursor: undefined,
      limit: 100,
    });
    for (const limit of ['', '0', '-1', '01', '2147483648', '1e2'])
      expect(() => bffPageQuery(new URLSearchParams({ limit }))).toThrow();
  });
});
