// @vitest-environment node
import { EventEmitter } from 'node:events';
import { readFileSync } from 'node:fs';
import { gzipSync } from 'node:zlib';

import { describe, expect, it } from 'vitest';

import {
  apiCompletionObserver,
  completionDelta,
  finalCompletionCensus,
} from '../../../../../scripts/demo-observation.mjs';
import {
  admitProduct,
  assertConfiguration,
  decodedSource,
  normalizedJavaScript,
  reportInventory,
  validateReport,
} from '../../scripts/public-demo-contract.mjs';
import { PublicDemoDocument, resolvedDemoCensus } from '../../scripts/public-demo-gatherer.mjs';

const census = JSON.parse(
  readFileSync(new URL('../../scripts/public-demo-census.json', import.meta.url), 'utf8'),
) as {
  audits: string[];
  categories: Record<string, Array<{ id: string; weight: number }>>;
};
const source = 'console.log("source");';
const html =
  '<!doctype html><html><head><meta name="viewport" content="width=device-width, initial-scale=1"></head><body><script>one\r\ntwo</script><script type="application/json">ignored</script><script src="/one.js">ignored</script><template><script>inert</script></template><script type="module">three</script></body></html>';
const inventory = () => [
  {
    id: 'one',
    url: 'https://public.example/one.js',
    status: 200,
    complete: true,
    body: { body: source, base64Encoded: false },
  },
];
const lane = {
  locale: 'en',
  displayName: 'Independent institution',
  catalogUrl: 'https://public.example/en/courses',
  courseUrl: 'https://public.example/en/courses/practice',
  lessonUrl: 'https://public.example/en/courses/practice/lessons/first',
  courseTitle: 'A practice',
  lessonTitle: 'A lesson',
  fields: [{ label: 'Label', value: 'Value' }],
  theme: { primary: '#112233', background: '#ffffff', foreground: '#111111', muted: '#555555' },
  excludedTitles: ['Foreign title'],
  excludedContent: ['Protected lesson'],
};
const document = () => ({
  status: 200,
  headers: { 'content-type': 'text/html', 'cache-control': 'no-store' },
  redirects: 0,
  html,
  dom: {
    url: lane.lessonUrl,
    lang: 'en',
    dir: 'ltr',
    headings: [lane.lessonTitle],
    institutions: [lane.displayName],
    mains: 1,
    states: 0,
    noindex: false,
    kinds: ['lesson'],
    theme: { primary: '#112233', bg: '#ffffff', fg: '#111111', muted: '#555555' },
    foreground: 'rgb(17, 17, 17)',
    background: 'rgb(255, 255, 255)',
    muted: 'rgb(85, 85, 85)',
    fields: [
      {
        label: 'Label',
        value: 'Value',
        labelLang: 'en',
        labelDir: 'ltr',
        valueLang: 'en',
        adjacent: true,
        children: 0,
      },
    ],
    links: [],
  },
});

describe('Public_Demo_Uses_Seeded_Production_Pages', () => {
  it('admits the actual seeded document and computed complete palette', () => {
    expect(() => admitProduct(document(), lane, 'lesson', 'Catalog')).not.toThrow();
  });
  it.each([
    'status',
    'url',
    'locale',
    'institution',
    'heading',
    'error',
    'noindex',
    'theme',
    'field',
    'active-field',
    'containment',
  ])('refuses a planted %s product offender', (kind) => {
    const value = document();
    switch (kind) {
      case 'status':
        value.status = 503;
        break;
      case 'url':
        value.dom.url += '?wrong=1';
        break;
      case 'locale':
        value.dom.lang = 'tr-TR';
        break;
      case 'institution':
        value.dom.institutions = ['Foreign institution'];
        break;
      case 'heading':
        value.dom.headings = ['Fallback'];
        break;
      case 'error':
        value.dom.states = 1;
        break;
      case 'noindex':
        value.dom.noindex = true;
        break;
      case 'theme':
        value.dom.theme.bg = '#000000';
        break;
      case 'field':
        value.dom.fields[0]!.value = 'Different';
        break;
      case 'active-field':
        value.dom.fields[0]!.children = 1;
        break;
      case 'containment':
        value.html += '<p>Protected lesson</p>';
        break;
    }
    expect(() => admitProduct(value, lane, 'lesson', 'Catalog')).toThrow();
    expect(() => admitProduct(document(), lane, 'lesson', 'Catalog')).not.toThrow();
  });
});

