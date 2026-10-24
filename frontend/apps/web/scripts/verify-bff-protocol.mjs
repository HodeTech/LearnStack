// Minimal production-mode Next protocol harness. --ui uses current product middleware/UI.
// Fake grant/account capabilities; the real provider/API chain has a separate owner.
import assert from 'node:assert/strict';
import { randomBytes } from 'node:crypto';
import { mkdtempSync, readFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { startBffProof, jar } from './bff-proof-host.mjs';
import { createFixtureOwner } from './fixture-support.mjs';
import { stopTestChild } from './stop-test-child.mjs';
const owner = createFixtureOwner();
owner.install();
const root = owner.ownRoot(mkdtempSync(join(tmpdir(), 'learnstack-bff-protocol-')));
const secret = randomBytes(32).toString('base64url');
const marker = randomBytes(32).toString('base64url');
const callbackCode = randomBytes(32).toString('base64url');
const errorCanary = randomBytes(32).toString('base64url');
const privateValues = [secret, marker, callbackCode, errorCanary];
const deadline = setTimeout(() => {
  void owner.cancel();
}, 120_000);
deadline.unref();
const devMode = ['--dev', '--without-native-diagnostics'].includes(process.argv[2]);
const uiMode = devMode || process.argv[2] === '--ui';
let stage = 'setup';
try {
  assert.ok(
    process.argv.length === 2 ||
      (process.argv.length === 3 &&
        [
          '--without-native-activity',
          '--ui',
          '--dev',
          '--without-native-diagnostics',
          '--without-successful-native-stop',
        ].includes(process.argv[2])),
    'closed proof mode',
  );
  const { app, server, advance, get, scanners, cookieCanaries } = await startBffProof({
    owner,
    root,
    privateValues,
    secret,
    marker,
    fakeLogout:
      'https://identity.example.test/realms/learnstack/protocol/openid-connect/logout?id_token_hint=' +
      marker,
    devMode,
    uiMode,
    mutation:
      process.argv[2] === '--without-native-activity'
        ? 'activity'
        : process.argv[2] === '--without-native-diagnostics'
          ? 'diagnostics'
          : process.argv[2] === '--without-successful-native-stop'
            ? 'shutdown'
            : undefined,
    mark: (value) => {
      stage = value;
    },
  });
  if (uiMode) {
    stage = 'ui-signin';
    for (const [host, label] of [
      ['institution.example.test:3000', 'Continue to sign in'],
      ['other.example.test:3000', 'Giriş yapmaya devam edin'],
    ]) {
      const page = await get('/api/auth/signin?returnTo=%2Fstudio', undefined, {
        headers: { host, Accept: 'text/html' },
      });
      stage = host.startsWith('other.') ? 'ui-signin-tr-status' : 'ui-signin-en-status';
      if (page.status !== 200) process.stderr.write(JSON.stringify({ status: page.status }) + '\n');
      assert.equal(page.status, 200);
      stage = host.startsWith('other.') ? 'ui-signin-tr-render' : 'ui-signin-en-render';
      assert.ok(page.body.includes(label));
      assert.ok(page.body.includes('name="csrfToken"'));
      assert.equal(page.body.includes(marker), false);
      assert.equal(page.headers['cache-control'], 'no-store');
      const head = await get('/api/auth/signin', undefined, { method: 'HEAD', headers: { host } });
      stage = 'ui-signin-head';
      assert.equal(head.status, 200);
      assert.equal(head.body, '');
    }
    stage = 'ui-unknown-host';
    const unknown = await get('/api/auth/signin', undefined, {
      headers: { host: 'unknown.example.test:3000' },
    });
    assert.equal(unknown.status, 404);
    assert.equal(unknown.headers['set-cookie'], undefined);
  }
  stage = 'csrf';
  const csrf = await get('/api/auth/csrf');
  assert.equal(csrf.status, 200);
  const token = JSON.parse(csrf.body).csrfToken;
  stage = 'signin';
  const signIn = await get('/api/auth/signin/keycloak', jar(csrf), {
    method: 'POST',
    headers: {
      origin: 'https://institution.example.test:3000',
      'content-type': 'application/x-www-form-urlencoded',
    },
    body: 'csrfToken=' + token + '&returnTo=%2Fstudio',
  });
  assert.equal(signIn.status, 303);
  assert.equal(signIn.headers['set-cookie'].length, 2);
  const transaction = signIn.headers['set-cookie'].find((value) =>
    value.startsWith('__Host-ls-login-'),
  );
  const state = transaction.slice('__Host-ls-login-'.length).split('=')[0];
  privateValues.push(state);
  stage = 'callback';
  const callback = await get(
    '/api/auth/callback/keycloak?state=' + state + '&code=' + callbackCode,
    jar(signIn),
  );
  if (callback.status !== 303) {
    let reason = 'unknown';
    try {
      const code = JSON.parse(callback.body).code;
      if (
        [
          'configuration',
          'cancelled',
          'revoked',
          'response',
          'identity',
          'transaction',
          'expired',
          'invalid_request',
        ].includes(code)
      )
        reason = code;
    } catch {
      /* A framework error is not a closed BFF response. */
    }
    process.stderr.write(JSON.stringify({ status: callback.status, reason }) + '\n');
  }
  assert.equal(callback.status, 303);
  const active = jar(callback);
  stage = 'rsc';
  const document = await get('/studio', active);
  assert.equal(document.status, 200);
  if (uiMode) {
    assert.match(document.body, /<h1>Studio<\/h1>/);
    assert.ok(document.body.includes('Account access'));
    assert.ok(document.body.includes('bff-history'));
  } else assert.match(document.body, /<h1>authenticated<\/h1>/);
  assert.equal(document.body.includes(marker), false);
  const before = await advance(901000);
  const prior = JSON.parse((await get('/api/auth/session', active)).body);
  const flight = await get('/studio?_rsc=probe', active, {
    headers: { RSC: '1', Accept: '*/*', 'Sec-Fetch-Dest': 'empty' },
  });
  assert.equal(flight.status, 200);
  assert.match(flight.headers['content-type'], /text\/x-component/);
  assert.equal(flight.body.includes(marker), false);
  const after = await advance(0);
  assert.equal(after.refreshes, before.refreshes + 1, 'actual RSC refreshes expired access');
  assert.equal(after.accounts, before.accounts + 1, 'actual RSC validates AccountSite');
  const session = await get('/api/auth/session', active);
  const projection = JSON.parse(session.body);
  assert.equal(projection.expiresAt, prior.expiresAt + 901000, 'RSC advances effective idle');
  assert.equal(projection.state, 'authenticated');
  assert.equal(session.body.includes(marker), false);
  stage = 'passive';
  await advance(60000);
  const prefetched = await get('/studio?_rsc=prefetch', active, {
    headers: { RSC: '1', 'Next-Router-Prefetch': '1', Accept: '*/*', 'Sec-Fetch-Dest': 'empty' },
  });
  assert.equal(prefetched.status, 200);
  const head = await get('/studio', active, { method: 'HEAD', headers: { Accept: 'text/html' } });
  assert.equal(head.status, 200);
  assert.equal(head.body, '');
  assert.equal(
    JSON.parse((await get('/api/auth/session', active)).body).expiresAt,
    projection.expiresAt,
    'prefetch/HEAD/poll never touch idle',
  );
  const passive = await advance(0);
  assert.equal(passive.accounts, after.accounts);
  assert.equal(passive.refreshes, after.refreshes);
  if (uiMode) {
    stage = 'ui-host-fence';
    const crossHost = await get('/portal', active, {
      headers: { host: 'other.example.test:3000' },
    });
    assert.equal(crossHost.status, 303);
    assert.ok(
      crossHost.headers.location.startsWith('https://other.example.test:3000/api/auth/signin'),
    );
    const csrfB = await get('/api/auth/csrf', undefined, {
      headers: { host: 'other.example.test:3000' },
    });
    const startB = await get('/api/auth/signin/keycloak', jar(csrfB), {
      method: 'POST',
      headers: {
        host: 'other.example.test:3000',
        origin: 'https://other.example.test:3000',
        'content-type': 'application/x-www-form-urlencoded',
      },
      body: 'csrfToken=' + JSON.parse(csrfB.body).csrfToken + '&returnTo=%2Fportal',
    });
    assert.equal(startB.status, 303);
    const stateB = startB.headers['set-cookie']
      .find((value) => value.startsWith('__Host-ls-login-'))
      .slice('__Host-ls-login-'.length)
      .split('=')[0];
    const callbackB = await get(
      '/api/auth/callback/keycloak?state=' + stateB + '&code=owned',
      jar(startB),
      { headers: { host: 'other.example.test:3000' } },
    );
    assert.equal(callbackB.status, 303);
    const portal = await get('/portal', jar(callbackB), {
      headers: { host: 'other.example.test:3000' },
    });
    assert.equal(portal.status, 200);
    assert.match(portal.body, /<html lang="tr"/);
    assert.ok(portal.body.includes('Portalınız'));
    assert.ok(portal.body.includes('Diğer kurum'));
    assert.equal(portal.body.includes(marker), false);
    stage = 'ui-extension';
    const extended = await get('/api/auth/extend', active, {
      method: 'POST',
      headers: {
        origin: 'https://institution.example.test:3000',
        'content-type': 'application/x-www-form-urlencoded',
      },
      body: 'csrfToken=' + projection.csrfToken,
    });
    assert.equal(extended.status, 200);
    assert.ok(JSON.parse(extended.body).expiresAt > projection.expiresAt);
  }
  stage = 'logout';
  const logout = await get('/api/auth/signout', active, {
    method: 'POST',
    headers: {
      origin: 'https://institution.example.test:3000',
      'content-type': 'application/x-www-form-urlencoded',
    },
    body: 'csrfToken=' + projection.csrfToken,
  });
  assert.equal(logout.status, 303);
  assert.equal(
    logout.body.includes(marker),
    false,
    'logout credentials never enter a redirect body',
  );
  stage = 'revocation';
  const refused = await get('/studio', active);
  if (uiMode) {
    assert.equal(refused.status, 303);
    assert.ok(refused.headers.location.includes('/api/auth/signin'));
  } else assert.match(refused.body, /<h1>anonymous<\/h1>/);
  if (devMode) {
    stage = 'dev-logout-return';
    const logoutState = logout.headers['set-cookie']
      .find((value) => value.startsWith('__Host-ls-logout-'))
      .slice('__Host-ls-logout-'.length)
      .split('=')[0];
    privateValues.push(logoutState);
    const returned = await get('/?state=' + logoutState, active + '; ' + jar(logout));
    assert.equal(returned.status, 303);
    const failedCallback = await get(
      '/api/auth/callback/keycloak?state=' +
        randomBytes(32).toString('base64url') +
        '&error=access_denied&error_description=' +
        errorCanary,
      active,
    );
    assert.equal(failedCallback.status, 400);
    assert.equal(failedCallback.body.includes(errorCanary), false);
    await advance(0); // Flush the pinned real reporter; no raw artifact leaves this owned root.
    const settlement = await stopTestChild(server.process);
    const stopped = await server.completed;
    assert.equal(settlement.forced, false);
    assert.equal(stopped.code, 0, 'development owner exits naturally before artifact inspection');
    assert.equal(stopped.signal, null);
    stage = 'dev-diagnostics';
    let trace = '';
    try {
      trace = readFileSync(join(app, '.next/trace'), 'utf8');
    } catch (error) {
      if (error?.code !== 'ENOENT') throw error;
    }
    for (const canary of [callbackCode, errorCanary, state, logoutState, ...cookieCanaries]) {
      stage = 'dev-trace';
      assert.equal(trace.includes(canary), false, 'no auth URL value in persisted trace');
      stage = 'dev-console';
      assert.equal(
        scanners.some((scanner) => scanner.tail.includes(canary)),
        false,
        'no auth URL value in console output',
      );
    }
  }
  stage = 'native-stop';
  const settlement = await stopTestChild(server.process);
  const stopped = await server.completed;
  assert.equal(settlement.forced, false);
  assert.equal(stopped.code, 0);
  assert.equal(stopped.signal, null);
  stage = 'cleanup';
  await owner.dispose();
  assert.equal(
    scanners.some((scanner) => scanner.leaked),
    false,
    'private output scanner',
  );
  clearTimeout(deadline);
  process.stdout.write(
    devMode
      ? 'BFF development diagnostics: passed\n'
      : uiMode
        ? 'BFF production UI: passed\n'
        : 'BFF native Next protocol: passed\n',
  );
} catch {
  let clean = true;
  try {
    await owner.dispose();
  } catch {
    clean = false;
  }
  clearTimeout(deadline);
  process.stderr.write('BFF native Next protocol failed: ' + stage + '\n');
  if (!clean) process.stderr.write('BFF protocol cleanup failed.\n');
  process.exitCode = 1;
}
