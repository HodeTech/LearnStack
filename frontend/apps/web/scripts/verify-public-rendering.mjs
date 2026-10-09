// Test-only production build, driven by PublicServerRenderingTests over private stdin.
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import {
  cpSync,
  existsSync,
  mkdirSync,
  mkdtempSync,
  readFileSync,
  readdirSync,
  rmSync,
  symlinkSync,
  writeFileSync,
} from 'node:fs';
import { request as httpRequest } from 'node:http';
import { request as httpsRequest } from 'node:https';
import { createRequire } from 'node:module';
import { createServer } from 'node:net';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { createInterface } from 'node:readline';
import { fileURLToPath } from 'node:url';

import {
  createFixtureOwner,
  createPrivateScanner,
  fixtureEnvironment,
} from './fixture-support.mjs';

const sourceApp = fileURLToPath(new URL('../', import.meta.url));
const { JSDOM } = createRequire(join(sourceApp, 'package.json'))('jsdom');
const owner = createFixtureOwner();
const scanners = [];
let fixtureRoot;
let app;
let input;
let lines;
let stage = 'configuration';
let configuration;
let certificate;
let privateScanner;
let catalogueCanaries = [];

function checkPrivate(value) {
  return privateScanner.contains(value);
}

async function line() {
  let timer;
  try {
    const result = await Promise.race([
      lines.next(),
      new Promise((_resolve, reject) => {
        timer = setTimeout(() => reject(new Error('Control deadline')), 30_000);
      }),
    ]);
    owner.assertActive();
    assert.equal(result.done, false, 'Parent control closed');
    return result.value;
  } finally {
    clearTimeout(timer);
  }
}

async function checkpoint(name) {
  process.stdout.write(name + '\n');
  assert.equal(await line(), 'continue', 'Invalid parent acknowledgement');
}

function environment() {
  return fixtureEnvironment({
    root: fixtureRoot,
    nodePath: join(sourceApp, '../../node_modules/.pnpm/node_modules'),
    privateValues: {
      LEARNSTACK_PUBLIC_API_ORIGIN: configuration.apiOrigin,
      LEARNSTACK_PUBLIC_HOP_SECRET: configuration.secret,
      LEARNSTACK_PUBLIC_TLS_CERT: join(fixtureRoot, 'cert.pem'),
      LEARNSTACK_PUBLIC_TLS_KEY: join(fixtureRoot, 'key.pem'),
    },
  });
}

function start(command, args) {
  owner.assertActive();
  const child = owner.ownChild(
    spawn(command, args, {
      cwd: app,
      env: environment(),
      stdio: ['ignore', 'pipe', 'pipe'],
      detached: process.platform !== 'win32',
    }),
  );
  child.failed = false;
  child.on('error', () => {
    child.failed = true;
  });
  const output = [];
  for (const stream of [child.stdout, child.stderr]) {
    const scanner = createPrivateScanner([configuration.secret]);
    scanners.push(scanner);
    output.push(scanner);
    stream.on('data', (chunk) => scanner.push(chunk));
    stream.once('end', () => scanner.finish());
    stream.once('close', () => scanner.finish());
  }
  Object.defineProperty(child, 'output', { get: () => output.map((scan) => scan.tail).join('\n') });
  return child;
}

async function completion(child, milliseconds = 180_000) {
  owner.assertActive();
  if (child.exitCode !== null) return child.exitCode;
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error('Child deadline')), milliseconds);
    child.once('error', () => {
      clearTimeout(timer);
      reject(new Error('Child startup'));
    });
    child.once('close', (code) => {
      clearTimeout(timer);
      resolve(code);
    });
  }).then((code) => {
    owner.assertActive();
    return code;
  });
}

async function vacant(port) {
  owner.assertActive();
  const probe = owner.ownServer(createServer());
  await new Promise((resolve, reject) => {
    probe.once('error', () => reject(new Error('Required test port is occupied')));
    probe.listen(port, '127.0.0.1', resolve);
  });
  await new Promise((resolve, reject) =>
    probe.close((error) => (error ? reject(error) : resolve())),
  );
}

function call(tls, port, path, headers = {}, method = 'GET') {
  owner.assertActive();
  return new Promise((resolve, reject) => {
    const send = tls ? httpsRequest : httpRequest;
    const request = send(
      {
        hostname: '127.0.0.1',
        port,
        path,
        headers,
        method,
        ...(tls
          ? {
              ca: certificate,
              servername: headers.Host?.split(':')[0] ?? 'localhost',
              rejectUnauthorized: true,
            }
          : {}),
        timeout: 15_000,
      },
      (response) => {
        response.setEncoding('utf8');
        let body = '';
        response.on('data', (chunk) => {
          body += chunk.toString();
          if (body.length > 2 * 1024 * 1024) request.destroy(new Error('Response bound'));
        });
        response.on('error', reject);
        response.on('end', () =>
          resolve({ status: response.statusCode, headers: response.headers, body }),
        );
      },
    );
    request.on('timeout', () => request.destroy(new Error('Request deadline')));
    request.on('error', reject);
    request.end();
  });
}

async function ready(child, tls, port) {
  // A healthy foreign listener cannot establish readiness: first observe this
  // owned process's successful bind. Both launchers print readiness after listen.
  const bound = tls ? /Public HTTPS listener ready on 127\.0\.0\.1:3000/ : /[✓✔] Ready in /;
  for (let attempt = 0; attempt < 200; attempt++) {
    assert.equal(
      child.failed || child.exitCode !== null || child.signalCode !== null,
      false,
      'Listener startup',
    );
    owner.assertActive();
    try {
      if (!bound.test(child.output)) throw new Error('Owned bind pending');
      const response = await call(tls, port, '/api/healthz');
      if (response.status === 200 && JSON.parse(response.body).status === 'healthy') {
        assert.equal(child.exitCode, null, 'Owned listener remains live');
        assert.equal(child.signalCode, null, 'Owned listener remains live');
        return;
      }
    } catch {
      /* Readiness only; public API calls are never retried. */
    }
    await new Promise((resolve) => setTimeout(resolve, 100));
  }
  throw new Error('Readiness deadline');
}

function safeResponse(
  response,
  status,
  rsc = false,
  contentType = rsc ? /^text\/x-component/ : /^text\/html/,
) {
  const phase = stage;
  if (response.status !== status) stage += ` (status ${response.status}, expected ${status})`;
  assert.equal(response.status, status, 'Public response status');
  stage = phase + ' cache-control';
  assert.match(response.headers['cache-control'], /(?:^|,\s*)no-store(?:,|$)/);
  stage = phase + ' forbidden headers';
  assert.equal(response.headers.etag, undefined);
  assert.equal(response.headers['set-cookie'], undefined);
  assert.equal(response.headers['x-powered-by'], undefined);
  stage = phase + ' private containment';
  assert.equal(checkPrivate(JSON.stringify(response)), false, 'Response private-data containment');
  stage = phase + ' content-type';
  if (status === 200) assert.match(response.headers['content-type'], contentType);
  stage = phase;
}