describe('Public_Initial_JavaScript_Gzip_Budget_Is_Measured', () => {
  it('counts each delivered source, async/cross-origin included, and original inline CRLF bytes', () => {
    const rows = [
      ...inventory(),
      { ...inventory()[0]!, id: 'two', url: 'https://other.example/lazy.js' },
    ];
    const result = normalizedJavaScript(html, rows);
    expect(result.scripts).toHaveLength(2);
    expect(result.inline.count).toBe(2);
    expect(result.inline.bytes).toBe(Buffer.byteLength('one\r\ntwo\nthree'));
    expect(result.total).toBe(
      2 * gzipSync(source, { level: 9 }).length +
        gzipSync('one\r\ntwo\nthree', { level: 9 }).length,
    );
  });
  it.each([
    'empty',
    'duplicate',
    'incomplete',
    'failed',
    'missing',
    'malformed-base64',
    'invalid-utf8',
    'replacement',
    'surrogate',
  ])('refuses %s sources with a clean control', (kind) => {
    const rows = inventory();
    if (kind === 'empty') rows.length = 0;
    else if (kind === 'duplicate') rows.push({ ...rows[0]! });
    else if (kind === 'incomplete') rows[0]!.complete = false;
    else if (kind === 'failed') rows[0]!.status = 404;
    else
      rows[0]!.body =
        kind === 'missing'
          ? { body: '', base64Encoded: false }
          : kind === 'invalid-utf8'
            ? { body: '/w==', base64Encoded: true }
            : kind === 'malformed-base64'
              ? { body: 'abc!', base64Encoded: true }
              : { body: kind === 'replacement' ? '\ufffd' : '\ud800', base64Encoded: false };
    expect(() => normalizedJavaScript(html, rows)).toThrow();
    expect(() => normalizedJavaScript(html, inventory())).not.toThrow();
  });
  it('decodes complete strict UTF-8 base64 and refuses an absent CDP body', () => {
    expect(
      decodedSource({ body: Buffer.from('Türkçe').toString('base64'), base64Encoded: true }),
    ).toBe('Türkçe');
    expect(() => decodedSource(undefined)).toThrow();
  });
  it('counts executable legacy JavaScript MIME types and excludes inert data', () => {
    const types = [
      'application/x-javascript',
      'text/javascript1.5',
      'text/jscript',
      'text/livescript',
      'text/x-ecmascript',
      'TEXT/JAVASCRIPT; charset=utf-8',
    ];
    const original =
      types.map((type) => `<script type="${type}">active</script>`).join('') +
      '<script type="application/ld+json">inert</script>';
    expect(normalizedJavaScript(original, inventory()).inline.count).toBe(types.length);
  });
  it('refuses measured over-budget source bytes through the report policy', () => {
    let state = 1;
    const body = Array.from({ length: 350000 }, () => {
      state ^= state << 13;
      state ^= state >>> 17;
      state ^= state << 5;
      return String.fromCharCode(33 + ((state >>> 0) % 90));
    }).join('');
    const measured = normalizedJavaScript(html, [
      { ...inventory()[0]!, body: { body, base64Encoded: false } },
    ]);
    expect(measured.total).toBeGreaterThanOrEqual(200000);
    const report = cleanReport();
    report.audits['initial-js-gzip-bytes']!.numericValue = measured.total;
    expect(() => validateReport(report, { url: lane.lessonUrl }, census)).toThrow();
    expect(() => validateReport(cleanReport(), { url: lane.lessonUrl }, census)).not.toThrow();
  });
});

