// @vitest-environment node
import { randomBytes } from 'node:crypto';
import { createServer } from 'node:http';
import type { RequestListener, Server } from 'node:http';
import { gzipSync } from 'node:zlib';

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { createConfiguredPublicClient, validatedTraceparent } from './configured-public-client';
import { INGRESS_HEADER, mintProvenance } from './ingress';

const secret = randomBytes(32).toString('base64url');
const context = {
  host: 'tenant.example:3000',
  peer: '203.0.113.42',
  method: 'GET',
  target: '/tr-TR/courses?locale=en',
};
const traceparent = '00-11223344556677889900aabbccddeeff-1234567890abcdef-01';
const site = {
  displayName: 'Tenant',
  enabledLocales: ['tr-TR'],
  defaultLocale: 'tr-TR',
  showPlatformAttribution: true,
  theme: null,
};
const problem = {
  type: 'https://errors.example/rate-limited',
  title: 'Rate limited',
  status: 429,
  instance: '/api/v1/public/site',
  code: 'rate_limited',
  messageKey: 'lockey_rate_limited',
  correlationId: traceparent,
};
const servers: Server[] = [];

beforeEach(() => {
  vi.stubEnv('LEARNSTACK_PUBLIC_API_ORIGIN', 'http://127.0.0.1:5080');
  vi.stubEnv('LEARNSTACK_PUBLIC_HOP_SECRET', secret);
  vi.stubEnv('LEARNSTACK_PUBLIC_TLS_CERT', '.data/test/cert.pem');
  vi.stubEnv('LEARNSTACK_PUBLIC_TLS_KEY', '.data/test/key.pem');
});

afterEach(async () => {
  vi.unstubAllGlobals();
  vi.unstubAllEnvs();
  vi.useRealTimers();
  for (const server of servers.splice(0)) {
    server.closeAllConnections();
    await new Promise<void>((resolve, reject) =>
      server.close((error) => (error ? reject(error) : resolve())),
    );
  }
});

function client(
  options: Parameters<typeof createConfiguredPublicClient>[1] = {},
  target = context.target,
) {
  const configured = createConfiguredPublicClient(
    mintProvenance({ ...context, target }, secret),
    options,
  );
  if (!configured) throw new Error('Valid test ingress was refused');
  return configured;
}

function stubResponse(body: unknown = site, status = 200, headers?: HeadersInit) {
  const transport = vi.fn<typeof globalThis.fetch>();
  const responseHeaders = new Headers(headers);
  if (!responseHeaders.has('content-type'))
    responseHeaders.set(
      'content-type',
      status < 300 ? 'application/json' : 'application/problem+json',
    );
  transport.mockImplementation(
    async () => new Response(JSON.stringify(body), { status, headers: responseHeaders }),
  );
  vi.stubGlobal('fetch', transport);
  return transport;
}

async function listen(listener: RequestListener): Promise<string> {
  const server = createServer(listener);
  servers.push(server);
  await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve));
  const address = server.address();
  if (address === null || typeof address === 'string') throw new Error('Test listener failed');
  return `http://127.0.0.1:${address.port}`;
}

