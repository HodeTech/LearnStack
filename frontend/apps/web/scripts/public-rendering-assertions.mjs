// Shared assertions used by the production fixture and its planted controls.
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';

const { JSDOM } = createRequire(import.meta.url)('jsdom');

// Next inlines JSON-serialized Flight strings inside HTML-safe JSON. Scan each
// supported serialization layer without executing scripts or decoding arbitrary JS.
function jsonText(value) {
  return JSON.stringify(value).slice(1, -1);
}

function htmlSafeJson(value) {
  const escapes = {
    '&': '\\u0026',
    '>': '\\u003e',
    '<': '\\u003c',
    '\u2028': '\\u2028',
    '\u2029': '\\u2029',
  };
  return value.replace(/[&><\u2028\u2029]/g, (character) => escapes[character]);
}

export function absentFromSerializedText(text, values, mark = () => {}) {
  for (const [index, value] of values.entries()) {
    mark(index);
    assert.ok(value.length > 0, 'Leak markers are nonempty');
    const serialized = [value, jsonText(value), jsonText(jsonText(value))];
    const representations = new Set(serialized.flatMap((text) => [text, htmlSafeJson(text)]));
    for (const representation of representations)
      assert.equal(text.includes(representation), false, 'Complete HTML/Flight containment');
  }
}

export function absentFromWholeResponse(response, values, mark = () => {}) {
  absentFromSerializedText(response.body, values, mark);
  const doc = new JSDOM(response.body).window.document;
  const decoded = [doc.documentElement.textContent];
  for (const element of doc.querySelectorAll('*'))
    for (const attribute of element.attributes) decoded.push(attribute.value);
  for (const value of values)
    assert.equal(
      decoded.some((text) => text.includes(value)),
      false,
      'Entity-decoded response containment',
    );
}

export function visibleDocument(response) {
  const dom = new JSDOM(response.body);
  // Assertions below inspect the rendered document, never strings embedded in
  // Flight/bootstrap scripts that may describe UI absent from visible HTML.
  dom.window.document
    .querySelectorAll('script, template, [hidden]')
    .forEach((node) => node.remove());
  return dom.window.document;
}

/** Inspect emitted viewport metadata, independently of browser reflow/zoom evidence. */
export function documentViewport(doc) {
  const viewports = doc.querySelectorAll('meta[name="viewport"]');
  assert.equal(viewports.length, 1, 'One emitted viewport declaration');
  const options = new Map(
    viewports[0].content.split(',').map((part) =>
      part
        .trim()
        .toLowerCase()
        .split(/\s*=\s*/),
    ),
  );
  assert.equal(options.get('width'), 'device-width', 'Viewport follows device width');
  assert.ok(!['no', '0'].includes(options.get('user-scalable')), 'Viewport must not disable zoom');
  const maximum = options.get('maximum-scale');
  assert.ok(
    maximum === undefined || Number(maximum) >= 2,
    'Viewport must permit at least 200% zoom',
  );
}

