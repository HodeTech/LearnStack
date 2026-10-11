import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { createRequire } from 'node:module';
import { gzipSync } from 'node:zlib';

import { absentFromWholeResponse, documentViewport } from './public-rendering-assertions.mjs';

const { JSDOM } = createRequire(import.meta.url)('jsdom');
export const digest = (value) => createHash('sha256').update(value).digest('hex');

/** Serializable DOM observation, executed in the same target that was navigated/audited. */
export function productDomSnapshot() {
  const visible = (node) => {
    if (!node || node.closest('[hidden],template')) return false;
    const style = getComputedStyle(node);
    const box = node.getBoundingClientRect();
    return (
      style.display !== 'none' &&
      style.visibility === 'visible' &&
      Number(style.opacity) !== 0 &&
      node.checkVisibility({ checkOpacity: true, checkVisibilityCSS: true }) &&
      box.width > 0 &&
      box.height > 0
    );
  };
  const pick = (selector) => [...document.querySelectorAll(selector)].filter(visible);
  const root = getComputedStyle(document.documentElement);
  const muted = pick('.public-muted')[0];
  return {
    url: location.href,
    lang: document.documentElement.lang,
    dir: document.documentElement.dir,
    headings: pick('h1').map((node) => node.textContent),
    institutions: pick('.public-institution').map((node) => node.textContent),
    mains: pick('main').length,
    states: document.querySelectorAll('.public-state').length,
    noindex: [...document.querySelectorAll('meta[name="robots"]')].some((node) =>
      /noindex/i.test(node.content),
    ),
    kinds: ['catalog', 'course', 'lesson'].filter((kind) => pick('.public-' + kind).length === 1),
    theme: Object.fromEntries(
      ['primary', 'bg', 'fg', 'muted'].map((key) => [
        key,
        root.getPropertyValue('--ls-' + key).trim(),
      ]),
    ),
    foreground: getComputedStyle(document.body).color,
    background: getComputedStyle(document.body).backgroundColor,
    muted: muted ? getComputedStyle(muted).color : null,
    fields: pick('.public-lesson-field').map((node) => {
      const term = node.querySelector('dt');
      const value = node.querySelector('dd');
      return {
        label: visible(term) ? term.textContent : undefined,
        value: visible(value) ? value.textContent : undefined,
        labelLang: term?.lang,
        labelDir: term?.dir,
        valueLang: value?.closest('[lang]')?.lang,
        adjacent: term?.nextElementSibling === value,
        children: (term?.childElementCount ?? -1) + (value?.childElementCount ?? -1),
      };
    }),
    links: pick('.public-course-card h2 a, .public-lesson-list a').map((node) => node.href),
  };
}

const rgb = (hex) =>
  `rgb(${[1, 3, 5].map((index) => parseInt(hex.slice(index, index + 2), 16)).join(', ')})`;

