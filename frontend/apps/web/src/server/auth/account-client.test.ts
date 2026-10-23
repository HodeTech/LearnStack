// @vitest-environment node
import { afterEach, describe, expect, it, vi } from 'vitest';

import { MAX_PUBLIC_RESPONSE_BYTES } from '../public-api-limits';
import { createAccountSiteCaller } from './account-client';

const configuration = {
  apiOrigin: 'http://127.0.0.1:5100',
  secret: 'private-hop-'.padEnd(48, 'x'),
};
const binding = {
  host: 'institution.example.test:3000',
  source: '203.0.113.19',
  traceparent: '00-' + 'a'.repeat(32) + '-' + 'b'.repeat(16) + '-01',
};
const actorId = '019a154b-0000-7000-8000-000000000011';
const tenantId = '10000000-0000-4000-8000-000000000001';
const site = {
  displayName: 'School',
  enabledLocales: ['en'],
  defaultLocale: 'en',
  theme: null,
  showPlatformAttribution: true,
};
const account = { actorId, tenantId, organizationId: null, site, extra: 'private-wire-canary' };
const response = (value: unknown = account, status = 200, media = 'application/json') =>
  new Response(JSON.stringify(value), { status, headers: { 'content-type': media } });
const signal = () => new AbortController().signal;
afterEach(() => vi.useRealTimers());
describe('configured exact AccountSite capability', () => {
  it('uses SDK with only server credentials/current host/visitor/trace, and discards additive response members', async () => {
    const transport = vi.fn(async () => response());
    const caller = createAccountSiteCaller(configuration, transport);
    expect(await caller('private-access-canary', binding, signal())).toEqual({
      actorId,
      tenantId,
      organizationId: null,
      displayName: 'School',
    });
    const [url, init] = transport.mock.calls[0]! as unknown as [string, RequestInit];
    expect(url).toBe(configuration.apiOrigin + '/api/v1/account/site');
    expect(init).toMatchObject({
      method: 'GET',
      cache: 'no-store',
      redirect: 'error',
      credentials: 'omit',
    });
    expect(Object.fromEntries(new Headers(init.headers))).toEqual({
      accept: 'application/json',
      authorization: 'Bearer private-access-canary',
      'x-learnstack-host': binding.host,
      'x-learnstack-hop-secret': configuration.secret,
      'x-learnstack-visitor-address': binding.source,
      traceparent: binding.traceparent,
    });
  });
  it.each([
    'https://api.example/path',
    'https://user:pass@api.example',
    'http://remote.example',
    'http://127.0.0.1:5100/',
  ])('rejects noncanonical/unqualified origin %s', (apiOrigin) => {
    expect(() => createAccountSiteCaller({ ...configuration, apiOrigin }, vi.fn())).toThrow(
      'BFF provider: configuration',
    );
  });
  it.each([
    { host: 'UPPER.example', source: binding.source },
    { host: binding.host, source: 'forged' },
  ])('rejects invalid binding %j before transport', async (bad) => {
    const transport = vi.fn();
    await expect(
      createAccountSiteCaller(configuration, transport)('token', bad, signal()),
    ).rejects.toMatchObject({ reason: 'configuration' });
    expect(transport).not.toHaveBeenCalled();
  });
  it('generates a valid trace when the request trace is absent/invalid', async () => {
    const transport = vi.fn(async () => response());
    await createAccountSiteCaller(configuration, transport)(
      'token',
      { ...binding, traceparent: 'private-canary' },
      signal(),
    );
    const init = (transport.mock.calls[0]! as unknown as [string, RequestInit])[1];
    expect(new Headers(init.headers).get('traceparent')).toMatch(
      /^00-[0-9a-f]{32}-[0-9a-f]{16}-00$/,
    );
  });
  it.each([401, 403, 404])(
    'maps a genuine API refusal %s to identity without exposing a problem payload',
    async (status) => {
      const problem = {
        type: 'https://errors.learnstack.dev/not_found',
        title: 'private-title-canary',
        instance: '/api/v1/account/site',
        status,
        code: 'not_found',
        messageKey: 'lockey_not_found',
        correlationId: 'private-correlation-canary',
      };
      await expect(
        createAccountSiteCaller(configuration, async () =>
          response(problem, status, 'application/problem+json'),
        )('token', binding, signal()),
      ).rejects.toMatchObject({ reason: 'identity', message: 'BFF provider: identity' });
    },
  );
  it.each([
    response(account, 201),
    response(account, 200, 'text/html'),
    response({ ...account, actorId: tenantId }),
    response({}, 503, 'application/problem+json'),
  ])('fails closed on unexpected status/media/identity/problem', async (bad) => {
    await expect(
      createAccountSiteCaller(configuration, async () => bad)('token', binding, signal()),
    ).rejects.toMatchObject({ reason: 'response' });
  });
  it('bounds decoded body bytes at the existing API ceiling and cancels oversize consumption', async () => {
    let cancelled = false;
    const transport = vi.fn(
      async () =>
        new Response(
          new ReadableStream({
            start(controller) {
              controller.enqueue(new Uint8Array(MAX_PUBLIC_RESPONSE_BYTES + 1));
            },
            cancel() {
              cancelled = true;
            },
          }),
          { headers: { 'content-type': 'application/json' } },
        ),
    );
    await expect(
      createAccountSiteCaller(configuration, transport)('token', binding, signal()),
    ).rejects.toMatchObject({ reason: 'response' });
    expect(cancelled).toBe(true);
  });
  it('distinguishes caller cancellation and its finite deadline with no replay', async () => {
    vi.useFakeTimers();
    const controller = new AbortController();
    const transport = vi.fn(
      async (_url: RequestInfo | URL, init?: RequestInit) =>
        new Promise<Response>((_resolve, reject) => {
          init!.signal!.addEventListener(
            'abort',
            () => reject(new Error('private-network-canary')),
            { once: true },
          );
        }),
    );
    const caller = createAccountSiteCaller(configuration, transport);
    const cancelled = expect(caller('token', binding, controller.signal)).rejects.toMatchObject({
      reason: 'cancelled',
    });
    controller.abort();
    await cancelled;
    const deadline = new AbortController();
    const timeout = vi.spyOn(AbortSignal, 'timeout').mockReturnValue(deadline.signal);
    const timed = expect(caller('token', binding, signal())).rejects.toMatchObject({
      reason: 'deadline',
    });
    deadline.abort();
    await timed;
    expect(timeout).toHaveBeenCalledWith(10_000);
    expect(transport).toHaveBeenCalledTimes(2);
  });
});