async function representation(tenant, path, rsc = false, extra = {}, status = 200) {
  const response = await call(true, 3000, path, {
    Host: tenant.host + ':3000',
    ...(rsc ? { RSC: '1' } : {}),
    ...extra,
  });
  safeResponse(response, status, rsc);
  const other = configuration.tenants.find((candidate) => candidate.host !== tenant.host);
  assert.equal(response.body.includes(other.name), false, 'Opposite tenant name');
  assert.equal(response.body.includes(other.courseTitle), false, 'Opposite tenant course');
  if (status === 200) {
    assert.equal(response.body.includes(tenant.name), true, 'Live bootstrap name');
    if (!rsc) assert.equal(response.body.includes(clientMarker), true, 'Rendered Client Component');
  }
  return response;
}

function scanClientAssets(directory, expectedMarker = false) {
  let files = 0;
  let marker = false;
  for (const entry of readdirSync(directory, { withFileTypes: true })) {
    const path = join(directory, entry.name);
    if (entry.isDirectory()) {
      const scanned = scanClientAssets(path);
      files += scanned.files;
      marker ||= scanned.marker;
    } else {
      const content = readFileSync(path, 'utf8');
      if (entry.name.endsWith('.js')) {
        files++;
        marker ||= content.includes(clientMarker);
      }
      assert.equal(checkPrivate(content), false, 'Static client private-data containment');
      assert.equal(
        catalogueCanaries.some((value) => content.includes(value)),
        false,
        'Complete UI catalogue cannot enter browser assets',
      );
    }
  }
  if (expectedMarker) assert.equal(marker, true, 'Built public Client Component marker');
  return { files, marker };
}

const clientMarker = 'learnstack-public-client-marker';
const clientComponent = `'use client';
export default function FixtureClient({ displayName }: { displayName: string }) {
  return <p data-fixture-client="${clientMarker}">{displayName}</p>;
}
`;

// This source exists only inside the disposable app. Every public value comes
// from the real configured SDK; headers, envelopes and errors are never serialized.
const page = `import { headers } from 'next/headers';
import { notFound } from 'next/navigation';
import { createConfiguredPublicClient } from '@/server/configured-public-client';
import { INGRESS_HEADER } from '@/server/ingress';
import FixtureClient from './fixture-client';

export default async function FixturePage({ params }: {
  params: Promise<{ locale: string; segments?: string[] }>;
}) {
  const incoming = await headers();
  const client = createConfiguredPublicClient(incoming.get(INGRESS_HEADER), {
    traceparent: incoming.get('traceparent'),
  });
  if (!client) notFound();
  const site = await client.getSite();
  if (site.kind !== 'success') throw new Error('Fixture bootstrap failed');
  const { segments = [] } = await params;
  const result = segments.length === 0 ? await client.getCourses() :
    segments.length === 1 ? await client.getCourse({ slug: segments[0]! }) :
    await client.getLesson({ slug: segments[0]!, lessonSlug: segments[2]! });
  if (result.kind === 'api-error' && result.status === 404) notFound();
  if (result.kind !== 'success') throw new Error('Fixture public read failed');
  return <section><h1>{site.data.displayName}</h1><FixtureClient displayName={site.data.displayName}/><pre>{JSON.stringify(result.data)}</pre></section>;
}
`;

// P5 deliberately keeps its own neutral document and independent SDK calls.
// Replacing only this disposable copy prevents later product layouts from adding
// reads to the transport proof or its synthetic catch-all shadowing product pages.
const transportRoot = `import type { ReactNode } from 'react';
import './globals.css';
export default function RootLayout({ children }: { children: ReactNode }) {
  return <html lang="en"><body>{children}</body></html>;
}
`;
const transportLayout = `import type { ReactNode } from 'react';
export const dynamic = 'force-dynamic';
export const revalidate = 0;
export const fetchCache = 'force-no-store';
export default function PublicLayout({ children }: { children: ReactNode }) {
  return <main>{children}</main>;
}
`;

function document(response) {
  const dom = new JSDOM(response.body);
  // Assertions below inspect the rendered document, never strings embedded in
  // Flight/bootstrap scripts that may describe UI absent from visible HTML.
  dom.window.document
    .querySelectorAll('script, template, [hidden]')
    .forEach((node) => node.remove());
  return dom.window.document;
}

function statusDocument(response, tenant, locale, uiLocale) {
  const phase = stage;
  stage = phase + ' status headers';
  safeResponse(response, 404);
  stage = phase + ' HTML content-type';
  assert.match(response.headers['content-type'], /^text\/html/);
  stage = phase + ' catalogue containment';
  assert.equal(
    catalogueCanaries.some((value) => response.body.includes(value)),
    false,
    'Complete UI catalogue cannot enter Flight payload',
  );
  const doc = document(response);
  stage = phase + ' document locale';
  assert.equal(doc.documentElement.lang, locale, 'Document retains admitted content locale');
  assert.equal(doc.documentElement.dir, locale === 'ar' ? 'rtl' : 'ltr');
  stage = phase + ' visible heading';
  const heading = doc.querySelector('h1');
  assert.equal(doc.querySelectorAll('h1').length, 1, 'One visible localized status heading');
  assert.equal(doc.querySelectorAll('main').length, 1, 'One main landmark');
  const title = uiLocale === 'tr' ? 'Sayfa bulunamadı' : 'Page not found';
  assert.equal(heading.textContent, title, 'Visible status translation');
  stage = phase + ' localized metadata';
  if (!doc.title.includes(title)) {
    const original = new JSDOM(response.body).window.document;
    const titleNode = original.querySelector('title');
    stage += titleNode?.closest('[hidden]') ? ' hidden' : titleNode ? ' unmatched' : ' absent';
  }
  assert.ok(doc.title.includes(title), 'Localized document title');
  stage = phase + ' UI locale';
  assert.equal(heading.closest('[lang]').getAttribute('lang'), uiLocale, 'UI fallback language');
  assert.equal(heading.closest('[dir]').getAttribute('dir'), 'ltr', 'UI fallback direction');
  stage = phase + ' robots recovery';
  assert.ok(doc.querySelector('meta[name="robots"]').content.includes('noindex'));
  assert.ok(doc.querySelector('a[href="/' + locale + '/courses"]'), 'Same-locale recovery');
  stage = phase + ' tenant chrome';
  assert.ok(doc.body.textContent.includes(tenant.name), 'Actual tenant chrome');
  const other = configuration.tenants.find((candidate) => candidate.host !== tenant.host);
  assert.equal(doc.body.textContent.includes(other.name), false, 'No opposite tenant chrome');
  assert.equal(
    response.body.includes('missing-foundation-course'),
    false,
    'No original resource echo',
  );
  assert.equal(response.body.includes('private-query-value'), false, 'No original query echo');
  stage = phase;
  return doc;
}