export function admitProduct(document, lane, kind, catalogTitle) {
  const url = lane[kind + 'Url'];
  assert.equal(document.status, 200, 'Actual product document status');
  assert.match(document.headers['content-type'] ?? '', /^text\/html/i);
  assert.match(document.headers['cache-control'] ?? '', /no-store/);
  assert.equal(document.redirects, 0, 'Product navigation has no redirects');
  const { dom, html } = document;
  assert.equal(dom.url, url, 'Actual displayed URL');
  assert.equal(dom.lang, lane.locale, 'Actual admitted content locale');
  assert.equal(dom.dir, 'ltr');
  assert.deepEqual(
    dom.headings,
    [kind === 'catalog' ? catalogTitle : lane[kind + 'Title']],
    'Visible seeded page identity',
  );
  assert.deepEqual(dom.institutions, [lane.displayName], 'Visible institution identity');
  assert.equal(dom.mains, 1);
  assert.equal(dom.states, 0, 'No controlled state can satisfy a product audit');
  assert.equal(dom.noindex, false, 'No refusal/fallback document');
  assert.deepEqual(dom.kinds, [kind]);
  assert.deepEqual(
    dom.theme,
    {
      primary: lane.theme.primary,
      bg: lane.theme.background,
      fg: lane.theme.foreground,
      muted: lane.theme.muted,
    },
    'Computed complete palette',
  );
  assert.equal(dom.foreground, rgb(lane.theme.foreground));
  assert.equal(dom.background, rgb(lane.theme.background));
  if (dom.muted !== null) assert.equal(dom.muted, rgb(lane.theme.muted));
  documentViewport(new JSDOM(html).window.document);
  assert.ok(lane.excludedTitles.length > 0, 'Containment markers are nonempty');
  absentFromWholeResponse({ body: html }, lane.excludedTitles);
  assert.ok(lane.excludedContent.length > 0, 'Protected content markers are nonempty');
  absentFromWholeResponse({ body: html }, lane.excludedContent);
  if (kind === 'catalog') assert.ok(dom.links.includes(lane.courseUrl), 'Rendered course link');
  if (kind === 'course') assert.ok(dom.links.includes(lane.lessonUrl), 'Rendered lesson link');
  if (kind === 'lesson')
    assert.deepEqual(
      dom.fields,
      lane.fields.map((field) => ({
        label: field.label,
        value: field.value,
        labelLang: lane.locale,
        labelDir: 'ltr',
        valueLang: lane.locale,
        adjacent: true,
        children: 0,
      })),
      'Exact ordered authored lesson fields',
    );
}