describe('configured public authority and operation boundary', () => {
  it('emits only the closed hop headers from private config and authenticated provenance', async () => {
    const transport = stubResponse();
    const result = await client({ traceparent }).getSite();
    expect(result).toEqual({ kind: 'success', status: 200, data: site });
    expect(transport).toHaveBeenCalledOnce();
    const [url, init] = transport.mock.calls[0] ?? [];
    expect(String(url)).toBe('http://127.0.0.1:5080/api/v1/public/site');
    expect(init).toEqual({
      method: 'GET',
      headers: {
        Accept: 'application/json',
        'X-LearnStack-Host': context.host,
        'X-LearnStack-Hop-Secret': secret,
        'X-LearnStack-Visitor-Address': context.peer,
        traceparent,
      },
      signal: expect.any(AbortSignal),
      redirect: 'error',
      cache: 'no-store',
      credentials: 'omit',
    });
    expect(JSON.stringify(result)).not.toContain(secret);
    expect(JSON.stringify(result)).not.toContain(INGRESS_HEADER);
  });

  it('ignores runtime unsigned authority, arbitrary headers and origin options', async () => {
    const transport = stubResponse();
    const options = {
      traceparent,
      host: 'attacker.example',
      peer: '192.0.2.99',
      origin: 'https://attacker.example',
      secret: 'attacker',
      headers: { Authorization: 'Bearer attacker', Cookie: 'session=attacker' },
    };
    await client(options).getSite();
    const [url, init] = transport.mock.calls[0] ?? [];
    expect(String(url)).toBe('http://127.0.0.1:5080/api/v1/public/site');
    expect(JSON.stringify(init)).not.toContain('attacker');
    expect(Object.keys(init?.headers ?? {})).toHaveLength(5);
  });

  it('refuses missing, unsigned, forged and repeated envelopes before any SDK call', () => {
    const transport = stubResponse();
    const stamp = mintProvenance(context, secret);
    const forged = mintProvenance(context, randomBytes(32).toString('base64url'));
    for (const envelope of [null, '', context.host, forged, stamp + ', ' + stamp])
      expect(createConfiguredPublicClient(envelope)).toBeNull();
    expect(transport).not.toHaveBeenCalled();
  });

  it.each([
    ['LEARNSTACK_PUBLIC_API_ORIGIN', undefined],
    ['LEARNSTACK_PUBLIC_API_ORIGIN', 'https://attacker.example'],
    ['LEARNSTACK_PUBLIC_API_ORIGIN', 'http://127.0.0.1:5080/escape'],
    ['LEARNSTACK_PUBLIC_API_ORIGIN', 'http://user:password@127.0.0.1:5080'],
    ['LEARNSTACK_PUBLIC_HOP_SECRET', 'short'],
    ['LEARNSTACK_PUBLIC_TLS_CERT', undefined],
    ['LEARNSTACK_PUBLIC_TLS_KEY', undefined],
  ])('fails closed on private configuration %s = %s', (key, value) => {
    const transport = stubResponse();
    vi.stubEnv(key, value);
    expect(() => client()).toThrow();
    expect(transport).not.toHaveBeenCalled();
  });

  it('uses the four SDK operations and only the canonical signed path locale', async () => {
    const transport = stubResponse();
    const configured = client();
    const query = { locale: 'en', cursor: 'next+page', limit: '3', headers: { Cookie: 'bad' } };
    await configured.getSite();
    await configured.getCourses(query);
    await configured.getCourse({ slug: 'intro' }, { lessonCursor: 'next', lessonLimit: '2' });
    await configured.getLesson({ slug: 'intro', lessonSlug: 'first' });
    expect(transport.mock.calls.map(([url]) => String(url))).toEqual([
      'http://127.0.0.1:5080/api/v1/public/site',
      'http://127.0.0.1:5080/api/v1/public/courses?locale=tr-TR&cursor=next%2Bpage&limit=3',
      'http://127.0.0.1:5080/api/v1/public/courses/intro?locale=tr-TR&lessonCursor=next&lessonLimit=2',
      'http://127.0.0.1:5080/api/v1/public/courses/intro/lessons/first?locale=tr-TR',
    ]);
  });

  it.each([
    '/',
    '/courses',
    '/tr-tr/courses',
    '/%74r-TR/courses',
    '/tr_TR/courses',
    '/studio',
    '/tr/status/not-found',
  ])('refuses Education calls without a canonical content route: %s', async (target) => {
    const transport = stubResponse();
    const configured = client({}, target);
    expect(await configured.getCourses()).toEqual({ kind: 'invalid-request' });
    expect(await configured.getCourse({ slug: 'intro' })).toEqual({ kind: 'invalid-request' });
    expect(await configured.getLesson({ slug: 'intro', lessonSlug: 'first' })).toEqual({
      kind: 'invalid-request',
    });
    expect(transport).not.toHaveBeenCalled();
    expect((await configured.getSite()).kind).toBe('success');
  });

  it('preserves SDK dot-segment rejection and contains encoded slug values on the private origin', async () => {
    const transport = stubResponse();
    const configured = client();
    expect(await configured.getCourse({ slug: '..' })).toEqual({ kind: 'invalid-request' });
    expect(await configured.getLesson({ slug: 'intro', lessonSlug: '.' })).toEqual({
      kind: 'invalid-request',
    });
    expect(transport).not.toHaveBeenCalled();
    await configured.getCourse({ slug: '//attacker.example/%2e%2e' });
    expect(String(transport.mock.calls[0]?.[0])).toBe(
      'http://127.0.0.1:5080/api/v1/public/courses/%2F%2Fattacker.example%2F%252e%252e?locale=tr-TR',
    );
  });
});