function productDocument(response, tenant, locale, heading, path, direction = 'ltr') {
  safeResponse(response, 200);
  const doc = document(response);
  assert.equal(doc.documentElement.lang, locale, 'Admitted document language');
  assert.equal(doc.documentElement.dir, direction, 'Admitted document direction');
  assert.equal(doc.querySelectorAll('main').length, 1, 'One product main landmark');
  assert.equal(doc.querySelectorAll('h1').length, 1, 'One visible product heading');
  assert.equal(doc.querySelector('h1').textContent, heading, 'Visible product heading');
  assert.ok(doc.title.includes(heading), 'Product metadata title');
  assert.equal(
    doc.querySelector('link[rel="canonical"]')?.getAttribute('href'),
    'https://' + tenant.host + ':3000' + path,
    'Verified-host canonical',
  );
  assert.equal(
    doc.querySelector('meta[property="og:url"]')?.getAttribute('content'),
    'https://' + tenant.host + ':3000' + path,
    'Verified-host Open Graph URL',
  );
  assert.equal(
    doc.querySelector('link[rel="alternate"][hreflang="' + locale + '"]')?.getAttribute('href'),
    'https://' + tenant.host + ':3000' + path,
    'Eligible current locale has a canonical hreflang self-reference',
  );
  assert.ok(doc.body.textContent.includes(tenant.name), 'Live tenant chrome');
  const other = configuration.tenants.find((candidate) => candidate.host !== tenant.host);
  assert.equal(doc.body.textContent.includes(other.name), false, 'No opposite tenant chrome');
  assert.equal(
    doc.body.textContent.includes(other.courseTitle),
    false,
    'No opposite tenant course',
  );
  return doc;
}

function paginationLink(doc, label, base, limitName, cursorName) {
  const link = [...doc.querySelectorAll('nav.public-pagination a')].find(
    (candidate) => candidate.textContent === label,
  );
  assert.ok(link, 'Visible pagination anchor');
  const target = new URL(link.getAttribute('href'), 'https://fixture.invalid');
  assert.equal(target.pathname, base, 'Same-locale pagination path');
  assert.deepEqual(
    [...target.searchParams.keys()].sort(),
    [cursorName, limitName].sort(),
    'Only owned pagination query names',
  );
  assert.equal(target.searchParams.get(limitName), '20', 'Default page size preserved');
  assert.match(
    target.searchParams.get(cursorName),
    /^[A-Za-z0-9_-]+$/,
    'Opaque continuation carried unchanged',
  );
  return link.getAttribute('href');
}

function seededCatalog(doc, locale, expected, excluded) {
  assert.equal(
    doc.querySelectorAll('ul.public-course-list > li').length,
    expected.length,
    'Only host-visible published courses are listed',
  );
  for (const { title, slug } of expected) {
    const link = doc.querySelector(
      'ul.public-course-list a[href="/' + locale + '/courses/' + slug + '"]',
    );
    assert.equal(link?.textContent, title, 'Visible course uses its exact-locale title and slug');
  }
  for (const title of excluded)
    assert.equal(
      doc.body.textContent.includes(title),
      false,
      'Draft or sibling-organization course is absent',
    );
  assert.equal(
    doc.querySelector('nav.public-pagination'),
    null,
    'Seed inventory fits the default page',
  );
}

