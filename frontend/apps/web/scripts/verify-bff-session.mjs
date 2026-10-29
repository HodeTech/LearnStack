// Private fixture IPC, never an application endpoint or a retained raw artifact.
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { createHash, randomBytes, X509Certificate } from 'node:crypto';
import { existsSync, mkdirSync, mkdtempSync, readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

import {
  readBrowserDevToolsPort,
  waitForBrowserDevTools,
  withBrowserCleanup,
} from './admission-browser.mjs';
import { startBffProof } from './bff-proof-host.mjs';
import { createBffCookieScanner, withProofWaiters } from './bff-proof-support.mjs';
import {
  createFixtureOwner,
  createPrivateScanner,
  fixtureEnvironment,
} from './fixture-support.mjs';
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

// This is one fixed browser observation of the real entry endpoint, not a browser
// command proxy. Its native server, certificate, API and cookie all belong to the
// same real-provider fixture. Every child remains under the existing EOF owner.
async function observeEntryBrowser(host, config, command, privateValues) {
  const site = config.sites.find((candidate) => candidate.host === command.host);
  assert.ok(site);
  assert.match(command.subjectKey, /^[0-9a-f]{64}$/);
  assert.ok(typeof command.cookie === 'string' && command.cookie.length <= 16384);
  const origin = 'https://' + site.host;
  const entryUrl = origin + '/api/auth/entry';
  privateValues.push(command.subjectKey, config.authorityId);
  const scanner = createPrivateScanner(privateValues);
  const privateFree = (value) => {
    assert.ok(typeof value === 'string' && Buffer.byteLength(value) <= 256 * 1024);
    if (scanner.contains(value)) return false;
    // The regression is base64url disclosure: a raw substring scan alone cannot
    // detect the API's AuthorityId:SubjectKey:MembershipId inside a browser cursor.
    return [...value.matchAll(/[A-Za-z0-9_-]{32,1024}/g)].every(
      (match) => !scanner.contains(Buffer.from(match[0], 'base64url').toString('utf8')),
    );
  };
  const entryLocation = (value) => {
    const url = new URL(value, origin);
    assert.equal(url.origin, origin);
    assert.equal(url.pathname, '/api/auth/entry');
    assert.equal(url.hash, '');
    const fields = [...url.searchParams];
    assert.ok(
      fields.length === 0 ||
        (fields.length === 1 &&
          fields[0][0] === 'cursor' &&
          /^[A-Za-z0-9_-]{1,1024}$/.test(fields[0][1])),
    );
    return url.href;
  };
  const cookies = command.cookie.split('; ').map((pair) => {
    const separator = pair.indexOf('=');
    const name = pair.slice(0, separator);
    const value = pair.slice(separator + 1);
    assert.ok(['__Host-ls-csrf', '__Host-ls-entry', '__Host-ls-browser'].includes(name));
    assert.match(value, /^[A-Za-z0-9_-]{43}$/);
    if (!host.cookieCanaries.includes(value)) host.cookieCanaries.push(value);
    return { name, value, url: origin + '/', secure: true, httpOnly: true, sameSite: 'Lax' };
  });
  assert.ok(cookies.length >= 2 && cookies.length <= 3);
  assert.equal(new Set(cookies.map((cookie) => cookie.name)).size, cookies.length);
  assert.ok(cookies.some((cookie) => cookie.name === '__Host-ls-entry'));
  assert.ok(cookies.some((cookie) => cookie.name === '__Host-ls-browser'));
  stage = 'browser-entry-confirmation';
  const html = await host.get('/api/auth/entry', command.cookie, {
    headers: { host: site.host, accept: 'text/html', 'sec-fetch-dest': 'document' },
  });
  assert.equal(html.status, 200);
  assert.ok(html.headers['content-type']?.includes('text/html'));
  assert.equal(html.headers['cache-control'], 'no-store');
  assert.equal(
    [...html.body.matchAll(/<form action="\/api\/auth\/entry\/select" method="post">/g)].length,
    20,
  );
  const htmlLinks = [...html.body.matchAll(/href="(\/api\/auth\/entry\?cursor=[A-Za-z0-9_-]+)"/g)];
  assert.equal(htmlLinks.length, 1);
  entryLocation(htmlLinks[0][1]);
  const htmlPrivateFree = privateFree(html.body) && privateFree(htmlLinks[0][1]);

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
  const executable = candidates.find(existsSync);
  assert.ok(executable, 'stock Chromium is required, never skipped');
  const require = createRequire(fileURLToPath(new URL('../package.json', import.meta.url)));
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
  const profile = mkdtempSync(join(root, 'entry-browser-profile-'));
  const artifacts = join(profile, 'artifacts');
  mkdirSync(artifacts, { mode: 0o700 });
  owner.assertActive();
  stage = 'browser-spawn';
  const child = owner.ownChild(
    spawn(
      executable,
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
        '--host-resolver-rules=MAP ' + site.host.split(':')[0] + ' 127.0.0.1',
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
  const output = createPrivateScanner(privateValues);
  const cookieOutput = createBffCookieScanner(host.cookieCanaries);
  host.scanners.push(output, cookieOutput);
  for (const stream of [child.stdout, child.stderr]) {
    stream.on('data', (chunk) => {
      output.push(chunk);
      cookieOutput.push(chunk);
    });
    stream.once('end', () => {
      output.finish();
      cookieOutput.finish();
    });
  }
  let failed = false;
  child.once('error', () => {
    failed = true;
  });
  let browser;
  return withBrowserCleanup(
    async () => {
      stage = 'browser-devtools';
      const port = await waitForBrowserDevTools({
        assertActive: () => owner.assertActive(),
        isLive: () => !failed && child.exitCode === null && child.signalCode === null,
        readPort: () => readBrowserDevToolsPort(join(profile, 'DevToolsActivePort')),
        timeout: 10000,
      });
      stage = 'browser-connect';
      browser = await chromium.connectOverCDP('http://127.0.0.1:' + port, {
        timeout: 10000,
        artifactsDir: artifacts,
      });
      stage = 'browser-context';
      const context = await browser.newContext();
      context.setDefaultTimeout(10000);
      context.setDefaultNavigationTimeout(10000);
      // The real provider callback already issued these cookies. Import only its
      // closed entry family into a new isolated browser; never synthesize authority.
      await context.addCookies(cookies);
      const page = await context.newPage();
      const cdp = await context.newCDPSession(page);
      let networkPrivateFree = true;
      const documents = [];
      context.on('request', (request) => {
        if (request.resourceType() !== 'document') return;
        if (documents.length >= 8) {
          networkPrivateFree = false;
          return;
        }
        const url = request.url();
        documents.push(url);
        networkPrivateFree &&= privateFree(url);
      });
      let browserPrivateFree = true;
      const observePage = async (expected) => {
        const count = await page.locator('form[action="/api/auth/entry/select"]').count();
        assert.equal(count, expected);
        const cursorFields = await page
          .locator('input[name="cursor"]')
          .evaluateAll((nodes) => nodes.map((node) => node.value));
        assert.ok(cursorFields.length <= 20);
        const storage = await page.evaluate(() =>
          JSON.stringify({
            state: history.state,
            local: Object.entries(localStorage),
            session: Object.entries(sessionStorage),
          }),
        );
        browserPrivateFree &&=
          privateFree(await page.content()) &&
          privateFree(page.url()) &&
          privateFree(storage) &&
          cursorFields.every(privateFree);
        return count;
      };
      stage = 'browser-entry-confirmation';
      const first = await page.goto(entryUrl, { waitUntil: 'domcontentloaded' });
      assert.equal(first.status(), 200);
      const firstItems = await observePage(20);
      const next = page.locator('a[href^="/api/auth/entry?cursor="]');
      assert.equal(await next.count(), 1);
      const nextUrl = entryLocation(await next.getAttribute('href'));
      browserPrivateFree &&= privateFree(nextUrl);
      const [nextResponse] = await withProofWaiters(
        [
          page.waitForResponse(
            (response) => response.url() === nextUrl && response.request().method() === 'GET',
          ),
          page.waitForURL(nextUrl, { waitUntil: 'domcontentloaded' }),
        ],
        () => next.click(),
      );
      assert.equal(nextResponse.status(), 200);
      assert.equal(entryLocation(page.url()), nextUrl);
      const secondItems = await observePage(2);
      assert.equal(await page.locator('a[href^="/api/auth/entry?cursor="]').count(), 0);
      assert.equal(await page.locator('input[name="cursor"]').count(), 2);
      stage = 'browser-history-revalidation';
      await page.goBack({ waitUntil: 'domcontentloaded' });
      assert.equal(page.url(), entryUrl);
      const restoredItems = await observePage(20);
      const navigation = await cdp.send('Page.getNavigationHistory');
      assert.ok(navigation.entries.length >= 2 && navigation.entries.length <= 8);
      assert.equal(navigation.entries[navigation.currentIndex].url, entryUrl);
      assert.ok(navigation.entries.some((entry) => entry.url === nextUrl));
      const historyPrivateFree = navigation.entries.every(
        (entry) => privateFree(entry.url) && privateFree(entry.title),
      );
      assert.ok(documents.includes(entryUrl) && documents.includes(nextUrl));
      await context.close();
      return {
        firstItems,
        secondItems,
        restoredItems,
        historyEntries: navigation.entries.length,
        htmlPrivateFree,
        browserPrivateFree,
        historyPrivateFree,
        networkPrivateFree,
      };
    },
    async () => {
      const failures = [];
      try {
        await browser?.close();
      } catch {
        failures.push(true);
      }
      try {
        if ((await stopTestChild(child)).forced) failures.push(true);
      } catch {
        failures.push(true);
      }
      assert.equal(failures.length, 0, 'actual entry browser cleanup');
    },
  );
}
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
    'apiOrigin,authority,authorityId,clientSecret,issuer,mode,nativeSourceSecret,protocol,secret,sites',
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
  assert.match(
    config.authorityId,
    /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/,
  );
  assert.notEqual(config.authorityId, '00000000-0000-0000-0000-000000000000');
  assert.match(config.nativeSourceSecret, /^[A-Za-z0-9_-]{43}$/);
  assert.equal(
    Buffer.from(config.nativeSourceSecret, 'base64url').toString('base64url'),
    config.nativeSourceSecret,
  );
  assert.notEqual(config.nativeSourceSecret, config.secret);
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
  assert.ok(Array.isArray(config.sites) && config.sites.length === 4);
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
  assert.equal(new Set(config.sites.map((site) => site.host)).size, 4);
  stage = 'setup';
  const privateValues = [config.secret, config.clientSecret, config.nativeSourceSecret];
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
  let entryBrowserUsed = false;
  let count = 0;
  for await (const command of input) {
    stage = 'command';
    assert.ok(++count <= 128 && command && command.protocol === 1);
    if (command.action === 'finish') {
      assert.equal(Object.keys(command).sort().join(','), 'action,protocol');
      finished = true;
      break;
    }
    if (command.action === 'entry-browser') {
      assert.equal(Object.keys(command).sort().join(','), 'action,cookie,host,protocol,subjectKey');
      assert.equal(entryBrowserUsed, false);
      entryBrowserUsed = true;
      const receipt = await observeEntryBrowser(host, config, command, privateValues);
      await send({ protocol: 1, phase: 'entry_browser', ...receipt });
      continue;
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
        '/api/auth/entry',
        '/api/auth/entry/create',
        '/api/auth/entry/select',
        '/api/auth/switch',
        '/api/account/profile',
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
          accept: path.pathname === '/api/auth/entry' ? 'application/json' : 'text/html',
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