describe('trace context and SDK outcomes', () => {
  it.each(['application/json', 'Application/JSON; charset=utf-8'])(
    'accepts the public success media type %s',
    async (contentType) => {
      stubResponse(site, 200, { 'content-type': contentType });
      expect(await client().getSite()).toEqual({ kind: 'success', status: 200, data: site });
    },
  );

  it.each(['application/problem+json', 'Application/Problem+JSON; charset=utf-8'])(
    'accepts the public failure media type %s',
    async (contentType) => {
      stubResponse(problem, 429, { 'content-type': contentType });
      expect(await client().getSite()).toMatchObject({ kind: 'api-error', status: 429 });
    },
  );

  it.each([
    [200, null],
    [200, 'text/plain'],
    [200, 'application/problem+json'],
    [200, 'application/json-extra'],
    [429, null],
    [429, 'text/plain'],
    [429, 'application/json'],
  ])('refuses status %s with media type %s and releases its body', async (status, contentType) => {
    vi.useFakeTimers();
    const cancel = vi.fn();
    const body = new ReadableStream<Uint8Array>({
      start(controller) {
        controller.enqueue(
          new TextEncoder().encode(JSON.stringify(status === 200 ? site : problem)),
        );
      },
      cancel,
    });
    const headers = contentType === null ? undefined : { 'content-type': contentType };
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(body, { status, headers })));
    expect(await client().getSite()).toEqual({ kind: 'transport-error' });
    expect(cancel).toHaveBeenCalledOnce();
    expect(body.locked).toBe(false);
    expect(vi.getTimerCount()).toBe(0);
  });

  it('preserves the incoming W3C trace and the Problem Details trace identifier', async () => {
    const transport = stubResponse(problem, 429, { 'Retry-After': '27' });
    const result = await client({ traceparent }).getSite();
    expect(result).toMatchObject({
      kind: 'api-error',
      status: 429,
      error: { code: 'rate_limited', retryAfter: 27 },
      problem: { correlationId: traceparent },
    });
    expect(new Headers(transport.mock.calls[0]?.[1]?.headers).get('traceparent')).toBe(traceparent);
  });

  it.each([
    undefined,
    null,
    '',
    'invalid',
    traceparent.toUpperCase(),
    traceparent + ', ' + traceparent,
    traceparent + '\n',
    'ff-' + traceparent.slice(3),
    '00-' + '0'.repeat(32) + '-1234567890abcdef-01',
    '00-11223344556677889900aabbccddeeff-' + '0'.repeat(16) + '-01',
  ])('starts a fresh valid trace for rejected incoming context %s', async (value) => {
    const transport = stubResponse();
    expect(validatedTraceparent(value)).toBeNull();
    await client({ traceparent: value }).getSite();
    const emitted = new Headers(transport.mock.calls[0]?.[1]?.headers).get('traceparent');
    expect(emitted).not.toBe(value);
    expect(validatedTraceparent(emitted)).toBe(emitted);
    expect(emitted).toMatch(/^00-[0-9a-f]{32}-[0-9a-f]{16}-00$/);
  });

  it('reuses one generated context within the incoming request and changes it for a new request', async () => {
    const transport = stubResponse();
    const configured = client();
    await configured.getSite();
    await configured.getCourses();
    await client().getSite();
    const contexts = transport.mock.calls.map(([, init]) =>
      new Headers(init?.headers).get('traceparent'),
    );
    expect(contexts[0]).toBe(contexts[1]);
    expect(contexts[2]).not.toBe(contexts[0]);
  });

  it('preserves malformed JSON, invalid wire shape and network failure as distinct SDK outcomes', async () => {
    const transport = stubResponse();
    transport.mockResolvedValueOnce(
      new Response('{', { headers: { 'content-type': 'application/json' } }),
    );
    expect(await client().getSite()).toEqual({
      kind: 'invalid-response',
      reason: 'malformed-json',
    });
    transport.mockResolvedValueOnce(
      new Response('{}', { headers: { 'content-type': 'application/json' } }),
    );
    expect(await client().getSite()).toEqual({
      kind: 'invalid-response',
      reason: 'invalid-success',
    });
    transport.mockRejectedValueOnce(new Error('private provider detail'));
    expect(await client().getSite()).toEqual({ kind: 'transport-error' });
    expect(transport).toHaveBeenCalledTimes(3);
  });

  it('does no request for a pre-cancelled caller and retains the captured signal', async () => {
    const transport = stubResponse();
    const cancellation = new AbortController();
    const options = { signal: cancellation.signal };
    const configured = client(options);
    options.signal = new AbortController().signal;
    cancellation.abort();
    expect(await configured.getSite()).toEqual({ kind: 'cancelled' });
    expect(await configured.getCourses()).toEqual({ kind: 'cancelled' });
    expect(transport).not.toHaveBeenCalled();
  });

  it('cancels and unlocks an oversized reader and clears its deadline/listener', async () => {
    vi.useFakeTimers();
    const cancellation = new AbortController();
    const remove = vi.spyOn(cancellation.signal, 'removeEventListener');
    const cancel = vi.fn();
    const body = new ReadableStream<Uint8Array>({
      start(controller) {
        controller.enqueue(new Uint8Array(8 * 1024 * 1024 + 1));
      },
      cancel,
    });
    vi.stubGlobal(
      'fetch',
      vi
        .fn()
        .mockResolvedValue(new Response(body, { headers: { 'content-type': 'application/json' } })),
    );
    expect(await client({ signal: cancellation.signal }).getSite()).toEqual({
      kind: 'transport-error',
    });
    expect(cancel).toHaveBeenCalledOnce();
    expect(body.locked).toBe(false);
    expect(remove).toHaveBeenCalledWith('abort', expect.any(Function));
    expect(vi.getTimerCount()).toBe(0);
  });

  it('cancels a pending body read at the deadline without waiting for cancellation to settle', async () => {
    vi.useFakeTimers();
    const cancel = vi.fn(() => new Promise<void>(() => {}));
    const body = new ReadableStream<Uint8Array>({ cancel });
    vi.stubGlobal(
      'fetch',
      vi
        .fn()
        .mockResolvedValue(new Response(body, { headers: { 'content-type': 'application/json' } })),
    );
    const pending = client().getSite();
    await vi.advanceTimersByTimeAsync(10_000);
    expect(await pending).toEqual({ kind: 'transport-error' });
    expect(cancel).toHaveBeenCalledOnce();
    expect(body.locked).toBe(false);
    expect(vi.getTimerCount()).toBe(0);
  });

  it('closes a late response from an abort-insensitive transport after returning the timeout', async () => {
    vi.useFakeTimers();
    let respond: (response: Response) => void = () => {};
    const delayed = new Promise<Response>((resolve) => {
      respond = resolve;
    });
    vi.stubGlobal('fetch', vi.fn().mockReturnValue(delayed));
    const pending = client().getSite();
    await vi.advanceTimersByTimeAsync(10_000);
    expect(await pending).toEqual({ kind: 'transport-error' });
    const cancel = vi.fn();
    respond(new Response(new ReadableStream<Uint8Array>({ cancel })));
    await vi.advanceTimersByTimeAsync(0);
    expect(cancel).toHaveBeenCalledOnce();
    expect(vi.getTimerCount()).toBe(0);
  });
});