async function verifyFoundation(native, nextBin) {
  const [first, second] = configuration.tenants;
  const course = '/' + configuration.locale + '/courses/' + configuration.courseSlug;
  const catalog = '/' + configuration.locale + '/courses';
  const request = (tenant, path, method = 'GET') =>
    call(true, 3000, path, { Host: tenant.host + ':3000' }, method);

  for (const checkpointName of ['foundation-normal', 'foundation-repeat']) {
    stage = checkpointName;
    const response = await request(first, course);
    const doc = productDocument(response, first, configuration.locale, first.courseTitle, course);
    assert.ok(doc.body.textContent.includes(first.courseSummary), 'Actual course summary');
    assert.ok(
      doc.querySelector(
        'ol.public-lesson-list a[href="' + course + '/lessons/' + first.lessonSlug + '"]',
      ),
      'Actual public outline link',
    );
    await checkpoint(checkpointName);
  }

  stage = 'foundation catalog English';
  const english = productDocument(await request(first, catalog), first, 'en', 'Courses', catalog);
  seededCatalog(english, 'en', first.catalogEn, first.hiddenEn);
  await checkpoint('foundation-catalog-english');

  stage = 'foundation catalog Yoga English';
  const yoga = productDocument(await request(second, catalog), second, 'en', 'Courses', catalog);
  seededCatalog(yoga, 'en', second.catalogEn, second.hiddenEn);
  await checkpoint('foundation-catalog-yoga');

  stage = 'foundation catalog Yoga Turkish';
  const turkishPath = '/' + second.defaultLocale + '/courses';
  const turkish = productDocument(
    await request(second, turkishPath),
    second,
    second.defaultLocale,
    'Kurslar',
    turkishPath,
  );
  seededCatalog(turkish, second.defaultLocale, second.catalogDefault, second.hiddenDefault);
  await checkpoint('foundation-catalog-turkish');

  stage = 'foundation Yoga English course';
  const yogaCourse = productDocument(
    await request(second, course),
    second,
    'en',
    second.courseTitle,
    course,
  );
  assert.ok(yogaCourse.body.textContent.includes(second.courseSummary));
  assert.equal(
    yogaCourse
      .querySelector('link[rel="alternate"][hreflang="' + second.defaultLocale + '"]')
      ?.getAttribute('href'),
    'https://' +
      second.host +
      ':3000/' +
      second.defaultLocale +
      '/courses/' +
      second.defaultCourseSlug,
    'Other-locale hreflang uses its actual translated slug',
  );
  assert.equal(
    yogaCourse.querySelector(
      'ol.public-lesson-list a[href="' + course + '/lessons/' + second.lessonSlug + '"]',
    )?.textContent,
    second.lessonTitle,
    'Studio host sees its own English outline',
  );
  await checkpoint('foundation-course-yoga');

  stage = 'foundation Yoga Turkish course';
  const turkishCoursePath = turkishPath + '/' + second.defaultCourseSlug;
  const turkishCourse = productDocument(
    await request(second, turkishCoursePath),
    second,
    second.defaultLocale,
    second.defaultCourseTitle,
    turkishCoursePath,
  );
  assert.ok(turkishCourse.body.textContent.includes(second.defaultCourseSummary));
  assert.equal(
    turkishCourse.querySelector('link[rel="alternate"][hreflang="en"]')?.getAttribute('href'),
    'https://' + second.host + ':3000' + course,
    'Translated course retains the eligible English reciprocal hreflang',
  );
  assert.equal(
    turkishCourse.querySelector(
      'ol.public-lesson-list a[href="' +
        turkishCoursePath +
        '/lessons/' +
        second.defaultLessonSlug +
        '"]',
    )?.textContent,
    second.defaultLessonTitle,
    'Studio host sees its own Turkish outline',
  );
  await checkpoint('foundation-course-turkish');

  stage = 'foundation restricted marketing';
  const restrictedPath = catalog + '/' + first.restrictedSlug;
  const restrictedResponse = await request(first, restrictedPath);
  const restricted = productDocument(
    restrictedResponse,
    first,
    'en',
    first.restrictedTitle,
    restrictedPath,
  );
  assert.ok(restricted.body.textContent.includes(first.restrictedSummary));
  assert.ok(
    restricted.body.textContent.includes('The lessons in this course are not publicly available.'),
  );
  assert.equal(
    restricted.querySelector('ol.public-lesson-list'),
    null,
    'Protected outline is absent',
  );
  assert.ok(first.restrictedCanaries.length > 0, 'Nonempty protected seed controls');
  for (const canary of first.restrictedCanaries)
    assert.equal(
      restrictedResponse.body.includes(canary),
      false,
      'Protected value absent from HTML and Flight',
    );
  await checkpoint('foundation-restricted');

  stage = 'foundation empty exact-locale catalog';
  const emptyPath = '/ar/courses';
  const empty = productDocument(
    await request(first, emptyPath),
    first,
    'ar',
    'Courses',
    emptyPath,
    'rtl',
  );
  assert.equal(
    empty.querySelector('ul.public-course-list'),
    null,
    'Empty catalog differs from a list',
  );
  assert.ok(empty.body.textContent.includes('No courses are available in this language yet.'));
  assert.equal(
    empty.querySelector('h1').closest('[lang]').getAttribute('lang'),
    'en',
    'English UI fallback is labelled separately from Arabic content locale',
  );
  await checkpoint('foundation-empty');

  stage = 'foundation cross-tenant detail refusal';
  const hidden = await request(first, '/en/courses/shared-practice');
  safeResponse(hidden, 307);
  assert.equal(hidden.headers.location, '/en/status/not-found');
  statusDocument(await request(first, '/en/status/not-found'), first, 'en', 'en');
  assert.equal(hidden.body.includes(second.courseTitle), false);
  await checkpoint('foundation-cross-tenant');

  stage = 'foundation-missing';
  const missing = await request(
    first,
    '/' + configuration.locale + '/courses/missing-foundation-course?cursor=private-query-value',
  );
  safeResponse(missing, 307);
  const statusPath = '/' + configuration.locale + '/status/not-found';
  stage = 'foundation missing redirect location';
  assert.equal(missing.headers.location, statusPath, 'Fixed relative missing-resource redirect');
  stage = 'foundation missing follow';
  statusDocument(await request(first, statusPath), first, configuration.locale, 'en');
  await checkpoint('foundation-missing');

  stage = 'foundation-status';
  // Concurrent different hosts and catalogue languages exercise request config
  // isolation; Arabic and exact Turkish are test-owned live memberships.
  const concurrentFailures = await Promise.all(
    [
      [first, 'en', 'en'],
      [second, second.defaultLocale, 'tr'],
      [first, 'ar', 'en'],
      [first, 'tr', 'tr'],
    ].map(async ([tenant, locale, uiLocale]) => {
      try {
        const response = await request(tenant, '/' + locale + '/status/not-found');
        stage = 'foundation status ' + locale;
        statusDocument(response, tenant, locale, uiLocale);
        return null;
      } catch {
        // Freeze the bounded diagnostic before another concurrent check changes
        // the shared stage. Wait for every request before reporting a failure.
        return stage;
      }
    }),
  );
  const failedStage = concurrentFailures.find((failure) => failure !== null);
  if (failedStage) {
    stage = failedStage;
    throw new Error('Concurrent status assertion failed');
  }
  await checkpoint('foundation-status');

  stage = 'foundation-head';
  const head = await request(first, '/ar/status/not-found', 'HEAD');
  safeResponse(head, 404);
  assert.equal(head.body, '', 'Localized status HEAD is bodyless');
  await checkpoint('foundation-head');

  stage = 'foundation-refused';
  for (const path of ['/fr/status/not-found', '/ar/status/unknown']) {
    const refused = await request(first, path);
    safeResponse(refused, 404);
    assert.equal(refused.body.includes(first.name), false, 'Admission failure has neutral output');
    assert.equal(
      refused.body.includes('Page not found'),
      false,
      'UI fallback cannot admit a locale',
    );
  }
  await checkpoint('foundation-refused');

  stage = 'foundation-canonical';
  const canonical = await request(first, '/TR-tr/status/not-found');
  safeResponse(canonical, 308);
  assert.equal(
    canonical.headers.location,
    'https://' + first.host + ':3000/tr-TR/status/not-found',
  );
  statusDocument(await request(first, '/tr-TR/status/not-found'), first, 'tr-TR', 'tr');
  await checkpoint('foundation-canonical');

  stage = 'foundation stock bypass';
  const stock = start(process.execPath, [
    nextBin,
    'start',
    '--hostname',
    '127.0.0.1',
    '--port',
    '3011',
  ]);
  await ready(stock, false, 3011);
  await checkpoint('stock-before');
  for (const rsc of [false, true]) {
    const response = await call(false, 3011, '/ar/status/not-found', {
      Host: first.host + ':3000',
      ...(rsc ? { RSC: '1' } : {}),
      'X-LearnStack-Ingress-Provenance': 'forged',
      'X-Middleware-Subrequest': 'middleware:middleware:middleware:middleware:middleware',
    });
    safeResponse(response, 404);
    assert.equal(response.body.includes(first.name), false);
  }
  await checkpoint('stock-after');
  assert.equal(native.exitCode, null, 'Foundation checks use one native process');
}

