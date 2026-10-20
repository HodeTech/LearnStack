import assert from 'node:assert/strict';
import { readFileSync, writeFileSync } from 'node:fs';
import { createServer } from 'node:http';
import { join } from 'node:path';

import lighthouse, { generateReport } from 'lighthouse';
import { chromium } from 'playwright';

import {
  admitProduct,
  assertConfiguration,
  digest,
  productDomSnapshot,
  reconcileScriptInventory,
  validateReport,
} from './public-demo-contract.mjs';
import { publicDemoWorkerStage } from './public-demo-diagnostics.mjs';
import { resolvedDemoCensus, demoLighthouseConfiguration } from './public-demo-gatherer.mjs';
import { readDemoToolchain } from './public-demo-toolchain.mjs';

const expectedCensus = JSON.parse(
  readFileSync(new URL('./public-demo-census.json', import.meta.url), 'utf8'),
);
const catalogue = (locale) =>
  JSON.parse(
    readFileSync(
      new URL(
        `../src/i18n/messages/${locale.startsWith('tr') ? 'tr' : 'en'}/public.json`,
        import.meta.url,
      ),
      'utf8',
    ),
  );

async function observeDocument(page, response) {
  assert.ok(response, 'Live product response exists');
  await response.finished();
  return {
    status: response.status(),
    headers: await response.allHeaders(),
    html: (await response.body()).toString('utf8'),
    dom: await page.evaluate(productDomSnapshot),
    redirects: response.request().redirectedFrom() ? 1 : 0,
  };
}

async function observe(page, response, lane, kind) {
  const document = await observeDocument(page, response);
  admitProduct(document, lane, kind, catalogue(lane.locale).catalog.title);
  return document;
}

async function browserPaths(browser, input) {
  let visibilityControls = 0;
  let navigations = 0;
  for (const [laneIndex, lane] of input.lanes.entries()) {
    const context = await browser.newContext({
      viewport: { width: 412, height: 823 },
      deviceScaleFactor: 1.75,
    });
    const page = await context.newPage();
    page.setDefaultTimeout(15000);
    try {
      let response = await page.goto(lane.catalogUrl, { waitUntil: 'networkidle', timeout: 15000 });
      for (const [index, kind] of ['catalog', 'course', 'lesson'].entries()) {
        if (index) {
          [response] = await Promise.all([
            page.waitForNavigation({ waitUntil: 'networkidle', timeout: 15000 }),
            page
              .locator(kind === 'course' ? '.public-course-card h2 a' : '.public-lesson-list a')
              .filter({ hasText: lane[kind + 'Title'] })
              .click(),
          ]);
        }
        await observe(page, response, lane, kind);
        navigations++;
        await page.screenshot({
          path: join(input.output, `browser-${laneIndex}-${kind}.png`),
          fullPage: true,
        });
        if (kind === 'lesson') {
          // Exercise the real DOM predicate, not a fabricated snapshot. No
          // additional document/API request is introduced by these mutations.
          for (const css of [
            'body { opacity: 0 !important }',
            'body { transform: scale(0) !important }',
            'dt { opacity: 0 !important }',
            'body { clip-path: inset(100%) !important }',
            'h1 { filter: opacity(0) !important }',
            'dt { mask-image: linear-gradient(transparent, transparent) !important }',
            'body { position: absolute !important; left: -100000px !important }',
            'body { height: 0 !important; overflow: hidden !important }',
          ]) {
            const mask = await page.addStyleTag({ content: css });
            try {
              const hidden = await observeDocument(page, response);
              assert.throws(
                () => admitProduct(hidden, lane, kind, catalogue(lane.locale).catalog.title),
                'Actual hidden product markers cannot satisfy admission',
              );
            } finally {
              await mask.evaluate((node) => node.remove());
            }
            await observe(page, response, lane, kind);
            visibilityControls++;
          }
        }
      }
    } finally {
      await context.close();
    }
  }
  return { navigations, visibilityControls };
}

/** The pinned Lighthouse load deadline is a warning, which admission must refuse. */
async function timeoutControl(port) {
  const server = createServer((request, response) => {
    if (request.url === '/pending.png') return;
    if (request.url === '/script.js') {
      response.writeHead(200, { 'content-type': 'text/javascript' });
      response.end('window.source = true;');
      return;
    }
    if (request.url === '/favicon.ico') {
      response.writeHead(204);
      response.end();
      return;
    }
    response.writeHead(200, { 'content-type': 'text/html', 'cache-control': 'no-store' });
    response.end(
      '<!doctype html><html lang="en"><head><meta name="viewport" content="width=device-width, initial-scale=1"><title>Deadline control</title></head><body><main><h1>Deadline control</h1><img src="/pending.png" alt="" width="1" height="1"><script>window.inline = true;</script><script src="/script.js"></script></main></body></html>',
    );
  });
  await new Promise((resolve, reject) => {
    server.once('error', reject);
    server.listen(0, '127.0.0.1', resolve);
  });
  try {
    const url = `http://127.0.0.1:${server.address().port}/`;
    const result = await lighthouse(
      url,
      { port, hostname: '127.0.0.1', logLevel: 'error', maxWaitForLoad: 15000 },
      demoLighthouseConfiguration,
    );
    assert.equal(result.lhr.runtimeError, undefined, 'Control reaches report-warning admission');
    assert.ok(result.lhr.runWarnings.length > 0, 'Actual incomplete-navigation warning');
    assert.throws(
      () => validateReport(result.lhr, { url }, expectedCensus),
      /Incomplete\/warning/,
      'Soft navigation timeout must refuse',
    );
    const clean = structuredClone(result.lhr);
    clean.runWarnings = [];
    validateReport(clean, { url }, expectedCensus);
  } finally {
    server.closeAllConnections();
    await new Promise((resolve, reject) =>
      server.close((error) => (error ? reject(error) : resolve())),
    );
  }
}

