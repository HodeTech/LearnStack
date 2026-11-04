// @vitest-environment node
import { NextRequest } from 'next/server';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { createAccountSiteCaller } from './account-client';
import { BFF_COOKIES } from './cookies';
import { middleware } from '../../middleware';
import { bffFixture, bffHost, bffRequest, bffSource } from '../../test/bff-fixture';
import { snapshotRequest, SnapshotWire } from '../../test/page-response-fixture';
import { INGRESS_HEADER, mintProvenance } from '../ingress';

const { getSite } = vi.hoisted(() => ({ getSite: vi.fn() }));
vi.mock('../configured-public-client', () => ({
  createConfiguredPublicClient: () => ({ getSite }),
  publicTraceparent: () => '00-' + 'a'.repeat(32) + '-' + 'b'.repeat(16) + '-00',
}));

const site = {
  displayName: 'Institution',
  enabledLocales: ['en'],
  defaultLocale: 'en',
  theme: null,
  showPlatformAttribution: true,
};
const account = {
  actorId: '019a154b-0000-7000-8000-000000000011',
  tenantId: '10000000-0000-4000-8000-000000000001',
  organizationId: null,
  site,
};
const secret = 'policy-test-hop-'.padEnd(48, 'x');
const fixtures: ReturnType<typeof bffFixture>[] = [];
const releases: (() => void)[] = [];
function gate() {
  let release!: () => void;
  const promise = new Promise<void>((resolve) => {
    release = resolve;
  });
  releases.push(release);
  return { promise, release };
}
function policyFixture() {
  let status = 200;
  let held: ReturnType<typeof gate> | undefined;
  let now = 0;
  const transport = vi.fn(async () => {
    const observed = status;
    await held?.promise;
    return new Response(
      JSON.stringify(
        observed === 200
          ? account
          : {
              type: 'https://errors.learnstack.dev/unauthorized',
              title: 'private-authority-canary',
              status: observed,
              code: 'unauthorized',
              messageKey: 'lockey_unauthorized',
              correlationId: 'private-correlation-canary',
              instance: '/api/v1/account/site',
            },
      ),
      {
        status: observed,
        headers: {
          'content-type': observed === 200 ? 'application/json' : 'application/problem+json',
        },
      },
    );
  });
  const caller = createAccountSiteCaller(
    {
      apiOrigin: 'http://127.0.0.1:5100',
      secret,
      nativeSourceSecret: Buffer.from(Array.from({ length: 32 }, (_, index) => index + 1)).toString(
        'base64url',
      ),
    },
    transport,
  );
  const f = bffFixture({ account: caller });
  fixtures.push(f);
  for (const method of ['redeem', 'refresh'] as const) {
    const original = f.provider[method].getMockImplementation()!;
    f.provider[method].mockImplementation(async () => ({
      ...(await original()),
      refreshExpiresAt: now + 24 * 60 * 60_000,
    }));
  }
  return {
    ...f,
    transport,
    advance(ms: number) {
      now += ms;
      f.advance(ms);
    },
    response(value: number, wait?: ReturnType<typeof gate>) {
      status = value;
      held = wait;
    },
  };
}
function middlewareHarness(f: ReturnType<typeof policyFixture>) {
  vi.stubEnv('LEARNSTACK_PUBLIC_HOP_SECRET', secret);
  getSite.mockResolvedValue({ kind: 'success', status: 200, data: site });
  const publicRuntime = f.publicAdmission;
  const requestRuntime = f.requestRuntime;
  const original = Object.getOwnPropertyDescriptor;
  const holder = Object.freeze({ version: 1, store: Object.freeze(f.store), http: f.protocol });
  vi.spyOn(Object, 'getOwnPropertyDescriptor').mockImplementation((object, key) => {
    const selected =
      key === Symbol.for('learnstack.public-admission.v1')
        ? publicRuntime.holder
        : key === Symbol.for('learnstack.bff-request.v1')
          ? requestRuntime.holder
          : key === Symbol.for('learnstack.bff-sessions.v1')
            ? holder
            : null;
    return object === globalThis && selected
      ? { value: selected, writable: false, enumerable: false, configurable: false }
      : original(object, key);
  });
  return {
    async run(target: string, cookie: string) {
      const context = { host: bffHost, peer: bffSource, method: 'GET', target };
      const headers = new Headers({
        [INGRESS_HEADER]: mintProvenance(context, secret),
        cookie,
        'sec-fetch-dest': 'document',
      });
      const request = snapshotRequest();
      request.headers = Object.fromEntries(headers);
      const response = new SnapshotWire();
      try {
        return await publicRuntime.run(context, request, response.native, () =>
          requestRuntime.run(context, request, response.native, async () => {
            const result = await middleware(
              new NextRequest('https://' + bffHost + target, { headers }),
            );
            const session = requestRuntime.holder.read(context).session;
            response.native.end(await result.text());
            return { response: result, session };
          }),
        );
      } finally {
        if (!response.writableEnded && !response.destroyed) response.destroy();
        response.drainCallbacks();
        request.socket.destroy();
        await vi.waitFor(() => {
          expect(requestRuntime.counts()).toBe(0);
          expect(requestRuntime.snapshotCounts()).toEqual({ active: 0, owners: 0, bytes: 0 });
          expect(f.store.counts()).toMatchObject({ operations: 0, pageSnapshots: 0 });
        });
      }
    },
    async stop() {
      publicRuntime.shutdown();
      await requestRuntime.stop();
    },
  };
}
afterEach(async () => {
  for (const release of releases.splice(0)) release();
  await Promise.all(fixtures.splice(0).map((f) => f.store.stop()));
  vi.restoreAllMocks();
  vi.unstubAllEnvs();
  getSite.mockReset();
});