async function verifyPagination(native) {
  const first = configuration.tenants[0];
  const catalog = '/en/courses';
  const course = catalog + '/' + configuration.courseSlug;
  const request = (path) => call(true, 3000, path, { Host: first.host + ':3000' });

  stage = 'pagination catalog default first page';
  const firstCatalog = productDocument(await request(catalog), first, 'en', 'Courses', catalog);
  assert.equal(
    firstCatalog.querySelectorAll('ul.public-course-list > li').length,
    20,
    'The default twenty-item catalog page is full',
  );
  assert.ok(firstCatalog.querySelector('a[href="' + course + '"]'));
  assert.equal(
    firstCatalog.querySelectorAll('nav.public-pagination a').length,
    1,
    'First page has next without invented previous cursor',
  );
  const nextCatalog = paginationLink(firstCatalog, 'Next courses', catalog, 'limit', 'cursor');
  await checkpoint('pagination-catalog-first');

  stage = 'pagination catalog continuation';
  const continuedCatalog = productDocument(
    await request(nextCatalog),
    first,
    'en',
    'Courses',
    catalog,
  );
  assert.equal(continuedCatalog.querySelectorAll('ul.public-course-list > li').length, 3);
  assert.equal(
    continuedCatalog.querySelector('a[href="' + course + '"]'),
    null,
    'Seek continuation does not repeat the first course',
  );
  assert.ok(continuedCatalog.querySelector('meta[name="robots"]')?.content.includes('noindex'));
  assert.equal(
    continuedCatalog.querySelectorAll('nav.public-pagination a').length,
    1,
    'Last page has only restart',
  );
  const catalogRestart = continuedCatalog
    .querySelector('nav.public-pagination a')
    .getAttribute('href');
  assert.equal(catalogRestart, catalog + '?limit=20');
  await checkpoint('pagination-catalog-next');

  stage = 'pagination catalog restart';
  const catalogAgain = productDocument(
    await request(catalogRestart),
    first,
    'en',
    'Courses',
    catalog,
  );
  assert.equal(catalogAgain.querySelectorAll('ul.public-course-list > li').length, 20);
  assert.ok(catalogAgain.querySelector('a[href="' + course + '"]'));
  await checkpoint('pagination-catalog-restart');

  stage = 'pagination outline default first page';
  const firstOutline = productDocument(
    await request(course),
    first,
    'en',
    first.courseTitle,
    course,
  );
  assert.equal(
    firstOutline.querySelectorAll('ol.public-lesson-list > li').length,
    20,
    'The default twenty-item outline page is full',
  );
  assert.ok(
    firstOutline.querySelector('a[href="' + course + '/lessons/' + first.lessonSlug + '"]'),
  );
  const nextOutline = paginationLink(
    firstOutline,
    'Next lessons',
    course,
    'lessonLimit',
    'lessonCursor',
  );
  await checkpoint('pagination-outline-first');

  stage = 'pagination outline continuation';
  const continuedOutline = productDocument(
    await request(nextOutline),
    first,
    'en',
    first.courseTitle,
    course,
  );
  assert.equal(continuedOutline.querySelectorAll('ol.public-lesson-list > li').length, 2);
  assert.equal(
    continuedOutline.querySelector('a[href="' + course + '/lessons/' + first.lessonSlug + '"]'),
    null,
    'Seek continuation does not repeat the first lesson',
  );
  assert.ok(continuedOutline.querySelector('meta[name="robots"]')?.content.includes('noindex'));
  assert.equal(continuedOutline.querySelectorAll('nav.public-pagination a').length, 1);
  const outlineRestart = continuedOutline
    .querySelector('nav.public-pagination a')
    .getAttribute('href');
  assert.equal(outlineRestart, course + '?lessonLimit=20');
  await checkpoint('pagination-outline-next');

  stage = 'pagination outline restart';
  const outlineAgain = productDocument(
    await request(outlineRestart),
    first,
    'en',
    first.courseTitle,
    course,
  );
  assert.equal(outlineAgain.querySelectorAll('ol.public-lesson-list > li').length, 20);
  assert.ok(
    outlineAgain.querySelector('a[href="' + course + '/lessons/' + first.lessonSlug + '"]'),
  );
  await checkpoint('pagination-outline-restart');
  assert.equal(native.exitCode, null, 'Pagination checks use one native process');
}

function lessonContainment(response, tenant) {
  const other = configuration.tenants.find((candidate) => candidate.host !== tenant.host);
  for (const value of [
    other.name,
    other.lessonTitle,
    ...other.content.fields.map((field) => field.value),
  ])
    assert.equal(response.body.includes(value), false, 'Opposite tenant lesson is absent');
  assert.equal(
    catalogueCanaries.some((value) => response.body.includes(value)),
    false,
    'Complete UI catalogue cannot enter the lesson Flight payload',
  );
}

function lessonDocument(response, tenant, locale, title, path, content) {
  lessonContainment(response, tenant);
  // Inspect the original DOM as well: the visible-document helper deliberately
  // removes Next scripts and must not hide an authored active element.
  const original = new JSDOM(response.body).window.document.querySelector('article.public-lesson');
  assert.ok(original, 'Actual lesson article is rendered');
  assert.equal(
    original.querySelector(
      'script, template, style, img, svg, math, iframe, object, embed, audio, video, form',
    ),
    null,
    'Authored values create no active DOM element',
  );
  for (const node of original.querySelectorAll('*'))
    for (const attribute of node.attributes)
      assert.equal(
        /^on|^(?:src|srcdoc|action|formaction)$/i.test(attribute.name),
        false,
        'No authored active attribute',
      );
  const doc = productDocument(response, tenant, locale, title, path);
  const article = doc.querySelector('article.public-lesson');
  assert.equal(article.lang, locale, 'Lesson text retains the exact content locale');
  assert.equal(article.dir, 'ltr');
  const back = article.querySelector('a');
  assert.equal(article.querySelectorAll('a').length, 1, 'URL-looking content is never linkified');
  assert.equal(
    back.getAttribute('href'),
    path.split('/lessons/')[0],
    'Back link uses the actual same-locale course',
  );
  assert.equal(
    back.textContent,
    locale === tenant.defaultLocale ? tenant.defaultCourseTitle : tenant.courseTitle,
  );
  assert.ok(doc.querySelector('a[href="#main-content"]'), 'Lesson has the product skip link');
  assert.ok(doc.querySelector('main#main-content'), 'Lesson has the product skip target');
  assert.equal(
    doc.querySelectorAll('h3, h4, h5, h6').length,
    0,
    'Lesson heading outline stays sequential',
  );
  if (content === null) {
    assert.equal(
      article.querySelector('h2, dl, dt, dd'),
      null,
      'Unavailable presentation has no authored card',
    );
    assert.ok(article.textContent.includes('This lesson content is currently unavailable.'));
    assert.equal(article.textContent.includes('This lesson has no content to display yet.'), false);
    assert.ok(doc.querySelector('meta[name="robots"]')?.content.includes('noindex'));
    assert.equal(
      response.body.includes('unavailable-private-body-canary'),
      false,
      'Unavailable payload stays absent from HTML and Flight',
    );
    return doc;
  }
  assert.equal(
    article.querySelectorAll('h2').length,
    1,
    'One card heading follows the lesson heading',
  );
  const label = article.querySelector('h2');
  assert.equal(label.textContent, content.label.value, 'Card heading uses the API label');
  assert.equal(label.lang, content.label.locale, 'Card label attributes its resolved locale');
  assert.equal(label.dir, content.label.locale === 'ar' ? 'rtl' : 'ltr');
  assert.equal(
    doc.querySelector('meta[name="robots"]')?.content.includes('noindex') ?? false,
    false,
    'Ready lesson metadata is indexable',
  );
  const terms = [...article.querySelectorAll('dl dt')];
  const values = [...article.querySelectorAll('dl dd')];
  assert.deepEqual(
    terms.map((term) => term.textContent),
    content.fields.map((field) => field.label.value),
    'Descriptor order controls authored labels',
  );
  assert.deepEqual(
    values.map((value) => value.textContent),
    content.fields.map((field) => field.value),
    'Every exact API string renders as text in descriptor order',
  );
  assert.equal(
    article.querySelectorAll('dl').length,
    content.fields.length ? 1 : 0,
    'Only nonempty cards have a definition list',
  );
  for (const [index, field] of content.fields.entries()) {
    const term = terms[index];
    const value = values[index];
    assert.equal(
      term.lang,
      field.label.locale,
      'Each label attributes the actual fallback-resolved language',
    );
    assert.equal(term.dir, field.label.locale === 'ar' ? 'rtl' : 'ltr');
    assert.equal(term.childElementCount, 0, 'Labels remain escaped text');
    assert.equal(value.childElementCount, 0, 'Values remain escaped text');
    assert.equal(
      value.closest('[lang]').lang,
      locale,
      'Content value does not inherit its label fallback locale',
    );
    assert.notEqual(term.textContent, field.name, 'Internal field names never become labels');
    assert.equal(term.nextElementSibling, value, 'Every term is followed by its definition');
  }
  assert.equal(
    article.textContent.includes('Omitted optional field'),
    false,
    'Absent optional field has no label or value',
  );
  assert.equal(
    article.textContent.includes('This lesson has no content to display yet.'),
    content.fields.length === 0,
    'Ready empty is distinct from unavailable',
  );
  assert.equal(
    article.textContent.includes('This lesson content is currently unavailable.'),
    false,
  );
  return doc;
}