export function statusDocument(response, tenant, locale, uiLocale, options) {
  const { tenants, catalogueSentinels = [], mark = () => {} } = options;
  mark('status headers');
  assert.equal(response.status, 404, 'Public status response');
  mark('HTML content-type');
  assert.match(response.headers['content-type'], /^text\/html/);
  mark('catalogue containment');
  absentFromWholeResponse(response, catalogueSentinels);
  const doc = visibleDocument(response);
  mark('viewport metadata');
  documentViewport(doc);
  mark('document locale');
  assert.equal(doc.documentElement.lang, locale, 'Document retains admitted content locale');
  assert.equal(doc.documentElement.dir, locale === 'ar' ? 'rtl' : 'ltr');
  mark('visible heading');
  const heading = doc.querySelector('h1');
  assert.equal(doc.querySelectorAll('h1').length, 1, 'One visible localized status heading');
  assert.equal(doc.querySelectorAll('main').length, 1, 'One main landmark');
  const title = uiLocale === 'tr' ? 'Sayfa bulunamadı' : 'Page not found';
  assert.equal(heading.textContent, title, 'Visible status translation');
  mark('localized metadata');
  if (!doc.title.includes(title)) {
    const titleNode = new JSDOM(response.body).window.document.querySelector('title');
    mark(
      'localized metadata ' +
        (titleNode?.closest('[hidden]') ? 'hidden' : titleNode ? 'unmatched' : 'absent'),
    );
  }
  assert.ok(doc.title.includes(title), 'Localized document title');
  mark('UI locale');
  assert.equal(heading.closest('[lang]').getAttribute('lang'), uiLocale, 'UI fallback language');
  assert.equal(heading.closest('[dir]').getAttribute('dir'), 'ltr', 'UI fallback direction');
  mark('robots recovery');
  assert.ok(doc.querySelector('meta[name="robots"]').content.includes('noindex'));
  assert.ok(doc.querySelector('a[href="/' + locale + '/courses"]'), 'Same-locale recovery');
  mark('tenant chrome');
  assert.ok(doc.body.textContent.includes(tenant.name), 'Actual tenant chrome');
  const other = tenants.find((candidate) => candidate.host !== tenant.host);
  assert.equal(doc.body.textContent.includes(other.name), false, 'No opposite tenant chrome');
  absentFromWholeResponse(response, [
    other.name,
    'missing-foundation-course',
    'private-query-value',
  ]);
  return doc;
}

export function productContainment(
  response,
  tenant,
  configuration,
  catalogueSentinels = [],
  mark = () => {},
) {
  const own = configuration.product.find((candidate) => candidate.host === tenant.host);
  const other = configuration.product.find((candidate) => candidate.host !== tenant.host);
  const opposite = configuration.tenants.find((candidate) => candidate.host !== tenant.host);
  assert.ok(own.protectedCanaries.length > 0 && other.allCanaries.length > 0);
  absentFromWholeResponse(
    response,
    [
      opposite.name,
      ...other.allCanaries,
      other.courseTitle,
      other.courseSummary,
      other.lessonTitle,
      ...other.content.fields.map((field) => field.value),
      ...own.protectedCanaries,
      ...catalogueSentinels,
    ],
    mark,
  );
}

export function productTheme(doc, details, malformed = false, readStylesheet) {
  const styles = [...doc.querySelectorAll('style,[style]')].filter((element) =>
    /--ls-(?:primary|bg|fg|muted)\s*:/.test(
      element.tagName === 'STYLE' ? element.textContent : element.getAttribute('style'),
    ),
  );
  const { primary, background, foreground, muted } = details.theme;
  assert.equal(styles.length, malformed ? 0 : 1, 'A malformed palette emits no partial override');
  if (!malformed)
    assert.equal(
      styles[0].textContent,
      `:root{--ls-primary:${primary};--ls-bg:${background};--ls-fg:${foreground};--ls-muted:${muted};}`,
      'Exact complete seed palette is independent of entitlement',
    );
  const footer = doc.querySelector('footer.public-footer');
  assert.equal(
    footer !== null,
    details.showAttribution,
    'Effective tenant entitlement alone selects attribution',
  );
  if (footer)
    assert.equal(
      footer.textContent,
      details.locale === 'tr-TR' ? 'LearnStack altyapısıyla' : 'Powered by LearnStack',
    );
  if (malformed) {
    const cssLinks = [...doc.querySelectorAll('link[rel="stylesheet"]')];
    assert.ok(cssLinks.length > 0, 'The fallback has a real compiled stylesheet');
    const css = cssLinks
      .map((link) => {
        const path = link.getAttribute('href').split('?')[0];
        assert.match(path, /^\/_next\/static\/css\/[a-zA-Z0-9._-]+\.css$/);
        return readStylesheet(path);
      })
      .join('');
    assert.match(
      css,
      /:root\{[^}]*--ls-primary:#1f6feb;[^}]*--ls-bg:#fff(?:fff)?;[^}]*--ls-fg:#0f172a;[^}]*--ls-muted:#64748b[;}]/,
      'All four existing CSS defaults remain available together',
    );
  }
}
