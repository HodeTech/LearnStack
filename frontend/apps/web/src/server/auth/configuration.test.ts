// @vitest-environment node
import {
  chmodSync,
  linkSync,
  lstatSync,
  mkdirSync,
  mkdtempSync,
  readFileSync,
  rmSync,
  symlinkSync,
  writeFileSync,
} from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { afterEach, describe, expect, it } from 'vitest';

import { bffConfiguration } from './configuration';

const roots: string[] = [];
const fields = {
  LEARNSTACK_BFF_ENABLED: 'true',
  LEARNSTACK_BFF_ISSUER: 'https://identity.example.test/realms/learnstack',
  KEYCLOAK_WEB_CLIENT_SECRET: 'private-client-canary',
};
function fixture(values: Record<string, string> = {}) {
  const root = mkdtempSync(join(tmpdir(), 'learnstack-bff-config-'));
  roots.push(root);
  mkdirSync(join(root, 'frontend/apps/web'), { recursive: true });
  const write = (path: string, entries: Record<string, string>) =>
    writeFileSync(
      join(root, path),
      Object.entries(entries)
        .map(([key, value]) => key + '=' + value)
        .join('\n') + '\n',
      { mode: 0o600 },
    );
  if (Object.keys(values).length) write('.env', values);
  return { root, write };
}
afterEach(() => {
  for (const root of roots.splice(0)) rmSync(root, { recursive: true, force: true });
});
describe('atomic optional native BFF configuration', () => {
  it('absent/false remains public-only even with unrelated or conflicting secrets', () => {
    const f = fixture({ KEYCLOAK_WEB_CLIENT_SECRET: 'old-private' });
    expect(bffConfiguration(f.root, {})).toEqual({ enabled: false });
    expect(
      bffConfiguration(f.root, {
        LEARNSTACK_BFF_ENABLED: 'false',
        KEYCLOAK_WEB_CLIENT_SECRET: 'different-private',
      }),
    ).toEqual({ enabled: false });
  });
  it('accepts agreeing private sources without mutating files or contacting a provider', () => {
    const f = fixture(fields);
    f.write('frontend/apps/web/.env.local', fields);
    const before = readFileSync(join(f.root, '.env'));
    expect(bffConfiguration(f.root, fields)).toEqual({
      enabled: true,
      issuer: fields.LEARNSTACK_BFF_ISSUER,
      clientSecret: fields.KEYCLOAK_WEB_CLIENT_SECRET,
      allowLoopbackHttp: false,
    });
    expect(readFileSync(join(f.root, '.env'))).toEqual(before);
  });
  it.each(['', '1', 'TRUE', 'enabled'])('refuses ambiguous opt-in %s', (value) => {
    const f = fixture();
    expect(() => bffConfiguration(f.root, { ...fields, LEARNSTACK_BFF_ENABLED: value })).toThrow(
      'BFF provider: configuration',
    );
  });
  it.each([
    'LEARNSTACK_BFF_ENABLED',
    'LEARNSTACK_BFF_ISSUER',
    'KEYCLOAK_WEB_CLIENT_SECRET',
  ] as const)('refuses conflicting %s before constructing an owner', (key) => {
    const f = fixture(fields);
    expect(() => bffConfiguration(f.root, { [key]: 'different-private-canary' })).toThrow(
      'BFF provider: configuration',
    );
  });
  it.each(['LEARNSTACK_BFF_ISSUER', 'KEYCLOAK_WEB_CLIENT_SECRET'] as const)(
    'true requires %s',
    (key) => {
      const f = fixture();
      const env = { ...fields };
      delete (env as Partial<typeof fields>)[key];
      expect(() => bffConfiguration(f.root, env)).toThrow('BFF provider: configuration');
    },
  );
  it.each(['symlink', 'mode', 'hardlink'] as const)(
    'refuses unsafe %s without silently repairing it',
    (kind) => {
      const f = fixture(fields);
      const path = join(f.root, '.env');
      const before = readFileSync(path);
      if (kind === 'mode') chmodSync(path, 0o644);
      if (kind === 'hardlink') linkSync(path, join(f.root, 'alias'));
      if (kind === 'symlink') {
        rmSync(path);
        f.write('source', fields);
        symlinkSync(join(f.root, 'source'), path);
      }
      const metadata = lstatSync(path);
      expect(() => bffConfiguration(f.root, {})).toThrow('BFF provider: configuration');
      expect(readFileSync(path)).toEqual(before);
      expect(lstatSync(path)).toMatchObject({
        mode: metadata.mode,
        nlink: metadata.nlink,
        ino: metadata.ino,
        uid: metadata.uid,
      });
    },
  );
  it('permits loopback HTTP only in the explicit development process and only in the tenant realm', () => {
    const f = fixture();
    const env = { ...fields, LEARNSTACK_BFF_ISSUER: 'http://localhost:8080/realms/learnstack' };
    expect(() => bffConfiguration(f.root, env)).toThrow();
    expect(bffConfiguration(f.root, { ...env, NODE_ENV: 'development' })).toMatchObject({
      enabled: true,
      allowLoopbackHttp: true,
    });
    for (const issuer of [
      'http://remote.example/realms/learnstack',
      'https://identity.example/realms/learnstack-hub',
      'https://identity.example/realms/learnstack/',
    ])
      expect(() =>
        bffConfiguration(f.root, {
          ...env,
          NODE_ENV: 'development',
          LEARNSTACK_BFF_ISSUER: issuer,
        }),
      ).toThrow();
  });
});
