// @vitest-environment node
import { execFileSync } from 'node:child_process';
import { runInNewContext } from 'node:vm';

import { afterEach, describe, expect, it, vi } from 'vitest';

import { CONSENT_HISTORY_SOURCE, createNativeConsentHandler } from './native-consent-http';
import type { NativeRetainedConsentCaller } from './native-operation-contract';
import en from '../../i18n/messages/en/auth.json';
import tr from '../../i18n/messages/tr/auth.json';
import { bffFixture, bffHost, bffSource } from '../../test/bff-fixture';
import { snapshotRequest, SnapshotWire } from '../../test/page-response-fixture';
const id = '019a154b-0000-7000-8000-000000000011';
const evidence = '019a154b-0000-7000-8000-000000000022';
const pageInfo = {
  nextCursor: null,
  previousCursor: null,
  hasNext: false,
  hasPrevious: false,
} as const;
const closes: (() => Promise<void>)[] = [];
afterEach(async () => {
  for (const close of closes.splice(0).reverse()) await close();
});
async function fixture() {
  const retained = vi.fn<NativeRetainedConsentCaller>(async (_token, _binding, operation) => {
    const response = {
      kind: 'success',
      status: 200,
      etag: null,
      location: null,
      replayed: false,
    } as const;
    if (operation.kind === 'listOwnConsentScopes')
      return {
        ...response,
        data: {
          items: [
            {
              membershipId: id,
              tenantId: '10000000-0000-4000-8000-000000000001',
              organizationId: null,
            },
          ],
          pageInfo,
        },
      };
    if (operation.kind === 'withdrawOwnFieldConsent')
      return { ...response, data: { evidenceId: evidence, sequence: '2', isGranted: false } };
    return {
      ...response,
      data: {
        items: [
          {
            evidenceId: evidence,
            sequence: '1',
            isGranted: true,
            membershipId: id,
            definitionId: id,
            definitionVersion: 1,
            purposeKey: 'purpose',
            noticeLocale: 'en',
            noticeHash: 'a'.repeat(64),
            noticeText: '<retained-notice-canary>',
            labels: { en: 'Retained field' },
          },
        ],
        pageInfo,
      },
    };
  });
  const f = bffFixture({ retainedConsent: retained });
  const entry = await f.enter();
  f.account.mockClear();
  f.native.self.mockClear();
  f.native.contexts.mockClear();
  f.administration.mockClear();
  const handler = createNativeConsentHandler({
    catalogues: { en: en.consents, tr: tr.consents },
    store: f.store,
    responseLease: f.requestRuntime.nativeConsentResponse,
  });
  closes.push(async () => {
    await f.requestRuntime.stop();
    await f.store.stop();
    f.publicAdmission.shutdown();
  });
  const run = async (
    method: string,
    target: string,
    form?: URLSearchParams,
    origin = 'https://' + bffHost,
  ) => {
    const request = snapshotRequest();
    request.method = method;
    request.url = target;
    request.headers.cookie = entry.cookie;
    if (form) {
      request.headers.origin = origin;
      request.headers['content-type'] = 'application/x-www-form-urlencoded';
      // A real HTTP parser marks the entire message complete before the body stream ends.
      // Leaving this false makes IncomingMessage treat normal stream cleanup as an abort.
      request.complete = true;
      request.push(form.toString());
      request.push(null);
    }
    const binding = { host: bffHost, peer: bffSource, method, target };
    const wire = new SnapshotWire();
    // No public-admission begin/publish occurs: lifecycle loss cannot require a public site DTO.
    await f.publicAdmission.run(binding, request, wire.native, () =>
      f.requestRuntime.run(binding, request, wire.native, () =>
        handler(binding, request, wire.native),
      ),
    );
    wire.drainCallbacks();
    for (let turn = 0; turn < 16; ++turn) await Promise.resolve();
    return wire;
  };
  return { ...f, entry, retained, run };
}
describe('independent retained consent native shell', () => {
  it('uses credentialed entry ownership and the shared bounded response pool without CurrentSession or site bootstrap', async () => {
    const f = await fixture();
    const wire = await f.run('GET', '/account/consents?membershipId=' + id);
    expect(wire.statusCode).toBe(200);
    expect(wire.chunks.join('')).toContain('&lt;retained-notice-canary&gt;');
    expect(wire.chunks.join('')).not.toContain('private-access-canary');
    expect(f.retained.mock.calls.map((call) => call[2].kind)).toEqual([
      'listOwnConsentScopes',
      'listOwnConsentHistory',
    ]);
    expect(f.account).not.toHaveBeenCalled();
    expect(f.native.self).not.toHaveBeenCalled();
    expect(f.native.contexts).not.toHaveBeenCalled();
    expect(f.administration).not.toHaveBeenCalled();
    expect(f.requestRuntime.snapshotCounts()).toEqual({ active: 0, owners: 0, bytes: 0 });
  });
  it('refuses wrong Origin and CSRF before any consent operation', async () => {
    const f = await fixture();
    const form = new URLSearchParams({
      csrfToken: f.entry.finished.entry.csrfToken,
      membershipId: id,
    });
    expect(
      (await f.run('POST', '/api/auth/entry/consents', form, 'https://other.example.test'))
        .statusCode,
    ).toBe(400);
    form.set('csrfToken', 'foreign-proof');
    expect((await f.run('POST', '/api/auth/entry/consents', form)).statusCode).toBe(403);
    expect(f.retained).not.toHaveBeenCalled();
  });
  it('keeps HEAD passive and bodyless', async () => {
    const f = await fixture();
    const wire = await f.run('HEAD', '/account/consents');
    expect(wire.chunks).toEqual([]);
    expect(f.retained).not.toHaveBeenCalled();
  });
});

