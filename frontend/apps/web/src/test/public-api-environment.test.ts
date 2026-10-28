// @vitest-environment node
import { spawnSync } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import {
  existsSync,
  mkdirSync,
  mkdtempSync,
  readFileSync,
  realpathSync,
  rmSync,
  writeFileSync,
} from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { afterEach, describe, expect, it } from 'vitest';

import {
  developmentSeederRuntimeEnvironment,
  publicSeederEnvironment,
} from '../../../../../scripts/demo-database.mjs';
import {
  publicApiEnvironment,
  publicDemoApiEnvironment,
  publicWebEnvironment,
} from '../../../../../scripts/public-runtime.mjs';
import { loadLocalEnvironment } from '../server/ingress';

const roots: string[] = [];
afterEach(() => {
  for (const root of roots.splice(0)) rmSync(root, { recursive: true, force: true });
});
function temporary() {
  const root = realpathSync(mkdtempSync(join(tmpdir(), 'learnstack-api-environment-')));
  roots.push(root);
  return root;
}
function connection(database: string, role = 'learnstack_app') {
  return Object.entries({
    Host: 'selected.invalid',
    Database: database,
    Username: role,
    Password: randomBytes(24).toString('base64url'),
    'Application Name': `runtime; "${randomBytes(8).toString('hex')}"`,
  })
    .map(([key, value]) => `${key}="${value.replaceAll('"', '""')}"`)
    .join(';');
}
function pair() {
  return {
    ConnectionStrings__IdentityAuthorization: connection('learnstack_identity_authorization'),
    ConnectionStrings__AuthorizationEvidence: connection('learnstack_authorization_evidence'),
  };
}
function selected() {
  return {
    ConnectionStrings__Default: connection('business'),
    ConnectionStrings__OutboxDispatcher: connection('business', 'learnstack_outbox_admin'),
  };
}
const configuration = {
  apiOrigin: 'http://127.0.0.1:5080',
  secret: randomBytes(32).toString('base64url'),
};
const keys = [
  'ConnectionStrings__IdentityAuthorization',
  'ConnectionStrings__AuthorizationEvidence',
] as const;

describe('selected API auxiliary source environment', () => {
  it.each([
    ['absent', {}, true],
    ['complete', pair(), true],
    ['both empty', Object.fromEntries(keys.map((key) => [key, ''])), false],
    ['authorization empty', { [keys[0]]: '' }, false],
    ['evidence empty', { [keys[1]]: '' }, false],
    ['authorization only', { [keys[0]]: pair()[keys[0]] }, false],
    ['evidence only', { [keys[1]]: pair()[keys[1]] }, false],
    ['both whitespace', Object.fromEntries(keys.map((key) => [key, ' \t'])), false],
  ] as const)('uses the same %s pair rule for seed, API and demo', (_name, auxiliary, valid) => {
    const values: Record<string, string> = {
      ...selected(),
      Identity__AuthorityId: '01930000-0000-7000-8000-000000009001',
      Authentication__Tenant__Issuer: 'http://localhost:8080/realms/learnstack',
      ...auxiliary,
    };
    const ambient = pair();
    for (const make of [
      () => developmentSeederRuntimeEnvironment(values, ambient),
      () => publicApiEnvironment(values, configuration, ambient),
      () => publicDemoApiEnvironment(values, configuration, temporary(), ambient),
    ]) {
      if (!valid) {
        expect(make).toThrow(/configuration is invalid$/);
        continue;
      }
      const environment = make();
      expect(environment.ConnectionStrings__Default).toBe(values.ConnectionStrings__Default);
      for (const key of keys) {
        if (key in auxiliary) expect(environment[key]).toBe(values[key]);
        else expect(environment).not.toHaveProperty(key);
      }
    }
  });

  it.each(['public-api', 'demo'] as const)(
    'preserves the selected auxiliary pair byte-exactly for %s',
    (launch) => {
      const values = { ...selected(), ...pair() };
      const ambient = {
        ...pair(),
        ConnectionStrings__Migration: connection('owner', 'learnstack_migration'),
      };
      const environment =
        launch === 'demo'
          ? publicDemoApiEnvironment(values, configuration, temporary(), ambient)
          : publicApiEnvironment(values, configuration, ambient);
      for (const key of keys) expect(environment[key]).toBe(values[key]);
      expect(environment).not.toHaveProperty('ConnectionStrings__Migration');
    },
  );

  it('keeps helper ambient values, native web and build prerequisites separate from runtime credentials', () => {
    const values = { ...selected(), ...pair() };
    const ambient = {
      ...values,
      KEYCLOAK_ADMIN_PASSWORD: randomBytes(24).toString('hex'),
      PATH: '/owned/bin',
    };
    const api = publicApiEnvironment(selected(), configuration, ambient);
    const demo = publicDemoApiEnvironment(selected(), configuration, temporary(), ambient);
    const web = publicWebEnvironment(values, temporary(), ambient);
    const build = publicSeederEnvironment(undefined, ambient);
    for (const environment of [api, demo, web, build]) {
      for (const key of [...keys, 'KEYCLOAK_ADMIN_PASSWORD'])
        expect(environment).not.toHaveProperty(key);
    }
  });

  for (const key of keys) {
    it(`rejects a partial selected ${key} without borrowing its ambient partner`, () => {
      const values = { ...selected(), [key]: pair()[key] };
      const ambient = pair();
      for (const make of [
        () => publicApiEnvironment(values, configuration, ambient),
        () => publicDemoApiEnvironment(values, configuration, temporary(), ambient),
      ])
        expect(make).toThrow('Authorization source configuration is invalid');
    });
  }

  it.each(['', ' ', '\t', '\n', '\r', '\0'])(
    'rejects an explicit invalid selected pair %j without printing its values',
    (invalid) => {
      const values = {
        ...selected(),
        ...pair(),
        ConnectionStrings__AuthorizationEvidence: invalid,
      };
      for (const make of [
        () => publicApiEnvironment(values, configuration, {}),
        () => publicDemoApiEnvironment(values, configuration, temporary(), {}),
      ])
        expect(make).toThrow('Authorization source configuration is invalid');
    },
  );

  it('uses a complete process pair atomically and otherwise preserves the selected file pair', () => {
    const root = temporary();
    const file = pair();
    const explicit = pair();
    writeFileSync(
      join(root, '.env'),
      Object.entries(file)
        .map(([key, value]) => `${key}=${value}`)
        .join('\n'),
    );
    const options = { apiAuthentication: true };
    expect(loadLocalEnvironment(root, {}, options)).toMatchObject(file);
    expect(loadLocalEnvironment(root, explicit, options)).toMatchObject(explicit);
    for (const key of keys) {
      expect(() => loadLocalEnvironment(root, { [key]: explicit[key] }, options)).toThrow(
        'Incomplete API authorization source configuration',
      );
    }
  });
});

