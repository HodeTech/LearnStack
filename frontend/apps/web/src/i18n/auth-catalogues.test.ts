import { readdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';

import { describe, expect, it, vi } from 'vitest';

import { createAuthTranslator, getAuthCatalogue } from './auth-catalogues';
import { checkCatalogues, publicCallsiteKeys } from '../test/ui-catalogue-checks';
import en from './messages/en/auth.json';
import tr from './messages/tr/auth.json';

function callsites(directory: string): string[] {
  return readdirSync(directory, { withFileTypes: true }).flatMap((entry) => {
    const path = join(directory, entry.name);
    if (entry.isDirectory()) return entry.name === 'test' ? [] : callsites(path);
    return /\.tsx?$/.test(path) && !/\.(test|spec)\.tsx?$/.test(path)
      ? publicCallsiteKeys(readFileSync(path, 'utf8'), path, 'copy')
      : [];
  });
}
describe('complete auth UI catalogues and typed callsites', () => {
  it('validates en/tr ICU and all auth copy callsites with the same catalogue guard as public UI', () => {
    const used = callsites(join(process.cwd(), 'src'));
    expect(used.length).toBeGreaterThan(25);
    checkCatalogues([en, tr], used);
    expect(getAuthCatalogue('tr-Latn-TR').messages).toBe(tr);
    expect(getAuthCatalogue('ar').messages).toBe(en);
  });
  it('kills missing-from-all, misspelled, dynamic and malformed ICU controls', () => {
    for (const source of [
      "copy('shell.unknown');",
      "const {copy: labels} = createAuthTranslator(locale); labels('shell.unknown');",
    ])
      expect(() =>
        checkCatalogues([en, tr], publicCallsiteKeys(source, 'test.tsx', 'copy')),
      ).toThrow('Used UI key');
    expect(() => publicCallsiteKeys('ui.copy(problem.messageKey)', 'test.tsx', 'copy')).toThrow(
      'literal',
    );
    const broken = structuredClone(tr);
    broken.session.extend = '{broken';
    expect(() => checkCatalogues([en, broken], [])).toThrow();
  });
  it('keeps fallback whole-catalogue and formatter failures credential-free', () => {
    const log = vi.spyOn(console, 'error').mockImplementation(() => {});
    expect(createAuthTranslator('tr-TR').copy('signin.title')).toBe(tr.signin.title);
    // @ts-expect-error A wire or absent key cannot become an auth UI identifier.
    expect(createAuthTranslator('tr').copy('lockey_secret')).toBe(tr.error.description);
    expect(log).not.toHaveBeenCalled();
  });
});
