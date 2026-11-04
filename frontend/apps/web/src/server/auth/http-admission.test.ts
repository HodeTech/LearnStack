// @vitest-environment node
import { IncomingMessage, ServerResponse, createServer, request as nodeRequest } from 'node:http';
import { Socket } from 'node:net';

import { NextRequest } from 'next/server';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { bffFixture, bffHost, bffOpaque, bffSource } from '../../test/bff-fixture';
import { admitIncomingRequest, INGRESS_HEADER, mintProvenance, verifyProvenance } from '../ingress';
import { BFF_COOKIES } from './cookies';
import { admitBffHttp } from './http-admission';

const fixtures: ReturnType<typeof bffFixture>[] = [];
const fixture = () => {
  const f = bffFixture();
  fixtures.push(f);
  return f;
};
const secret = 'test-hop-'.padEnd(48, 'x');
afterEach(async () => {
  vi.restoreAllMocks();
  vi.unstubAllEnvs();
  await Promise.all(fixtures.splice(0).map((f) => f.store.stop()));
});
function harness(f: ReturnType<typeof bffFixture>) {
  vi.stubEnv('LEARNSTACK_PUBLIC_HOP_SECRET', secret);
  const publicRuntime = f.publicAdmission;
  const requestRuntime = f.requestRuntime;
  const original = Object.getOwnPropertyDescriptor;
  vi.spyOn(Object, 'getOwnPropertyDescriptor').mockImplementation((object, key) => {
    const holder =
      key === Symbol.for('learnstack.public-admission.v1')
        ? publicRuntime.holder
        : key === Symbol.for('learnstack.bff-request.v1')
          ? requestRuntime.holder
          : key === Symbol.for('learnstack.bff-sessions.v1')
            ? Object.freeze({ version: 1, store: Object.freeze(f.store), http: f.protocol })
            : null;
    return object === globalThis && holder
      ? { value: holder, configurable: false, writable: false, enumerable: false }
      : original(object, key);
  });
  async function run(
    target: string,
    options: {
      method?: string;
      body?: string;
      cookie?: string;
      known?: boolean;
      observed?: string;
      observedMethod?: string;
      extra?: HeadersInit;
    } = {},
  ) {
    const context = { host: bffHost, peer: bffSource, method: options.method ?? 'GET', target };
    const headers = new Headers({
      [INGRESS_HEADER]: mintProvenance(context, secret),
      ...options.extra,
    });
    if (options.cookie) headers.set('cookie', options.cookie);
    if (options.body !== undefined) {
      headers.set('content-type', 'application/x-www-form-urlencoded');
      headers.set('origin', 'https://' + bffHost);
    }
    const next = new NextRequest('https://' + bffHost + (options.observed ?? target), {
      method: options.observedMethod ?? context.method,
      headers,
      body: options.body,
    });
    const request = new IncomingMessage(new Socket());
    request.headers = Object.fromEntries(headers);
    const response = new ServerResponse(request);
    try {
      return await publicRuntime.run(context, request, response, () =>
        requestRuntime.run(context, request, response, async () => {
          if (options.known !== false)
            publicRuntime.holder.begin(context).publish({
              displayName: 'School',
              enabledLocales: ['en'],
              defaultLocale: 'en',
              theme: null,
              showPlatformAttribution: true,
            });
          const result = await admitBffHttp(next, context, new AbortController().signal);
          if (!result.response) {
            const admitted = requestRuntime.holder.read(context);
            expect(admitted.session).toEqual(result.session);
            if (context.target === '/studio' && result.session)
              expect(admitted.page?.outcome).toEqual({ kind: 'ready', view: { kind: 'studio' } });
          }
          return result;
        }),
      );
    } finally {
      response.emit('finish');
      request.socket.destroy();
    }
  }
  return {
    run,
    stop() {
      publicRuntime.shutdown();
      requestRuntime.stop();
    },
  };
}
describe('pinned Next / native BFF admission seam', () => {
  it('malformed public auth input publishes anonymous admission and response-owned cookie recovery', async () => {
    const f = fixture();
    const h = harness(f);
    try {
      const result = await h.run('/en/courses', {
        cookie: '__Host-ls-session=private-malformed-canary; foreign=keep',
        extra: { 'sec-fetch-dest': 'document' },
      });
      expect(result.response).toBeNull();
      expect(result.session).toBeNull();
      expect(result.unavailable).toBe(false);
      expect(result.cookies).toEqual([
        '__Host-ls-session=; Path=/; Max-Age=0; HttpOnly; Secure; SameSite=Lax',
      ]);
      expect(f.account).not.toHaveBeenCalled();
    } finally {
      h.stop();
    }
  });
  it('a real NextRequest POST consumes only verified native authority and returns all distinct host cookies', async () => {
    const f = fixture();
    const h = harness(f);
    const csrf = bffOpaque();
    try {
      const result = await h.run('/api/auth/signin/keycloak', {
        method: 'POST',
        body: 'csrfToken=' + csrf + '&returnTo=%2Fstudio',
        cookie: BFF_COOKIES.csrf + '=' + csrf,
      });
      expect(result.response?.status).toBe(303);
      expect(result.response!.headers.getSetCookie()).toHaveLength(2);
      expect(result.response!.headers.get('cache-control')).toBe('no-store');
      expect(f.provider.begin).toHaveBeenCalledTimes(1);
    } finally {
      h.stop();
    }
  });
  it('no provider/cookie/session work runs before native live-host bootstrap, including auth POST', async () => {
    const f = fixture();
    const h = harness(f);
    try {
      await expect(
        h.run('/api/auth/signin/keycloak', { method: 'POST', body: 'csrfToken=x', known: false }),
      ).rejects.toThrow('Public admission context unavailable');
      expect(f.provider.begin).not.toHaveBeenCalled();
      expect(f.store.counts().owners).toBe(0);
    } finally {
      h.stop();
    }
  });
  it('wrong projected method/target is refused, while the pinned Next inert-query projection retains signed raw authority', async () => {
    const f = fixture();
    const h = harness(f);
    try {
      await expect(h.run('/api/auth/csrf', { observed: '/api/auth/session' })).rejects.toThrow(
        'BFF request context unavailable',
      );
      await expect(h.run('/api/auth/csrf', { observedMethod: 'POST' })).rejects.toThrow(
        'BFF request context unavailable',
      );
      const active = await f.login();
      const result = await h.run('/en/courses?inert=a%20b', {
        cookie: active.cookie,
        observed: '/en/courses?inert=a+b',
        extra: { 'sec-fetch-dest': 'document' },
      });
      expect(result.session?.state).toBe('authenticated');
      expect(f.account).toHaveBeenCalledTimes(2);
      const invalidProtected = await h.run('/studio?inert=a%20b', {
        cookie: active.cookie,
        observed: '/studio?inert=a+b',
        extra: { 'sec-fetch-dest': 'document' },
      });
      expect(invalidProtected.response?.status).toBe(400);
      expect(invalidProtected.session).toBeNull();
      expect(f.account).toHaveBeenCalledTimes(2);
      expect(f.administration).not.toHaveBeenCalled();
      const protectedPage = await h.run('/studio', {
        cookie: active.cookie,
        extra: { 'sec-fetch-dest': 'document' },
      });
      expect(protectedPage.response).toBeNull();
      expect(protectedPage.session?.state).toBe('authenticated');
      expect(f.account).toHaveBeenCalledTimes(3);
      expect(f.administration.mock.calls.map((call) => call[2].kind)).toEqual([
        'getOwnCapabilities',
        'getOwnProfile',
      ]);
    } finally {
      h.stop();
    }
  });
  it('actual native HTTP admission defaults closed and its explicit opt-in grants only twelve exact POST targets', async () => {
    let enabled = false;
    const server = createServer((request, response) => {
      const admitted = admitIncomingRequest(request, secret, enabled);
      const proof = admitted && verifyProvenance(String(request.headers[INGRESS_HEADER]), secret);
      response.writeHead(proof ? 200 : 404, { connection: 'close' });
      response.end(proof ? JSON.stringify(proof) : 'Not found');
    });
    await new Promise<void>((yes) => server.listen(0, '127.0.0.1', yes));
    const address = server.address();
    if (!address || typeof address === 'string') throw new Error('owned address unavailable');
    const send = (path: string, extraHeaders: Record<string, string> = {}, chunked = false) =>
      new Promise<{ status: number; body: string }>((resolve, reject) => {
        const request = nodeRequest(
          {
            hostname: '127.0.0.1',
            port: address.port,
            method: 'POST',
            path,
            agent: false,
            headers: {
              host: bffHost,
              [INGRESS_HEADER]: 'forged',
              'x-forwarded-for': '198.51.100.1',
              ...extraHeaders,
            },
          },
          (response) => {
            let body = '';
            response.setEncoding('utf8');
            response.on('data', (chunk) => {
              body += chunk;
            });
            response.on('end', () => resolve({ status: response.statusCode!, body }));
          },
        );
        request.on('error', reject);
        if (chunked) request.write('x');
        request.end();
      });
    try {
      expect((await send('/api/auth/signout')).status).toBe(404);
      enabled = true;
      for (const path of [
        '/api/auth/signin/keycloak',
        '/api/auth/signout',
        '/api/auth/extend',
        '/api/auth/entry/create',
        '/api/auth/entry/select',
        '/api/auth/switch',
        '/api/studio/membership-profile',
        '/api/studio/membership-roles',
        '/api/studio/membership-status',
        '/api/studio/role-grants',
        '/api/studio/role-manifest-adoption',
        '/api/account/profile',
      ]) {
        const result = await send(path);
        expect(result.status).toBe(200);
        expect(JSON.parse(result.body)).toEqual({
          host: bffHost,
          peer: '127.0.0.1',
          method: 'POST',
          target: path,
        });
      }
      expect((await send('/api/auth/signout', { 'content-length': '16385' })).status).toBe(404);
      expect((await send('/api/auth/signout', {}, true)).status).toBe(404);
      for (const path of [
        '/studio',
        '/api/auth/signout/',
        '/api/auth/extend?x=1',
        '/api/auth/callback/keycloak',
        '/api/auth/session',
        '/api/studio/arbitrary',
        '/api/account/arbitrary',
        '/api/account/profile/',
        '/api/studio/role-grants?x=1',
      ])
        expect((await send(path)).status).toBe(404);
    } finally {
      server.closeAllConnections();
      await new Promise<void>((yes, no) => server.close((error) => (error ? no(error) : yes())));
    }
  });
});