export function decodedSource(body) {
  assert.equal(typeof body?.body, 'string', 'Complete CDP body is required');
  let text;
  if (body.base64Encoded) {
    assert.match(
      body.body,
      /^(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?$/,
      'Canonical base64 source',
    );
    const bytes = Buffer.from(body.body, 'base64');
    assert.equal(bytes.toString('base64'), body.body);
    text = new TextDecoder('utf-8', { fatal: true }).decode(bytes);
  } else text = body.body;
  assert.ok(text.isWellFormed() && !text.includes('\ufffd'), 'Undecodable source is refused');
  assert.ok(Buffer.byteLength(text) <= 8 * 1024 * 1024, 'Source byte bound');
  return text;
}

export function normalizedJavaScript(document, inventory) {
  assert.ok(
    Array.isArray(inventory) && inventory.length > 0 && inventory.length <= 1000,
    'Nonempty bounded Script census',
  );
  const ids = new Set();
  const scripts = inventory.map((row) => {
    assert.equal(ids.has(row.id), false, 'Each delivered response is counted once');
    ids.add(row.id);
    assert.equal(row.status, 200, 'Every delivered Script response succeeds');
    assert.equal(row.complete, true, 'Truncated/incomplete source is refused');
    assert.ok(/^https?:\/\//.test(row.url), 'External source URL');
    const text = decodedSource(row.body);
    assert.ok(text.length > 0, 'Unavailable source never counts as zero');
    return {
      id: row.id,
      url: row.url,
      digest: digest(text),
      bytes: Buffer.byteLength(text),
      gzip: gzipSync(text, { level: 9 }).length,
    };
  });
  const dom = new JSDOM(document, { includeNodeLocations: true });
  const bodies = [...dom.window.document.querySelectorAll('script')]
    .filter((node) => {
      const type = (node.getAttribute('type') ?? '').split(';')[0].trim().toLowerCase();
      return (
        !node.hasAttribute('src') &&
        (!type ||
          type === 'module' ||
          /^(?:application\/(?:x-)?(?:java|ecma)script|text\/(?:(?:x-)?(?:java|ecma)script|javascript1\.[0-5]|jscript|livescript))$/.test(
            type,
          ))
      );
    })
    .map((node) => {
      const location = dom.nodeLocation(node);
      assert.ok(location?.startTag && location?.endTag, 'Original inline source bounds');
      return document.slice(location.startTag.endOffset, location.endTag.startOffset);
    });
  assert.ok(bodies.length > 0, 'Original Flight/executable inline census');
  const inlineText = bodies.join('\n');
  decodedSource({ body: inlineText, base64Encoded: false });
  const inline = {
    count: bodies.length,
    digest: digest(inlineText),
    bytes: Buffer.byteLength(inlineText),
    gzip: gzipSync(inlineText, { level: 9 }).length,
  };
  const bytes = scripts.reduce((sum, row) => sum + row.bytes, inline.bytes);
  assert.ok(bytes <= 16 * 1024 * 1024, 'Total source byte bound');
  return { scripts, inline, total: scripts.reduce((sum, row) => sum + row.gzip, inline.gzip) };
}

/** Settings and configured weights are immutable; LHR legally zeros manual/N/A weights. */
export function assertConfiguration(actual, expected) {
  assert.deepEqual(actual, expected, 'Pinned resolved audit/artifact/category census');
}

export function validateReport(lhr, expected, census) {
  assert.equal(lhr.lighthouseVersion, '13.5.0');
  assert.equal(lhr.runtimeError, undefined, 'Runtime errors refuse the report');
  assert.deepEqual(lhr.runWarnings, [], 'Incomplete/warning audit reports are refused');
  for (const key of ['requestedUrl', 'mainDocumentUrl', 'finalDisplayedUrl'])
    assert.equal(lhr[key], expected.url, key);
  assert.deepEqual(Object.keys(lhr.categories).sort(), ['accessibility', 'performance']);
  assert.deepEqual(Object.keys(lhr.audits).sort(), census.audits);
  for (const result of Object.values(lhr.audits)) {
    assert.notEqual(result.scoreDisplayMode, 'error', 'Audit errors refuse the report');
    assert.equal(result.errorMessage, undefined, 'Audit error message');
  }
  for (const [name, category] of Object.entries(census.categories)) {
    const references = lhr.categories[name].auditRefs;
    assert.deepEqual(
      references.map((row) => ({ id: row.id, weight: row.weight })),
      category.map((row) => ({
        id: row.id,
        weight: ['notApplicable', 'informative', 'manual'].includes(
          lhr.audits[row.id].scoreDisplayMode,
        )
          ? 0
          : row.weight,
      })),
      'Complete report weight set',
    );
  }
  const metrics = {};
  for (const key of [
    'initial-js-gzip-bytes',
    'cumulative-layout-shift',
    'largest-contentful-paint',
    'server-response-time',
    'total-blocking-time',
  ]) {
    const value = lhr.audits[key]?.numericValue;
    assert.ok(
      typeof value === 'number' && Number.isFinite(value) && value >= 0,
      'Finite measured ' + key,
    );
    metrics[key] = value;
  }
  assert.ok(metrics['initial-js-gzip-bytes'] < 200000, 'Initial JavaScript gzip budget');
  assert.ok(metrics['cumulative-layout-shift'] < 0.05, 'CLS budget');
  assert.equal(lhr.categories.accessibility.score, 1, 'Automated accessibility budget');
  return metrics;
}

export function reportInventory(rows, expected) {
  assert.equal(rows.length, 27, 'Exactly 27 successful reports');
  assert.deepEqual(
    rows.map((row) => `${row.url}#${row.run}`).sort(),
    expected.map((row) => `${row.url}#${row.run}`).sort(),
    'Complete unique URL/run census',
  );
  for (const row of rows) {
    assert.equal(row.commit, expected[0].commit, 'Exact invocation commit');
    assert.equal(row.build, expected[0].build, 'Exact invocation build');
    assert.equal(row.invocation, expected[0].invocation, 'Fresh invocation receipt');
    assert.equal(row.configDigest, expected[0].configDigest, 'Exact audited configuration');
    assert.equal(row.browserVersion, expected[0].browserVersion, 'Exact audited browser');
  }
}
