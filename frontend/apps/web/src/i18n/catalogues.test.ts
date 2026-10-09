import { readdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';

import { isStructurallySame, parse, TYPE } from '@formatjs/icu-messageformat-parser';
import type { MessageFormatElement } from '@formatjs/icu-messageformat-parser';
import ts from 'typescript';
import { describe, expect, it, vi } from 'vitest';

import { createPublicTranslator, getPublicCatalogue, publicFormattingOptions } from './catalogues';
import en from './messages/en/public.json';
import tr from './messages/tr/public.json';

type Catalogue = Record<string, unknown>;

function flatten(catalogue: Catalogue, prefix = ''): Map<string, string> {
  const entries = new Map<string, string>();
  for (const [segment, value] of Object.entries(catalogue)) {
    if (!/^[a-z][a-z0-9_]*$/.test(segment) || segment.startsWith('lockey_')) {
      throw new Error('Invalid UI key grammar');
    }
    const key = prefix ? `${prefix}.${segment}` : segment;
    if (typeof value === 'string' && value.trim()) {
      entries.set(key, value);
    } else if (value !== null && typeof value === 'object' && !Array.isArray(value)) {
      const nested = flatten(value as Catalogue, key);
      if (!nested.size) throw new Error('Empty catalogue group');
      for (const [nestedKey, message] of nested) entries.set(nestedKey, message);
    } else {
      throw new Error('Empty or invalid UI message');
    }
  }
  return entries;
}

function assertPlainMessages(elements: MessageFormatElement[]): void {
  for (const element of elements) {
    if (element.type === TYPE.tag) throw new Error('Rich UI messages are forbidden');
    if (element.type === TYPE.plural || element.type === TYPE.select) {
      for (const option of Object.values(element.options)) assertPlainMessages(option.value);
    }
  }
}

function checkCatalogues(catalogues: Catalogue[], usedKeys: readonly string[]): void {
  const flattened = catalogues.map((catalogue) => flatten(catalogue));
  const reference = flattened[0];
  if (!reference?.size) throw new Error('UI catalogues must be nonempty');
  const referenceKeys = [...reference.keys()].sort();
  for (const catalogue of flattened) {
    if (JSON.stringify([...catalogue.keys()].sort()) !== JSON.stringify(referenceKeys)) {
      throw new Error('UI catalogue keys differ');
    }
    for (const [key, message] of catalogue) {
      const parsed = parse(message);
      assertPlainMessages(parsed);
      const expected = parse(reference.get(key)!);
      if (!isStructurallySame(expected, parsed).success) {
        throw new Error('UI message arguments differ');
      }
    }
    for (const key of usedKeys) {
      if (!catalogue.has(key)) throw new Error('Used UI key is missing');
    }
  }
}

/** Census literal calls to the public translator, including destructured aliases. */
function publicCallsiteKeys(source: string, filename = 'consumer.tsx'): string[] {
  const file = ts.createSourceFile(
    filename,
    source,
    ts.ScriptTarget.Latest,
    true,
    ts.ScriptKind.TSX,
  );
  const translatorNames = new Set(['t']);
  const translationFactories = new Set(['getTranslations']);
  const collectImports = (node: ts.Node): void => {
    if (
      ts.isImportSpecifier(node) &&
      (node.propertyName?.text ?? node.name.text) === 'getTranslations'
    ) {
      translationFactories.add(node.name.text);
    }
    ts.forEachChild(node, collectImports);
  };
  collectImports(file);
  const collectBindings = (node: ts.Node): void => {
    if (ts.isVariableDeclaration(node) && ts.isIdentifier(node.name) && node.initializer) {
      const initializer = ts.isAwaitExpression(node.initializer)
        ? node.initializer.expression
        : node.initializer;
      if (
        ts.isCallExpression(initializer) &&
        ts.isIdentifier(initializer.expression) &&
        translationFactories.has(initializer.expression.text)
      ) {
        translatorNames.add(node.name.text);
      }
      if (ts.isPropertyAccessExpression(initializer) && initializer.name.text === 't') {
        translatorNames.add(node.name.text);
      }
    }
    if (ts.isVariableDeclaration(node) && ts.isObjectBindingPattern(node.name)) {
      for (const element of node.name.elements) {
        if (
          (element.propertyName?.getText(file) ?? element.name.getText(file)) === 't' &&
          ts.isIdentifier(element.name)
        )
          translatorNames.add(element.name.text);
      }
    }
    ts.forEachChild(node, collectBindings);
  };
  collectBindings(file);
  const keys: string[] = [];
  const visit = (node: ts.Node): void => {
    if (ts.isCallExpression(node)) {
      const expression = node.expression;
      const isTranslator = ts.isIdentifier(expression)
        ? translatorNames.has(expression.text)
        : (ts.isPropertyAccessExpression(expression) && expression.name.text === 't') ||
          (ts.isElementAccessExpression(expression) &&
            ts.isStringLiteralLike(expression.argumentExpression) &&
            expression.argumentExpression.text === 't');
      if (isTranslator) {
        const key = node.arguments[0];
        if (!key || !ts.isStringLiteralLike(key))
          throw new Error('UI callsite key must be literal');
        keys.push(key.text);
      }
    }
    ts.forEachChild(node, visit);
  };
  visit(file);
  return keys;
}

function productionCallsiteKeys(directory: string): string[] {
  const keys: string[] = [];
  for (const entry of readdirSync(directory, { withFileTypes: true })) {
    const path = join(directory, entry.name);
    if (entry.isDirectory() && entry.name !== 'test') keys.push(...productionCallsiteKeys(path));
    if (entry.isFile() && /\.tsx?$/.test(entry.name) && !/\.(test|spec)\.tsx?$/.test(entry.name)) {
      keys.push(...publicCallsiteKeys(readFileSync(path, 'utf8'), path));
    }
  }
  return keys;
}

describe('Ui_Catalogues_Cover_Public_Call_Sites', () => {
  it('validates complete bundled catalogues and every production callsite', () => {
    const root = join(process.cwd(), 'src');
    checkCatalogues([en, tr], productionCallsiteKeys(root));
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
    expect(t('catalog.course_count', { count: 1 })).toBe('1 course');
    expect(t('catalog.course_count', { count: 2 })).toBe('2 courses');
    expect(t('catalog.course_count', { count: 1000 })).toBe('1,000 courses');
    expect(createPublicTranslator('tr-TR').t('course.lesson_count', { count: 1000 })).toBe(
      '1.000 ders',
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
