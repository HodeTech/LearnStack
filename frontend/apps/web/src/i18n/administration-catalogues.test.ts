import { readdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';

import { describe, expect, it, vi } from 'vitest';

import {
  createAdministrationTranslator,
  getAdministrationCatalogue,
} from './administration-catalogues';
import { checkCatalogues, publicCallsiteKeys } from '../test/ui-catalogue-checks';
import enAdmin from './messages/en/admin.json';
import enProfile from './messages/en/profile.json';
import trAdmin from './messages/tr/admin.json';
import trProfile from './messages/tr/profile.json';

function callsites(directory: string, namespace: 'admin' | 'profile'): string[] {
  return readdirSync(directory, { withFileTypes: true }).flatMap((entry) => {
    const path = join(directory, entry.name);
    if (entry.isDirectory()) return entry.name === 'test' ? [] : callsites(path, namespace);
    if (!/\.tsx?$/.test(path) || /\.(test|spec)\.tsx?$/.test(path)) return [];
    const source = readFileSync(path, 'utf8');
    // The native form grammar also has an unrelated profile(fields) parser.
    // UI owners bind these translators through the factory or typed copy port.
    return /\b(?:createAdministrationTranslator|AdminCopy|ProfileCopy)\b/.test(source)
      ? publicCallsiteKeys(source, path, namespace)
      : [];
  });
}
describe('complete administration and profile UI catalogues', () => {
  it.each([
    ['admin', enAdmin, trAdmin],
    ['profile', enProfile, trProfile],
  ] as const)(
    'validates all production %s callsites and both complete ICU catalogues',
    (namespace, en, tr) => {
      const used = callsites(join(process.cwd(), 'src'), namespace);
      expect(used.length).toBeGreaterThan(10);
      checkCatalogues([en, tr], used);
      expect(getAdministrationCatalogue('tr-Latn-TR').messages[namespace]).toBe(tr);
      expect(getAdministrationCatalogue('ar').messages[namespace]).toBe(en);
    },
  );

  it.each(['admin', 'profile'] as const)(
    'rejects missing, aliased, dynamic and rich %s keys',
    (namespace) => {
      const catalogues = namespace === 'admin' ? [enAdmin, trAdmin] : [enProfile, trProfile];
      for (const source of [
        `${namespace}('absent_from_both');`,
        `const {${namespace}: labels} = createAdministrationTranslator(locale); labels('absent_from_both');`,
        `const labels = ui.${namespace}; labels('absent_from_both');`,
      ])
        expect(() =>
          checkCatalogues(catalogues, publicCallsiteKeys(source, 'consumer.tsx', namespace)),
        ).toThrow('Used UI key');
      for (const source of [
        `${namespace}(problem.messageKey);`,
        `ui.${namespace}(capability.key);`,
        `${namespace}.rich('title');`,
      ])
        expect(() => publicCallsiteKeys(source, 'consumer.tsx', namespace)).toThrow();
      expect(() => checkCatalogues([{ title: 'Title' }, { title: '{broken' }], [])).toThrow();
      expect(() => checkCatalogues([{ title: 'Hi {name}' }, { title: 'Hi {actor}' }], [])).toThrow(
        'arguments',
      );
    },
  );

  it('uses a whole-catalogue fallback and never logs API or absent translation keys', () => {
    const log = vi.spyOn(console, 'error').mockImplementation(() => {});
    try {
      const selected = createAdministrationTranslator('tr-TR');
      expect(selected.admin('members.title')).toBe(trAdmin.members.title);
      expect(selected.profile('title')).toBe(trProfile.title);
      // @ts-expect-error Wire keys have no admission into the finite UI key contract.
      expect(selected.admin('lockey_private_wire_key')).toBe(trAdmin.failure.unavailable);
      // @ts-expect-error A permission key is data, never a profile translator key.
      expect(selected.profile('identity.membership.write')).toBe(trProfile.unavailable);
      expect(log).not.toHaveBeenCalled();
    } finally {
      log.mockRestore();
    }
  });
});
