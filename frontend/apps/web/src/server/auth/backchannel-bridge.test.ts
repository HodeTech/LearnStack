// @vitest-environment node
import { execFileSync } from 'node:child_process';
import { once } from 'node:events';
import { chmodSync, mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { createServer, request } from 'node:https';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { expect, it } from 'vitest';

import { createBackchannelBridge } from './backchannel-bridge.js';
import { createBackchannelAdmission, createNativeBackchannel } from './backchannel-http.js';

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
  );
  await bridge.start();
  const target = bridge.server.address();
  if (!target || typeof target === 'string') throw new Error();
  try {
    const status = await new Promise<number | undefined>((resolve, reject) => {
      const req = request(
        {
          host: '127.0.0.1',
          port: target.port,
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
    expect(status).toBe(200);
    expect(applied).toBe(1);
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
    await Promise.all([start, stopped]);
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
    const untrusted = createBackchannelBridge(config, admission, address.port);
    await untrusted.start();
    const untrustedAddress = untrusted.server.address();
    if (!untrustedAddress || typeof untrustedAddress === 'string') throw new Error();
    expect(await call(untrustedAddress.port)).toBe(400);
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
