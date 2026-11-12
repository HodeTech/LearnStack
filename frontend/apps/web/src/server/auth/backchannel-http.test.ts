// @vitest-environment node
import { once } from 'node:events';
import { createServer, request } from 'node:http';
import type { IncomingHttpHeaders } from 'node:http';

import { describe, expect, it, vi } from 'vitest';

import { createBackchannelAdmission, createNativeBackchannel } from './backchannel-http.js';

it('preserves the exact shared eight-request and two-per-peer admission bounds', () => {
  const admission = createBackchannelAdmission();
  const releases = Array.from({ length: 8 }, (_, index) => {
    const release = admission.reserve('192.0.2.' + (1 + Math.floor(index / 2)));
    expect(release).not.toBeNull();
    return release!;
  });
  expect(admission.counts()).toEqual({ active: 8, peers: 4 });
  expect(admission.reserve('192.0.2.5')).toBeNull();
  releases[7]!();
  expect(admission.reserve('192.0.2.1')).toBeNull();
  const restored = admission.reserve('192.0.2.5');
  expect(restored).not.toBeNull();
  for (const release of releases) {
    release();
    release();
  }
  restored!();
  expect(admission.counts()).toEqual({ active: 0, peers: 0 });
});

describe('native backchannel HTTP admission', () => {
  it('accepts only the exact machine request and retires actual work', async () => {
    let applied = 0;
    const diagnostics: unknown[] = [];
    const admission = createBackchannelAdmission();
    const handler = createNativeBackchannel({
      authority: 'callback.test:4443',
      admission,
      diagnostic: (category, stage) => diagnostics.push({ category, ...(stage ? { stage } : {}) }),
      verify: async () => ({
        issuer: 'issuer',
        sid: 'sid',
        subject: null,
        jti: 'jti',
        fingerprint: 'digest',
        issuedAt: 1,
        expiresAt: 2,
      }),
      apply: () => {
        applied++;
        return applied === 1 ? 'accepted' : 'duplicate';
      },
    });
    const server = createServer(handler.handle);
    server.listen(0, '127.0.0.1');
    await once(server, 'listening');
    const address = server.address();
    if (!address || typeof address === 'string') throw new Error();
    const port = address.port;
    async function send(
      body: string,
      method = 'POST',
      host = 'callback.test:4443',
      path = '/api/auth/backchannel/keycloak',
      type: string | null = 'application/x-www-form-urlencoded',
    ) {
      return new Promise<{
        status: number | undefined;
        headers: IncomingHttpHeaders;
        body: string;
      }>((resolve, reject) => {
        const req = request(
          {
            host: '127.0.0.1',
            port,
            method,
            path,
            headers: {
              host,
              ...(type === null ? {} : { 'content-type': type }),
              'content-length': Buffer.byteLength(body),
              cookie: 'ignored=1',
              'x-forwarded-for': '1.2.3.4',
            },
          },
          (res) => {
            let text = '';
            res.setEncoding('utf8');
            res.on('data', (chunk: string) => {
              text += chunk;
            });
            res.on('end', () =>
              resolve({ status: res.statusCode, headers: res.headers, body: text }),
            );
          },
        );
        req.on('error', reject);
        req.end(body);
      });
    }
    try {
      const first = await send('ignored=x&logout_token=signed');
      expect(first.status).toBe(200);
      expect(first.body).toBe('');
      expect(first.headers['cache-control']).toBe('no-store');
      expect(first.headers['set-cookie']).toBeUndefined();
      expect((await send('logout_token=signed')).status).toBe(200);
      for (const body of ['logout_token=x&logout_token=y', 'logout_token=%zz', 'other=x'])
        expect((await send(body)).status).toBe(400);
      expect((await send('logout_token=x', 'GET')).status).toBe(400);
      expect((await send('logout_token=x', 'POST', 'other.test')).status).toBe(400);
      for (const type of [
        null,
        'application/json',
        'text/plain',
        'application/x-www-form-urlencoded; charset=iso-8859-1',
      ])
        expect(
          (await send('logout_token=x', 'POST', 'callback.test:4443', undefined, type)).status,
        ).toBe(400);
      expect(
        (
          await send(
            'logout_token=signed',
            'POST',
            'callback.test:4443',
            undefined,
            'application/x-www-form-urlencoded; charset=UTF-8',
          )
        ).status,
      ).toBe(200);
      expect(diagnostics).toContainEqual({ category: 'refused' });
      expect(JSON.stringify(diagnostics)).not.toMatch(/signed|logout_token|other\.test/);
      expect(
        (
          await send(
            'logout_token=x',
            'POST',
            'callback.test:4443',
            '/api/auth/backchannel/keycloak?x=1',
          )
        ).status,
      ).toBe(400);
      expect(applied).toBe(3);
    } finally {
      await handler.stop();
      await new Promise<void>((resolve) => server.close(() => resolve()));
    }
    expect(admission.counts().active).toBe(0);
  });
});