function cleanReport() {
  const audits: Record<string, { scoreDisplayMode: string; numericValue?: number }> =
    Object.fromEntries(census.audits.map((id) => [id, { scoreDisplayMode: 'binary' }]));
  for (const id of [
    'initial-js-gzip-bytes',
    'cumulative-layout-shift',
    'largest-contentful-paint',
    'server-response-time',
    'total-blocking-time',
  ])
    audits[id]!.numericValue = 0;
  return {
    lighthouseVersion: '13.5.0',
    runWarnings: [] as string[],
    requestedUrl: lane.lessonUrl,
    mainDocumentUrl: lane.lessonUrl,
    finalDisplayedUrl: lane.lessonUrl,
    categories: Object.fromEntries(
      Object.entries(census.categories).map(([id, auditRefs]) => [id, { auditRefs, score: 1 }]),
    ),
    audits,
  };
}
describe('Public_Lighthouse_Reports_Enforce_Budgets', () => {
  it('resolves the installed pinned Lighthouse configuration against the independent receipt', async () => {
    expect(() => assertConfiguration({ ...census, audits: [] }, census)).toThrow();
    assertConfiguration(
      await resolvedDemoCensus({
        port: 12345,
        hostname: '127.0.0.1',
        logLevel: 'error',
        output: ['json', 'html'],
        maxWaitForLoad: 15000,
      }),
      census,
    );
  });
  it.each([
    'missing',
    'runtime',
    'warning',
    'audit',
    'nonfinite',
    'gzip',
    'cls',
    'accessibility',
    'weights',
    'redirect',
  ])('refuses a planted %s report', (kind) => {
    const value = structuredClone(cleanReport());
    if (kind === 'missing') delete value.audits[census.audits[0]!];
    if (kind === 'runtime') Object.assign(value, { runtimeError: { code: 'failure' } });
    if (kind === 'warning') value.runWarnings.push('Results may be incomplete');
    if (kind === 'audit') value.audits[census.audits[0]!]!.scoreDisplayMode = 'error';
    if (kind === 'nonfinite') value.audits['largest-contentful-paint']!.numericValue = NaN;
    if (kind === 'gzip') value.audits['initial-js-gzip-bytes']!.numericValue = 200000;
    if (kind === 'cls') value.audits['cumulative-layout-shift']!.numericValue = 0.05;
    if (kind === 'accessibility') value.categories.accessibility!.score = 0.99;
    if (kind === 'weights') value.categories.accessibility!.auditRefs[0]!.weight = 999;
    if (kind === 'redirect') value.finalDisplayedUrl = lane.catalogUrl;
    expect(() => validateReport(value, { url: lane.lessonUrl }, census)).toThrow();
    expect(() => validateReport(cleanReport(), { url: lane.lessonUrl }, census)).not.toThrow();
  });
  it('allows high advisory timing and legal N/A weight zeroing, preserving configured census', () => {
    const value = structuredClone(cleanReport());
    value.audits['largest-contentful-paint']!.numericValue = 99999;
    const reference = value.categories.accessibility!.auditRefs.find((row) => row.weight > 0)!;
    value.audits[reference.id]!.scoreDisplayMode = 'notApplicable';
    reference.weight = 0;
    expect(() => validateReport(value, { url: lane.lessonUrl }, census)).not.toThrow();
    expect(() => assertConfiguration({ ...census, audits: [] }, census)).toThrow();
    expect(() => assertConfiguration(census, census)).not.toThrow();
  });
  it('refuses duplicate, stale, omitted and cross-invocation reports', () => {
    const expected = Array.from({ length: 27 }, (_, index) => ({
      url: 'https://public.example/' + Math.floor(index / 3),
      run: (index % 3) + 1,
      commit: 'a',
      build: 'b',
      invocation: 'c',
      configDigest: 'd',
      browserVersion: 'e',
    }));
    expect(() => reportInventory(expected, expected)).not.toThrow();
    for (const rows of [
      expected.slice(1),
      expected.map((row, index) => (index ? row : expected[1])),
      expected.map((row, index) => (index ? row : { ...row, commit: 'stale' })),
      expected.map((row, index) => (index ? row : { ...row, invocation: 'foreign' })),
      expected.map((row, index) => (index ? row : { ...row, configDigest: 'different' })),
      expected.map((row, index) => (index ? row : { ...row, browserVersion: 'different' })),
    ])
      expect(() => reportInventory(rows, expected)).toThrow();
  });
});

