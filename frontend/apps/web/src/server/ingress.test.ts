// @vitest-environment node
import { execFileSync } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { createServer, request } from 'node:https';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { connect } from 'node:tls';

import { afterAll, describe, expect, it } from 'vitest';

import {
  admitIncomingRequest,
  canonicalAddress,
  INGRESS_HEADER,
  loadLocalEnvironment,
  mintProvenance,
  normalizeHost,
  parseLocalEnvironment,
  publicServerConfiguration,
  refuseIngress,
  validTarget,
  verifyProvenance,
  verifyNextProvenance,
} from './ingress';

const secret = randomBytes(32).toString('base64url');
const context = {
  host: 'tenant.example:3000',
  peer: '127.0.0.1',
  method: 'GET',
  target: '/en/courses?next=https%3A%2F%2Fevil.example',
} as const;

describe('captured ingress provenance', () => {
  it.each([
    ['127.0.0.1', '127.0.0.1'],
    ['::ffff:127.0.0.1', '127.0.0.1'],
    ['0:0:0:0:0:ffff:7f00:1', '127.0.0.1'],
    ['2001:0DB8::0001', '2001:db8::1'],
    ['::1', '::1'],
    ['127.000.0.1', null],
    ['127.1', null],
    ['2130706433', null],
    ['2001:db8::1%eth0', null],
    ['[::1]', null],
    ['1.2.3.4:80', null],
    ['', null],
    [' 127.0.0.1', null],
    ['127.0.0.1, 1.2.3.4', null],
  ])('canonicalizes only an IP literal: %s', (input, output) => {
    expect(canonicalAddress(input ?? '')).toBe(output);
  });

  it.each([
    ['Tenant.Example.:3000', 'tenant.example:3000'],
    ['localhost', 'localhost'],
    ['xn--bcher-kva.example', 'xn--bcher-kva.example'],
    ['[2001:db8::1]:3000', '[2001:db8::1]:3000'],
    ['@tenant.example', null],
    ['tenant.example/evil', null],
    ['tenant.example,evil', null],
    ['tenant.example:0', null],
    ['tenant.example:65536', null],
    [' tenant.example', null],
    ['tenant..example', null],
    ['tenant.example:03000', null],
    ['tenant.example?', null],
    ['-tenant.example', null],
    ['tenant.example\\evil', null],
  ])('validates Host syntax without resolving tenancy: %s', (input, output) => {
    expect(normalizeHost(input ?? '')).toBe(output);
  });

  it.each([
    '/en/courses',
    '/en/courses?next=https%3A%2F%2Fevil.example&x=%2F',
    '/en/courses?literal=%252f',
    '/en/courses?locale=tr',
  ])('keeps a valid raw target intact: %s', (target) => expect(validTarget(target)).toBe(true));

  it.each([
    '//evil.example/en',
    '/en/../courses',
    '/en/%2e%2e/courses',
    '/en/%2fadmin',
    '/en/%5cadmin',
    '/en/%0dadmin',
    '/en/%',
    '/en\\courses',
    '/en/courses#x',
    '/en/./courses',
    '/'.repeat(8193),
    'https://tenant.example/en',
    '/en/ x',
  ])('refuses URL normalization/authority ambiguities: %s', (target) => {
    expect(validTarget(target)).toBe(false);
  });

  it('binds all four inputs and accepts a frozen valid control', () => {
    const envelope = mintProvenance(context, secret);
    expect(verifyProvenance(envelope, secret, context)).toEqual(context);
    expect(Object.isFrozen(verifyProvenance(envelope, secret))).toBe(true);
    expect(verifyProvenance(envelope, secret, { ...context, method: 'POST' })).toBeNull();
    expect(verifyProvenance(envelope, secret, { ...context, target: '/tr/courses' })).toBeNull();
    expect(verifyProvenance(envelope, 'x'.repeat(32), context)).toBeNull();
    const forged = envelope.replace(
      envelope.split('.')[1] ?? '',
      Buffer.from(JSON.stringify(Object.values({ ...context, peer: '1.2.3.4' }))).toString(
        'base64url',
      ),
    );
    expect(verifyProvenance(forged, secret, context)).toBeNull();
  });

  it('refuses missing, repeated, oversized and noncanonical envelopes', () => {
    const envelope = mintProvenance(context, secret);
    for (const value of [
      null,
      'plain-context',
      envelope + ', ' + envelope,
      envelope + '=',
      'v2' + envelope.slice(2),
      'x'.repeat(12289),
    ]) {
      expect(verifyProvenance(value, secret, context)).toBeNull();
    }
    expect(verifyProvenance(envelope, undefined)).toBeNull();
    expect(verifyProvenance(envelope, '')).toBeNull();
  });
});

