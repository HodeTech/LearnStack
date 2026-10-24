// Private fixture IPC, never an application endpoint or a retained raw artifact.
import assert from 'node:assert/strict';
import { randomBytes } from 'node:crypto';
import { mkdtempSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { startBffProof } from './bff-proof-host.mjs';
import { createFixtureOwner, createPrivateScanner } from './fixture-support.mjs';
import { stopTestChild } from './stop-test-child.mjs';

const owner = createFixtureOwner();
owner.install({ control: process.stdin, event: 'end' });
const root = owner.ownRoot(mkdtempSync(join(tmpdir(), 'learnstack-bff-session-')));
const deadline = setTimeout(() => {
  void owner.cancel();
}, 240_000);
deadline.unref();
let stage = 'input';
function lines() {
  let buffered = '';
  const queued = [];
  let ended = false;
  let failure;
  let wake;
  const changed = () => wake?.();
  process.stdin.setEncoding('utf8');
  // Flow independently of the command iterator: compile/build/request awaits
  // must never postpone the parent's EOF lifetime signal.
  process.stdin.on('data', (chunk) => {
    try {
      buffered += chunk;
      assert.ok(Buffer.byteLength(buffered) <= 65536, 'bounded private input');
      while (buffered.includes('\n')) {
        const end = buffered.indexOf('\n');
        queued.push(JSON.parse(buffered.slice(0, end)));
        buffered = buffered.slice(end + 1);
        assert.ok(queued.length <= 2, 'one private command at a time');
      }
    } catch {
      failure = new Error('Invalid bounded private input');
      void owner.cancel();
    }
    changed();
  });
  process.stdin.once('end', () => {
    ended = true;
    if (buffered) failure = new Error('Incomplete private input');
    changed();
  });
  process.stdin.once('error', () => {
    failure = new Error('Private input failed');
    changed();
    void owner.cancel();
  });
  return (async function* () {
    while (true) {
      if (failure) throw failure;
      if (queued.length) yield queued.shift();
      else if (ended) return;
      else
        await new Promise((resolve) => {
          wake = resolve;
        });
    }
  })();
}
const send = (value) =>
  new Promise((resolve, reject) => {
    process.stdout.write(JSON.stringify(value) + '\n', (error) =>
      error ? reject(error) : resolve(),
    );
  });
try {
  const shutdownCounter = process.argv[2] === '--without-successful-native-stop';
  const heldBuild = process.argv[2] === '--hold-build';
  assert.ok(
    process.argv.length === 2 || (process.argv.length === 3 && (shutdownCounter || heldBuild)),
  );
  const input = lines();
  const { value: config } = await input.next();
  assert.ok(config && config.protocol === 1 && config.mode === 'real');
  assert.equal(
    Object.keys(config).sort().join(','),
    'apiOrigin,authority,clientSecret,issuer,mode,protocol,secret,sites',
  );
  const issuer = new URL(config.issuer);
  const api = new URL(config.apiOrigin);
  stage = 'input-issuer';
  assert.ok(
    issuer.protocol === 'https:' &&
      ['127.0.0.1', 'localhost'].includes(issuer.hostname) &&
      issuer.pathname === '/realms/learnstack' &&
      !issuer.search &&
      !issuer.hash &&
      !issuer.username &&
      !issuer.password,
  );
  stage = 'input-api';
  assert.ok(
    api.protocol === 'http:' &&
      api.origin === config.apiOrigin &&
      api.hostname === '127.0.0.1' &&
      api.pathname === '/' &&
      !api.search &&
      !api.hash &&
      !api.username &&
      !api.password,
  );
  stage = 'input-secrets';
  assert.match(config.secret, /^[a-z0-9]{48,64}$/);
  assert.ok(
    typeof config.clientSecret === 'string' &&
      config.clientSecret.length > 0 &&
      config.clientSecret.length <= 256 &&
      !/[\r\n]/.test(config.clientSecret),
  );
  assert.ok(
    typeof config.authority === 'string' &&
      config.authority.length < 8192 &&
      config.authority.startsWith('-----BEGIN CERTIFICATE-----'),
  );
  stage = 'input-sites';
  assert.ok(Array.isArray(config.sites) && config.sites.length === 2);
  for (const site of config.sites) {
    assert.equal(Object.keys(site).sort().join(','), 'displayName,host,locale');
    assert.match(site.host, /^[a-z0-9-]+(?:\.[a-z0-9-]+)+:3000$/);
    assert.ok(
      typeof site.displayName === 'string' &&
        site.displayName.length > 0 &&
        site.displayName.length <= 256,
    );
    assert.ok(['en', 'tr', 'tr-TR'].includes(site.locale));
  }
  assert.notEqual(config.sites[0].host, config.sites[1].host);
  stage = 'setup';
  const privateValues = [config.secret, config.clientSecret];
  const host = await startBffProof({
    owner,
    root,
    privateValues,
    secret: config.secret,
    marker: randomBytes(32).toString('base64url'),
    uiMode: true,
    sites: config.sites,
    external: config,
    mutation: shutdownCounter ? 'shutdown' : heldBuild ? 'held-build' : undefined,
    mark: (value) => {
      stage = value;
      void send({
        protocol: 1,
        phase: value === 'held-build' ? 'held-build' : 'progress',
        stage: value,
      }).catch(() => owner.cancel());
    },
  });
  await send({ protocol: 1, phase: 'ready' });
  let finished = false;
  let count = 0;
  for await (const command of input) {
    stage = 'command';
    assert.ok(++count <= 128 && command && command.protocol === 1);
    if (command.action === 'finish') {
      assert.equal(Object.keys(command).sort().join(','), 'action,protocol');
      finished = true;
      break;
    }
    assert.equal(
      Object.keys(command).sort().join(','),
      'action,body,cookie,host,method,path,protocol',
    );
    assert.equal(command.action, 'request');
    const site = config.sites.find((site) => site.host === command.host);
    assert.ok(site);
    assert.ok(['GET', 'HEAD', 'POST'].includes(command.method));
    assert.ok(
      typeof command.path === 'string' &&
        command.path.length <= 16384 &&
        !/[\r\n#]/.test(command.path),
    );
    const path = new URL(command.path, 'https://' + site.host);
    assert.equal(path.origin, 'https://' + site.host);
    assert.ok(
      [
        '/studio',
        '/portal',
        '/',
        '/api/auth/csrf',
        '/api/auth/session',
        '/api/auth/signin',
        '/api/auth/signin/keycloak',
        '/api/auth/callback/keycloak',
        '/api/auth/signout',
        '/api/auth/extend',
      ].includes(path.pathname),
    );
    assert.ok(
      typeof command.cookie === 'string' &&
        command.cookie.length <= 16384 &&
        !/[\r\n]/.test(command.cookie),
    );
    assert.ok(typeof command.body === 'string' && Buffer.byteLength(command.body) <= 16384);
    for (const key of ['code', 'state', 'error_description']) {
      const value = path.searchParams.get(key);
      if (value) privateValues.push(value);
    }
    stage = 'request';
    let response;
    try {
      response = await host.get(command.path, command.cookie, {
        method: command.method,
        body: command.body || undefined,
        headers: {
          host: site.host,
          accept: 'text/html',
          ...(command.method === 'GET' && ['/studio', '/portal'].includes(path.pathname)
            ? { 'sec-fetch-dest': 'document' }
            : {}),
          ...(command.method === 'POST'
            ? {
                origin: 'https://' + site.host,
                'content-type': 'application/x-www-form-urlencoded',
                'content-length': Buffer.byteLength(command.body).toString(),
              }
            : {}),
        },
      });
    } catch (error) {
      stage =
        error?.message === 'owned request deadline'
          ? 'request-deadline'
          : error?.code === 'ECONNREFUSED'
            ? 'request-connect'
            : [
                  'CERT_HAS_EXPIRED',
                  'DEPTH_ZERO_SELF_SIGNED_CERT',
                  'UNABLE_TO_VERIFY_LEAF_SIGNATURE',
                  'ERR_TLS_CERT_ALTNAME_INVALID',
                ].includes(error?.code)
              ? 'request-tls'
              : 'request-socket';
      throw new Error('Closed BFF fixture transport refusal');
    }
    // Private redirects/cookies return only to the owning C# process. No raw HTML,
    // token, provider diagnostics or request headers become test result artifacts.
    stage = 'response-json';
    const data =
      response.status !== 303 &&
      command.method !== 'HEAD' &&
      response.headers['content-type']?.includes('application/json')
        ? JSON.parse(response.body)
        : null;
    await send({
      protocol: 1,
      phase: 'response',
      status: response.status,
      cookies: response.headers['set-cookie'] ?? [],
      location: response.headers.location ?? null,
      cache: response.headers['cache-control'] ?? null,
      data,
      rendered: response.body.includes(site.displayName),
      privateFree: !createPrivateScanner(privateValues).contains(response.body),
    });
  }
  assert.ok(finished, 'explicit successful protocol completion');
  stage = 'cleanup';
  const stopped = await stopTestChild(host.server.process);
  await owner.dispose();
  stage = 'native-stop';
  const native = await host.server.completed;
  assert.equal(stopped.forced, false);
  assert.equal(native.code, 0);
  assert.equal(native.signal, null);
  assert.equal(
    host.scanners.some((scanner) => scanner.leaked),
    false,
  );
  clearTimeout(deadline);
  await send({ protocol: 1, phase: 'complete' });
} catch {
  let cleanup = true;
  try {
    await owner.dispose();
  } catch {
    cleanup = false;
  }
  clearTimeout(deadline);
  process.stdin.destroy();
  process.stderr.write('BFF session fixture failed: ' + stage + '\n');
  if (!cleanup) process.stderr.write('BFF session cleanup failed.\n');
  process.exitCode = 1;
}
