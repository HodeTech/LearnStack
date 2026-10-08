// Production-build companion: a disposable configuration, real TLS and stock bypass.
import assert from 'node:assert/strict';
import { execFileSync, spawn } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import {
  copyFileSync,
  mkdirSync,
  mkdtempSync,
  readFileSync,
  rmSync,
  symlinkSync,
  writeFileSync,
} from 'node:fs';
import { request as httpRequest } from 'node:http';
import { request as httpsRequest } from 'node:https';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

const appRoot = fileURLToPath(new URL('../', import.meta.url));
const fixtureRoot = mkdtempSync(join(tmpdir(), 'learnstack-production-ingress-'));
const app = join(fixtureRoot, 'frontend/apps/web');
const secret = randomBytes(32).toString('base64url');
const children = [];
let logs = '';
mkdirSync(join(app, 'scripts'), { recursive: true });
writeFileSync(
  join(fixtureRoot, 'tls.cnf'),
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
    join(fixtureRoot, 'tls.cnf'),
    '-keyout',
    join(fixtureRoot, 'key.pem'),
    '-out',
    join(fixtureRoot, 'cert.pem'),
  ],
  { stdio: 'ignore' },
);
const certificate = readFileSync(join(fixtureRoot, 'cert.pem'));
writeFileSync(
  join(fixtureRoot, '.env'),
  [
    'LEARNSTACK_PUBLIC_API_ORIGIN=http://127.0.0.1:5080',
    'LEARNSTACK_PUBLIC_HOP_SECRET=' + secret,
    'LEARNSTACK_PUBLIC_TLS_CERT=cert.pem',
    'LEARNSTACK_PUBLIC_TLS_KEY=key.pem',
    '',
  ].join('\n'),
  { mode: 0o600 },
);
for (const name of ['.next', '.server', 'node_modules', 'src']) {
  symlinkSync(join(appRoot, name), join(app, name), 'dir');
}
for (const name of ['package.json', 'next.config.ts', 'tsconfig.json']) {
  copyFileSync(join(appRoot, name), join(app, name));
}
copyFileSync(join(appRoot, 'scripts/public-server.mjs'), join(app, 'scripts/public-server.mjs'));

function start(argv) {
  const env = { ...process.env, NODE_ENV: 'production' };
  for (const key of Object.keys(env)) {
    if (key.startsWith('LEARNSTACK_PUBLIC_')) delete env[key];
  }
  const child = spawn(process.execPath, argv, { cwd: app, env, stdio: ['ignore', 'pipe', 'pipe'] });
  children.push(child);
  for (const stream of [child.stdout, child.stderr]) {
    stream.on('data', (chunk) => {
      logs = (logs + chunk.toString()).slice(-65536);
    });
  }
  return child;
}

function call(tls, port, path, headers = {}) {
  return new Promise((resolve, reject) => {
    const send = tls ? httpsRequest : httpRequest;
    const outgoing = send(
      {
        hostname: '127.0.0.1',
        port,
        path,
        headers,
        ...(tls ? { ca: certificate, servername: 'localhost' } : {}),
        timeout: 2000,
      },
      (response) => {
        let body = '';
        response.on('data', (chunk) => {
          body += chunk.toString();
        });
        response.on('end', () =>
          resolve({ status: response.statusCode, headers: response.headers, body }),
        );
      },
    );
    outgoing.on('timeout', () => outgoing.destroy(new Error('Readiness timeout')));
    outgoing.on('error', reject);
    outgoing.end();
  });
}

async function ready(child, tls, port) {
  for (let i = 0; i < 200; i++) {
    if (child.exitCode !== null) throw new Error('Fixture startup failed');
    try {
      const response = await call(tls, port, '/api/healthz');
      if (response.status === 200 && JSON.parse(response.body).status === 'healthy') return;
    } catch {
      /* Readiness may race preparation; no application retry policy here. */
    }
    await new Promise((resolve) => setTimeout(resolve, 100));
  }
  throw new Error('Fixture did not become ready');
}

try {
  const native = start([join(app, 'scripts/public-server.mjs')]);
  await ready(native, true, 3000);
  const positive = await call(true, 3000, '/en/courses?next=https%3A%2F%2Fevil.example', {
    Host: 'tenant.example:3000',
    'X-LearnStack-Ingress-Provenance': 'forged',
    'X-Forwarded-For': 'attacker',
    'X-Middleware-Subrequest': 'middleware:middleware:middleware',
  });
  assert.equal(positive.status, 503); // Verified ingress; entry is delivered in Step 3.
  assert.equal(positive.headers['cache-control'], 'no-store');
  for (const query of [
    '?',
    '?x=%20',
    '?x=~',
    '?x=%2f',
    '?x',
    '?x=%41',
    '?_rsc=abc',
    '?x=1&x=2',
    '?x=%20&_rsc=abc',
    '?next=https://evil.example',
  ]) {
    const response = await call(true, 3000, '/en/courses' + query, {
      Host: 'tenant.example:3000',
    });
    assert.equal(response.status, 503, 'Valid raw query must survive Next processing: ' + query);
    assert.equal(response.headers['cache-control'], 'no-store');
  }
  const ambiguous = await call(true, 3000, '/en//courses?next=https://evil.example', {
    Host: 'unknown.example:3000',
  });
  assert.equal(ambiguous.status, 404);
  assert.equal(ambiguous.headers['cache-control'], 'no-store');
  assert.equal(ambiguous.headers.location, undefined);
  const stock = start([
    join(appRoot, 'node_modules/next/dist/bin/next'),
    'start',
    '--hostname',
    '127.0.0.1',
    '--port',
    '3011',
  ]);
  await ready(stock, false, 3011);
  const bypass = await call(false, 3011, '/en/courses', {
    Host: 'tenant.example:3000',
    'X-LearnStack-Ingress-Provenance': 'forged',
    'X-LearnStack-Host': 'tenant.example',
    'X-LearnStack-Visitor-Address': '1.2.3.4',
    'X-Middleware-Subrequest': 'middleware:middleware:middleware:middleware:middleware',
    'X-Middleware-Subrequest-Id': 'attacker',
  });
  assert.equal(bypass.status, 404);
  for (const response of [positive, bypass]) {
    assert.equal(JSON.stringify(response).includes(secret), false, 'Secret containment failed');
    assert.equal(
      JSON.stringify(response).includes('x-learnstack-'),
      false,
      'Carrier containment failed',
    );
  }
  assert.equal(logs.includes(secret), false, 'Secret entered diagnostics');
  assert.equal(logs.includes('v1.'), false, 'Provenance entered diagnostics');
  console.warn('Production ingress: trusted TLS readiness, sanitation and stock bypass passed.');
} finally {
  for (const child of children) {
    if (child.exitCode === null) {
      child.kill('SIGTERM');
      await new Promise((resolve) => child.once('exit', resolve));
    }
  }
  rmSync(fixtureRoot, { recursive: true, force: true });
}