async function verifyPresentation(native) {
  const [first, second] = configuration.tenants;
  const course = '/' + configuration.locale + '/courses/' + configuration.courseSlug;
  const englishPath = course + '/lessons/' + first.lessonSlug;
  const yogaPath = course + '/lessons/' + second.lessonSlug;
  const turkishPath =
    '/' +
    second.defaultLocale +
    '/courses/' +
    second.defaultCourseSlug +
    '/lessons/' +
    second.defaultLessonSlug;
  const request = (tenant, path, rsc = false) =>
    call(true, 3000, path, { Host: tenant.host + ':3000', ...(rsc ? { RSC: '1' } : {}) });
  const html = async (name, tenant, locale, title, path, content) => {
    stage = name;
    const doc = lessonDocument(await request(tenant, path), tenant, locale, title, path, content);
    await checkpoint(name);
    return doc;
  };
  const rsc = async (name, tenant, path, title, content) => {
    stage = name;
    const response = await request(tenant, path, true);
    safeResponse(response, 200, true);
    lessonContainment(response, tenant);
    for (const value of [
      title,
      content.label.value,
      ...content.fields.flatMap((field) => [field.label.value, field.value]),
    ])
      assert.ok(
        response.body.includes(value) || response.body.includes(JSON.stringify(value).slice(1, -1)),
        'Actual lesson Flight contains the projected text',
      );
    await checkpoint(name);
  };
  await html('presentation-english', first, 'en', first.lessonTitle, englishPath, first.content);
  const englishYoga = await html(
    'presentation-yoga-en',
    second,
    'en',
    second.lessonTitle,
    yogaPath,
    second.content,
  );
  assert.equal(
    englishYoga
      .querySelector('link[rel="alternate"][hreflang="' + second.defaultLocale + '"]')
      ?.getAttribute('href'),
    'https://' + second.host + ':3000' + turkishPath,
    'Lesson alternate uses both actual translated slugs',
  );
  const turkishYoga = await html(
    'presentation-yoga-tr',
    second,
    second.defaultLocale,
    second.defaultLessonTitle,
    turkishPath,
    second.defaultContent,
  );
  assert.equal(
    turkishYoga.querySelector('link[rel="alternate"][hreflang="en"]')?.getAttribute('href'),
    'https://' + second.host + ':3000' + yogaPath,
    'Reverse alternate uses the actual English course and lesson slugs',
  );
  await rsc('presentation-rsc-english', first, englishPath, first.lessonTitle, first.content);
  await rsc(
    'presentation-rsc-yoga-tr',
    second,
    turkishPath,
    second.defaultLessonTitle,
    second.defaultContent,
  );

  // One running app first warms the old pin, then observes a new active revision
  // and generation. The old deprecated pin must still render its old fields.
  stage = 'presentation publish revision';
  await checkpoint('presentation-publish-revision');
  const pinned = await html(
    'presentation-exact-pin',
    first,
    'en',
    first.lessonTitle,
    englishPath,
    first.content,
  );
  assert.equal(
    pinned.body.textContent.includes(configuration.presentation.label.value),
    false,
    'A current revision cannot replace an exact pin',
  );
  await checkpoint('presentation-pin-revision');
  const swapped = await html(
    'presentation-swapped',
    first,
    'en',
    first.lessonTitle,
    englishPath,
    configuration.presentation,
  );
  for (const field of first.content.fields)
    assert.equal(
      swapped.body.textContent.includes(field.value),
      false,
      'New exact pin replaces the old fields',
    );
  await rsc(
    'presentation-rsc-swapped',
    first,
    englishPath,
    first.lessonTitle,
    configuration.presentation,
  );
  await html(
    'presentation-yoga-unchanged',
    second,
    second.defaultLocale,
    second.defaultLessonTitle,
    turkishPath,
    second.defaultContent,
  );

  await checkpoint('presentation-make-empty');
  await html('presentation-empty', first, 'en', first.lessonTitle, englishPath, {
    ...configuration.presentation,
    fields: [],
  });
  await checkpoint('presentation-make-unavailable');
  await html('presentation-unavailable', first, 'en', first.lessonTitle, englishPath, null);
  await checkpoint('presentation-restore-lesson');
  await html('presentation-restored', first, 'en', first.lessonTitle, englishPath, first.content);

  for (const [name, path, locale, uiLocale] of [
    ['presentation-cross-host', englishPath, 'en', 'en'],
    [
      'presentation-protected',
      '/' +
        second.defaultLocale +
        '/courses/' +
        second.restrictedDefaultSlug +
        '/lessons/' +
        second.restrictedDefaultLessonSlug,
      second.defaultLocale,
      'tr',
    ],
  ]) {
    stage = name;
    const refusal = await request(second, path);
    safeResponse(refusal, 307);
    const location = refusal.headers.location;
    const target = new URL(location, 'https://' + second.host + ':3000');
    assert.equal(target.origin, 'https://' + second.host + ':3000');
    assert.equal(target.pathname, '/' + locale + '/status/not-found');
    assert.equal(target.search, '');
    const response = await request(second, target.pathname);
    statusDocument(response, second, locale, uiLocale);
    for (const [index, value] of [
      first.lessonTitle,
      ...first.content.fields.map((field) => field.value),
      ...second.restrictedDefaultCanaries,
    ].entries()) {
      stage = name + ' redirect containment ' + index;
      // Next's redirect Flight tree can contain the request's own route segments.
      // They reveal no new content; titles/body values must still be absent, and
      // the destination below must omit even the original requested slug.
      if (!path.split('/').includes(value))
        assert.equal(
          refusal.body.includes(value),
          false,
          'Refused lesson has no content Flight leak',
        );
      stage = name + ' status containment ' + index;
      assert.equal(
        response.body.includes(value),
        false,
        'Localized missing page has no lesson leak',
      );
    }
    await checkpoint(name);
  }
  assert.equal(native.exitCode, null, 'All schema and content changes use the same native process');
}

// The real Next adapter removes Flight inputs before user middleware, then
// restores them after its request-header override. Observe that final request
// through a disposable admitted route, without exposing any header values.
const protocolProbe = `export const dynamic = 'force-dynamic';

export function GET(request: Request) {
  return Response.json({
    rsc: request.headers.get('rsc') === '1',
    stateTree: request.headers.get('next-router-state-tree') === '%5B%22%22%2C%7B%7D%5D',
    routerPrefetch: request.headers.get('next-router-prefetch') === '1',
    segmentPrefetch: request.headers.get('next-router-segment-prefetch') === '/_tree',
    hmrRefresh: request.headers.get('next-hmr-refresh') === '1',
  }, { headers: { 'cache-control': 'no-store' } });
}
`;

