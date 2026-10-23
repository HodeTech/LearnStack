import { readdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';

import { describe, expect, it, vi } from 'vitest';

import { createPublicTranslator, getPublicCatalogue, publicFormattingOptions } from './catalogues';
import { uiLocales } from './locale';
import { checkCatalogues, publicCallsiteKeys } from '../test/ui-catalogue-checks';
import type { Catalogue } from '../test/ui-catalogue-checks';
import en from './messages/en/public.json';
import tr from './messages/tr/public.json';

function productionCallsiteKeys(directory: string): string[] {
  const keys: string[] = [];
  for (const entry of readdirSync(directory, { withFileTypes: true })) {
    const path = join(directory, entry.name);
    if (entry.isDirectory() && entry.name !== 'test') keys.push(...productionCallsiteKeys(path));
    if (
      entry.isFile() &&
      /\.tsx?$/.test(entry.name) &&
      !/\.(test|spec)\.tsx?$/.test(entry.name) &&
      // This typed adapter forwards checked keys, rather than choosing copy.
      // public-ui.test.ts proves its namespace and exact argument forwarding.
      !path.endsWith(join('server', 'public-ui.ts'))
    ) {
      keys.push(...publicCallsiteKeys(readFileSync(path, 'utf8'), path));
    }
  }
  return keys;
}

describe('Ui_Catalogues_Cover_Public_Call_Sites', () => {
  it('validates complete bundled catalogues and every production callsite', () => {
    const root = join(process.cwd(), 'src');
    const bundles = uiLocales.map((locale) => {
      const messages = JSON.parse(
        readFileSync(join(root, 'i18n', 'messages', locale, 'public.json'), 'utf8'),
      ) as Catalogue;
      expect(getPublicCatalogue(locale)).toMatchObject({ locale, messages });
      return messages;
    });
    const keys = productionCallsiteKeys(root);
    expect(keys.length).toBeGreaterThan(0);
    checkCatalogues(bundles, keys);
  });

  it.each([
    "const copy = t; copy('catalog.titel');",
    "const first = ui.t; const copy = first; copy('catalog.titel');",
    "const [locale, labels] = await Promise.all([getLocale(), getTranslations('public')]); labels('catalog.titel');",
    "import {getTranslations as messages} from 'next-intl/server'; const [labels] = await Promise.all([messages('public')]); const copy = labels; copy('catalog.titel');",
  ])('includes ordinary translator aliases in missing-key controls: %s', (source) => {
    const keys = publicCallsiteKeys(source);
    expect(keys).toEqual(['catalog.titel']);
    expect(() => checkCatalogues([en, tr], keys)).toThrow('Used UI key');
  });

  it.each([
    'const copy = t; copy(problem.messageKey);',
    "const [copy] = await Promise.all([getTranslations('public')]); copy(problem.messageKey);",
  ])('refuses dynamic keys through ordinary aliases: %s', (source) => {
    expect(() => publicCallsiteKeys(source)).toThrow('must be literal');
  });

  it.each(['rich', 'raw', 'has'])('refuses public translator %s methods', (method) => {
    for (const source of [
      `t.${method}('catalog.title');`,
      `ui.t['${method}']('catalog.title');`,
      `const copy = t; const method = copy.${method};`,
      `const [copy] = await Promise.all([getTranslations('public')]); copy.${method}('catalog.title');`,
    ])
      expect(() => publicCallsiteKeys(source)).toThrow('methods are forbidden');
  });

  it('rejects empty catalogues, empty messages, and empty groups', () => {
    expect(() => checkCatalogues([{}, {}], [])).toThrow('nonempty');
    expect(() => checkCatalogues([{ page: { title: '' } }], [])).toThrow('Empty');
    expect(() => checkCatalogues([{ page: {} }], [])).toThrow('Empty');
  });

  it('rejects a planted key missing in one locale', () => {
    const incomplete = structuredClone(tr);
    Reflect.deleteProperty(incomplete.catalog, 'title');
    expect(() => checkCatalogues([en, incomplete], [])).toThrow('keys differ');
  });

  it('rejects a used key absent from every catalogue', () => {
    const first = structuredClone(en);
    const second = structuredClone(tr);
    Reflect.deleteProperty(first.catalog, 'title');
    Reflect.deleteProperty(second.catalog, 'title');
    const keys = publicCallsiteKeys(
      "const t = await getTranslations('public'); t('catalog.title');",
    );
    expect(() => checkCatalogues([first, second], keys)).toThrow('Used UI key');
  });

  it('rejects a misspelled callsite and a dynamic wire key', () => {
    const keys = publicCallsiteKeys(
      "const {t: copy} = createPublicTranslator(locale); copy('catalog.titel');",
    );
    expect(() => checkCatalogues([en, tr], keys)).toThrow('Used UI key');
    expect(() => publicCallsiteKeys('publicUi.t(problem.messageKey);')).toThrow('must be literal');
    expect(() =>
      checkCatalogues(
        [en, tr],
        publicCallsiteKeys(
          "import {getTranslations as messages} from 'next-intl/server'; const labels = await messages('public'); labels('catalog.titel');",
        ),
      ),
    ).toThrow('Used UI key');
    expect(() =>
      checkCatalogues([en, tr], publicCallsiteKeys("ui['t']('catalog.titel');")),
    ).toThrow('Used UI key');
  });

  it('rejects malformed ICU even if all locales share the same malformed value', () => {
    expect(() =>
      checkCatalogues([{ catalog: { count: '{count, plural, one {#}' } }], []),
    ).toThrow();
  });

  it.each([
    '{total, plural, one {# kurs} other {# kurs}}',
    '{count, select, one {Bir kurs} other {Kurslar}}',
    '{count, number} kurs',
    '{count, plural, one {{name} kurs} other {# kurs}}',
  ])('rejects mismatched ICU arguments: %s', (message) => {
    const mismatch = structuredClone(tr);
    mismatch.catalog.course_count = message;
    expect(() => checkCatalogues([en, mismatch], [])).toThrow('arguments differ');
  });

  it('rejects rich messages and backend wire identifiers', () => {
    expect(() => checkCatalogues([{ catalog: { title: '<b>Courses</b>' } }], [])).toThrow('Rich');
    expect(() => checkCatalogues([{ lockey_missing: 'Missing' }], [])).toThrow('grammar');
  });

  it('does not mistake comments, unrelated strings or helper internals for calls', () => {
    expect(
      publicCallsiteKeys("// t('missing')\nconst copy = 't(unknown)'; translate(key, values);"),
    ).toEqual([]);
    expect(publicCallsiteKeys("ui.t('page.recovery'); t('catalog.title');")).toEqual([
      'page.recovery',
      'catalog.title',
    ]);
  });
});

describe('server-only public message formatting', () => {
  it('selects whole static bundles without changing the supplied content locale', () => {
    expect(getPublicCatalogue('tr-Latn-TR')).toEqual({
      locale: 'tr',
      direction: 'ltr',
      messages: tr,
    });
    expect(getPublicCatalogue('ar')).toEqual({ locale: 'en', direction: 'ltr', messages: en });
  });

  it('uses the selected UI language for pluralization and number formatting', () => {
    const { t } = createPublicTranslator('zh');
    expect(t('catalog.course_count', { count: 1 })).toBe('1 course on this page');
    expect(t('catalog.course_count', { count: 2 })).toBe('2 courses on this page');
    expect(t('catalog.course_count', { count: 1000 })).toBe('1,000 courses on this page');
    expect(createPublicTranslator('tr-TR').t('course.lesson_count', { count: 1000 })).toBe(
      'Bu sayfada 1.000 ders',
    );
  });

  it('returns translated bounded copy on formatter failure without logging inputs', () => {
    const error = vi.spyOn(console, 'error').mockImplementation(() => {});
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => {});
    const log = vi.spyOn(console, 'log').mockImplementation(() => {});
    const { t } = createPublicTranslator('tr-TR');
    // @ts-expect-error Planted missing argument exercises the runtime failure boundary.
    expect(t('catalog.course_count')).toBe(tr.page.unavailable.description);
    // @ts-expect-error Wire keys cannot become general UI lookup identifiers.
    expect(t('lockey_private_wire_key', { secret: 'private-parameter' })).toBe(
      tr.page.unavailable.description,
    );
    expect(error).not.toHaveBeenCalled();
    expect(warn).not.toHaveBeenCalled();
    expect(log).not.toHaveBeenCalled();
  });

  it('returns the selected translated fallback when the ICU formatter itself fails', () => {
    const { messages } = getPublicCatalogue('tr');
    const original = messages.catalog.course_count;
    const error = vi.spyOn(console, 'error').mockImplementation(() => {});
    try {
      messages.catalog.course_count = '{private_diagnostic';
      expect(createPublicTranslator('tr').t('catalog.course_count', { count: 7 })).toBe(
        tr.page.unavailable.description,
      );
      expect(error).not.toHaveBeenCalled();
    } finally {
      messages.catalog.course_count = original;
    }
  });

  it('shares bounded callbacks with the server request configuration', () => {
    const options = publicFormattingOptions('ar');
    expect(options.getMessageFallback()).toBe(en.page.unavailable.description);
    expect(options.onError()).toBeUndefined();
  });
});
