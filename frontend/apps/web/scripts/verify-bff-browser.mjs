// Narrow actual HTTPS browser proof with fake grants/API. Real OIDC/API has a separate owner.
import assert from 'node:assert/strict';
import { createHash, randomBytes, X509Certificate } from 'node:crypto';
import { existsSync, mkdtempSync, readFileSync } from 'node:fs';
import { createServer } from 'node:https';
import { createRequire } from 'node:module';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

import { startBffProof } from './bff-proof-host.mjs';
import { createFixtureOwner, fixtureEnvironment } from './fixture-support.mjs';

const owner = createFixtureOwner();
owner.install();
const root = owner.ownRoot(mkdtempSync(join(tmpdir(), 'learnstack-bff-browser-')));
const source = fileURLToPath(new URL('../', import.meta.url));
const secret = randomBytes(32).toString('base64url');
const marker = randomBytes(32).toString('base64url');
const privateValues = [secret, marker];
const counter = process.argv[2] === '--without-history';
let stage = 'input';
let browser;
let launched;
let version;
let naturalRestores = 0;
let restoresVeiled = true;
let scanners = [];
let counterProbe;
let counterWitness;
let authStatus = 0;
let callbackStatus = 0;
let authorizeCalls = 0;
let logoutRoutes = 0;
const deadline = setTimeout(() => {
  void owner.cancel();
}, 180_000);
deadline.unref();
async function cleanup() {
  const failures = [];
  for (const operation of [() => browser?.close(), () => launched?.close(), () => owner.dispose()])
    try {
      await operation();
    } catch {
      failures.push(true);
    }
  assert.equal(failures.length, 0, 'actual browser/native cleanup');
}
try {
  assert.ok(process.argv.length === 2 || (process.argv.length === 3 && counter));
  let data = '';
  process.stdin.setEncoding('utf8');
  for await (const chunk of process.stdin) {
    data += chunk;
    assert.ok(Buffer.byteLength(data) <= 65536);
  }
  const input = JSON.parse(data);
  assert.equal(Object.keys(input).sort().join(','), 'protocol,sites');
  assert.equal(input.protocol, 1);
  assert.ok(Array.isArray(input.sites) && input.sites.length === 2);
  for (const site of input.sites) {
    assert.equal(Object.keys(site).sort().join(','), 'displayName,host,locale');
    assert.match(site.host, /^[a-z0-9-]+(?:\.[a-z0-9-]+)+:3000$/);
    assert.ok(
      typeof site.displayName === 'string' &&
        site.displayName.length > 0 &&
        site.displayName.length < 256,
    );
    assert.ok(['en', 'tr', 'tr-TR'].includes(site.locale));
  }
  assert.notEqual(input.sites[0].host, input.sites[1].host);
  // Redirect targets are not a second Playwright routing boundary. Use an
  // actually owned HTTPS endpoint for the fake provider's logout document.
  const logoutServer = owner.ownServer(
    createServer((_request, response) => {
      ++logoutRoutes;
      response.writeHead(200, { 'content-type': 'text/html', 'cache-control': 'no-store' });
      response.end('<!doctype html><title>Owned provider sentinel</title>');
    }),
  );
  await new Promise((resolve) => logoutServer.listen(0, '127.0.0.1', resolve));
  const logoutTarget = 'https://localhost:' + logoutServer.address().port + '/logout';
  const host = await startBffProof({
    owner,
    root,
    privateValues,
    secret,
    marker,
    uiMode: true,
    sites: input.sites,
    fakeLogout: logoutTarget,
    clockBase: Date.now(),
    mutation: counter ? 'history' : undefined,
    mark: (value) => {
      stage = value;
    },
  });
  scanners = host.scanners;
  logoutServer.setSecureContext({
    key: readFileSync(join(root, 'key.pem')),
    cert: readFileSync(join(root, 'cert.pem')),
  });
  stage = 'browser-launch';
  const candidates =
    process.platform === 'darwin'
      ? [
          '/Applications/Brave Browser.app/Contents/MacOS/Brave Browser',
          '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome',
        ]
      : [
          '/usr/bin/google-chrome',
          '/usr/bin/google-chrome-stable',
          '/usr/bin/chromium',
          '/usr/bin/chromium-browser',
        ];
  const executablePath = candidates.find(existsSync);
  assert.ok(executablePath, 'stock Chromium is required, never skipped');
  const require = createRequire(join(source, 'package.json'));
  process.env.DEBUG = '-*';
  process.env.PWDEBUG = '0';
  const { chromium } = require('playwright');
  const fingerprint = createHash('sha256')
    .update(
      new X509Certificate(readFileSync(join(root, 'cert.pem'))).publicKey.export({
        type: 'spki',
        format: 'der',
      }),
    )
    .digest('base64');
  launched = await chromium.launchServer({
    executablePath,
    headless: true,
    chromiumSandbox: true,
    env: fixtureEnvironment({ root }),
    ignoreDefaultArgs: ['--disable-back-forward-cache'],
    args: [
      '--disable-background-networking',
      '--disable-component-update',
      '--disable-sync',
      '--disable-breakpad',
      '--no-proxy-server',
      '--host-resolver-rules=' +
        input.sites.map((site) => 'MAP ' + site.host.split(':')[0] + ' 127.0.0.1').join(','),
      '--ignore-certificate-errors-spki-list=' + fingerprint,
    ],
  });
  owner.ownChild(launched.process());
  browser = await chromium.connect(launched.wsEndpoint());
  version = browser.version();
  assert.match(version, /^\d+(?:\.\d+){1,3}$/);
  const context = await browser.newContext(); // No ignoreHTTPSErrors or manual production-cookie injection.
  const documents = [];
  const headerChecks = [];
  const cookiesSeen = [];
  const origin = (site) => 'https://' + site.host;
  context.on('request', (request) => {
    if (request.resourceType() === 'document') documents.push(request.url());
  });
  context.on('response', (response) => {
    if (input.sites.some((site) => new URL(response.url()).origin === origin(site)))
      headerChecks.push(
        (async () => {
          try {
            const cookies = (await response.headersArray()).filter(
              (header) => header.name.toLowerCase() === 'set-cookie',
            );
            return (
              cookies.every((cookie) => !/;\s*domain=/i.test(cookie.value)) &&
              (await response.request().allHeaders()).authorization === undefined
            );
          } catch {
            return false;
          }
        })(),
      );
    const path = new URL(response.url()).pathname;
    if (path === '/api/auth/signin/keycloak') authStatus = response.status();
    if (path === '/api/auth/callback/keycloak') callbackStatus = response.status();
  });
  await context.exposeBinding('ownedRestored', (_source, value) => {
    restoresVeiled &&= value === 'hidden';
    ++naturalRestores;
  });
  await context.addInitScript(() => {
    window.addEventListener('pageshow', (event) => {
      if (event.isTrusted && event.persisted && document.querySelector('[data-bff-protected]'))
        void window.ownedRestored(document.documentElement.style.visibility);
    });
  });
  let currentSite;
  let wrongHostChecked = false;
  // Only the external stub provider is intercepted. Native TLS/cookie/Origin,
  // callbacks, middleware, UI and the browser's storage/send rules remain real.
  let routeFailed = false;
  await context.route('https://identity.example.test/authorize', async (route) => {
    try {
      ++authorizeCalls;
      const cookies = await context.cookies(origin(currentSite));
      const transaction = cookies.find((cookie) => cookie.name.startsWith('__Host-ls-login-'));
      assert.ok(transaction);
      const state = transaction.name.slice('__Host-ls-login-'.length);
      const code = randomBytes(32).toString('base64url');
      if (!wrongHostChecked) {
        wrongHostChecked = true;
        const other = input.sites.find((site) => site.host !== currentSite.host);
        const negative = await context.newPage();
        const refusal = await negative.goto(
          origin(other) + '/api/auth/callback/keycloak?state=' + state + '&code=' + code,
        );
        assert.equal(refusal.status(), 400);
        assert.equal(
          (await context.cookies(origin(currentSite))).some(
            (cookie) => cookie.name === transaction.name,
          ),
          true,
        );
        assert.ok(
          (await Promise.all(headerChecks)).every(Boolean),
          'headers captured before closing the negative page',
        );
        await negative.close();
      }
      await route.fulfill({
        status: 302,
        headers: {
          location:
            origin(currentSite) + '/api/auth/callback/keycloak?state=' + state + '&code=' + code,
        },
      });
    } catch {
      routeFailed = true;
      await route.abort().catch(() => {});
    }
  });
  const pages = [];
  const documentCount = (site) =>
    documents.filter((value) => {
      const url = new URL(value);
      return url.origin === origin(site) && url.pathname === '/studio';
    }).length;
  for (const site of input.sites) {
    stage = 'browser-anonymous';
    const page = await context.newPage();
    pages.push(page);
    const anonymous = await page.goto(origin(site) + '/api/auth/session');
    assert.equal(anonymous.status(), 200);
    assert.equal((await anonymous.json()).state, 'anonymous', 'other host cookies are not sent');
    currentSite = site;
    stage = 'browser-signin-document';
    await page.goto(origin(site) + '/api/auth/signin?returnTo=%2Fstudio');
    const postedRequest = page.waitForRequest(
      (request) =>
        request.method() === 'POST' &&
        new URL(request.url()).pathname === '/api/auth/signin/keycloak',
    );
    stage = 'browser-submit';
    await page.getByRole('button').click();
    const posted = await postedRequest;
    const submittedOrigin = await posted.headerValue('origin');
    if (submittedOrigin !== origin(site)) {
      stage =
        submittedOrigin === 'null'
          ? 'browser-origin-null'
          : submittedOrigin === null
            ? 'browser-origin-absent'
            : 'browser-origin-other';
      throw new Error('Browser form must retain its exact same-origin proof');
    }
    stage = 'browser-callback';
    await page.waitForURL(origin(site) + '/studio');
    stage = 'browser-storage';
    await page.locator('[data-bff-protected]').waitFor();
    await page.waitForLoadState('networkidle');
    assert.ok((await page.textContent('body')).includes(site.displayName));
    const cookies = await context.cookies(origin(site));
    const owned = cookies.filter((cookie) => cookie.name.startsWith('__Host-ls-'));
    assert.ok(owned.some((cookie) => cookie.name === '__Host-ls-session'));
    for (const cookie of owned) {
      assert.equal(cookie.domain, site.host.split(':')[0]);
      assert.equal(cookie.path, '/');
      assert.equal(cookie.httpOnly, true);
      assert.equal(cookie.secure, true);
      assert.equal(cookie.sameSite, 'Lax');
    }
    assert.equal((await page.evaluate(() => document.cookie)).includes('__Host-ls-'), false);
    assert.equal((await page.content()).includes(marker), false);
    assert.equal((await page.content()).includes(secret), false);
    cookiesSeen.push(owned.find((cookie) => cookie.name === '__Host-ls-session').value);
    // Actual keyboard activation reaches the existing focusable main target.
    await page.keyboard.press('Tab');
    assert.equal(
      await page.evaluate(() => document.activeElement?.getAttribute('href')),
      '#main-content',
    );
    await page.keyboard.press('Enter');
    assert.equal(await page.evaluate(() => document.activeElement?.id), 'main-content');
    stage = 'browser-extension';
    await host.advance(60000);
    const before = (await host.advance(0)).refreshes;
    const extend = page.locator('button[type=button]');
    await extend.click();
    await page
      .getByText(
        site.locale === 'en'
          ? 'Your session end time has been updated.'
          : 'Oturumunuzun bitiş saati güncellendi.',
      )
      .waitFor();
    assert.equal((await host.advance(0)).refreshes, before + 1);
  }
  assert.notEqual(
    cookiesSeen[0],
    cookiesSeen[1],
    'two real host jars contain independent opaque handles',
  );
  const [page, otherPage] = pages;
  stage = 'browser-logout-submit';
  const first = input.sites[0];
  const outgoingLogout = page.waitForRequest(
    (request) =>
      request.method() === 'POST' && new URL(request.url()).pathname === '/api/auth/signout',
  );
  const logoutResponse = page.waitForResponse(
    (response) =>
      response.request().method() === 'POST' &&
      new URL(response.url()).pathname === '/api/auth/signout',
  );
  await page.locator('form[action="/api/auth/signout"] button').click();
  stage = 'browser-logout-origin';
  const logoutOrigin = await (await outgoingLogout).headerValue('origin');
  if (logoutOrigin !== origin(first)) {
    stage = logoutOrigin === 'null' ? 'browser-logout-origin-null' : 'browser-logout-origin-other';
    throw new Error('Logout form must retain its exact same-origin proof');
  }
  stage = 'browser-logout-response';
  const observedLogout = await logoutResponse;
  const logoutStatus = observedLogout.status();
  if (logoutStatus !== 303) {
    stage =
      'browser-logout-status-' +
      (new Set([400, 403, 404, 429, 503]).has(logoutStatus) ? logoutStatus : 'other');
    throw new Error('Logout form must revoke and navigate');
  }
  stage = 'browser-logout-location';
  assert.equal(await observedLogout.headerValue('location'), logoutTarget);
  stage = 'browser-logout-navigation';
  await page.waitForURL(
    (url) =>
      url.origin === new URL(logoutTarget).origin &&
      url.pathname === '/logout' &&
      url.search === '',
  );
  assert.ok(logoutRoutes > 0, 'the browser actually fetched the owned provider logout document');
  stage = 'browser-logout-cookie-header';
  const deletion = (await observedLogout.headersArray()).find(
    (header) =>
      header.name.toLowerCase() === 'set-cookie' && header.value.startsWith('__Host-ls-session=;'),
  );
  assert.ok(deletion && /max-age=0/i.test(deletion.value));
  assert.equal(await observedLogout.headerValue('referrer-policy'), 'no-referrer');
  stage = 'browser-logout-cookie';
  assert.equal(
    (await context.cookies(origin(first))).some((cookie) => cookie.name === '__Host-ls-session'),
    false,
  );
  const beforeBack = documentCount(first);
  counterWitness = 'natural_back';
  counterProbe = async () =>
    documentCount(first) === beforeBack &&
    (await page.evaluate(
      () =>
        location.pathname === '/studio' &&
        document.documentElement.style.visibility === 'hidden' &&
        !!document.querySelector('[data-bff-protected]'),
    ));
  stage = 'browser-back-revalidation';
  await page.goBack();
  await page.waitForURL(
    (url) => url.origin === origin(first) && url.pathname === '/api/auth/signin',
    { timeout: 10_000 },
  );
  assert.ok(
    documentCount(first) > beforeBack,
    'actual browser back navigation obtains fresh native admission after logout',
  );
  assert.equal(await page.locator('[data-bff-protected]').count(), 0);
  // Keep a second already-rendered document alive while another tab logs it out.
  // An explicit persisted PageTransitionEvent reaches the restoration branch even
  // when this stock browser invalidates no-store BFCache entries after cookie changes.
  stage = 'browser-history-setup';
  const second = input.sites[1];
  const control = await context.newPage();
  await control.goto(origin(second) + '/portal');
  await control.locator('form[action="/api/auth/signout"] button').click();
  await control.waitForURL(
    (url) => url.origin === new URL(logoutTarget).origin && url.pathname === '/logout',
  );
  assert.equal(await otherPage.locator('[data-bff-protected]').count(), 1);
  assert.equal(
    (await context.cookies(origin(second))).some((cookie) => cookie.name === '__Host-ls-session'),
    false,
  );
  const beforeRestore = documentCount(second);
  counterWitness = 'persisted_event';
  counterProbe = async () =>
    documentCount(second) === beforeRestore &&
    (await otherPage.evaluate(
      () =>
        location.pathname === '/studio' &&
        document.documentElement.style.visibility === 'hidden' &&
        !!document.querySelector('[data-bff-protected]'),
    ));
  stage = 'browser-history-revalidation';
  await otherPage.evaluate(() =>
    window.dispatchEvent(new PageTransitionEvent('pageshow', { persisted: true })),
  );
  await otherPage.waitForURL(
    (url) => url.origin === origin(second) && url.pathname === '/api/auth/signin',
    { timeout: 10_000 },
  );
  assert.equal(await otherPage.locator('[data-bff-protected]').count(), 0);
  stage = 'browser-headers';
  assert.equal(restoresVeiled, true, 'naturally restored protected documents are already veiled');
  assert.equal(routeFailed, false, 'fake authorization control completed');
  assert.ok(headerChecks.length > 0 && (await Promise.all(headerChecks)).every(Boolean));
  stage = 'cleanup';
  await cleanup();
  assert.equal(
    host.scanners.some((scanner) => scanner.leaked),
    false,
  );
  clearTimeout(deadline);
  process.stdout.write(
    JSON.stringify({
      protocol: 1,
      phase: 'browser_result',
      outcome: 'passed',
      hosts: 2,
      version,
      naturalRestores,
    }) + '\n',
  );
} catch {
  let expectedCounter = false;
  if (
    counter &&
    ['browser-history-revalidation', 'browser-back-revalidation'].includes(stage) &&
    counterProbe
  )
    try {
      expectedCounter = await counterProbe();
    } catch {
      /* unrelated failure is not the mutant witness */
    }
  let clean = true;
  try {
    await cleanup();
    clean &&= !scanners.some((scanner) => scanner.leaked);
  } catch {
    clean = false;
  }
  clearTimeout(deadline);
  if (expectedCounter && clean) {
    process.stdout.write(
      JSON.stringify({
        protocol: 1,
        phase: 'browser_result',
        outcome: 'rejected_history',
        witness: counterWitness,
        hosts: 2,
        version,
      }) + '\n',
    );
  } else {
    if (stage === 'browser-callback') {
      const codes = new Set([0, 200, 303, 400, 403, 404, 429, 503]);
      const status = authorizeCalls ? callbackStatus : authStatus;
      stage =
        (authorizeCalls ? 'browser-callback-' : 'browser-authorize-') +
        (codes.has(status) ? status : 'other');
    }
    process.stderr.write('BFF browser fixture failed: ' + (clean ? stage : 'cleanup') + '\n');
  }
  process.exitCode = 1;
}