describe('Public_Demo_Preserves_Admission_And_Cleanup observer', () => {
  const completion = (status: number) =>
    JSON.stringify({
      SourceContext: 'Microsoft.AspNetCore.Hosting.Diagnostics',
      EventId: { Id: 2 },
      Path: '/api/v1/public/site',
      StatusCode: status,
      Method: 'GET',
    });
  it.each([
    'unterminated',
    'malformed',
    'missing-brace',
    'prefixed',
    'unexpected-endpoint',
    'eof',
    'late-success',
    'late-refusal',
  ])('refuses actual %s records after a successful positive control', (kind) => {
    const observer = apiCompletionObserver();
    const before = structuredClone(observer.state);
    observer.consume(Buffer.from(completion(200) + '\n'));
    expect(completionDelta(before, observer.state, 1).count).toBe(1);
    const beforeCleanup = structuredClone(observer.state);
    if (kind === 'malformed') observer.consume(Buffer.from('{"StatusCode":429\n'));
    else if (kind === 'missing-brace')
      observer.consume(Buffer.from(completion(429).slice(1) + '\n'));
    else if (kind === 'prefixed') observer.consume(Buffer.from('prefix ' + completion(429) + '\n'));
    else if (kind === 'unexpected-endpoint')
      observer.consume(
        Buffer.from(completion(429).replace('/api/v1/public/site', '/api/v1/other') + '\n'),
      );
    else if (kind === 'late-success' || kind === 'late-refusal')
      observer.consume(Buffer.from(completion(kind === 'late-success' ? 200 : 429) + '\n'));
    else observer.consume(Buffer.from(completion(429)));
    if (kind === 'unterminated') expect(() => completionDelta(before, observer.state, 1)).toThrow();
    observer.finish();
    expect(() => finalCompletionCensus(beforeCleanup, observer.state, 1)).toThrow();
  });
  it('keeps interleaved streams separate and confirms joined clean output', () => {
    const observer = apiCompletionObserver();
    const before = structuredClone(observer.state);
    const value = completion(200);
    observer.consume(Buffer.from(value.slice(0, 30)), 'stdout');
    observer.consume(
      Buffer.from(JSON.stringify({ SourceContext: 'Startup', Message: 'Ready' }) + '\n'),
      'stderr',
    );
    expect(() => completionDelta(before, observer.state, 0)).toThrow();
    observer.consume(Buffer.from(value.slice(30) + '\n'), 'stdout');
    expect(completionDelta(before, observer.state, 1).count).toBe(1);
    const beforeCleanup = structuredClone(observer.state);
    observer.finish();
    expect(() => finalCompletionCensus(beforeCleanup, observer.state, 1)).not.toThrow();
    expect(() => finalCompletionCensus(beforeCleanup, observer.state, 2)).toThrow();
  });
  it('observes split UTF-8 completion lines while retaining only bounded aggregates', () => {
    const observer = apiCompletionObserver();
    const before = structuredClone(observer.state);
    const event = Buffer.from(
      JSON.stringify({
        SourceContext: 'Microsoft.AspNetCore.Hosting.Diagnostics',
        EventId: { Id: 2 },
        Path: '/api/v1/public/site',
        StatusCode: 200,
        Method: 'GET',
        private: 'Türkçe',
      }) + '\n',
    );
    for (const byte of event) observer.consume(Buffer.from([byte]));
    expect(completionDelta(before, observer.state, 1).count).toBe(1);
    expect(JSON.stringify(observer.state)).not.toContain('private');
    expect(() => completionDelta(before, { ...observer.state, total: 2 }, 1)).toThrow();
    expect(() => completionDelta(before, { ...observer.state, statuses: { 429: 1 } }, 1)).toThrow();
    expect(() => completionDelta(before, { ...observer.state, malformed: true }, 1)).toThrow();
  });
});

describe('actual initial source gatherer completeness controls', () => {
  it.each(['clean', 'missing-body', 'incomplete', 'foreign-target', 'truncated'])(
    'exercises the actual collector with %s',
    async (kind) => {
      const session = Object.assign(new EventEmitter(), {
        id: () => 'main',
        sendCommand: async (name: string, args: { requestId?: string } = {}) => {
          if (name === 'Page.getFrameTree') return { frameTree: { frame: { id: 'frame' } } };
          if (name !== 'Network.getResponseBody') return {};
          if (kind === 'missing-body') throw new Error('unavailable');
          return { body: args.requestId === 'doc' ? html : source, base64Encoded: false };
        },
      });
      const targets = new EventEmitter();
      const context = {
        driver: {
          defaultSession: session,
          targetManager: targets,
          executionContext: { evaluate: async () => ({ url: lane.lessonUrl }) },
        },
      };
      const gatherer = new PublicDemoDocument();
      await gatherer.startInstrumentation(context);
      for (const [id, type] of [
        ['doc', 'Document'],
        ['js', 'Script'],
      ]) {
        session.emit('Network.requestWillBeSent', {
          requestId: id,
          type,
          frameId: 'frame',
          request: { url: lane.lessonUrl + (id === 'js' ? '.js' : '') },
        });
        session.emit('Network.responseReceived', {
          requestId: id,
          response: { status: 200, headers: {} },
        });
        if (kind === 'truncated')
          session.emit('Network.dataReceived', { requestId: id, dataLength: 1 });
        if (kind !== 'incomplete') session.emit('Network.loadingFinished', { requestId: id });
      }
      if (kind === 'foreign-target')
        targets.emit('protocolevent', {
          method: 'Network.responseReceived',
          sessionId: 'other',
          params: { type: 'Script' },
        });
      if (kind === 'clean')
        await expect(gatherer.getArtifact(context)).resolves.toHaveProperty('gzip');
      else await expect(gatherer.getArtifact(context)).rejects.toThrow();
      expect(session.eventNames()).toEqual([]);
      expect(targets.eventNames()).toEqual([]);
    },
  );
});