try {
  input = createInterface({ input: process.stdin, terminal: false });
  owner.install({ control: input, event: 'close' });
  lines = input[Symbol.asyncIterator]();
  configuration = JSON.parse(await line());
  assert.match(configuration.apiOrigin, /^http:\/\/127\.0\.0\.1:\d+$/);
  assert.match(configuration.secret, /^[A-Za-z0-9_-]{43}$/);
  assert.equal(configuration.tenants.length, 2);
  const presentation = configuration.mode === 'presentation';
  const foundation =
    configuration.mode === 'foundation' ||
    configuration.mode === 'foundation-pagination' ||
    presentation;
  const pagination = configuration.mode === 'foundation-pagination';
  if (foundation) {
    catalogueCanaries = ['en', 'tr'].map(
      (locale) =>
        JSON.parse(
          readFileSync(join(sourceApp, 'src/i18n/messages', locale, 'public.json'), 'utf8'),
        ).catalog.course_count,
    );
  }
  privateScanner = createPrivateScanner([configuration.secret]);
  owner.assertActive();
  fixtureRoot = owner.ownRoot(mkdtempSync(join(tmpdir(), 'learnstack-public-rendering-')));
  app = join(fixtureRoot, 'frontend/apps/web');
  await vacant(3000);
  await vacant(3011);
  owner.assertActive();
  mkdirSync(app, { recursive: true });
  for (const name of [
    'src',
    'scripts',
    'package.json',
    'next.config.ts',
    'next-env.d.ts',
    'tsconfig.json',
    'tsconfig.server.json',
    'postcss.config.cjs',
    'tailwind.config.ts',
    '.eslintrc.cjs',
  ]) {
    cpSync(join(sourceApp, name), join(app, name), {
      recursive: true,
      filter: (path) =>
        !/\.test\.[jt]sx?$/.test(path) &&
        path !== join(sourceApp, 'src/test') &&
        !path.startsWith(join(sourceApp, 'src/test/')),
    });
  }
  symlinkSync(join(sourceApp, 'node_modules'), join(app, 'node_modules'), 'dir');
  const coursesDirectory = join(app, 'src/app/(public)/[locale]/courses');
  let fixtureClient;
  if (foundation) {
    assert.ok(existsSync(join(coursesDirectory, 'page.tsx')), 'Copied product catalog route');
    assert.ok(existsSync(join(coursesDirectory, '[slug]/page.tsx')), 'Copied product course route');
    if (presentation)
      assert.ok(
        existsSync(join(coursesDirectory, '[slug]/lessons/[lessonSlug]/page.tsx')),
        'Copied product lesson route',
      );
  } else {
    rmSync(join(app, 'src/app/(public)/[locale]'), { recursive: true, force: true });
    // The original transport probe predates product Suspense/error boundaries.
    for (const name of ['loading.tsx', 'error.tsx'])
      rmSync(join(app, 'src/app/(public)', name), { force: true });
    writeFileSync(join(app, 'src/app/layout.tsx'), transportRoot);
    writeFileSync(join(app, 'src/app/(public)/layout.tsx'), transportLayout);
    const fixturePage = join(coursesDirectory, '[[...segments]]/page.tsx');
    mkdirSync(dirname(fixturePage), { recursive: true });
    writeFileSync(fixturePage, page);
    fixtureClient = join(dirname(fixturePage), 'fixture-client.tsx');
    writeFileSync(fixtureClient, clientComponent);
    const fixtureProbe = join(coursesDirectory, 'protocol-probe/route.ts');
    mkdirSync(dirname(fixtureProbe), { recursive: true });
    writeFileSync(fixtureProbe, protocolProbe);
  }
  const nextBin = join(sourceApp, 'node_modules/next/dist/bin/next');

  if (!foundation) {
    stage = 'client import rejection';
    const mutant = join(app, 'src/app/client-import-probe/page.tsx');
    mkdirSync(dirname(mutant), { recursive: true });
    writeFileSync(
      mutant,
      "'use client';\n" +
        "import { createConfiguredPublicClient } from '@/server/configured-public-client';\n" +
        'export default function Probe() { return <p>{String(createConfiguredPublicClient(null))}</p>; }\n',
    );
    const rejected = start(process.execPath, [nextBin, 'build']);
    assert.notEqual(
      await completion(rejected),
      0,
      'Client import mutant must fail a real production build',
    );
    assert.match(rejected.output, /server-only/);
    assert.match(rejected.output, /client-import-probe\/page\.tsx/);
    rmSync(dirname(mutant), { recursive: true });
    rmSync(join(app, '.next'), { recursive: true, force: true });

    stage = 'built client canary rejection';
    // This mutant must compile successfully. Only scanning real emitted browser
    // assets can reject it; source fences or an import failure prove another seam.
    writeFileSync(
      fixtureClient,
      clientComponent.replace(
        '{displayName}</p>',
        '{displayName}{' + JSON.stringify(configuration.secret) + '}</p>',
      ),
    );
    const canary = start(process.execPath, [nextBin, 'build']);
    assert.equal(await completion(canary), 0, 'Client canary builds successfully');
    assert.throws(
      () => scanClientAssets(join(app, '.next/static'), true),
      /Static client private-data containment/,
      'The emitted client canary must be rejected by asset scanning',
    );
    writeFileSync(fixtureClient, clientComponent);
    rmSync(join(app, '.next'), { recursive: true, force: true });
  }

  stage = 'healthy production build';
  const ingress = start(process.execPath, [
    join(sourceApp, 'node_modules/typescript/bin/tsc'),
    '--project',
    'tsconfig.server.json',
  ]);
  assert.equal(await completion(ingress), 0, 'Production ingress compilation');
  const build = start(process.execPath, [nextBin, 'build']);
  assert.equal(await completion(build), 0, 'Healthy production build');
  assert.ok(
    scanClientAssets(join(app, '.next/static'), !foundation).files > 0,
    'Nonempty production client assets',
  );
  await checkpoint('build-complete');

  stage = 'isolated TLS';
  writeFileSync(
    join(fixtureRoot, 'tls.cnf'),
    '[req]\ndistinguished_name=dn\nx509_extensions=ext\nprompt=no\n' +
      '[dn]\nCN=localhost\n[ext]\nsubjectAltName=DNS:localhost,' +
      configuration.tenants.map((tenant) => 'DNS:' + tenant.host).join(',') +
      '\nbasicConstraints=critical,CA:TRUE\n',
  );
  const openssl = start('openssl', [
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
  ]);
  assert.equal(await completion(openssl, 20_000), 0, 'Isolated TLS generation');
  certificate = readFileSync(join(fixtureRoot, 'cert.pem'));
  const native = start(process.execPath, [join(app, 'scripts/public-server.mjs')]);
  await ready(native, true, 3000);
  const [first, second] = configuration.tenants;
  const catalog = '/' + configuration.locale + '/courses';
  const course = catalog + '/' + configuration.courseSlug;

  if (presentation) {
    await verifyPresentation(native);
  } else if (pagination) {
    await verifyPagination(native);
  } else if (foundation) {
    await verifyFoundation(native, nextBin);
  } else {
    stage = 'cold and interleaved host representations';
    const firstCatalog = await representation(first, catalog, false, {
      traceparent: configuration.traceparent,
      'X-LearnStack-Ingress-Provenance': 'forged',
      'X-LearnStack-Host': second.host,
      'X-Forwarded-For': '203.0.113.99',
      Cookie: 'locale=invalid',
    });
    assert.ok(firstCatalog.body.includes(first.courseTitle));
    await checkpoint('trace-supplied');
    assert.ok((await representation(second, catalog)).body.includes(second.courseTitle));
    await checkpoint('trace-missing');
    assert.ok(
      (await representation(first, course, false, { traceparent: 'malformed' })).body.includes(
        first.courseTitle,
      ),
    );
    await checkpoint('trace-malformed');
    assert.ok((await representation(second, course)).body.includes(second.courseTitle));
    for (const path of [catalog, course])
      for (const tenant of configuration.tenants) {
        assert.ok((await representation(tenant, path, true)).body.includes(tenant.courseTitle));
      }
    for (const tenant of configuration.tenants) {
      const lesson = await representation(tenant, course + '/lessons/' + tenant.lessonSlug);
      assert.ok(lesson.body.includes(tenant.lessonTitle), 'Real SDK lesson body');
      assert.ok(lesson.body.includes(tenant.lessonText), 'Public projected field');
    }
    const redirect = await call(true, 3000, '/', { Host: second.host + ':3000' });
    safeResponse(redirect, 307);
    assert.equal(
      redirect.headers.location,
      'https://' + second.host + ':3000/' + second.defaultLocale + '/courses',
    );

    stage = 'real adapter Flight header preservation';
    await checkpoint('protocol-before');
    const absentProtocol = {
      rsc: false,
      stateTree: false,
      routerPrefetch: false,
      segmentPrefetch: false,
      hmrRefresh: false,
    };
    const navigationHeaders = {
      RSC: '1',
      'Next-Router-State-Tree': '%5B%22%22%2C%7B%7D%5D',
    };
    for (const [headers, expected] of [
      [{}, absentProtocol],
      [navigationHeaders, { ...absentProtocol, rsc: true, stateTree: true }],
      [
        { ...navigationHeaders, 'Next-Router-Prefetch': '1' },
        { ...absentProtocol, rsc: true, stateTree: true, routerPrefetch: true },
      ],
      [
        { RSC: '1', 'Next-Router-Segment-Prefetch': '/_tree' },
        { ...absentProtocol, rsc: true, segmentPrefetch: true },
      ],
      [
        { RSC: '1', 'Next-Hmr-Refresh': '1' },
        { ...absentProtocol, rsc: true, hmrRefresh: true },
      ],
    ]) {
      const response = await call(true, 3000, catalog + '/protocol-probe', {
        Host: first.host + ':3000',
        ...headers,
      });
      safeResponse(response, 200, false, /^application\/json/);
      assert.deepEqual(JSON.parse(response.body), expected, 'Final route protocol inputs');
    }
    stage = 'real adapter bodyless HEAD';
    const head = await call(
      true,
      3000,
      catalog + '/protocol-probe',
      { Host: first.host + ':3000', ...navigationHeaders },
      'HEAD',
    );
    safeResponse(head, 200, false, /^application\/json/);
    assert.equal(head.body, '', 'Supported HEAD has no response body');
    await checkpoint('protocol-after');

    stage = 'stock launcher forgery before bootstrap';
    const stock = start(process.execPath, [
      nextBin,
      'start',
      '--hostname',
      '127.0.0.1',
      '--port',
      '3011',
    ]);
    await ready(stock, false, 3011);
    await checkpoint('stock-before');
    for (const rsc of [false, true]) {
      const bypass = await call(false, 3011, course, {
        Host: first.host + ':3000',
        ...(rsc ? { RSC: '1' } : {}),
        'X-LearnStack-Ingress-Provenance': 'forged',
        'X-LearnStack-Host': first.host,
        'X-LearnStack-Visitor-Address': '203.0.113.99',
        'X-Middleware-Subrequest': 'middleware:middleware:middleware:middleware:middleware',
        'X-Middleware-Subrequest-Id': 'attacker',
      });
      safeResponse(bypass, 404);
      assert.equal(bypass.body.includes(first.name), false);
    }
    await checkpoint('stock-after');

    stage = 'same process publication freshness';
    const nativePid = native.pid;
    await checkpoint('make-draft');
    for (const rsc of [false, true]) {
      stage = rsc ? 'draft RSC catalog' : 'draft HTML catalog';
      const fresh = await representation(first, catalog, rsc);
      assert.equal(fresh.body.includes(first.courseTitle), false, 'Draft vanished from catalog');
      stage = rsc ? 'draft RSC detail refusal' : 'draft HTML detail refusal';
      // Next's streamed RSC refusal carries its not-found digest after HTTP 200;
      // the document is 404. Both must discard the previously rendered body.
      const hidden = rsc
        ? await call(true, 3000, course, { Host: first.host + ':3000', RSC: '1' })
        : await representation(first, course, false, {}, 404);
      if (rsc) {
        safeResponse(hidden, 200, true);
        assert.ok(hidden.body.includes('NEXT_HTTP_ERROR_FALLBACK;404'), 'RSC not-found digest');
        assert.equal(
          hidden.body.includes(first.name),
          false,
          'Refused RSC has no tenant representation',
        );
        assert.equal(
          hidden.body.includes(second.name),
          false,
          'Refused RSC has no opposite tenant',
        );
      }
      assert.equal(hidden.body.includes(first.courseTitle), false, 'Draft course is hidden');
    }
    stage = 'unaffected tenant after draft';
    assert.ok((await representation(second, course)).body.includes(second.courseTitle));
    assert.equal(native.pid, nativePid);
    assert.equal(native.exitCode, null, 'Freshness used the same live native process');
    await checkpoint('restore-published');
    stage = 'restored publication';
    assert.ok(
      (await representation(first, course)).body.includes(first.courseTitle),
      'Restored row is fresh too',
    );
  }

  stage = 'private output containment';
  assert.equal(
    scanners.some((scanner) => scanner.leaked),
    false,
    'Private data entered child logs',
  );
  assert.ok(scanClientAssets(join(app, '.next/static'), !foundation).files > 0);
  await checkpoint('verified');
} catch {
  // Assertions and compiler/provider errors may contain private values. Report
  // only this finite test-owned stage; raw stdout/stderr never leave this process.
  process.stderr.write('Production rendering fixture failed during ' + stage + '.\n');
  process.exitCode = 1;
} finally {
  try {
    await owner.dispose();
    assert.equal(
      scanners.some((scanner) => scanner.leaked),
      false,
      'Private shutdown logs',
    );
  } catch {
    process.stderr.write('Production rendering fixture cleanup failed.\n');
    process.exitCode = 1;
  }
  input?.close();
}