describe('pinned Next target projection', () => {
  it.each([
    ['?', ''],
    ['?x=%20', '?x=+'],
    ['?x=~', '?x=%7E'],
    ['?x=%2f', '?x=%2F'],
    ['?x', '?x='],
    ['?x=%41', '?x=A'],
    ['?_rsc=abc', ''],
    ['?x=1&x=2', '?x=1&x=2'],
    ['?x=%20&_rsc=abc', '?x=+'],
  ])('accepts Next processing and preserves raw bytes: %s', (raw, next) => {
    const captured = { ...context, target: '/en/courses' + raw };
    const envelope = mintProvenance(captured, secret);
    expect(
      verifyNextProvenance(envelope, secret, {
        method: 'GET',
        target: '/en/courses' + next,
      }),
    ).toEqual(captured);
    expect(
      verifyNextProvenance(envelope, secret, {
        method: 'POST',
        target: '/en/courses' + next,
      }),
    ).toBeNull();
    expect(
      verifyNextProvenance(envelope, secret, {
        method: 'GET',
        target: '/tr/courses' + next,
      }),
    ).toBeNull();
    expect(
      verifyNextProvenance(envelope, secret, {
        method: 'GET',
        target: '/en/courses?x=substituted',
      }),
    ).toBeNull();
  });

  it('rejects an interior repeated slash before Next can redirect it', () => {
    expect(validTarget('/en//courses?next=https://evil.example')).toBe(false);
    expect(validTarget('/en/courses?next=https://evil.example')).toBe(true);
  });
});

describe('private local configuration', () => {
  it('reads literal values without executing or expanding them', () => {
    expect(
      parseLocalEnvironment("A='$(touch /tmp/not-executed)'\nB=" + '$' + '{A}\nC="value"\n'),
    ).toEqual({ A: '$(touch /tmp/not-executed)', B: '$' + '{A}', C: 'value' });
    expect(() => parseLocalEnvironment('A=1\nA=2')).toThrow('Invalid local environment');
    expect(() => parseLocalEnvironment('export A=1')).toThrow();
    expect(() => parseLocalEnvironment("A='unclosed")).toThrow();
  });

  const directory = mkdtempSync(join(tmpdir(), 'learnstack-private-env-'));
  afterAll(() => rmSync(directory, { recursive: true, force: true }));
  it('loads one source and refuses mismatched process/projection credentials', () => {
    writeFileSync(join(directory, '.env'), 'LEARNSTACK_PUBLIC_HOP_SECRET=' + secret);
    expect(loadLocalEnvironment(directory, {}).LEARNSTACK_PUBLIC_HOP_SECRET).toBe(secret);
    expect(() =>
      loadLocalEnvironment(directory, { LEARNSTACK_PUBLIC_HOP_SECRET: 'other' }),
    ).toThrow('Conflicting public server configuration');
  });

  const configuration = {
    LEARNSTACK_PUBLIC_API_ORIGIN: 'http://127.0.0.1:5080',
    LEARNSTACK_PUBLIC_HOP_SECRET: secret,
    LEARNSTACK_PUBLIC_TLS_CERT: '.data/tls/public.pem',
    LEARNSTACK_PUBLIC_TLS_KEY: '.data/tls/public-key.pem',
  };
  it('requires a complete private loopback renderer configuration', () => {
    expect(publicServerConfiguration(configuration, directory)).toMatchObject({
      apiOrigin: 'http://127.0.0.1:5080',
      secret,
      certificate: join(directory, '.data/tls/public.pem'),
    });
    expect(() => publicServerConfiguration({}, directory)).toThrow();
    for (const origin of [
      'https://evil.example',
      'http://localhost:5080',
      'http://127.0.0.1:5080/',
      'http://u:p@127.0.0.1:5080',
      'http://127.0.0.1:5080/escape',
      'http://127.0.0.1:5080?next=evil',
    ]) {
      expect(() =>
        publicServerConfiguration(
          { ...configuration, LEARNSTACK_PUBLIC_API_ORIGIN: origin },
          directory,
        ),
      ).toThrow();
    }
    expect(() =>
      publicServerConfiguration(
        { ...configuration, LEARNSTACK_PUBLIC_HOP_SECRET: 'short' },
        directory,
      ),
    ).toThrow();
  });
});

