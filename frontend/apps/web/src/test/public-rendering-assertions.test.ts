// @vitest-environment node
import { createRequire } from 'node:module';

import { describe, expect, it } from 'vitest';

import {
  absentFromSerializedText,
  absentFromWholeResponse,
  documentViewport,
  documentOpenGraphLocales,
  productContainment,
  productTheme,
  statusDocument,
  visibleDocument,
} from '../../scripts/public-rendering-assertions.mjs';

const require = createRequire(import.meta.url);
const { JSDOM } = require('jsdom') as {
  JSDOM: new (html: string) => { window: { document: Document } };
};
const { htmlEscapeJsonString } = require('next/dist/shared/lib/htmlescape') as {
  htmlEscapeJsonString: (value: string) => string;
};
const flight = (value: string) =>
  `<script>self.__next_f.push(${htmlEscapeJsonString(JSON.stringify([1, `0:${JSON.stringify({ value })}\n`]))})</script>`;
const response = (body: string) => ({
  status: 404,
  headers: { 'content-type': 'text/html' },
  body,
});
const first = { host: 'first.example', name: 'First institution' };
const second = { host: 'second.example', name: 'Other & institution' };
const product = (host: string) => ({
  host,
  protectedCanaries: [`${host} protected & <secret>`],
  allCanaries: [`${host} private`],
  courseTitle: `${host} course`,
  courseSummary: `${host} summary`,
  lessonTitle: `${host} lesson`,
  content: { fields: [{ value: `${host} value & <secret>` }] },
});
const configuration = {
  tenants: [first, second],
  product: [product(first.host), product(second.host)],
};
const sentinel = '{count, plural, one {# course} other {# courses}}';

// The production fixture calls these same predicates; controls alter one seam
// while retaining a known-good document or response for the others.
describe('production rendering containment assertions', () => {
  it.each([
    'A & B < C > D',
    'quotes " and \\ slash',
    'mixed " \\ & < > \u2028 \u2029',
    'line\u2028separator\u2029end',
  ])('rejects an inline Flight-only leak encoded by pinned Next: %j', (value) => {
    const body = flight(value);
    expect(() => absentFromWholeResponse(response(body), [value])).toThrow('containment');
    expect(() => absentFromSerializedText(body, [value])).toThrow('containment');
    expect(() =>
      absentFromWholeResponse(response(flight('independent public value')), [value]),
    ).not.toThrow();
  });

  it.each([
    '<p>private &amp; &lt;value&gt;</p>',
    '<p title="private &amp; &lt;value&gt;">Public text</p>',
    '<script>private & <value></script>',
  ])('rejects raw and entity-encoded leaks anywhere in HTML: %s', (body) => {
    expect(() => absentFromWholeResponse(response(body), ['private & <value>'])).toThrow(
      'containment',
    );
    expect(() =>
      absentFromWholeResponse(response('<p title="Public">Public text</p>'), ['private & <value>']),
    ).not.toThrow();
  });

  it('refuses an empty marker instead of making a vacuous assertion', () => {
    expect(() => absentFromWholeResponse(response('clean'), [''])).toThrow('nonempty');
  });

  it.each([
    second.name,
    configuration.product[1]!.allCanaries[0]!,
    configuration.product[1]!.content.fields[0]!.value,
    configuration.product[0]!.protectedCanaries[0]!,
    sentinel,
  ])('rejects opposite, protected and unused catalogue values in Flight: %s', (value) => {
    const check = (body: string) =>
      productContainment(response(body), first, configuration, [sentinel]);
    expect(() => check(`<h1>${first.name}</h1>${flight(value)}`)).toThrow('containment');
    expect(() => check(`<h1>${first.name}</h1>${flight('public own content')}`)).not.toThrow();
  });

  it('detects an unchanged complete catalogue through its unused ICU sentinel', () => {
    const bundle = JSON.stringify({ catalog: { title: 'Courses', course_count: sentinel } });
    expect(() => absentFromSerializedText(flight(bundle), [sentinel])).toThrow('containment');
    expect(() => absentFromSerializedText(flight('Courses'), [sentinel])).not.toThrow();
  });
});

const details = {
  theme: { primary: '#123456', background: '#ffffff', foreground: '#111111', muted: '#555555' },
  showAttribution: true,
  locale: 'en',
};
const palette = ':root{--ls-primary:#123456;--ls-bg:#ffffff;--ls-fg:#111111;--ls-muted:#555555;}';
const defaults = ':root{--ls-primary:#1f6feb;--ls-bg:#fff;--ls-fg:#0f172a;--ls-muted:#64748b;}';
const themeDoc = (
  style = `<style>${palette}</style>`,
  footer = '<footer class="public-footer">Powered by LearnStack</footer>',
) => new JSDOM(`<html><head>${style}</head><body>${footer}</body></html>`).window.document;