it('keeps verification work owned after stop until it actually settles', async () => {
  let release!: () => void;
  let entered!: () => void;
  const held = new Promise<void>((resolve) => {
    release = resolve;
  });
  const started = new Promise<void>((resolve) => {
    entered = resolve;
  });
  const admission = createBackchannelAdmission();
  const handler = createNativeBackchannel({
    authority: 'callback.test',
    admission,
    verify: async () => {
      entered();
      await held;
      throw new Error('refused');
    },
    apply: () => 'accepted',
  });
  const server = createServer(handler.handle);
  server.listen(0, '127.0.0.1');
  await once(server, 'listening');
  const address = server.address();
  if (!address || typeof address === 'string') throw new Error();
  const req = request({
    host: '127.0.0.1',
    port: address.port,
    method: 'POST',
    path: '/api/auth/backchannel/keycloak',
    headers: { host: 'callback.test', 'content-type': 'application/x-www-form-urlencoded' },
  });
  req.on('error', () => {});
  req.end('logout_token=signed');
  await started;
  let stopped = false;
  const stopping = handler.stop().then(() => {
    stopped = true;
  });
  await new Promise((resolve) => setImmediate(resolve));
  expect(stopped).toBe(false);
  expect(admission.counts().active).toBe(1);
  release();
  await stopping;
  expect(admission.counts().active).toBe(0);
  await new Promise<void>((resolve) => server.close(() => resolve()));
});

it('keeps per-peer reservations for held bodies until their actual sockets retire', async () => {
  const admission = createBackchannelAdmission();
  const verify = vi.fn(async () => ({
    issuer: 'issuer',
    sid: 'sid',
    subject: null,
    jti: 'jti',
    fingerprint: 'digest',
    issuedAt: 1,
    expiresAt: 2,
  }));
  const handler = createNativeBackchannel({
    authority: 'callback.test',
    admission,
    verify,
    apply: () => 'accepted',
  });
  let arrivals = 0;
  let arrived!: () => void;
  const both = new Promise<void>((resolve) => {
    arrived = resolve;
  });
  const server = createServer((req, res) => {
    handler.handle(req, res);
    if (++arrivals === 2) arrived();
  });
  server.listen(0, '127.0.0.1');
  await once(server, 'listening');
  const address = server.address();
  if (!address || typeof address === 'string') throw new Error();
  const options = {
    host: '127.0.0.1',
    port: address.port,
    method: 'POST',
    path: '/api/auth/backchannel/keycloak',
    headers: {
      host: 'callback.test',
      'content-type': 'application/x-www-form-urlencoded',
      'content-length': '100',
    },
  };
  const first = request(options),
    second = request(options);
  first.on('error', () => {});
  second.on('error', () => {});
  first.write('logout_token=');
  second.write('logout_token=');
  try {
    await both;
    expect(admission.counts().active).toBe(2);
    const refusal = await new Promise<number | undefined>((resolve, reject) => {
      const third = request(
        { ...options, headers: { ...options.headers, 'content-length': '14' } },
        (res) => {
          res.resume();
          res.on('end', () => resolve(res.statusCode));
        },
      );
      third.on('error', reject);
      third.end('logout_token=x');
    });
    expect(refusal).toBe(400);
    expect(verify).not.toHaveBeenCalled();
    expect(admission.counts().active).toBe(2);
  } finally {
    first.destroy();
    second.destroy();
    await handler.stop();
    expect(admission.counts().active).toBe(0);
    await new Promise<void>((resolve) => server.close(() => resolve()));
  }
});

it('rejects an oversized streaming body without waiting for the sender to end', async () => {
  const admission = createBackchannelAdmission();
  const handler = createNativeBackchannel({
    authority: 'callback.test',
    admission,
    verify: async () => {
      throw new Error();
    },
    apply: () => 'accepted',
  });
  const server = createServer(handler.handle);
  server.listen(0, '127.0.0.1');
  await once(server, 'listening');
  const address = server.address();
  if (!address || typeof address === 'string') throw new Error();
  const status = await new Promise<number | undefined>((resolve, reject) => {
    const req = request(
      {
        host: '127.0.0.1',
        port: address.port,
        method: 'POST',
        path: '/api/auth/backchannel/keycloak',
        headers: { host: 'callback.test', 'content-type': 'application/x-www-form-urlencoded' },
      },
      (res) => {
        res.resume();
        res.on('end', () => resolve(res.statusCode));
      },
    );
    req.on('error', reject);
    req.write('x'.repeat(16385));
  });
  expect(status).toBe(400);
  await handler.stop();
  expect(admission.counts().active).toBe(0);
  await new Promise<void>((resolve) => server.close(() => resolve()));
});

