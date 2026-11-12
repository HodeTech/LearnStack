// @vitest-environment node
import { execFileSync } from 'node:child_process';
import { once } from 'node:events';
import { chmodSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { createServer, request } from 'node:https';
import { connect as connectTcp } from 'node:net';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { connect as connectTls } from 'node:tls';

import { expect, it, vi } from 'vitest';

import { qualifyTlsServer } from '#tls-server-qualification';

import { createBackchannelBridge } from './backchannel-bridge.js';
import {
  createBackchannelAdmission,
  createNativeBackchannel,
  type BackchannelDiagnosticSink,
} from './backchannel-http.js';

it('qualifies server purpose and CA signing constraints before binding', async () => {
  const root = mkdtempSync(join(tmpdir(), 'backchannel-purpose-'));
  const path = (name: string) => join(root, name);
  const run = (...args: string[]) => execFileSync('openssl', args, { stdio: 'ignore' });
  try {
    for (const [name, usage] of [
      ['good', 'keyCertSign,cRLSign'],
      ['bad', 'digitalSignature'],
    ]) {
      run(
        'req',
        '-x509',
        '-newkey',
        'rsa:2048',
        '-nodes',
        '-days',
        '1',
        '-subj',
        `/CN=${name}-ca`,
        '-addext',
        'basicConstraints=critical,CA:TRUE',
        '-addext',
        `keyUsage=critical,${usage}`,
        '-keyout',
        path(`${name}-ca.key`),
        '-out',
        path(`${name}-ca.pem`),
      );
    }
    run(
      'req',
      '-new',
      '-newkey',
      'rsa:2048',
      '-nodes',
      '-subj',
      '/CN=localhost',
      '-keyout',
      path('leaf.key'),
      '-out',
      path('leaf.csr'),
    );
    chmodSync(path('leaf.key'), 0o600);
    for (const [name, ca, purpose] of [
      ['server', 'good', 'serverAuth'],
      ['client', 'good', 'clientAuth'],
      ['bad-ca', 'bad', 'serverAuth'],
    ]) {
      writeFileSync(
        path('extensions'),
        `basicConstraints=critical,CA:FALSE\nsubjectAltName=DNS:localhost\nextendedKeyUsage=${purpose}\n`,
      );
      run(
        'x509',
        '-req',
        '-in',
        path('leaf.csr'),
        '-CA',
        path(`${ca}-ca.pem`),
        '-CAkey',
        path(`${ca}-ca.key`),
        '-set_serial',
        '1',
        '-days',
        '1',
        '-extfile',
        path('extensions'),
        '-out',
        path(`${name}.pem`),
      );
    }
    const options = (leaf: string, ca: string) => ({
      certificate: readFileSync(path(`${leaf}.pem`)),
      privateKey: readFileSync(path('leaf.key')),
      ca: readFileSync(path(`${ca}-ca.pem`)),
      hostname: 'localhost',
      signal: AbortSignal.timeout(10_000),
    });
    await expect(qualifyTlsServer(options('server', 'good'))).resolves.toBeUndefined();
    await expect(qualifyTlsServer(options('client', 'good'))).rejects.toThrow();
    await expect(qualifyTlsServer(options('bad-ca', 'bad'))).rejects.toThrow();
    await expect(
      qualifyTlsServer({ ...options('server', 'good'), hostname: 'foreign.invalid' }),
    ).rejects.toThrow();
    await expect(
      qualifyTlsServer({ ...options('server', 'good'), signal: AbortSignal.abort() }),
    ).rejects.toThrow();
    const bridge = createBackchannelBridge(
      {
        enabled: true,
        authority: 'localhost:4443',
        port: 0,
        bindAddress: '127.0.0.1',
        certificate: path('client.pem'),
        privateKey: path('leaf.key'),
        ca: path('good-ca.pem'),
        nativeCa: path('good-ca.pem'),
      },
      createBackchannelAdmission(),
      443,
    );
    await expect(bridge.start()).rejects.toThrow();
    expect(bridge.server.listening).toBe(false);
    await bridge.stop();
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

it('bounds raw connections and joins incomplete TLS and HTTP sockets on stop', async () => {
  const root = mkdtempSync(join(tmpdir(), 'backchannel-raw-'));
  const cert = join(root, 'cert.pem'),
    key = join(root, 'key.pem');
  execFileSync(
    'openssl',
    [
      'req',
      '-x509',
      '-newkey',
      'rsa:2048',
      '-nodes',
      '-keyout',
      key,
      '-out',
      cert,
      '-days',
      '1',
      '-subj',
      '/CN=localhost',
      '-addext',
      'subjectAltName=DNS:localhost',
      '-addext',
      'basicConstraints=critical,CA:TRUE',
    ],
    { stdio: 'ignore' },
  );
  chmodSync(key, 0o600);
  const admission = createBackchannelAdmission();
  const bridge = createBackchannelBridge(
    {
      enabled: true,
      authority: 'localhost:4443',
      port: 0,
      bindAddress: '127.0.0.1',
      certificate: cert,
      privateKey: key,
      ca: cert,
      nativeCa: cert,
    },
    admission,
    443,
  );
  const clients: Array<ReturnType<typeof connectTcp>> = [];
  const serverClosures: Promise<unknown>[] = [];
  try {
    await bridge.start();
    const address = bridge.server.address();
    if (!address || typeof address === 'string') throw new Error();
    const raw = async () => {
      const accepted = once(bridge.server, 'connection');
      const client = connectTcp(address.port, '127.0.0.1');
      client.on('error', () => {});
      clients.push(client);
      const [socket] = await accepted;
      serverClosures.push(new Promise<void>((resolve) => socket.once('close', resolve)));
      return client;
    };
    for (let i = 0; i < 8; i++) await raw();
    const refusedClosed = new Promise<void>((resolve) => {
      const client = connectTcp(address.port, '127.0.0.1');
      client.on('error', () => {});
      clients.push(client);
      client.once('close', resolve);
    });
    await refusedClosed;
    expect(admission.counts().active).toBe(0);
    const firstClosed = new Promise<void>((resolve) => clients[0]!.once('close', resolve));
    clients[0]!.destroy();
    await firstClosed;
    await serverClosures[0];
    const connected = once(bridge.server, 'connection');
    const secured = once(bridge.server, 'secureConnection');
    const partial = connectTls({
      host: '127.0.0.1',
      port: address.port,
      servername: 'localhost',
      ca: readFileSync(cert),
    });
    partial.on('error', () => {});
    clients.push(partial);
    await connected;
    await once(partial, 'secureConnect');
    const [tlsSocket] = await secured;
    const partialReceived = once(tlsSocket, 'data');
    partial.write('POST /api/auth/backchannel/keycloak HTTP/1.1\r\nHost: localhost:4443\r\n');
    await partialReceived;
    const closed = clients
      .filter((client) => !client.closed)
      .map((client) => new Promise<void>((resolve) => client.once('close', resolve)));
    await bridge.stop();
    await Promise.all(closed);
    expect(bridge.server.listening).toBe(false);
    expect(admission.counts().active).toBe(0);
  } finally {
    for (const client of clients) client.destroy();
    await bridge.stop();
    rmSync(root, { recursive: true, force: true });
  }
});

it('forwards only the fixed callback over verified TLS and joins both listeners', async () => {
  const root = mkdtempSync(join(tmpdir(), 'backchannel-test-'));
  const cert = join(root, 'cert.pem'),
    key = join(root, 'key.pem');
  execFileSync(
    'openssl',
    [
      'req',
      '-x509',
      '-newkey',
      'rsa:2048',
      '-nodes',
      '-keyout',
      key,
      '-out',
      cert,
      '-days',
      '1',
      '-subj',
      '/CN=localhost',
      '-addext',
      'subjectAltName=DNS:localhost',
      '-addext',
      'basicConstraints=critical,CA:TRUE',
    ],
    { stdio: 'ignore' },
  );
  chmodSync(key, 0o600);
  const admission = createBackchannelAdmission();
  const diagnostics: unknown[] = [];
  let applied = 0;
  const native = createNativeBackchannel({
    authority: 'localhost:4443',
    admission,
    verify: async () => ({
      issuer: 'i',
      sid: 's',
      subject: null,
      jti: 'j',
      fingerprint: 'f',
      issuedAt: 1,
      expiresAt: 2,
    }),
    apply: () => {
      applied++;
      return 'accepted';
    },
  });
  const server = createServer({ cert: readFileSync(cert), key: readFileSync(key) }, native.handle);
  server.listen(0, '127.0.0.1');
  await once(server, 'listening');
  const address = server.address();
  if (!address || typeof address === 'string') throw new Error();
  const bridge = createBackchannelBridge(
    {
      enabled: true,
      authority: 'localhost:4443',
      port: 0,
      bindAddress: '127.0.0.1',
      certificate: cert,
      privateKey: key,
      ca: cert,
      nativeCa: cert,
    },
    admission,
    address.port,
    (category, stage) =>
      diagnostics.push({ boundary: 'bridge', category, ...(stage ? { stage } : {}) }),
  );
  await bridge.start();
  const target = bridge.server.address();
  if (!target || typeof target === 'string') throw new Error();
  try {
    const call = (body = 'logout_token=signed') =>
      new Promise<number | undefined>((resolve, reject) => {
        const req = request(
          {
            host: '127.0.0.1',
            port: target.port,
            servername: 'localhost',
            ca: readFileSync(cert),
            method: 'POST',
            path: '/api/auth/backchannel/keycloak',
            headers: {
              host: 'localhost:4443',
              'content-type': 'application/x-www-form-urlencoded',
            },
          },
          (res) => {
            res.resume();
            res.on('end', () => resolve(res.statusCode));
          },
        );
        req.on('error', reject);
        req.end(body);
      });
    const status = await call();
    expect(status).toBe(200);
    expect(applied).toBe(1);
    expect(diagnostics).toEqual([{ boundary: 'bridge', category: 'accepted' }]);
    await expect.poll(() => admission.counts().active).toBe(0);
    const first = admission.reserve('127.0.0.1')!;
    const second = admission.reserve('127.0.0.1')!;
    try {
      expect(await call()).toBe(400);
      expect(diagnostics).toContainEqual({
        boundary: 'bridge',
        category: 'unavailable',
        stage: 'capacity',
      });
    } finally {
      first();
      second();
    }
    expect(await call('logout_token=%zz')).toBe(400);
    expect(diagnostics).toContainEqual({ boundary: 'bridge', category: 'refused' });
    expect(applied).toBe(1);
    expect(JSON.stringify(diagnostics)).not.toMatch(/signed|logout_token|%zz|localhost/);
  } finally {
    await bridge.stop();
    await native.stop();
    await new Promise<void>((resolve) => server.close(() => resolve()));
    rmSync(root, { recursive: true, force: true });
  }
  expect(admission.counts().active).toBe(0);
});

it('latches stop before and during a physical bind', async () => {
  const root = mkdtempSync(join(tmpdir(), 'backchannel-bind-'));
  const cert = join(root, 'cert.pem'),
    key = join(root, 'key.pem');
  execFileSync(
    'openssl',
    [
      'req',
      '-x509',
      '-newkey',
      'rsa:2048',
      '-nodes',
      '-keyout',
      key,
      '-out',
      cert,
      '-days',
      '1',
      '-subj',
      '/CN=localhost',
      '-addext',
      'subjectAltName=DNS:localhost',
      '-addext',
      'basicConstraints=critical,CA:TRUE',
    ],
    { stdio: 'ignore' },
  );
  chmodSync(key, 0o600);
  const config = {
    enabled: true as const,
    authority: 'localhost:4443',
    port: 0,
    bindAddress: '127.0.0.1' as const,
    certificate: cert,
    privateKey: key,
    ca: cert,
    nativeCa: cert,
  };
  try {
    const early = createBackchannelBridge(config, createBackchannelAdmission(), 443);
    await early.stop();
    expect(() => early.start()).toThrow();
    expect(early.server.listening).toBe(false);
    const pending = createBackchannelBridge(config, createBackchannelAdmission(), 443);
    const start = pending.start();
    const stopped = pending.stop();
    const [startResult, stopResult] = await Promise.allSettled([start, stopped]);
    expect(startResult.status).toBe('rejected');
    expect(stopResult.status).toBe('fulfilled');
    expect(pending.server.listening).toBe(false);
    expect(() => pending.start()).toThrow();
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

it('refuses an untrusted native TLS peer and joins a held forwarded socket', async () => {
  const root = mkdtempSync(join(tmpdir(), 'backchannel-forward-'));
  const cert = join(root, 'cert.pem'),
    key = join(root, 'key.pem'),
    wrongCert = join(root, 'wrong.pem'),
    wrongKey = join(root, 'wrong-key.pem');
  const makeCert = (output: string, privateKey: string) => {
    execFileSync(
      'openssl',
      [
        'req',
        '-x509',
        '-newkey',
        'rsa:2048',
        '-nodes',
        '-keyout',
        privateKey,
        '-out',
        output,
        '-days',
        '1',
        '-subj',
        '/CN=localhost',
        '-addext',
        'subjectAltName=DNS:localhost',
        '-addext',
        'basicConstraints=critical,CA:TRUE',
      ],
      { stdio: 'ignore' },
    );
    chmodSync(privateKey, 0o600);
  };
  makeCert(cert, key);
  makeCert(wrongCert, wrongKey);
  let entered!: () => void;
  const seen = new Promise<void>((resolve) => {
    entered = resolve;
  });
  const server = createServer({ cert: readFileSync(cert), key: readFileSync(key) }, (req) => {
    req.resume();
    req.on('end', entered);
  });
  server.listen(0, '127.0.0.1');
  await once(server, 'listening');
  const address = server.address();
  if (!address || typeof address === 'string') throw new Error();
  const config = {
    enabled: true as const,
    authority: 'localhost:4443',
    port: 0,
    bindAddress: '127.0.0.1' as const,
    certificate: cert,
    privateKey: key,
    ca: cert,
    nativeCa: wrongCert,
  };
  const admission = createBackchannelAdmission();
  const diagnostics: unknown[] = [];
  const diagnostic: BackchannelDiagnosticSink = (category, stage) =>
    diagnostics.push({ boundary: 'bridge', category, ...(stage ? { stage } : {}) });
  const call = (port: number) =>
    new Promise<number | undefined>((resolve, reject) => {
      const req = request(
        {
          host: '127.0.0.1',
          port,
          servername: 'localhost',
          ca: readFileSync(cert),
          method: 'POST',
          path: '/api/auth/backchannel/keycloak',
          headers: { host: 'localhost:4443', 'content-type': 'application/x-www-form-urlencoded' },
        },
        (res) => {
          res.resume();
          res.on('end', () => resolve(res.statusCode));
        },
      );
      req.on('error', reject);
      req.end('logout_token=signed');
    });
  try {
    const untrusted = createBackchannelBridge(config, admission, address.port, diagnostic);
    await untrusted.start();
    const untrustedAddress = untrusted.server.address();
    if (!untrustedAddress || typeof untrustedAddress === 'string') throw new Error();
    expect(await call(untrustedAddress.port)).toBe(400);
    expect(diagnostics).toEqual([{ boundary: 'bridge', category: 'unavailable', stage: 'native' }]);
    expect(JSON.stringify(diagnostics)).not.toMatch(/signed|logout_token|CERT|Error|localhost/);
    await untrusted.stop();
    const held = createBackchannelBridge({ ...config, nativeCa: cert }, admission, address.port);
    await held.start();
    const heldAddress = held.server.address();
    if (!heldAddress || typeof heldAddress === 'string') throw new Error();
    const caller = call(heldAddress.port).catch(() => undefined);
    await seen;
    expect(admission.counts().active).toBe(1);
    await held.stop();
    await caller;
    expect(held.server.listening).toBe(false);
    expect(admission.counts().active).toBe(0);
  } finally {
    server.closeAllConnections();
    await new Promise<void>((resolve) => server.close(() => resolve()));
    rmSync(root, { recursive: true, force: true });
  }
});

it('times out held bridge TLS input before forwarding and joins actual closure', async () => {
  const root = mkdtempSync(join(tmpdir(), 'backchannel-deadline-'));
  const cert = join(root, 'cert.pem'),
    key = join(root, 'key.pem');
  execFileSync(
    'openssl',
    [
      'req',
      '-x509',
      '-newkey',
      'rsa:2048',
      '-nodes',
      '-keyout',
      key,
      '-out',
      cert,
      '-days',
      '1',
      '-subj',
      '/CN=localhost',
      '-addext',
      'subjectAltName=DNS:localhost',
      '-addext',
      'basicConstraints=critical,CA:TRUE',
    ],
    { stdio: 'ignore' },
  );
  chmodSync(key, 0o600);
  const admission = createBackchannelAdmission();
  const diagnostics: unknown[] = [];
  const bridge = createBackchannelBridge(
    {
      enabled: true,
      authority: 'localhost:4443',
      port: 0,
      bindAddress: '127.0.0.1',
      certificate: cert,
      privateKey: key,
      ca: cert,
      nativeCa: cert,
    },
    admission,
    443,
    (category, stage) => diagnostics.push({ category, ...(stage ? { stage } : {}) }),
  );
  await bridge.start();
  const address = bridge.server.address();
  if (!address || typeof address === 'string') throw new Error();
  const arrived = once(bridge.server, 'request');
  vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
  const req = request({
    host: '127.0.0.1',
    port: address.port,
    servername: 'localhost',
    ca: readFileSync(cert),
    method: 'POST',
    path: '/api/auth/backchannel/keycloak',
    headers: {
      host: 'localhost:4443',
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
    const [incoming] = await arrived;
    let observedClose = false;
    incoming.once('close', () => {
      observedClose = true;
      expect(admission.counts().active).toBe(1);
    });
    expect(admission.counts().active).toBe(1);
    vi.advanceTimersByTime(9_999);
    expect(received).toBe(false);
    expect(admission.counts().active).toBe(1);
    vi.advanceTimersByTime(1);
    expect(admission.counts().active).toBe(1);
    expect(await reply).toEqual({ status: 400, cache: 'no-store', body: '' });
    await bridge.stop();
    expect(observedClose).toBe(true);
    expect(admission.counts().active).toBe(0);
    expect(diagnostics).toEqual([{ category: 'refused' }]);
  } finally {
    req.destroy();
    await bridge.stop();
    vi.useRealTimers();
    rmSync(root, { recursive: true, force: true });
  }
});