describe('real TLS socket boundary', () => {
  const directory = mkdtempSync(join(tmpdir(), 'learnstack-ingress-tls-'));
  writeFileSync(
    join(directory, 'certificate.cnf'),
    '[req]\ndistinguished_name=dn\n' +
      'x509_extensions=ext\nprompt=no\n[dn]\nCN=localhost\n[ext]\n' +
      'subjectAltName=DNS:localhost\nbasicConstraints=critical,CA:TRUE\n',
  );
  execFileSync(
    'openssl',
    [
      'req',
      '-x509',
      '-newkey',
      'rsa:2048',
      '-nodes',
      '-days',
      '1',
      '-config',
      join(directory, 'certificate.cnf'),
      '-keyout',
      join(directory, 'key.pem'),
      '-out',
      join(directory, 'cert.pem'),
    ],
    { stdio: 'ignore' },
  );
  const certificate = readFileSync(join(directory, 'cert.pem'));
  const server = createServer(
    { key: readFileSync(join(directory, 'key.pem')), cert: certificate },
    (incoming, response) => {
      if (!admitIncomingRequest(incoming, secret)) return refuseIngress(response);
      const proof = verifyProvenance(String(incoming.headers[INGRESS_HEADER]), secret);
      expect(proof?.peer).toBe('127.0.0.1');
      expect(incoming.headers['x-forwarded-for']).toBeUndefined();
      expect(incoming.headers['x-tenant-id']).toBeUndefined();
      expect(incoming.headers['x-middleware-subrequest']).toBeUndefined();
      for (const name of [
        'x-matched-path',
        'x-now-route-matches',
        'x-next-resume-state-length',
        'next-url',
        'next-resume',
        'x-prerender-revalidate',
        'x-prerender-revalidate-if-generated',
      ])
        expect(incoming.headers[name]).toBeUndefined();
      expect(incoming.rawHeaders.join(' ')).not.toContain('attacker');
      response.writeHead(200, {
        'cache-control': 'no-store',
        'set-cookie': 'transport-control=ok; Secure; HttpOnly; SameSite=Lax',
      });
      response.end('safe');
    },
  );
  const listening = new Promise<number>((resolve) =>
    server.listen(0, '127.0.0.1', () => {
      const address = server.address();
      if (address && typeof address !== 'string') resolve(address.port);
    }),
  );
  afterAll(async () => {
    await new Promise<void>((resolve, reject) =>
      server.close((error) => (error ? reject(error) : resolve())),
    );
    rmSync(directory, { recursive: true, force: true });
  });

  it('ignores forged peer/authority and trusts only the test-owned certificate', async () => {
    const port = await listening;
    const result = await new Promise<{ status: number; body: string; headers: object }>(
      (resolve, reject) => {
        const outgoing = request(
          {
            hostname: '127.0.0.1',
            port,
            servername: 'localhost',
            ca: certificate,
            path: '/en/courses',
            headers: {
              Host: 'tenant.example:3000',
              'X-Forwarded-For': 'attacker',
              'X-Tenant-Id': 'attacker',
              'X-Middleware-Subrequest': 'attacker',
              'X-Matched-Path': 'attacker',
              'X-Now-Route-Matches': 'attacker',
              'X-Next-Resume-State-Length': 'attacker',
              'NeXt-UrL': 'attacker',
              'Next-Resume': 'attacker',
              'X-Prerender-Revalidate': 'attacker',
              'X-Prerender-Revalidate-If-Generated': 'attacker',
              [INGRESS_HEADER]: 'attacker',
            },
          },
          (response) => {
            let body = '';
            response.on('data', (chunk: Buffer) => {
              body += chunk.toString();
            });
            response.on('end', () =>
              resolve({
                status: response.statusCode ?? 0,
                body,
                headers: response.headers,
              }),
            );
          },
        );
        outgoing.on('error', reject);
        outgoing.end();
      },
    );
    expect(result).toEqual({
      status: 200,
      body: 'safe',
      headers: expect.objectContaining({
        'cache-control': 'no-store',
        'set-cookie': [expect.stringContaining('Secure')],
      }),
    });
    expect(JSON.stringify(result)).not.toContain(secret);
    expect(JSON.stringify(result)).not.toContain(INGRESS_HEADER);
    // Certificate verification is on; the same endpoint without the test root fails.
    await expect(
      new Promise<void>((resolve, reject) => {
        const outgoing = request({ hostname: '127.0.0.1', port, servername: 'localhost' }, () =>
          resolve(),
        );
        outgoing.on('error', reject);
        outgoing.end();
      }),
    ).rejects.toThrow();
  });

  it.each([
    'Host: tenant.example\r\nHost: other.example',
    'Host: bad@host',
    'X-Tenant-Id: attacker',
  ])('refuses missing/malformed/duplicate Host: %s', async (hostLines) => {
    const port = await listening;
    const response = await new Promise<string>((resolve, reject) => {
      const socket = connect(
        { host: '127.0.0.1', port, servername: 'localhost', ca: certificate },
        () =>
          socket.write(
            'GET /en/courses HTTP/1.1\r\n' + hostLines + '\r\nConnection: close\r\n\r\n',
          ),
      );
      let value = '';
      socket.on('data', (chunk: Buffer) => {
        value += chunk.toString();
      });
      socket.on('end', () => resolve(value));
      socket.on('error', reject);
    });
    expect(response).toMatch(/^HTTP\/1.1 (?:400|404)/);
    expect(response).not.toContain(secret);
    expect(response).not.toContain(INGRESS_HEADER);
  });
});
