// @vitest-environment node
import { once } from 'node:events';
import { createServer, request } from 'node:http';
import type { IncomingHttpHeaders } from 'node:http';

import { describe, expect, it } from 'vitest';

import { createBackchannelAdmission, createNativeBackchannel } from './backchannel-http.js';

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
              'content-type': 'application/x-www-form-urlencoded',
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
      expect(applied).toBe(2);
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
  const handler = createNativeBackchannel({
    authority: 'callback.test',
    admission,
    verify: async () => {
      throw new Error();
    },
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
  expect(admission.counts().active).toBe(2);
  first.destroy();
  second.destroy();
  await handler.stop();
  expect(admission.counts().active).toBe(0);
  await new Promise<void>((resolve) => server.close(() => resolve()));
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
