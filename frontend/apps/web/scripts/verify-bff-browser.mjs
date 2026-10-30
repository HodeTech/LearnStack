// Narrow actual HTTPS browser proof with fake grants/API. Real OIDC/API has a separate owner.
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { createHash, randomBytes, X509Certificate } from 'node:crypto';
import { existsSync, mkdirSync, mkdtempSync, readFileSync } from 'node:fs';
import { createServer } from 'node:https';
import { createRequire } from 'node:module';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

import { readBrowserDevToolsPort, waitForBrowserDevTools } from './admission-browser.mjs';
import { startBffProof } from './bff-proof-host.mjs';
import {
  assertBffCookieAttributes,
  assertBffPrivateOutput,
  bffDevToolsFailureStage,
  bffProofCopy,
  createBffCookieScanner,
  rememberBffCookies,
  withProofWaiters,
} from './bff-proof-support.mjs';
import {
  createFixtureOwner,
  createPrivateScanner,
  fixtureEnvironment,
} from './fixture-support.mjs';
import { stopTestChild } from './stop-test-child.mjs';

const owner = createFixtureOwner();
owner.install({ control: process.stdin, event: 'end' });
const root = owner.ownRoot(mkdtempSync(join(tmpdir(), 'learnstack-bff-browser-')));
const source = fileURLToPath(new URL('../', import.meta.url));
const secret = randomBytes(32).toString('base64url');
const marker = randomBytes(32).toString('base64url');
const privateValues = [secret, marker];
const counter = process.argv[2] === '--without-history';
const pageCounter = process.argv[2] === '--without-ready-pages';
let stage = 'input';
let browser;
let browserProcess;
let nativeServer;
let version;
let naturalRestores = 0;
let restoresVeiled = true;
let scanners = [];
let counterProbe;
let counterWitness;
let counterPage;
let counterPageResponse;
let administrationPages = 0;
let noScriptHosts = 0;
let disabledPreferenceChoices = 0;
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
  for (const operation of [
    () => browser?.close(),
    async () => {
      for (const child of [browserProcess, nativeServer?.process].filter(Boolean)) {
        const stopped = await stopTestChild(child);
        if (stopped.forced) failures.push(true);
      }
    },
    () => owner.dispose(),
  ])
    try {
      await operation();
    } catch {
      failures.push(true);
    }
  assert.equal(failures.length, 0, 'actual browser/native cleanup');
  if (nativeServer) {
    const stopped = await nativeServer.completed;
    assert.equal(stopped.code, 0);
    assert.equal(stopped.signal, null);
  }
}
// A single bounded frame initializes the proof. Keep the pipe actively draining
// afterwards: EOF is the parent's private lifetime signal, not input framing.
function configuration() {
  return new Promise((resolve, reject) => {
    let buffered = '';
    let accepted = false;
    process.stdin.setEncoding('utf8');
    process.stdin.on('data', (chunk) => {
      if (accepted) {
        void owner.cancel();
        return;
      }
      buffered += chunk;
      try {
        assert.ok(Buffer.byteLength(buffered) <= 65536);
        const end = buffered.indexOf('\n');
        if (end === -1) return;
        assert.equal(end, buffered.length - 1);
        const value = JSON.parse(buffered.slice(0, end));
        accepted = true;
        buffered = '';
        resolve(value);
      } catch {
        accepted = true;
        reject(new Error('Invalid private browser input'));
      }
    });
    process.stdin.once('error', () => {
      reject(new Error('Private browser input failed'));
      void owner.cancel();
    });
    process.stdin.once('end', () => {
      if (!accepted) reject(new Error('Private browser input incomplete'));
    });
  });
}
try {
  assert.ok(process.argv.length === 2 || (process.argv.length === 3 && (counter || pageCounter)));
  const input = await configuration();
  owner.assertActive();
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
  // Chromium forwards redirect-chain requests without a second Playwright route
  // interception. Both fake provider endpoints therefore use this actual owned
  // HTTPS listener, including the authorization target after the native POST 303.
  let authorize;
  const authorizePath = '/realms/learnstack/protocol/openid-connect/auth';
  const logoutPath = '/realms/learnstack/protocol/openid-connect/logout';
  const logoutServer = owner.ownServer(
    createServer((request, response) => {
      if (request.method === 'GET' && request.url === authorizePath && authorize) {
        void authorize(response);
        return;
      }
      if (request.method === 'GET' && request.url === logoutPath) {
        ++logoutRoutes;
        response.writeHead(200, { 'content-type': 'text/html', 'cache-control': 'no-store' });
        response.end('<!doctype html><title>Owned provider sentinel</title>');
        return;
      }
      response.writeHead(404, { 'cache-control': 'no-store' });
      response.end();
    }),
  );
  await new Promise((resolve) => logoutServer.listen(0, '127.0.0.1', resolve));
  const fakeIssuer = 'https://localhost:' + logoutServer.address().port + '/realms/learnstack';
  const logoutTarget = fakeIssuer + '/protocol/openid-connect/logout';
  const host = await startBffProof({
    owner,
    root,
    privateValues,
    secret,
    marker,
    uiMode: true,
    disabledOwnPreference: true,
    sites: input.sites,
    fakeIssuer,
    fakeLogout: logoutTarget,
    clockBase: Date.now(),
    mutation: counter ? 'history' : pageCounter ? 'unavailable-pages' : undefined,
    mark: (value) => {
      stage = value;
    },
  });
  scanners = host.scanners;
  nativeServer = host.server;
  logoutServer.setSecureContext({
    key: readFileSync(join(root, 'key.pem')),
    cert: readFileSync(join(root, 'cert.pem')),
  });
  stage = 'browser-executable';
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
  const profile = join(root, 'chromium-profile');
  mkdirSync(profile, { mode: 0o700 });
  // Keep SDK artifacts inside the root whose actual cleanup the fixture verifies.
  const artifacts = join(root, 'playwright-artifacts');
  mkdirSync(artifacts, { mode: 0o700 });
  owner.assertActive();
  stage = 'browser-spawn';
  browserProcess = owner.ownChild(
    spawn(
      executablePath,
      [
        '--headless=new',
        '--remote-debugging-address=127.0.0.1',
        '--remote-debugging-port=0',
        '--user-data-dir=' + profile,
        '--no-first-run',
        '--no-default-browser-check',
        '--disable-background-networking',
        '--disable-component-update',
        '--disable-sync',
        '--disable-breakpad',
        '--password-store=basic',
        '--use-mock-keychain',
        '--no-proxy-server',
        '--host-resolver-rules=' +
          input.sites.map((site) => 'MAP ' + site.host.split(':')[0] + ' 127.0.0.1').join(','),
        '--ignore-certificate-errors-spki-list=' + fingerprint,
        'about:blank',
      ],
      {
        env: fixtureEnvironment({ root }),
        detached: process.platform !== 'win32',
        stdio: ['ignore', 'pipe', 'pipe'],
      },
    ),
  );
  const browserOutput = createPrivateScanner(privateValues);
  const browserCookies = createBffCookieScanner(host.cookieCanaries);
  scanners.push(browserOutput, browserCookies);
  for (const stream of [browserProcess.stdout, browserProcess.stderr]) {
    stream.on('data', (chunk) => {
      browserOutput.push(chunk);
      browserCookies.push(chunk);
    });
    stream.once('end', () => {
      browserOutput.finish();
      browserCookies.finish();
    });
  }
  let browserFailed = false;
  browserProcess.once('error', () => {
    browserFailed = true;
  });
  const portFile = join(profile, 'DevToolsActivePort');
  stage = 'browser-devtools';
  const port = await waitForBrowserDevTools({
    assertActive: () => owner.assertActive(),
    isLive: () =>
      !browserFailed && browserProcess.exitCode === null && browserProcess.signalCode === null,
    readPort: () => readBrowserDevToolsPort(portFile),
    timeout: 10_000,
  });
  stage = 'browser-connect';
  browser = await chromium.connectOverCDP('http://127.0.0.1:' + port, {
    timeout: 10000,
    artifactsDir: artifacts,
  });
  version = browser.version();
  assert.match(version, /^\d+(?:\.\d+){1,3}$/);
  stage = 'browser-context';
  const context = await browser.newContext(); // No ignoreHTTPSErrors or manual production-cookie injection.
  const documents = [];
  const headerChecks = [];
  const cookiesSeen = [];
  const origin = (site) => 'https://' + site.host;
  function observeContext(observedContext) {
    observedContext.on('request', (request) => {
      if (request.resourceType() === 'document') documents.push(request.url());
    });
    observedContext.on('response', (response) => {
      if (input.sites.some((site) => new URL(response.url()).origin === origin(site)))
        headerChecks.push(
          (async () => {
            try {
              const cookies = (await response.headersArray()).filter(
                (header) => header.name.toLowerCase() === 'set-cookie',
              );
              rememberBffCookies(
                cookies.map((cookie) => cookie.value),
                host.cookieCanaries,
              );
              assertBffCookieAttributes(cookies.map((cookie) => cookie.value));
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
  }
  observeContext(context);
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
  let authorizingContext = context;
  let wrongHostChecked = false;
  // Only provider grant/account operations are fake. The browser fetches both
  // provider documents over owned HTTPS; native callbacks and cookies remain real.
  let routeFailed = false;
  authorize = async (response) => {
    try {
      ++authorizeCalls;
      const cookies = await authorizingContext.cookies(origin(currentSite));
      const transaction = cookies.find((cookie) => cookie.name.startsWith('__Host-ls-login-'));
      assert.ok(transaction);
      const state = transaction.name.slice('__Host-ls-login-'.length);
      const code = randomBytes(32).toString('base64url');
      if (!wrongHostChecked) {
        wrongHostChecked = true;
        const other = input.sites.find((site) => site.host !== currentSite.host);
        const negative = await authorizingContext.newPage();
        const refusal = await negative.goto(
          origin(other) + '/api/auth/callback/keycloak?state=' + state + '&code=' + code,
        );
        assert.equal(refusal.status(), 400);
        assert.equal(
          (await authorizingContext.cookies(origin(currentSite))).some(
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
      response.writeHead(302, {
        location:
          origin(currentSite) + '/api/auth/callback/keycloak?state=' + state + '&code=' + code,
        'cache-control': 'no-store',
      });
      response.end();
    } catch {
      routeFailed = true;
      response.destroy();
    }
  };
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
    stage = 'browser-submit';
    await page.getByRole('button').focus();
    const [posted] = await withProofWaiters(
      [
        page.waitForRequest(
          (request) =>
            request.method() === 'POST' &&
            new URL(request.url()).pathname === '/api/auth/signin/keycloak',
        ),
      ],
      () => page.keyboard.press('Enter'),
    );
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
    await page.waitForURL(origin(site) + '/api/auth/entry');
    stage = 'browser-entry-confirmation';
    await page.getByText(bffProofCopy(site.locale, 'entry.confirm')).waitFor();
    const entryCookies = await context.cookies(origin(site));
    assert.ok(entryCookies.some((cookie) => cookie.name === '__Host-ls-entry'));
    assert.equal(
      entryCookies.some((cookie) => cookie.name === '__Host-ls-session'),
      false,
      'callback has no tenant session',
    );
    for (const cookie of entryCookies.filter((cookie) => cookie.name !== '__Host-ls-csrf'))
      assert.equal(
        (await page.content()).includes(cookie.value),
        false,
        'entry cookie stays out of HTML',
      );
    const choice = page.locator('form[action="/api/auth/entry/select"] button');
    assert.equal(await choice.count(), 1, 'one independently discovered fixture context');
    await choice.focus();
    const [selectionRequest, selectionResponse] = await withProofWaiters(
      [
        page.waitForRequest(
          (request) =>
            request.method() === 'POST' &&
            new URL(request.url()).pathname === '/api/auth/entry/select',
        ),
        page.waitForResponse(
          (response) =>
            response.request().method() === 'POST' &&
            new URL(response.url()).pathname === '/api/auth/entry/select',
        ),
      ],
      () => page.keyboard.press('Enter'),
    );
    assert.equal(await selectionRequest.headerValue('origin'), origin(site));
    assert.equal(selectionResponse.status(), 303);
    assert.equal(await selectionResponse.headerValue('location'), origin(site) + '/studio');
    await page.waitForURL(origin(site) + '/studio');
    assert.equal(
      (await context.cookies(origin(site))).some((cookie) => cookie.name === '__Host-ls-entry'),
      false,
    );
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
    const content = await page.content();
    assert.equal(content.includes(marker), false);
    assert.equal(content.includes(secret), false);
    for (const cookie of owned.filter((cookie) => cookie.name !== '__Host-ls-csrf'))
      assert.equal(content.includes(cookie.value), false, 'opaque cookie stays out of HTML');
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
    await extend.focus();
    await page.keyboard.press('Enter');
    await page.getByText(bffProofCopy(site.locale, 'session.extended')).waitFor();
    assert.equal((await host.advance(0)).refreshes, before + 1);
    stage = 'browser-administration-pages';
    for (const [path, kind, selector] of [
      [
        '/studio/members',
        'members',
        '.auth-resource-list a[href="/studio/members/019a154b-0000-7000-8000-000000000012"]',
      ],
      [
        '/studio/members/019a154b-0000-7000-8000-000000000012',
        'member',
        'form[action="/api/studio/membership-profile"] input[name="displayName"]',
      ],
      [
        '/studio/roles',
        'roles',
        '.auth-resource-list a[href="/studio/roles/019a154b-0000-7000-8000-000000000013"]',
      ],
      [
        '/studio/roles/019a154b-0000-7000-8000-000000000013',
        'role',
        'section[aria-labelledby="role-grants-title"] > ul:not(.auth-actions) > li',
      ],
      [
        '/portal/profile',
        'profile',
        'form[action="/api/account/profile"] input[name="displayName"]',
      ],
    ]) {
      const response = await page.goto(origin(site) + path);
      counterPage = page;
      counterPageResponse = response;
      assert.equal(response.status(), 200);
      assert.equal(await response.headerValue('cache-control'), 'no-store');
      await page.locator('[data-bff-protected]').waitFor();
      assert.equal(await page.getByRole('heading', { level: 1 }).count(), 1);
      // The unavailable view deliberately retains this wrapper and h1. Neither
      // is a positive witness for the selected page's actual contents.
      assert.equal(
        await page.locator('[data-bff-page-view]').getAttribute('data-bff-page-view'),
        kind,
      );
      const specific = page.locator(selector);
      assert.equal(await specific.count(), 1);
      assert.equal(await specific.isVisible(), true);
      if (kind === 'member' || kind === 'profile')
        assert.equal(await specific.inputValue(), 'Fixture member');
      else assert.ok((await specific.textContent()).trim().length > 0);
      ++administrationPages;
      const body = await page.content();
      assert.equal(body.includes(marker), false);
      assert.equal(body.includes('019a154b-0000-7000-8000-000000000011'), false);
      assert.equal(
        await page.locator('html').getAttribute('lang'),
        site.locale === 'tr-TR' ? 'tr' : site.locale,
      );
    }
    // Preserve the exact existing GET history mutation witness below.
    await page.goto(origin(site) + '/studio');
    await page.locator('[data-bff-protected]').waitFor();
    await page.waitForLoadState('networkidle');
  }
  assert.notEqual(
    cookiesSeen[0],
    cookiesSeen[1],
    'two real host jars contain independent opaque handles',
  );
  const [page, otherPage] = pages;
  stage = 'browser-logout-submit';
  const first = input.sites[0];
  await page.locator('form[action="/api/auth/signout"] button').focus();
  const [outgoingLogout, logoutResponse] = await withProofWaiters(
    [
      page.waitForRequest(
        (request) =>
          request.method() === 'POST' && new URL(request.url()).pathname === '/api/auth/signout',
      ),
      page.waitForResponse(
        (response) =>
          response.request().method() === 'POST' &&
          new URL(response.url()).pathname === '/api/auth/signout',
      ),
    ],
    () => page.keyboard.press('Enter'),
  );
  stage = 'browser-logout-origin';
  const logoutOrigin = await outgoingLogout.headerValue('origin');
  if (logoutOrigin !== origin(first)) {
    stage = logoutOrigin === 'null' ? 'browser-logout-origin-null' : 'browser-logout-origin-other';
    throw new Error('Logout form must retain its exact same-origin proof');
  }
  stage = 'browser-logout-response';
  const observedLogout = logoutResponse;
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
      url.pathname === logoutPath &&
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
  await control.locator('form[action="/api/auth/signout"] button').focus();
  await control.keyboard.press('Enter');
  await control.waitForURL(
    (url) => url.origin === new URL(logoutTarget).origin && url.pathname === logoutPath,
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
  stage = 'browser-no-script';
  const noScript = await browser.newContext({ javaScriptEnabled: false });
  observeContext(noScript);
  authorizingContext = noScript;
  for (const site of input.sites) {
    currentSite = site;
    const nativePage = await noScript.newPage();
    stage = 'browser-no-script-anonymous';
    const anonymousNoScript = await nativePage.goto(origin(site) + '/api/auth/session');
    assert.equal((await anonymousNoScript.json()).state, 'anonymous');
    stage = 'browser-no-script-signin';
    await nativePage.goto(origin(site) + '/api/auth/signin?returnTo=%2Fportal%2Fprofile');
    await nativePage.getByRole('button').click();
    await nativePage.waitForURL(origin(site) + '/api/auth/entry');
    stage = 'browser-no-script-confirm';
    await nativePage.getByText(bffProofCopy(site.locale, 'entry.confirm')).waitFor();
    stage = 'browser-no-script-select';
    await nativePage.locator('form[action="/api/auth/entry/select"] button').click();
    await nativePage.waitForURL(origin(site) + '/portal/profile');
    stage = 'browser-no-script-controls';
    assert.equal(
      await nativePage.locator('html').getAttribute('lang'),
      site.locale === 'tr-TR' ? 'tr' : site.locale,
    );
    assert.equal(
      await nativePage.locator('[data-bff-page-view]').getAttribute('data-bff-page-view'),
      'profile',
    );
    const noScriptProfile = nativePage.locator('form[action="/api/account/profile"]');
    if ((await noScriptProfile.count()) !== 1) {
      stage = 'browser-no-script-missing-form';
      assert.fail('The no-script document needs one profile form');
    }
    if (!(await noScriptProfile.isVisible())) {
      stage =
        (await nativePage.locator('[hidden] form[action="/api/account/profile"]').count()) === 1
          ? 'browser-no-script-hidden-render'
          : 'browser-no-script-invisible-form';
      assert.fail('The no-script profile form must be visible');
    }
    // Playwright's text engine skips NOSCRIPT and its descendants. Observe the
    // actual no-script element directly, preserving visibility and exact-copy checks.
    const noScriptNotice = nativePage.locator('[data-bff-protected] noscript > p');
    await noScriptNotice.waitFor({ state: 'visible' });
    assert.equal(await noScriptNotice.count(), 1);
    assert.equal(
      await noScriptNotice.textContent(),
      bffProofCopy(site.locale, 'session.javascript_required'),
    );
    stage = 'browser-no-script-native-buttons';
    assert.equal(
      await nativePage.locator('button[type="button"]').count(),
      0,
      'no JavaScript-dependent extension control is presented as usable',
    );
    stage = 'browser-no-script-native-profile-form';
    const profileForm = nativePage.locator('form[action="/api/account/profile"]');
    assert.equal(await profileForm.count(), 1);
    await profileForm
      .locator('input[name="displayName"]')
      .fill('Saved without JavaScript at ' + site.locale);
    const firstHost = site === input.sites[0];
    if (firstHost) {
      const radios = profileForm.locator('input[type="radio"][name="preferredLocale"]');
      assert.ok((await radios.count()) >= 2);
      assert.equal(
        await profileForm.locator('input[type="radio"][name="preferredLocale"]:checked').count(),
        0,
      );
      assert.equal(await profileForm.evaluate((form) => form.checkValidity()), false);
      assert.equal(
        await profileForm.evaluate((form) => new FormData(form).has('preferredLocale')),
        false,
      );
      await profileForm.locator('input[type="radio"][name="preferredLocale"][value=""]').check();
      assert.equal(await profileForm.evaluate((form) => form.checkValidity()), true);
    } else {
      assert.equal(await profileForm.locator('select[name="preferredLocale"]').inputValue(), '');
    }
    stage = 'browser-no-script-submit';
    const [nativeWrite, nativeResult] = await withProofWaiters(
      [
        nativePage.waitForRequest(
          (request) =>
            request.method() === 'POST' &&
            new URL(request.url()).pathname === '/api/account/profile',
        ),
        nativePage.waitForResponse(
          (response) =>
            response.request().method() === 'POST' &&
            new URL(response.url()).pathname === '/api/account/profile',
        ),
      ],
      () => profileForm.getByRole('button').click(),
    );
    stage = 'browser-no-script-origin';
    assert.equal(await nativeWrite.headerValue('origin'), origin(site));
    assert.equal(new URLSearchParams(nativeWrite.postData()).get('preferredLocale'), '');
    stage = 'browser-no-script-result';
    assert.equal(nativeResult.status(), 303);
    assert.equal(await nativeResult.headerValue('location'), origin(site) + '/portal/profile');
    stage = 'browser-no-script-cache';
    assert.equal(await nativeResult.headerValue('cache-control'), 'no-store');
    stage = 'browser-no-script-saved';
    await nativePage.goto(origin(site) + '/portal/profile');
    assert.equal(
      await nativePage.locator('input[name="displayName"]').inputValue(),
      'Saved without JavaScript at ' + site.locale,
    );
    assert.equal(await nativePage.locator('select[name="preferredLocale"]').inputValue(), '');
    if (firstHost) ++disabledPreferenceChoices;
    stage = 'browser-no-script-privacy';
    const noScriptCookies = await noScript.cookies(origin(site));
    assert.ok(noScriptCookies.some((cookie) => cookie.name === '__Host-ls-session'));
    for (const cookie of noScriptCookies.filter((cookie) => cookie.name.startsWith('__Host-ls-')))
      assert.equal(cookie.domain, site.host.split(':')[0]);
    const nativeBody = await nativePage.content();
    for (const cookie of noScriptCookies.filter((cookie) => cookie.name !== '__Host-ls-csrf'))
      assert.equal(nativeBody.includes(cookie.value), false);
    stage = 'browser-no-script-signout';
    await nativePage.locator('form[action="/api/auth/signout"] button').click();
    await nativePage.waitForURL(
      (url) => url.origin === new URL(logoutTarget).origin && url.pathname === logoutPath,
    );
    assert.equal(
      (await noScript.cookies(origin(site))).some((cookie) => cookie.name === '__Host-ls-session'),
      false,
    );
    const signedOut = await nativePage.goto(origin(site) + '/api/auth/session');
    assert.equal(signedOut.status(), 200);
    assert.equal((await signedOut.json()).state, 'anonymous');
    stage = 'browser-no-script-headers';
    assert.ok(
      (await Promise.all(headerChecks)).every(Boolean),
      'actual no-script response headers settle before closing their browser context',
    );
    ++noScriptHosts;
    await nativePage.close();
  }
  await noScript.close();
  authorizingContext = context;
  stage = 'browser-headers';
  assert.equal(restoresVeiled, true, 'naturally restored protected documents are already veiled');
  assert.equal(routeFailed, false, 'fake authorization control completed');
  assert.equal(
    authorizeCalls,
    input.sites.length * 2,
    'both hosts with and without JavaScript fetch the owned HTTPS authorization endpoint',
  );
  assert.ok(headerChecks.length > 0 && (await Promise.all(headerChecks)).every(Boolean));
  stage = 'cleanup';
  await cleanup();
  stage = 'privacy';
  assertBffPrivateOutput(scanners);
  clearTimeout(deadline);
  process.stdin.destroy();
  process.stdout.write(
    JSON.stringify({
      protocol: 1,
      phase: 'browser_result',
      outcome: 'passed',
      hosts: 2,
      administrationPages,
      noScriptHosts,
      disabledPreferenceChoices,
      version,
      naturalRestores,
    }) + '\n',
  );
} catch (error) {
  if (stage === 'browser-devtools') stage = bffDevToolsFailureStage(error);
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
  if (pageCounter && stage === 'browser-administration-pages')
    try {
      // Keep any failed predicate as a closed scalar stage. No DOM, exception
      // message or other private value belongs in the driver diagnostic.
      // Next's route announcer also has role=alert in an open shadow root.
      // Observe the actual failure paragraph, not that framework live region.
      const failureAlert = counterPage?.locator(
        'section[aria-labelledby="page-failure-title"] > p[role="alert"]',
      );
      const checks = [
        ['progress', administrationPages === 0],
        ['status', counterPageResponse?.status() === 200],
        ['cache', (await counterPageResponse?.headerValue('cache-control')) === 'no-store'],
        ['wrapper', (await counterPage?.locator('[data-bff-protected]').count()) === 1],
        ['heading', (await counterPage?.getByRole('heading', { level: 1 }).count()) === 1],
        [
          'view',
          (await counterPage
            ?.locator('[data-bff-page-view]')
            .getAttribute('data-bff-page-view')) === 'unavailable',
        ],
        [
          'alert',
          (await failureAlert?.count()) === 1 && (await failureAlert?.isVisible()) === true,
        ],
      ];
      const failed = checks.find(([, passed]) => !passed);
      expectedCounter = failed === undefined;
      if (failed) stage = 'browser-administration-counter-' + failed[0];
      counterWitness = 'unavailable_view';
    } catch {
      stage = 'browser-administration-counter-observer';
    }
  let clean = true;
  try {
    await cleanup();
  } catch {
    clean = false;
  }
  clearTimeout(deadline);
  process.stdin.destroy();
  if (pageCounter && expectedCounter && scanners.some((scanner) => scanner.leaked))
    stage = 'browser-administration-counter-private-output';
  if (expectedCounter && clean && !scanners.some((scanner) => scanner.leaked)) {
    process.stdout.write(
      JSON.stringify({
        protocol: 1,
        phase: 'browser_result',
        outcome: pageCounter ? 'rejected_pages' : 'rejected_history',
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
    // Preserve the original closed stage even if later actual cleanup fails.
    process.stderr.write('BFF browser fixture failed: ' + stage + '\n');
    if (!clean) process.stderr.write('BFF browser cleanup failed.\n');
  }
  process.exitCode = 1;
}