async function main(input, stage) {
  stage('preflight');
  assert.deepEqual(readDemoToolchain(), expectedCensus.versions, 'Pinned tool/browser census');
  stage('connect');
  const browser = await chromium.connectOverCDP(`http://127.0.0.1:${input.port}`, {
    timeout: 10000,
  });
  let browserVersion;
  try {
    browserVersion = browser.version();
    assert.equal(
      browserVersion,
      expectedCensus.versions.browserVersion,
      'Pinned managed full Chromium version',
    );
    // The native browser starts with no product target. Retire its blank target.
    for (const context of browser.contexts())
      for (const page of context.pages()) await page.close();
    if (input.mode === 'tls-refusal') {
      stage('preflight');
      const page = await browser.newPage();
      let refused = false;
      let failureCode = 'no-navigation-error';
      try {
        await page.goto(input.lanes[0].catalogUrl, { timeout: 10000 });
      } catch (error) {
        failureCode = error.message.match(/ERR_[A-Z_]+/)?.[0] ?? error.name;
        refused = failureCode === 'ERR_CERT_AUTHORITY_INVALID';
      }
      assert.equal(
        refused,
        true,
        'Untrusted/wrong-SPKI browser certificate refusal: ' + failureCode,
      );
      stage('report');
      writeFileSync(input.result, JSON.stringify({ refused: true, browserVersion }));
      return;
    }
    if (input.mode === 'browser') {
      stage('preflight');
      const counts = await browserPaths(browser, input);
      stage('report');
      writeFileSync(input.result, JSON.stringify({ browserVersion, ...counts }));
      return;
    }
    if (input.mode !== 'timeout-control') {
      stage('preflight');
      const context = await browser.newContext({
        viewport: { width: 412, height: 823 },
        deviceScaleFactor: 1.75,
      });
      const page = await context.newPage();
      try {
        const response = await page.goto(input.url, { waitUntil: 'networkidle', timeout: 15000 });
        await observe(page, response, input.lane, input.kind);
      } finally {
        await context.close();
      }
    }
  } finally {
    await browser.close();
  }

  if (input.mode === 'timeout-control') {
    stage('audit');
    await timeoutControl(input.port);
    stage('report');
    writeFileSync(input.result, JSON.stringify({ browserVersion, refused: true }));
    return;
  }

  const flags = {
    port: input.port,
    hostname: '127.0.0.1',
    logLevel: 'error',
    output: ['json', 'html'],
    maxWaitForLoad: 15000,
  };
  stage('audit');
  assertConfiguration(await resolvedDemoCensus(flags), expectedCensus);
  const result = await lighthouse(input.url, flags, demoLighthouseConfiguration);
  stage('report');
  assert.ok(result?.artifacts?.PublicDemoDocument, 'Actual audit product artifact');
  for (const { id } of expectedCensus.artifacts)
    assert.ok(result.artifacts[id], 'Required gathered artifact ' + id);
  admitProduct(
    result.artifacts.PublicDemoDocument,
    input.lane,
    input.kind,
    catalogue(input.lane.locale).catalog.title,
  );
  const metrics = validateReport(result.lhr, input, expectedCensus);
  assert.equal(metrics['initial-js-gzip-bytes'], result.artifacts.PublicDemoDocument.gzip.total);
  const source = result.artifacts.PublicDemoDocument;
  // This is independent network-window coverage, not a second gzip measurement.
  reconcileScriptInventory(result.artifacts.DevtoolsLog, source.inventory);
  const base = join(input.output, `audit-${input.laneIndex}-${input.kind}-${input.run}`);
  const report = JSON.stringify(result.lhr);
  const html = generateReport(result.lhr, 'html');
  writeFileSync(base + '.json', report, { flag: 'wx' });
  writeFileSync(base + '.html', html, { flag: 'wx' });
  writeFileSync(
    input.result,
    JSON.stringify({
      ...input.identity,
      url: input.url,
      run: input.run,
      browserVersion,
      metrics,
      configDigest: digest(JSON.stringify(expectedCensus)),
      reportDigest: digest(report),
      htmlDigest: digest(html),
      gzip: source.gzip,
      cutoff: source.cutoff,
    }),
  );
}

let input;
let stage = 'input';
try {
  input = JSON.parse(readFileSync(process.argv[2], 'utf8'));
  await main(input, (next) => {
    stage = publicDemoWorkerStage(next);
  });
} catch {
  // The private failure result carries only a closed phase; the parent validates
  // it again before promoting it to the public receipt. No provider text crosses.
  if (input?.result) {
    try {
      writeFileSync(
        input.result,
        JSON.stringify({ failure: { stage: publicDemoWorkerStage(stage) } }),
      );
    } catch {
      /* Parent still records a closed worker-failure when no result can be written. */
    }
  }
  console.error('Public demo worker refused: ' + (publicDemoWorkerStage(stage) ?? 'unknown'));
  process.exitCode = 1;
}