it('times out a held native input at ten seconds while retaining physical ownership', async () => {
  const admission = createBackchannelAdmission();
  const verify = vi.fn(async () => {
    throw new Error();
  });
  const handler = createNativeBackchannel({
    authority: 'callback.test',
    admission,
    verify,
    apply: () => 'accepted',
  });
  let arrived!: () => void;
  const started = new Promise<void>((resolve) => {
    arrived = resolve;
  });
  let observedClose = false;
  const server = createServer((req, res) => {
    req.once('close', () => {
      observedClose = true;
      expect(admission.counts().active).toBe(1);
    });
    handler.handle(req, res);
    arrived();
  });
  server.listen(0, '127.0.0.1');
  await once(server, 'listening');
  const address = server.address();
  if (!address || typeof address === 'string') throw new Error();
  vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
  const req = request({
    host: '127.0.0.1',
    port: address.port,
    method: 'POST',
    path: '/api/auth/backchannel/keycloak',
    headers: {
      host: 'callback.test',
      'content-type': 'application/x-www-form-urlencoded',
      'content-length': '100',
    },
  });
  req.on('error', () => {});
  let received = false;
  const reply = new Promise<{
    status: number | undefined;
    cache: string | undefined;
    body: string;
  }>((resolve) => {
    req.once('response', (res) => {
      received = true;
      let body = '';
      res.setEncoding('utf8');
      res.on('data', (chunk: string) => {
        body += chunk;
      });
      res.once('end', () =>
        resolve({ status: res.statusCode, cache: res.headers['cache-control'], body }),
      );
    });
  });
  try {
    req.write('logout_token=');
    await started;
    expect(admission.counts().active).toBe(1);
    vi.advanceTimersByTime(9_999);
    expect(received).toBe(false);
    expect(admission.counts().active).toBe(1);
    expect(verify).not.toHaveBeenCalled();
    vi.advanceTimersByTime(1);
    expect(admission.counts().active).toBe(1);
    expect(await reply).toEqual({ status: 400, cache: 'no-store', body: '' });
    await handler.stop();
    expect(observedClose).toBe(true);
    expect(admission.counts().active).toBe(0);
    expect(verify).not.toHaveBeenCalled();
  } finally {
    req.destroy();
    await handler.stop();
    await new Promise<void>((resolve) => server.close(() => resolve()));
    vi.useRealTimers();
  }
});

it('clears the input deadline before held verification and preserves its actual work', async () => {
  const admission = createBackchannelAdmission();
  let entered!: () => void, release!: () => void;
  const started = new Promise<void>((resolve) => {
    entered = resolve;
  });
  const held = new Promise<void>((resolve) => {
    release = resolve;
  });
  const handler = createNativeBackchannel({
    authority: 'callback.test',
    admission,
    verify: async () => {
      entered();
      await held;
      return {
        issuer: 'i',
        sid: 's',
        subject: null,
        jti: 'j',
        fingerprint: 'f',
        issuedAt: 1,
        expiresAt: 2,
      };
    },
    apply: () => 'accepted',
  });
  const server = createServer(handler.handle);
  server.listen(0, '127.0.0.1');
  await once(server, 'listening');
  const address = server.address();
  if (!address || typeof address === 'string') throw new Error();
  vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
  const req = request({
    host: '127.0.0.1',
    port: address.port,
    method: 'POST',
    path: '/api/auth/backchannel/keycloak',
    headers: { host: 'callback.test', 'content-type': 'application/x-www-form-urlencoded' },
  });
  req.on('error', () => {});
  const reply = new Promise<number | undefined>((resolve) => {
    req.once('response', (res) => {
      res.resume();
      res.once('end', () => resolve(res.statusCode));
    });
  });
  try {
    req.end('logout_token=signed');
    await started;
    vi.advanceTimersByTime(10_001);
    expect(admission.counts().active).toBe(1);
    expect(req.destroyed).toBe(false);
    release();
    expect(await reply).toBe(200);
    await handler.stop();
    expect(admission.counts().active).toBe(0);
  } finally {
    release();
    req.destroy();
    await handler.stop();
    await new Promise<void>((resolve) => server.close(() => resolve()));
    vi.useRealTimers();
  }
});

it('clears a held input timer on stop while waiting for request retirement', async () => {
  const admission = createBackchannelAdmission();
  const handler = createNativeBackchannel({
    authority: 'callback.test',
    admission,
    verify: async () => {
      throw new Error();
    },
    apply: () => 'accepted',
  });
  let arrived!: () => void;
  const started = new Promise<void>((resolve) => {
    arrived = resolve;
  });
  const server = createServer((req, res) => {
    handler.handle(req, res);
    arrived();
  });
  server.listen(0, '127.0.0.1');
  await once(server, 'listening');
  const address = server.address();
  if (!address || typeof address === 'string') throw new Error();
  vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
  const req = request({
    host: '127.0.0.1',
    port: address.port,
    method: 'POST',
    path: '/api/auth/backchannel/keycloak',
    headers: {
      host: 'callback.test',
      'content-type': 'application/x-www-form-urlencoded',
      'content-length': '100',
    },
  });
  req.on('error', () => {});
  try {
    req.write('logout_token=');
    await started;
    expect(vi.getTimerCount()).toBe(1);
    const stopping = handler.stop();
    expect(vi.getTimerCount()).toBe(0);
    expect(admission.counts().active).toBe(1);
    await stopping;
    expect(admission.counts().active).toBe(0);
  } finally {
    req.destroy();
    await handler.stop();
    await new Promise<void>((resolve) => server.close(() => resolve()));
    vi.useRealTimers();
  }
});
