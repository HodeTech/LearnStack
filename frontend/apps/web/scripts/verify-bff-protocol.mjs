// Minimal production-mode Next protocol harness. --ui uses current product middleware/UI.
// Fake grant/account capabilities; the real provider/API chain has a separate owner.
import assert from 'node:assert/strict';
import { randomBytes } from 'node:crypto';
import { mkdtempSync, readFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { startBffProof, jar } from './bff-proof-host.mjs';
import { assertBffPrivateOutput, bffProofCopy } from './bff-proof-support.mjs';
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
let releaseHeldRender;
const pendingOwnedRequests = [];
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
  const { app, server, advance, renderControl, get, scanners, cookieCanaries } =
    await startBffProof({
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
  async function settledPages() {
    const until = Date.now() + 5000;
    let state;
    do {
      state = await renderControl('status');
      if (state.snapshots.active === 0) return;
      assert.ok(Date.now() < until, 'actual page handler/body cleanup settles');
      await new Promise((resolve) => setTimeout(resolve, 10));
    } while (state.snapshots.active !== 0);
  }
  async function selectEntry(callback, host, destination) {
    stage = 'entry-cookie';
    assert.equal(callback.headers.location, 'https://' + host + '/api/auth/entry');
    assert.ok(callback.headers['set-cookie'].some((value) => value.startsWith('__Host-ls-entry=')));
    assert.equal(
      callback.headers['set-cookie'].some((value) => value.startsWith('__Host-ls-session=')),
      false,
    );
    const entryCookie = jar(callback);
    assert.equal(
      JSON.parse((await get('/api/auth/session', entryCookie, { headers: { host } })).body).state,
      'anonymous',
    );
    stage = 'entry-confirmation';
    const response = await get('/api/auth/entry', entryCookie, { headers: { host } });
    assert.equal(response.status, 200);
    assert.equal(response.headers['cache-control'], 'no-store');
    assert.equal(response.headers['set-cookie'], undefined);
    let csrfToken;
    let tenantId;
    if (uiMode) {
      const locale = host.startsWith('other.') ? 'tr' : 'en';
      assert.ok(response.body.includes(bffProofCopy(locale, 'entry.confirm')));
      const form =
        /<form action="\/api\/auth\/entry\/select" method="post">([\s\S]*?)<\/form>/.exec(
          response.body,
        );
      assert.ok(form, 'real entry selection form');
      csrfToken = /name="csrfToken" value="([A-Za-z0-9_-]{43})"/.exec(form[1])?.[1];
      tenantId = /name="tenantId" value="([0-9a-f-]{36})"/.exec(form[1])?.[1];
    } else {
      const entry = JSON.parse(response.body);
      assert.equal(entry.state, 'entry');
      assert.equal(entry.identityState, 'active');
      assert.equal(entry.contexts.items.length, 1);
      csrfToken = entry.csrfToken;
      tenantId = entry.contexts.items[0].tenantId;
    }
    assert.match(csrfToken, /^[A-Za-z0-9_-]{43}$/);
    assert.match(tenantId, /^[0-9a-f-]{36}$/);
    privateValues.push(csrfToken);
    stage = 'entry-selection';
    const selected = await get('/api/auth/entry/select', entryCookie, {
      method: 'POST',
      headers: {
        host,
        origin: 'https://' + host,
        'content-type': 'application/x-www-form-urlencoded',
      },
      body: new URLSearchParams({ csrfToken, tenantId }).toString(),
    });
    assert.equal(selected.status, 303);
    assert.equal(selected.headers.location, 'https://' + host + destination);
    assert.ok(
      selected.headers['set-cookie'].some((value) => value.startsWith('__Host-ls-session=')),
    );
    assert.ok(
      selected.headers['set-cookie'].some((value) => value.startsWith('__Host-ls-entry=;')),
    );
    return jar(selected);
  }
  if (uiMode) {
    stage = 'ui-signin';
    for (const [host, locale] of [
      ['institution.example.test:3000', 'en'],
      ['other.example.test:3000', 'tr'],
    ]) {
      const page = await get('/api/auth/signin?returnTo=%2Fstudio', undefined, {
        headers: { host, Accept: 'text/html' },
      });
      stage = host.startsWith('other.') ? 'ui-signin-tr-status' : 'ui-signin-en-status';
      if (page.status !== 200) process.stderr.write(JSON.stringify({ status: page.status }) + '\n');
      assert.equal(page.status, 200);
      stage = host.startsWith('other.') ? 'ui-signin-tr-render' : 'ui-signin-en-render';
      assert.ok(page.body.includes(bffProofCopy(locale, 'signin.continue')));
      assert.ok(page.body.includes('<html lang="' + locale + '"'));
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
  const beforeCallback = await advance(0);
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
  assert.equal(
    (await advance(0)).accounts,
    beforeCallback.accounts,
    'callback never invokes AccountSite',
  );
  const active = await selectEntry(callback, 'institution.example.test:3000', '/studio');
  function assertFlightEnvelope(response) {
    assert.equal(response.status, 200);
    assert.match(response.headers['content-type'], /text\/x-component/);
    assert.equal(/(?:^|\n)[0-9a-f]+:E\{/.test(response.body), false, 'Flight has no error frame');
    assert.equal(response.body.includes(marker), false);
  }
  function assertProtectedFlight(response) {
    assertFlightEnvelope(response);
    assert.ok(response.body.includes('Institution'), 'Flight contains the admitted display name');
    if (uiMode) {
      assert.ok(response.body.includes('auth-shell'), 'Flight contains the protected shell');
      assert.ok(response.body.includes(bffProofCopy('en', 'shell.studio')));
      assert.ok(response.body.includes(bffProofCopy('en', 'shell.account')));
      assert.ok(
        response.body.includes('"state":"authenticated"'),
        'Flight contains the session projection',
      );
    } else assert.ok(response.body.includes('authenticated'));
  }
  stage = 'rsc';
  const beforeDocument = await advance(0);
  const document = await get('/studio', active);
  assert.equal(document.status, 200);
  if (uiMode) {
    assert.ok(document.body.includes('<h1>' + bffProofCopy('en', 'shell.studio') + '</h1>'));
    assert.ok(document.body.includes(bffProofCopy('en', 'shell.account')));
    assert.ok(document.body.includes('bff-history'));
  } else assert.match(document.body, /<h1>authenticated<\/h1>/);
  assert.equal(document.body.includes(marker), false);
  const afterDocument = await advance(0);
  for (const kind of ['getOwnCapabilities', 'getOwnProfile'])
    assert.equal(
      afterDocument.administrations[kind],
      (beforeDocument.administrations[kind] ?? 0) + 1,
      'native preparation performs one common read per document',
    );
  if (uiMode) {
    const ordinaryQuery = await get('/studio?_rsc=document', active, {
      headers: { Accept: 'text/html', 'Sec-Fetch-Dest': 'document' },
    });
    assert.equal(ordinaryQuery.status, 404, 'document query cannot assert native Flight intent');
    const repeatedQuery = await get('/studio?_rsc=one&_rsc=two', active, {
      headers: { RSC: '1', Accept: '*/*', 'Sec-Fetch-Dest': 'empty' },
    });
    assert.equal(repeatedQuery.status, 404, 'native Flight still rejects duplicate query tokens');
    await settledPages();
    for (const [path, operation, visible] of [
      ['/studio/members', 'listMemberships', 'Fixture member'],
      ['/studio/members/019a154b-0000-7000-8000-000000000012', 'getMembership', 'Fixture member'],
      ['/studio/roles', 'listRoles', '019a154b-0000-7000-8000-000000000013'],
      ['/studio/roles/019a154b-0000-7000-8000-000000000013', 'getRole', 'role-grants-title'],
      ['/portal/profile', 'getOwnProfile', 'Fixture member'],
    ]) {
      const phase = {
        listMemberships: 'ui-members',
        getMembership: 'ui-member',
        listRoles: 'ui-roles',
        getRole: 'ui-role',
        getOwnProfile: 'ui-profile',
      }[operation];
      stage = phase + '-request';
      const beforePage = await advance(0);
      const page = await get(path, active);
      stage = phase + '-status';
      assert.equal(page.status, 200);
      stage = phase + '-cache';
      // Pinned Next's development renderer adds must-revalidate; both forms prohibit storage.
      assert.equal(
        page.headers['cache-control'],
        devMode ? 'no-store, must-revalidate' : 'no-store',
      );
      stage = phase + '-content';
      assert.ok(page.body.includes(visible));
      stage = phase + '-privacy';
      assert.equal(
        page.body.includes('019a154b-0000-7000-8000-000000000011'),
        false,
        'API user identity is absent from protected views',
      );
      stage = phase + '-counts';
      const afterPage = await advance(0);
      assert.equal(
        afterPage.administrations[operation],
        (beforePage.administrations[operation] ?? 0) + 1,
      );
      assert.equal(
        afterPage.administrations.getOwnCapabilities,
        beforePage.administrations.getOwnCapabilities + 1,
      );
      stage = phase + '-cleanup';
      await settledPages();
    }
  }
  const before = await advance(901000);
  const prior = JSON.parse((await get('/api/auth/session', active)).body);
  const flight = await get('/studio?_rsc=probe', active, {
    headers: { RSC: '1', Accept: '*/*', 'Sec-Fetch-Dest': 'empty' },
  });
  assertProtectedFlight(flight);
  const after = await advance(0);
  assert.equal(after.refreshes, before.refreshes + 1, 'actual RSC refreshes expired access');
  assert.equal(after.accounts, before.accounts + 1, 'actual RSC validates AccountSite');
  for (const kind of ['getOwnCapabilities', 'getOwnProfile'])
    assert.equal(
      after.administrations[kind],
      before.administrations[kind] + 1,
      'actual navigation receives one prepared common snapshot',
    );
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
  // Pinned Next omits dynamic seed data for speculative prefetch. Actual
  // navigation above must contain the full admitted shell and session.
  assertFlightEnvelope(prefetched);
  assert.equal(
    prefetched.body.includes(projection.csrfToken),
    false,
    'prefetch discloses no session proof',
  );
  assert.equal(
    prefetched.body.includes('Fixture member'),
    false,
    'prefetch discloses no own profile',
  );
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
  assert.deepEqual(
    passive.administrations,
    after.administrations,
    'prefetch and HEAD do not load private page data',
  );
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
    const activeB = await selectEntry(callbackB, 'other.example.test:3000', '/portal');
    const portal = await get('/portal', activeB, {
      headers: { host: 'other.example.test:3000' },
    });
    assert.equal(portal.status, 200);
    assert.match(portal.body, /<html lang="tr"/);
    assert.ok(portal.body.includes(bffProofCopy('tr', 'shell.portal')));
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
  let heldRender;
  if (!uiMode) {
    stage = 'registered-render';
    await renderControl('arm');
    releaseHeldRender = () => renderControl('release');
    heldRender = get('/studio', active, { allowAborted: true });
    pendingOwnedRequests.push(heldRender);
    void heldRender.catch(() => {});
    const until = Date.now() + 5000;
    let retained;
    do {
      retained = await renderControl('status');
      if (retained.held === 1) break;
      assert.ok(Date.now() < until, 'actual Next reaches the registered render gate');
      await new Promise((resolve) => setTimeout(resolve, 10));
    } while (retained.held !== 1);
    assert.equal(retained.snapshots.active, 1);
    assert.ok(retained.snapshots.bytes > 0);
  }
  stage = 'logout';
  const loggingOut = get('/api/auth/signout', active, {
    method: 'POST',
    headers: {
      origin: 'https://institution.example.test:3000',
      'content-type': 'application/x-www-form-urlencoded',
    },
    body: 'csrfToken=' + projection.csrfToken,
  });
  pendingOwnedRequests.push(loggingOut);
  void loggingOut.catch(() => {});
  if (heldRender) {
    stage = 'registered-render-invalidation';
    const until = Date.now() + 5000;
    while (JSON.parse((await get('/api/auth/session', active)).body).state !== 'anonymous') {
      assert.ok(Date.now() < until, 'real signout invalidates the held snapshot owner');
      await new Promise((resolve) => setTimeout(resolve, 10));
    }
    const retained = await renderControl('status');
    assert.equal(retained.held, 1);
    assert.equal(
      retained.snapshots.active,
      1,
      'logical signout and response close cannot release registered framework work',
    );
    await renderControl('release');
    releaseHeldRender = undefined;
    const withheld = await heldRender;
    assert.equal(withheld.body.includes('Institution'), false);
    assert.equal(withheld.body.includes('authenticated'), false);
    assert.ok(withheld.aborted || withheld.status === 503, 'late private response is withheld');
    const cleanedUntil = Date.now() + 5000;
    while ((await renderControl('status')).snapshots.active !== 0) {
      assert.ok(Date.now() < cleanedUntil, 'registered render cleanup releases its actual slot');
      await new Promise((resolve) => setTimeout(resolve, 10));
    }
  }
  const logout = await loggingOut;
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
  stage = 'privacy';
  assertBffPrivateOutput(scanners);
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
    if (releaseHeldRender) await releaseHeldRender();
  } catch {
    clean = false;
  }
  try {
    await owner.dispose();
  } catch {
    clean = false;
  }
  await Promise.allSettled(pendingOwnedRequests);
  clearTimeout(deadline);
  process.stderr.write('BFF native Next protocol failed: ' + stage + '\n');
  if (!clean) process.stderr.write('BFF protocol cleanup failed.\n');
  process.exitCode = 1;
}