describe('real HTTP consumer resource and redirect boundary', () => {
  it('counts decompressed bytes with an inclusive 8 MiB limit', async () => {
    const budget = 8 * 1024 * 1024;
    const prefix = JSON.stringify({ ...site, padding: '' });
    const exact = JSON.stringify({
      ...site,
      padding: 'x'.repeat(budget - Buffer.byteLength(prefix)),
    });
    expect(Buffer.byteLength(exact)).toBe(budget);
    const oversized = exact.slice(0, -1) + ' ' + exact.slice(-1);
    let count = 0;
    const origin = await listen((_request, response) => {
      const decoded = count++ === 0 ? exact : oversized;
      const compressed = gzipSync(decoded);
      expect(compressed.byteLength).toBeLessThan(budget / 100);
      response.writeHead(200, {
        'content-type': 'application/json',
        'content-encoding': 'gzip',
        'content-length': String(compressed.byteLength),
      });
      response.end(compressed);
    });
    vi.stubEnv('LEARNSTACK_PUBLIC_API_ORIGIN', origin);
    expect((await client().getSite()).kind).toBe('success');
    expect(await client().getSite()).toEqual({ kind: 'transport-error' });
    expect(count).toBe(2);
  });

  it('refuses redirects without delivering the hop credentials to their target or retrying', async () => {
    let targetCalls = 0;
    let sourceCalls = 0;
    const target = await listen((_request, response) => {
      targetCalls++;
      response.end(JSON.stringify(site));
    });
    const origin = await listen((request, response) => {
      sourceCalls++;
      expect(request.headers['x-learnstack-hop-secret']).toBe(secret);
      response.writeHead(307, { location: target + '/capture' });
      response.end();
    });
    vi.stubEnv('LEARNSTACK_PUBLIC_API_ORIGIN', origin);
    expect(await client().getSite()).toEqual({ kind: 'transport-error' });
    expect(sourceCalls).toBe(1);
    expect(targetCalls).toBe(0);
  });

  it('cancels an in-progress body as caller cancellation and closes the socket', async () => {
    const cancellation = new AbortController();
    let closeResponse: () => void = () => {};
    const closed = new Promise<void>((resolve) => {
      closeResponse = resolve;
    });
    const origin = await listen((_request, response) => {
      response.on('close', closeResponse);
      response.writeHead(200, { 'content-type': 'application/json' });
      response.write('{"displayName":');
      setTimeout(() => cancellation.abort(), 50);
    });
    vi.stubEnv('LEARNSTACK_PUBLIC_API_ORIGIN', origin);
    expect(await client({ signal: cancellation.signal }).getSite()).toEqual({ kind: 'cancelled' });
    await closed;
  });

  it('enforces one ten-second deadline across both delayed headers and delayed body', async () => {
    const closed: Promise<void>[] = [];
    let requests = 0;
    const origin = await listen((request, response) => {
      requests++;
      closed.push(new Promise<void>((resolve) => response.on('close', resolve)));
      if (request.url?.startsWith('/api/v1/public/courses')) {
        const headers = setTimeout(() => {
          response.writeHead(200, { 'content-type': 'application/json' });
          response.write('{"locale":"tr-TR",');
        }, 6000);
        response.on('close', () => clearTimeout(headers));
      }
    });
    vi.stubEnv('LEARNSTACK_PUBLIC_API_ORIGIN', origin);
    const started = performance.now();
    const results = await Promise.all([client().getSite(), client().getCourses()]);
    const elapsed = performance.now() - started;
    expect(results).toEqual([{ kind: 'transport-error' }, { kind: 'transport-error' }]);
    expect(elapsed).toBeGreaterThanOrEqual(9500);
    expect(elapsed).toBeLessThan(13_000);
    expect(requests).toBe(2);
    await Promise.all(closed);
  }, 15_000);
});