describe('AccountSite authority status through caller, owner and HTTP response', () => {
  it.each(['/en/courses', '/studio'])(
    'held uncertain 401 on %s withholds checked content without touching idle or consuming another grant',
    async (target) => {
      const f = policyFixture();
      const current = await f.login();
      const before = f.store.peek(current.binding)!;
      f.advance(120_000);
      const held = gate();
      f.response(401, held);
      const h = middlewareHarness(f);
      try {
        const pending = h.run(target, current.cookie);
        void pending.catch(() => {});
        await vi.waitFor(() => expect(f.transport).toHaveBeenCalledTimes(2));
        expect(f.store.counts()).toMatchObject({
          owners: 1,
          credentialRecords: 1,
          operations: target === '/studio' ? 2 : 1,
          pageSnapshots: target === '/studio' ? 1 : 0,
        });
        expect(f.requestRuntime.snapshotCounts()).toMatchObject({
          active: target === '/studio' ? 1 : 0,
          owners: target === '/studio' ? 1 : 0,
        });
        f.advance(30_000);
        held.release();
        const refused = await pending;
        expect(refused.session).toBeNull();
        expect(refused.response.status).toBe(target === '/studio' ? 503 : 200);
        expect(refused.response.headers.get('x-middleware-next')).toBe(
          target === '/studio' ? null : '1',
        );
        expect(refused.response.headers.getSetCookie()).toEqual([]);
        expect(refused.response.headers.get('cache-control')).toBe('no-store');
        expect(f.store.peek(current.binding)).toEqual(before);
        expect(f.store.counts()).toMatchObject({ owners: 1, credentialRecords: 1, operations: 0 });
        expect(f.provider.redeem).toHaveBeenCalledTimes(1);
        expect(f.provider.refresh).not.toHaveBeenCalled();
        expect(f.transport).toHaveBeenCalledTimes(2);

        f.response(200);
        f.advance(60_000);
        const recovered = await h.run(target, current.cookie);
        expect(recovered.response.status).toBe(200);
        expect(recovered.session).toMatchObject({
          state: 'authenticated',
          expiresAt: before.expiresAt + 210_000,
        });
        expect(recovered.response.headers.getSetCookie()).toEqual([]);
        expect(f.store.peek(current.binding)).toEqual(recovered.session);
        expect(f.store.counts()).toMatchObject({ owners: 1, credentialRecords: 1 });
        expect(f.provider.begin).toHaveBeenCalledTimes(1);
        expect(f.provider.redeem).toHaveBeenCalledTimes(1);
        expect(f.provider.refresh).not.toHaveBeenCalled();
      } finally {
        held.release();
        await h.stop();
      }
    },
  );

  it.each([false, true])(
    'initial explicit selection 401 refuses publication and preserves any prior replacement: %s',
    async (replacing) => {
      const f = policyFixture();
      const prior = replacing ? await f.login() : null;
      const before = prior && f.store.peek(prior.binding);
      const started = await f.store.start({
        host: bffHost,
        source: bffSource,
        returnPath: '/studio',
        ...(prior ? { browser: prior.binding.browser, handle: prior.binding.handle } : {}),
      });
      const completed = await f.store.complete({
        host: bffHost,
        source: bffSource,
        browser: started.browser,
        state: started.state,
        proof: started.proof,
        responseUrl:
          'https://' +
          bffHost +
          '/api/auth/callback/keycloak?state=' +
          started.state +
          '&code=owned',
      });
      const held = gate();
      f.response(401, held);
      const pending = f.http.handle(
        bffRequest('/api/auth/entry/select', {
          method: 'POST',
          cookie:
            BFF_COOKIES.browser +
            '=' +
            completed.browser +
            '; ' +
            BFF_COOKIES.entry +
            '=' +
            completed.handle,
          form: new URLSearchParams({
            csrfToken: completed.entry.csrfToken,
            tenantId: account.tenantId,
          }).toString(),
        }),
      );
      await vi.waitFor(() => expect(f.transport).toHaveBeenCalledTimes(prior ? 2 : 1));
      expect(f.store.counts()).toMatchObject({
        owners: prior ? 2 : 1,
        pending: 1,
        operations: 1,
        credentialRecords: prior ? 2 : 1,
      });
      held.release();
      const response = (await pending)!;
      expect(response.status).toBe(503);
      expect(response.headers.get('retry-after')).toBe('30');
      expect(await response.json()).toEqual({ code: 'provider_refused' });
      expect(response.headers.getSetCookie()).toEqual([]);
      expect(f.store.counts()).toMatchObject({
        owners: prior ? 2 : 1,
        pending: 1,
        operations: 0,
        credentialRecords: prior ? 2 : 1,
        transactionRecords: 0,
        proofRecords: 0,
        loginStates: 0,
      });
      if (prior) expect(f.store.peek(prior.binding)).toEqual(before);
      expect(f.provider.redeem).toHaveBeenCalledTimes(prior ? 2 : 1);
      expect(f.provider.refresh).not.toHaveBeenCalled();
    },
  );

  it('post-refresh AccountSite 401 returns availability status but revokes the unvalidated revision without replay', async () => {
    const f = policyFixture();
    const current = await f.login();
    const held = gate();
    f.response(401, held);
    const pending = f.http.handle(
      bffRequest('/api/auth/extend', {
        method: 'POST',
        cookie: current.cookie,
        form: new URLSearchParams({ csrfToken: current.finished.session.csrfToken }).toString(),
      }),
    );
    await vi.waitFor(() => expect(f.transport).toHaveBeenCalledTimes(2));
    expect(f.provider.refresh).toHaveBeenCalledTimes(1);
    expect(f.store.counts()).toMatchObject({ owners: 1, operations: 1, credentialRecords: 1 });
    held.release();
    const response = (await pending)!;
    expect(response.status).toBe(503);
    expect(response.headers.get('retry-after')).toBe('30');
    expect(await response.json()).toEqual({ code: 'provider_refused' });
    expect(response.headers.getSetCookie()).toEqual([]);
    expect(f.store.peek(current.binding)).toBeNull();
    expect(Object.values(f.store.counts()).every((value) => value === 0)).toBe(true);
    const refused = await f.http.handle(
      bffRequest('/api/auth/extend', {
        method: 'POST',
        cookie: current.cookie,
        form: new URLSearchParams({ csrfToken: current.finished.session.csrfToken }).toString(),
      }),
    );
    expect(refused!.status).toBe(403);
    expect(await refused!.json()).toEqual({ code: 'csrf' });
    expect(f.provider.refresh).toHaveBeenCalledTimes(1);
    expect(f.provider.redeem).toHaveBeenCalledTimes(1);
    expect(f.transport).toHaveBeenCalledTimes(2);
  });
});