describe('production theme assertion controls', () => {
  it.each([
    '<style>:root{--ls-primary:#123456;}</style>',
    `<style>${palette}</style><p style="--ls-muted:#555555">Text</p>`,
    '<p style="--ls-primary:#123456;--ls-bg:#ffffff;--ls-fg:#111111;--ls-muted:#555555;">Text</p>',
  ])('rejects partial, duplicate and inline palette emission: %s', (style) => {
    expect(() => productTheme(themeDoc(style), details)).toThrow();
    expect(() => productTheme(themeDoc(), details)).not.toThrow();
  });

  it('rejects attribution that disagrees with effective entitlement', () => {
    expect(() => productTheme(themeDoc(undefined, ''), details)).toThrow(
      'Effective tenant entitlement alone selects attribution',
    );
    expect(() => productTheme(themeDoc(), { ...details, showAttribution: false })).toThrow(
      'Effective tenant entitlement alone selects attribution',
    );
    expect(() => productTheme(themeDoc(), details)).not.toThrow();
    expect(() =>
      productTheme(themeDoc(undefined, ''), { ...details, showAttribution: false }),
    ).not.toThrow();
  });

  it('requires whole compiled defaults and no override for malformed input', () => {
    const link = '<link rel="stylesheet" href="/_next/static/css/theme.css">';
    expect(() => productTheme(themeDoc(link), details, true, () => defaults)).not.toThrow();
    expect(() =>
      productTheme(
        themeDoc(link + '<style>:root{--ls-primary:#abcdef;}</style>'),
        details,
        true,
        () => defaults,
      ),
    ).toThrow('partial override');
    expect(() =>
      productTheme(themeDoc(link), details, true, () =>
        defaults.replace('--ls-muted:#64748b;', ''),
      ),
    ).toThrow('defaults');
  });
});

const statusHtml = `<html lang="ar" dir="rtl"><head><meta name="viewport" content="width=device-width, initial-scale=1"><title>Page not found | First institution</title><meta name="robots" content="noindex"></head><body><header>${first.name}</header><main><section lang="en" dir="ltr"><h1>Page not found</h1><a href="/ar/courses">Course catalog</a></section></main></body></html>`;
const checkStatus = (body: string, status = 404) =>
  statusDocument({ ...response(body), status }, first, 'ar', 'en', {
    tenants: [first, second],
    catalogueSentinels: [sentinel],
  });

describe('production status assertion controls', () => {
  it.each([
    statusHtml.replace('lang="ar"', 'lang="en"'),
    statusHtml.replace('dir="rtl"', 'dir="ltr"'),
    statusHtml.replace('<h1>Page not found</h1>', '<h1>Page not found</h1><h1>Extra</h1>'),
    statusHtml.replace('<h1>Page not found</h1>', '<script>Page not found</script>'),
    statusHtml.replace(
      '<title>Page not found | First institution</title>',
      '<title>Wrong title</title>',
    ),
    statusHtml.replace('content="noindex"', 'content="index"'),
    statusHtml.replace('href="/ar/courses"', 'href="/en/courses"'),
    statusHtml + flight('private-query-value'),
    statusHtml + flight(second.name),
    statusHtml + flight(sentinel),
  ])('rejects a status-document mutation while the clean document passes', (body) => {
    expect(() => checkStatus(body)).toThrow();
    expect(() => checkStatus(statusHtml)).not.toThrow();
  });

  it('rejects HTTP 200 even when the visible status document is correct', () => {
    expect(() => checkStatus(statusHtml, 200)).toThrow('Public status response');
    expect(() => checkStatus(statusHtml)).not.toThrow();
  });

  it('does not count Flight or hidden headings as visible HTML', () => {
    const doc = visibleDocument(
      response(statusHtml + '<div hidden><h1>Hidden</h1></div>' + flight('<h1>Flight</h1>')),
    );
    expect(doc.querySelectorAll('h1')).toHaveLength(1);
  });
});

// Metadata checks cannot replace the packet's manual reflow and zoom smoke.
describe('emitted product viewport metadata', () => {
  it.each([
    '',
    '<meta name="viewport" content="width=320">',
    '<meta name="viewport" content="width=device-width, user-scalable=no">',
    '<meta name="viewport" content="width=device-width, user-scalable=0">',
    '<meta name="viewport" content="width=device-width, maximum-scale=1">',
  ])('rejects absent or zoom-restricting metadata: %s', (metadata) => {
    expect(() => documentViewport(themeDoc(metadata))).toThrow();
    expect(() =>
      documentViewport(
        themeDoc('<meta name="viewport" content="width=device-width, initial-scale=1">'),
      ),
    ).not.toThrow();
  });
});

const graphLocaleDoc = (head: string) =>
  new JSDOM(`<html><head>${head}</head></html>`).window.document;
const graphEligible =
  '<link rel="alternate" hreflang="en" href="/en/courses"><link rel="alternate" hreflang="tr-TR" href="/tr-TR/courses">';
const graphAlternate = '<meta property="og:locale:alternate" content="tr_TR">';

describe('production Open Graph locale assertion controls', () => {
  it.each([
    graphEligible,
    '<link rel="alternate" hreflang="en" href="/en/courses">' + graphAlternate,
    graphEligible + graphAlternate + graphAlternate,
    graphEligible + '<meta property="og:locale:alternate" content="tr-TR">',
    graphEligible + graphAlternate + '<meta property="og:locale" content="en_US">',
  ])('rejects missing, ineligible, duplicate or invented locales: %s', (head) => {
    expect(() => documentOpenGraphLocales(graphLocaleDoc(head), 'en')).toThrow('Open Graph');
    expect(() =>
      documentOpenGraphLocales(graphLocaleDoc(graphEligible + graphAlternate), 'en'),
    ).not.toThrow();
  });

  it('requires the explicit current territory and omits unrepresentable alternates', () => {
    const links =
      graphEligible + '<link rel="alternate" hreflang="zh-Hans-CN" href="/zh-Hans-CN/courses">';
    expect(() => documentOpenGraphLocales(graphLocaleDoc(links), 'tr-TR')).toThrow('locale');
    expect(() =>
      documentOpenGraphLocales(
        graphLocaleDoc(links + '<meta property="og:locale" content="tr_TR">'),
        'tr-TR',
      ),
    ).not.toThrow();
  });
});