describe('retained consent private history guard', () => {
  const install = () => {
    const listeners = new Map<string, (event?: { persisted?: boolean }) => void>();
    const style = { visibility: '' };
    const replace = vi.fn();
    const reload = vi.fn();
    const replaceState = vi.fn();
    // This evaluates only the exact fixed source, never a request or authored payload.
    runInNewContext(CONSENT_HISTORY_SOURCE, {
      addEventListener: (name: string, callback: (event?: { persisted?: boolean }) => void) =>
        listeners.set(name, callback),
      document: { documentElement: { style } },
      history: { replaceState },
      location: { replace, reload },
    });
    return { listeners, style, replace, reload, replaceState };
  };
  it.each([false, true])(
    'veils a departing page and restores through a fixed GET (persisted=%s)',
    (persisted) => {
      const page = install();
      expect(page.replaceState).toHaveBeenCalledExactlyOnceWith(null, '', '/account/consents');
      page.listeners.get('pageshow')!({ persisted: false });
      expect(page.style.visibility).toBe('');
      expect(page.replace).not.toHaveBeenCalled();
      page.listeners.get('pagehide')!();
      expect(page.style.visibility).toBe('hidden');
      page.listeners.get('pageshow')!({ persisted });
      expect(page.style.visibility).toBe('hidden');
      expect(page.replace).toHaveBeenCalledExactlyOnceWith('/account/consents');
      expect(page.reload).not.toHaveBeenCalled();
    },
  );
  it('veils an independently restored persisted page before its fresh GET', () => {
    const page = install();
    page.listeners.get('pageshow')!({ persisted: true });
    expect(page.style.visibility).toBe('hidden');
    expect(page.replace).toHaveBeenCalledExactlyOnceWith('/account/consents');
  });
  it('keeps history-scrub failure private and navigates only to the fixed GET', () => {
    const style = { visibility: '' };
    const replace = vi.fn();
    const addEventListener = vi.fn();
    runInNewContext(CONSENT_HISTORY_SOURCE, {
      document: { documentElement: { style } },
      history: {
        replaceState: () => {
          throw new Error('Test history failure');
        },
      },
      location: { replace },
      addEventListener,
    });
    expect(style.visibility).toBe('hidden');
    expect(replace).toHaveBeenCalledExactlyOnceWith('/account/consents');
    expect(addEventListener).not.toHaveBeenCalled();
  });
  it('permits only the request nonce and installs the fixed guard before private markup', async () => {
    const f = await fixture();
    const response = await f.run('GET', '/account/consents?membershipId=' + id);
    const html = response.chunks.join('');
    const nonce = /<script nonce="([A-Za-z0-9+/]{22}==)">/.exec(html)?.[1];
    expect(nonce).toBeDefined();
    expect(response.headers.get('content-security-policy')).toContain(
      "script-src 'nonce-" + nonce + "'",
    );
    expect(html).toContain('<script nonce="' + nonce + '">' + CONSENT_HISTORY_SOURCE + '</script>');
    expect(html.indexOf(CONSENT_HISTORY_SOURCE)).toBeLessThan(html.indexOf('<body>'));
    expect(CONSENT_HISTORY_SOURCE).not.toContain('csrfToken');
    expect(CONSENT_HISTORY_SOURCE).not.toContain('notice');
  });
});

it('executes all independent closed native-consent HTML observation controls', () => {
  const script = new URL('../../../scripts/test-consent-proof-view.mjs', import.meta.url);
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
  for (const [field, count] of [
    ['tests', 4],
    ['pass', 4],
    ['fail', 0],
    ['cancelled', 0],
    ['skipped', 0],
    ['todo', 0],
  ] as const)
    expect(output).toMatch(new RegExp('^# ' + field + ' ' + count + '$', 'm'));
});