function launcher() {
  const root = temporary();
  for (const path of ['scripts', 'frontend/apps/web/.server/auth', 'backend', 'bin'])
    mkdirSync(join(root, path), { recursive: true });
  writeFileSync(join(root, 'package.json'), '{"type":"module"}\n');
  for (const name of ['public-local.mjs', 'public-runtime.mjs', 'demo-database.mjs'])
    writeFileSync(
      join(root, 'scripts', name),
      readFileSync(new URL(`../../../../../scripts/${name}`, import.meta.url)),
    );
  for (const name of ['ingress.js', 'auth/http-paths.js'])
    writeFileSync(
      join(root, 'frontend/apps/web/.server', name),
      readFileSync(new URL(`../../.server/${name}`, import.meta.url)),
    );
  const marker = join(root, 'api-child');
  writeFileSync(
    join(root, 'bin/dotnet'),
    `#!${process.execPath}\nimport { writeFileSync } from 'node:fs';\nwriteFileSync(${JSON.stringify(marker)}, JSON.stringify({ args: process.argv.slice(2), env: process.env }), { mode: 0o600 });\n`,
    { mode: 0o700 },
  );
  const values = {
    ...selected(),
    ...pair(),
    LEARNSTACK_PUBLIC_API_ORIGIN: configuration.apiOrigin,
    LEARNSTACK_PUBLIC_HOP_SECRET: configuration.secret,
    LEARNSTACK_PUBLIC_TLS_CERT: '/owned/cert',
    LEARNSTACK_PUBLIC_TLS_KEY: '/owned/key',
  };
  return { root, marker, values };
}
function launch(
  fixture: ReturnType<typeof launcher>,
  values: Record<string, string>,
  environment: Record<string, string> = {},
) {
  writeFileSync(
    join(fixture.root, '.env'),
    Object.entries(values)
      .map(([key, value]) => `${key}=${value}`)
      .join('\n'),
    { mode: 0o600 },
  );
  return spawnSync(process.execPath, [join(fixture.root, 'scripts/public-local.mjs'), 'api'], {
    cwd: fixture.root,
    env: { NODE_ENV: 'test', PATH: join(fixture.root, 'bin'), ...environment },
    encoding: 'utf8',
    timeout: 4000,
    killSignal: 'SIGKILL',
  });
}

describe('actual public-api child admission', () => {
  it.each(['file', 'process'] as const)(
    'passes the selected auxiliary pair from %s into the actual child environment',
    (source) => {
      const fixture = launcher();
      const explicit = pair();
      const result = launch(fixture, fixture.values, source === 'process' ? explicit : {});
      expect(result.error).toBeUndefined();
      expect(result.status, result.stderr).toBe(0);
      const child = JSON.parse(readFileSync(fixture.marker, 'utf8')) as {
        args: string[];
        env: Record<string, string>;
      };
      expect(child.args).toEqual(['run', '--no-launch-profile', '--project', 'src/LearnStack.Api']);
      for (const key of keys)
        expect(child.env[key]).toBe(source === 'process' ? explicit[key] : fixture.values[key]);
      expect(result.stdout).toBe('');
      expect(result.stderr).toBe('');
    },
  );

  it.each([
    'file-partial',
    'process-partial',
    'file-empty',
    'process-empty',
    'file-control',
  ] as const)(
    'refuses %s before the API child starts and keeps the diagnostic fixed',
    (invalid) => {
      const fixture = launcher();
      const values: Record<string, string> = { ...fixture.values };
      let environment: Record<string, string> = {};
      if (invalid === 'file-partial') delete values.ConnectionStrings__AuthorizationEvidence;
      if (invalid === 'process-partial')
        environment = {
          ConnectionStrings__IdentityAuthorization: pair().ConnectionStrings__IdentityAuthorization,
        };
      if (invalid === 'file-empty') values.ConnectionStrings__AuthorizationEvidence = '';
      if (invalid === 'process-empty')
        environment = { ...pair(), ConnectionStrings__AuthorizationEvidence: ' ' };
      if (invalid === 'file-control') values.ConnectionStrings__AuthorizationEvidence += '\0';
      const result = launch(fixture, values, environment);
      expect(result.error).toBeUndefined();
      expect(result.status).toBe(1);
      expect(existsSync(fixture.marker)).toBe(false);
      expect(result.stdout).toBe('');
      expect(result.stderr).toBe('Local public configuration or launch failed\n');
    },
  );
});
