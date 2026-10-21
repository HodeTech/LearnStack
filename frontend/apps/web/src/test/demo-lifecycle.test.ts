// @vitest-environment node
import { execFileSync, spawnSync } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import { once } from 'node:events';
import {
  chmodSync,
  mkdirSync,
  mkdtempSync,
  readFileSync,
  realpathSync,
  rmSync,
  symlinkSync,
  writeFileSync,
} from 'node:fs';
import { createServer as createHttpsServer } from 'node:https';
import { createServer } from 'node:net';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { afterEach, describe, expect, it } from 'vitest';

import { composeSelection } from '../../../../../scripts/compose.mjs';
import {
  publicSeederEnvironment,
  validateDemoDatabaseConfiguration,
} from '../../../../../scripts/demo-database.mjs';
import { boundedBody, demoRequest, vacant } from '../../../../../scripts/demo-http.mjs';
import {
  apiCompletionObserver,
  finalCompletionCensus,
} from '../../../../../scripts/demo-observation.mjs';
import { DemoProcesses } from '../../../../../scripts/demo-process.mjs';
import {
  demoFailureMessage,
  disposeDemoResources,
  isApiListenerEvent,
  validateCertificate,
} from '../../../../../scripts/demo.mjs';
import {
  publicApiEnvironment,
  publicDemoApiEnvironment,
  publicWebEnvironment,
} from '../../../../../scripts/public-runtime.mjs';
import { defaultServices, healthyServices } from '../../../../../scripts/wait-compose.mjs';

function writePrivateFileSync(path: string, source: Parameters<typeof writeFileSync>[1]) {
  writeFileSync(path, source, { mode: 0o600 });
}

const roots: string[] = [];
const owners: DemoProcesses[] = [];
afterEach(async () => {
  await Promise.all(owners.splice(0).map((owner) => owner.dispose()));
  for (const root of roots.splice(0)) rmSync(root, { recursive: true, force: true });
});
function temporary() {
  const root = realpathSync(mkdtempSync(join(tmpdir(), 'learnstack-demo-control-')));
  roots.push(root);
  return root;
}
function owner(signal = new AbortController().signal) {
  const processes = new DemoProcesses(signal);
  owners.push(processes);
  return processes;
}

function seedFixture(root: string) {
  for (const directory of ['scripts', 'backend', 'bin']) mkdirSync(join(root, directory));
  for (const file of [
    'seed.sh',
    'seed.mjs',
    'connection-string.awk',
    'compose.mjs',
    'demo-database.mjs',
    'demo-process.mjs',
    'development-realm-owner.mjs',
    'development-realm-errors.mjs',
  ])
    writeFileSync(
      join(root, 'scripts', file),
      readFileSync(new URL(`../../../../../scripts/${file}`, import.meta.url)),
    );
  mkdirSync(join(root, 'frontend/apps/web/scripts'), { recursive: true });
  writeFileSync(
    join(root, 'frontend/apps/web/scripts/stop-test-child.mjs'),
    readFileSync(new URL('../../scripts/stop-test-child.mjs', import.meta.url)),
  );
  for (const [file, method, event] of [
    ['wait-compose', 'waitCompose', 'ready'],
    ['reconcile-development-realm', 'reconcileDevelopmentRealm', 'realm'],
  ])
    writeFileSync(
      join(root, `scripts/${file}.mjs`),
      `import { appendFileSync } from 'node:fs';
export async function ${method}() { appendFileSync(${JSON.stringify(join(root, 'events'))}, '${event}\\n'); return { mutations: 0 }; }
`,
    );
  return [
    'ConnectionStrings__Migration=Username=learnstack_migration',
    'ConnectionStrings__Default=Username=learnstack_app',
    'ConnectionStrings__OutboxDispatcher=Username=learnstack_outbox_admin',
    'KEYCLOAK_ADMIN=fixture-admin',
    `KEYCLOAK_ADMIN_PASSWORD=${randomBytes(16).toString('hex')}`,
    `KEYCLOAK_WEB_CLIENT_SECRET=${randomBytes(16).toString('hex')}`,
    '',
  ].join('\n');
}

describe('launcher-owned local database validation', () => {
  const roles = [
    ['Migration', 'learnstack_migration', 'LEARNSTACK_MIGRATION_PW'],
    ['Default', 'learnstack_app', 'LEARNSTACK_APP_PW'],
    ['PlatformAdmin', 'learnstack_platform', 'LEARNSTACK_PLATFORM_PW'],
    ['OutboxDispatcher', 'learnstack_outbox_admin', 'LEARNSTACK_OUTBOX_PW'],
  ] as const;
  const databasePassword = randomBytes(16).toString('hex');
  function databaseValues(password = databasePassword) {
    const values: Record<string, string> = { POSTGRES_DB: 'learnstack' };
    for (const [suffix, role, passwordKey] of roles) {
      values[passwordKey] = password;
      values[`ConnectionStrings__${suffix}`] =
        `Host=localhost;Port=5432;Database=learnstack;Username=${role};Password="${password.replaceAll('"', '""')}"`;
    }
    return values;
  }
  it.each([null, undefined])(
    'refuses absent configuration (%s) with the fixed diagnostic',
    (values) => {
      expect(() => validateDemoDatabaseConfiguration(values)).toThrowError(
        /^Local demo database configuration is invalid$/,
      );
    },
  );
  it('reports the real database validation failure through the launcher diagnostic boundary', () => {
    const values = databaseValues();
    delete values.LEARNSTACK_OUTBOX_PW;
    let failure: unknown;
    try {
      validateDemoDatabaseConfiguration(values);
    } catch (error) {
      failure = error;
    }
    expect(failure).toBeInstanceOf(Error);
    expect(demoFailureMessage(failure)).toBe('Local demo database configuration is invalid');
    const secret = randomBytes(16).toString('hex');
    for (const message of [
      `Provider failure: ${secret}`,
      `Local demo database configuration is invalid: ${secret}`,
      `Local demo database configuration is invalid\n${secret}`,
    ]) {
      expect(demoFailureMessage(new Error(message))).toBe(
        'demo: startup/proof failed; inspect prerequisites, TLS, configuration and selected stack',
      );
    }
  });
  it.each([
    'simple',
    'semi;colon',
    ' leading and trailing ',
    '   ',
    'a"double"quote',
    "a'single'quote",
  ])('accepts all four local roles with quoted password %s', (password) => {
    expect(() => validateDemoDatabaseConfiguration(databaseValues(password))).not.toThrow();
  });
  it('accepts supported key aliases, single quoting, loopback and the default port', () => {
    const values = databaseValues("semi;single'quote");
    for (const [suffix, role] of roles)
      values[`ConnectionStrings__${suffix}`] =
        ` Server = 127.0.0.1 ; Database = learnstack ; User ID = ${role} ; Pwd = 'semi;single''quote' ;`;
    expect(() => validateDemoDatabaseConfiguration(values)).not.toThrow();
  });
  it.each(
    roles.flatMap(([suffix]) =>
      [
        ['role', (value: string) => value.replace(/Username=[^;]+/, 'Username=foreign')],
        ['password', (value: string) => value.replace(/Password=.*/, 'Password=foreign')],
        ['database', (value: string) => value.replace('Database=learnstack', 'Database=foreign')],
        ['host', (value: string) => value.replace('Host=localhost', 'Host=foreign')],
        ['port', (value: string) => value.replace('Port=5432', 'Port=5433')],
        ['malformed', (value: string) => value + ';Password="unclosed'],
      ].map(([field, mutate]) => ({ suffix, field, mutate })),
    ),
  )('refuses $suffix $field without exposing configuration', ({ suffix, mutate }) => {
    const values = databaseValues();
    const key = `ConnectionStrings__${suffix}`;
    if (typeof mutate !== 'function') throw new Error('Missing mutation');
    values[key] = mutate(values[key] ?? '');
    expect(() => validateDemoDatabaseConfiguration(values)).toThrowError(
      /^Local demo database configuration is invalid$/,
    );
  });
  const applicationConnection = databaseValues().ConnectionStrings__Default!;
  it.each([
    applicationConnection.replace(/Password=.*/, 'Password="unclosed'),
    applicationConnection.replace(/Password=.*/, 'Password="closed"junk'),
    applicationConnection.replace(/Password=.*/, 'Password=plain"quote'),
    applicationConnection + ';UID=learnstack_platform',
    applicationConnection + ';Unknown=value',
    applicationConnection.replace('Host', 'H o s t'),
    applicationConnection.replace('Port=5432', 'Port=nope'),
    applicationConnection.replace('Port=5432', 'Port=5432.0'),
    applicationConnection.replace(/;Password=.*/, ''),
    applicationConnection.replace(';Username=learnstack_app', ''),
    applicationConnection.replace(';Database=learnstack', ''),
    applicationConnection.replace(/^[^;]+;/, ''),
    applicationConnection.replace(/Password=.*/, 'Password='),
    ['postgres://learnstack_app', databasePassword + '@localhost/learnstack'].join(':'),
    '',
  ])('refuses unsupported or malformed local connection syntax %#', (connection) => {
    const values = databaseValues();
    values.ConnectionStrings__Default = connection;
    expect(() => validateDemoDatabaseConfiguration(values)).toThrowError(
      /^Local demo database configuration is invalid$/,
    );
  });
  it.each([
    ...roles.map(([suffix]) => `ConnectionStrings__${suffix}`),
    ...roles.map(([, , key]) => key),
    'POSTGRES_DB',
  ])('requires selected configuration %s', (missing) => {
    const values = databaseValues();
    delete values[missing];
    expect(() => validateDemoDatabaseConfiguration(values)).toThrowError(
      /^Local demo database configuration is invalid$/,
    );
  });
  it.each(roles)(
    'refuses %s when both password sources are missing',
    (suffix, role, passwordKey) => {
      const values = databaseValues();
      delete values[passwordKey];
      values[`ConnectionStrings__${suffix}`] =
        `Host=localhost;Port=5432;Database=learnstack;Username=${role}`;
      expect(() => validateDemoDatabaseConfiguration(values)).toThrowError(
        /^Local demo database configuration is invalid$/,
      );
      expect(() => validateDemoDatabaseConfiguration(databaseValues())).not.toThrow();
    },
  );
  it('keeps every Seeder mode free of inherited credentials and diagnostics', () => {
    const tooling = Object.fromEntries(
      [
        'HTTP_PROXY',
        'HTTPS_PROXY',
        'ALL_PROXY',
        'NO_PROXY',
        'http_proxy',
        'https_proxy',
        'all_proxy',
        'no_proxy',
        'NODE_EXTRA_CA_CERTS',
        'SSL_CERT_FILE',
        'SSL_CERT_DIR',
        'NUGET_PACKAGES',
        'NUGET_HTTP_CACHE_PATH',
        'COREPACK_HOME',
        'PNPM_HOME',
        'XDG_CACHE_HOME',
        'NPM_CONFIG_CACHE',
        'npm_config_cache',
        'NPM_CONFIG_STORE_DIR',
        'npm_config_store_dir',
      ].map((key) => [key, `selected-${key}`]),
    );
    const base = {
      PATH: '/bin',
      HOME: '/home',
      DOTNET_ROOT: '/dotnet',
      TMPDIR: '/tmp',
      APPDATA: '/profile',
      ...tooling,
      ConnectionStrings__Default: 'ambient-application',
      ConnectionStrings__Migration: 'owner',
      ConnectionStrings__OutboxDispatcher: 'dispatcher',
      CONNECTIONSTRINGS__OUTBOXDISPATCHER: 'uppercase-dispatcher',
      'ConnectionStrings:OutboxDispatcher': 'colon-dispatcher',
      LEARNSTACK_OUTBOX_PW: 'dispatcher-password',
      DEBUG: '*',
      NODE_TLS_REJECT_UNAUTHORIZED: '0',
    };
    const prerequisites = {
      PATH: '/bin',
      HOME: '/home',
      DOTNET_ROOT: '/dotnet',
      TMPDIR: '/tmp',
      APPDATA: '/profile',
      ...tooling,
    };
    expect(publicSeederEnvironment(undefined, base)).toEqual(prerequisites);
    expect(publicSeederEnvironment('selected-application', base)).toEqual({
      ...prerequisites,
      ConnectionStrings__Default: 'selected-application',
    });
    const source = readFileSync(
      new URL('../../../../../scripts/demo.mjs', import.meta.url),
      'utf8',
    );
    expect(source).not.toContain('--validate-demo-config');
  });
});

describe('shared explicit demo stack selection', () => {
  function composeCliFixture() {
    const root = temporary();
    mkdirSync(join(root, 'bin'));
    mkdirSync(join(root, 'scripts'));
    for (const name of ['compose', 'development-realm-errors'])
      writeFileSync(
        join(root, `scripts/${name}.mjs`),
        readFileSync(new URL(`../../../../../scripts/${name}.mjs`, import.meta.url)),
      );
    const file = join(root, 'selected.env');
    writePrivateFileSync(file, '# Older private configuration without a web secret\n');
    const canary = randomBytes(20).toString('hex');
    writeFileSync(
      join(root, 'bin/docker'),
      `#!${process.execPath}
const argv = process.argv.slice(2);
if (process.env.KEYCLOAK_WEB_CLIENT_SECRET !== 'learnstack-lifecycle-placeholder') {
  console.error('fixture: required web secret is absent');
  process.exit(43);
}
process.stdout.write(JSON.stringify({ argv, placeholder: true,
  inheritedSecret: Object.keys(process.env).some(key => /bff|unrelated_private/i.test(key)) }));
`,
      { mode: 0o700 },
    );
    return {
      root,
      file,
      run(args: string[]) {
        const result = spawnSync(
          process.execPath,
          [join(root, 'scripts/compose.mjs'), 'selected', ...args],
          {
            cwd: root,
            env: {
              NODE_ENV: 'test',
              PATH: join(root, 'bin'),
              LEARNSTACK_COMPOSE_MODE: 'e2e',
              LEARNSTACK_COMPOSE_PROJECT: 'owned-lifecycle',
              LEARNSTACK_COMPOSE_ENV_FILE: file,
              KEYCLOAK_WEB_CLIENT_SECRET: canary,
              Authentication__Bff__ClientSecret: canary,
              UNRELATED_PRIVATE_VALUE: canary,
            },
            encoding: 'utf8',
            timeout: 5000,
          },
        );
        expect(result.error).toBeUndefined();
        expect(result.stdout).not.toContain(canary);
        expect(result.stderr).not.toContain(canary);
        return result;
      },
    };
  }

  it.each([
    ['down'],
    ['--profile', '*', 'down', '-v', '--remove-orphans'],
    ['logs', '-f', '--tail=100'],
    ['ps', '-a', '-q'],
    ['stop', 'keycloak'],
    ['config', '--services'],
  ])('gives the actual lifecycle CLI %j only a nonsecret interpolation placeholder', (...args) => {
    const f = composeCliFixture();
    const result = f.run(args);
    expect(result.status, result.stderr).toBe(0);
    expect(JSON.parse(result.stdout)).toEqual({
      argv: [
        'compose',
        '--project-name',
        'owned-lifecycle',
        '--env-file',
        f.file,
        '-f',
        join(f.root, 'infra/compose/dev.yml'),
        '-f',
        join(f.root, 'infra/compose/e2e.yml'),
        ...args,
      ],
      placeholder: true,
      inheritedSecret: false,
    });
    expect(readFileSync(f.file, 'utf8')).toBe(
      '# Older private configuration without a web secret\n',
    );
  });

  it.each([
    ['up', '-d'],
    ['create'],
    ['run', '--rm', 'keycloak'],
    ['start'],
    ['restart'],
    ['config'],
    ['config', '--format', 'json'],
    ['config', '--services', '--environment'],
    ['--unknown', 'down'],
    ['--profile'],
    ['--profile', '*'],
    ['--profile=*', 'down'],
    ['--profile', '*', 'up', '-d'],
    ['down', '--file', 'foreign.yml'],
  ])('keeps creation, full config and ambiguous CLI %j strict', (...args) => {
    const result = composeCliFixture().run(args);
    expect(result.status).toBe(43);
    expect(result.stdout).toBe('');
    expect(result.stderr).toBe('fixture: required web secret is absent\n');
    expect(
      readFileSync(new URL('../../../../../infra/compose/dev.yml', import.meta.url), 'utf8'),
    ).toContain('KEYCLOAK_WEB_CLIENT_SECRET: ${KEYCLOAK_WEB_CLIENT_SECRET:?');
  });

  it.each(['shared', 'symlink', 'reserved'])(
    'keeps lifecycle private-source qualification for %s files ahead of Docker',
    (kind) => {
      const f = composeCliFixture();
      if (kind === 'shared') chmodSync(f.file, 0o644);
      else if (kind === 'symlink') {
        const target = join(f.root, 'target.env');
        writePrivateFileSync(target, '# private\n');
        rmSync(f.file);
        symlinkSync(target, f.file);
      } else writePrivateFileSync(f.file, 'COMPOSE_FILE=foreign.yml\n');
      const result = f.run(['--profile', '*', 'down']);
      expect(result.status).toBe(1);
      expect(result.stdout).toBe('');
      expect(result.stderr).toContain(
        kind === 'reserved' ? 'Reserved COMPOSE_*' : 'preflight/private_file',
      );
      expect(result.stderr).not.toContain(f.file);
    },
  );

  it.each([
    ['missing', 'Compose environment file is missing; run make public-env'],
    ['mode', 'Unsupported Compose mode'],
    ['project', 'Invalid Compose project'],
    ['reserved', 'Reserved COMPOSE_* controls cannot appear in the selected environment'],
  ])(
    'retains the fixed %s Compose selection notice through the actual seed CLI',
    (kind, notice) => {
      const root = temporary();
      const source = seedFixture(root);
      if (kind !== 'missing')
        writePrivateFileSync(
          join(root, '.env'),
          source + (kind === 'reserved' ? 'COMPOSE_FILE=foreign.yml\n' : ''),
        );
      const result = spawnSync(
        process.execPath,
        [join(root, 'scripts/seed.mjs'), '--validate-config'],
        {
          cwd: root,
          env: {
            NODE_ENV: 'test',
            PATH: root,
            ...(kind === 'mode' ? { LEARNSTACK_COMPOSE_MODE: 'unknown' } : {}),
            ...(kind === 'project' ? { LEARNSTACK_COMPOSE_PROJECT: '../foreign' } : {}),
          },
          encoding: 'utf8',
          timeout: 5000,
        },
      );
      expect(result.error).toBeUndefined();
      expect(result.status).toBe(1);
      expect(result.stdout).toBe('');
      expect(result.stderr).toBe(`seed: ${notice}\n`);
      expect(result.stderr).not.toContain(root);
    },
  );

  it.each([
    'COMPOSE_PROFILES=gated',
    ' export COMPOSE_PROFILES = gated',
    'COMPOSE_FILE: foreign.yml',
    'COMPOSE_PROJECT_NAME=foreign',
    'COMPOSE_ENV_FILES=foreign.env',
  ])('refuses a selected dotenv Compose control before Docker: %s', (assignment) => {
    const root = temporary();
    writePrivateFileSync(join(root, '.env'), `POSTGRES_PASSWORD=private\n${assignment}\n`);
    expect(() => composeSelection('dev', {}, root)).toThrow('Reserved COMPOSE_*');
    writePrivateFileSync(
      join(root, '.env'),
      '# COMPOSE_PROFILES=gated\nPOSTGRES_PASSWORD=private\n',
    );
    expect(() => composeSelection('dev', {}, root)).not.toThrow();
  });
  it('refuses stale ambient seed credentials before stack or database actions', () => {
    const root = temporary();
    const privateSource = seedFixture(root);
    const script = join(root, 'scripts/seed.sh');
    const migration = 'Host=localhost;Username=learnstack_migration';
    const app = 'Host=localhost;Username=learnstack_app';
    writePrivateFileSync(
      join(root, 'selected.env'),
      privateSource
        .replace('Username=learnstack_migration', migration)
        .replace('Username=learnstack_app', app),
    );
    const env = {
      PATH: process.env.PATH,
      LEARNSTACK_COMPOSE_ENV_FILE: 'selected.env',
      ConnectionStrings__Migration: migration,
      ConnectionStrings__Default: app,
    };
    const run = (environment: Record<string, string | undefined>) =>
      spawnSync('bash', [script, '--validate-config'], {
        cwd: root,
        env: { ...environment, NODE_ENV: 'test' },
        encoding: 'utf8',
        timeout: 5000,
      });
    expect(run(env).status).toBe(0);
    for (const key of ['ConnectionStrings__Migration', 'ConnectionStrings__Default']) {
      const result = run({ ...env, [key]: 'Host=foreign;Username=stale' });
      expect(result.error).toBeUndefined();
      expect(result.status).not.toBe(0);
      expect(result.stderr).toContain('realm: preflight/configuration');
      expect(result.stderr).not.toContain('foreign');
    }
  });
  it('freshly builds the Release Seeder and launches its argument-free DLL with only its credential', () => {
    const root = temporary();
    writePrivateFileSync(join(root, '.env'), seedFixture(root));
    writeFileSync(
      join(root, 'bin/docker'),
      `#!${process.execPath}
const { appendFileSync } = require('node:fs');
if (JSON.stringify(process.argv.slice(2)) === JSON.stringify(['context', 'inspect'])) {
  process.stdout.write(JSON.stringify([{ Endpoints: { docker: { Host: 'unix:///var/run/docker.sock' } } }]));
  process.exit(0);
}
if (JSON.stringify(process.argv.slice(2)) !== JSON.stringify(['compose', 'version']) || Object.keys(process.env).some(key => /keycloak|connectionstrings|_pw$|bff|debug/i.test(key))) process.exit(13);
appendFileSync(${JSON.stringify(join(root, 'events'))}, 'compose-version\\n');
`,
      { mode: 0o700 },
    );
    writeFileSync(
      join(root, 'bin/make'),
      `#!${process.execPath}
const { appendFileSync } = require('node:fs');
const args = process.argv.slice(2);
const startup = JSON.stringify(args) === JSON.stringify(['dev']);
const migration = JSON.stringify(args) === JSON.stringify(['migrate']);
const forbidden = Object.keys(process.env).filter(key => /keycloak|connectionstrings|_pw$|bff|debug/i.test(key) && !(migration && key === 'ConnectionStrings__Migration'));
if ((!startup && !migration) || forbidden.length || process.env.ConnectionStrings__Migration !== (migration ? 'Username=learnstack_migration' : undefined) || process.env.LEARNSTACK_COMPOSE_PROJECT !== 'learnstack-dev' || process.env.LEARNSTACK_COMPOSE_ENV_FILE !== ${JSON.stringify(join(root, '.env'))} || process.env.LEARNSTACK_COMPOSE_MODE !== 'dev') process.exit(13);
appendFileSync(${JSON.stringify(join(root, 'events'))}, (startup ? 'stack-up' : 'migrate') + '\\n');
`,
      { mode: 0o700 },
    );
    writeFileSync(
      join(root, 'bin/dotnet'),
      `#!${process.execPath}
const { appendFileSync } = require('node:fs');
const { join } = require('node:path');
const args = process.argv.slice(2);
const build = JSON.stringify(args) === JSON.stringify(['build', 'src/LearnStack.Tools.Seeder', '--configuration', 'Release', '--nologo']);
const seed = args.length === 1 && args[0] === join(${JSON.stringify(root)}, 'backend/src/LearnStack.Tools.Seeder/bin/Release/net10.0/LearnStack.Tools.Seeder.dll');
const forbidden = Object.keys(process.env).filter(key => /keycloak|outbox|migration|platform|debug/i.test(key));
if ((!build && !seed) || process.cwd() !== ${JSON.stringify(join(root, 'backend'))} || forbidden.length || process.env.HOME !== '/seed-home' || !process.env.PATH || (build ? process.env.ConnectionStrings__Default !== undefined : process.env.ConnectionStrings__Default !== 'Username=learnstack_app')) process.exit(13);
appendFileSync(${JSON.stringify(join(root, 'events'))}, (build ? 'build' : 'appseed') + '\\n');
`,
      { mode: 0o700 },
    );
    const launch = () =>
      spawnSync('bash', [join(root, 'scripts/seed.sh')], {
        cwd: root,
        env: {
          PATH: `${join(root, 'bin')}:${process.env.PATH}`,
          HOME: '/seed-home',
          NODE_ENV: 'test',
          ConnectionStrings__OutboxDispatcher: 'ambient-dispatcher',
          CONNECTIONSTRINGS__OUTBOXDISPATCHER: 'ambient-alias',
          'ConnectionStrings:OutboxDispatcher': 'ambient-colon-alias',
          LEARNSTACK_OUTBOX_PW: 'ambient-password',
        },
        encoding: 'utf8',
        timeout: 5000,
      });
    expect(launch().status).toBe(0);
    expect(readFileSync(join(root, 'events'), 'utf8')).toBe(
      'compose-version\nstack-up\nready\nmigrate\nready\nbuild\nappseed\nrealm\n',
    );
    const script = join(root, 'scripts/seed.mjs');
    const source = readFileSync(script, 'utf8');
    expect(source).toContain('[seederDll]');
    writeFileSync(script, source.replace('[seederDll]', "[seederDll, '--nologo']"));
    writeFileSync(join(root, 'events'), '');
    const refused = launch();
    expect(refused.status).not.toBe(0);
    expect(readFileSync(join(root, 'events'), 'utf8')).toBe(
      'compose-version\nstack-up\nready\nmigrate\nready\nbuild\n',
    );
  });
  it('reads the selected migration env file and refuses its runtime role before any database act', () => {
    const root = temporary();
    writeFileSync(
      join(root, 'Makefile'),
      readFileSync(new URL('../../../../../Makefile', import.meta.url)),
    );
    mkdirSync(join(root, 'scripts'));
    writeFileSync(
      join(root, 'scripts/connection-string.awk'),
      readFileSync(new URL('../../../../../scripts/connection-string.awk', import.meta.url)),
    );
    writePrivateFileSync(
      join(root, '.env'),
      'ConnectionStrings__Migration=Username=learnstack_platform\n',
    );
    writeFileSync(
      join(root, 'alternate.env'),
      'ConnectionStrings__Migration=Username=learnstack_app\n',
    );
    const result = spawnSync('make', ['migrate'], {
      cwd: root,
      env: {
        PATH: process.env.PATH,
        NODE_ENV: 'test',
        LEARNSTACK_COMPOSE_ENV_FILE: 'alternate.env',
      },
      encoding: 'utf8',
      timeout: 5000,
    });
    expect(result.error).toBeUndefined();
    expect(result.status).not.toBe(0);
    expect(result.stdout).toContain("Username='learnstack_app'");
    expect(result.stdout).not.toContain("Username='learnstack_platform'");
  });
  it('accepts only the owned API address in a structured hosting lifetime event', () => {
    const origin = 'http://127.0.0.1:5080';
    const event = { SourceContext: 'Microsoft.Hosting.Lifetime', address: origin };
    expect(isApiListenerEvent(JSON.stringify(event), origin)).toBe(true);
    expect(isApiListenerEvent(JSON.stringify({ ...event, address: origin + '1' }), origin)).toBe(
      false,
    );
    expect(isApiListenerEvent(JSON.stringify({ ...event, SourceContext: 'foreign' }), origin)).toBe(
      false,
    );
    expect(isApiListenerEvent(`Now listening on: ${origin}`, origin)).toBe(false);
    expect(isApiListenerEvent('null', origin)).toBe(false);
  });
  it('keeps project/files/env stable and removes inherited Compose and dotenv shadows', () => {
    const root = temporary();
    writePrivateFileSync(join(root, '.env'), 'POSTGRES_PASSWORD=private-file-value\n');
    const selected = composeSelection(
      'e2e',
      {
        PATH: '/bin',
        COMPOSE_PROFILES: 'gated',
        COMPOSE_FILE: 'foreign.yml',
        COMPOSE_PROJECT_NAME: 'foreign',
        POSTGRES_PASSWORD: 'ambient-shadow',
        LEARNSTACK_COMPOSE_PROJECT: 'owned-demo',
        Authentication__Bff__ClientSecret: randomBytes(20).toString('hex'),
        UNRELATED_PRIVATE_VALUE: randomBytes(20).toString('hex'),
        DEBUG: '*',
        NODE_OPTIONS: '--trace-warnings',
        DOCKER_CONTEXT: 'desktop-linux',
        DOCKER_HOST: 'unix:///var/run/docker.sock',
        DOCKER_CONFIG: '/docker-config',
      },
      root,
    );
    expect(selected.project).toBe('owned-demo');
    expect(selected.args).toEqual([
      'compose',
      '--project-name',
      'owned-demo',
      '--env-file',
      join(root, '.env'),
      '-f',
      join(root, 'infra/compose/dev.yml'),
      '-f',
      join(root, 'infra/compose/e2e.yml'),
    ]);
    expect(selected.env).not.toHaveProperty('COMPOSE_PROFILES');
    expect(selected.env).not.toHaveProperty('COMPOSE_FILE');
    expect(selected.env).not.toHaveProperty('COMPOSE_PROJECT_NAME');
    expect(selected.env).not.toHaveProperty('POSTGRES_PASSWORD');
    expect(selected.env).toEqual({
      PATH: '/bin',
      DOCKER_CONTEXT: 'desktop-linux',
      DOCKER_HOST: 'unix:///var/run/docker.sock',
      DOCKER_CONFIG: '/docker-config',
    });
    expect(Object.isFrozen(selected.args)).toBe(true);
  });
  it('refuses missing environment, unsupported mode and unsafe project names', () => {
    const root = temporary();
    expect(() => composeSelection('dev', {}, root)).toThrow();
    writePrivateFileSync(join(root, '.env'), 'A=B\n');
    expect(() => composeSelection('unknown', {}, root)).toThrow();
    expect(() =>
      composeSelection('dev', { LEARNSTACK_COMPOSE_PROJECT: '../foreign' }, root),
    ).toThrow();
  });
  it('waits for postgres first, then refuses missing/extra/unhealthy full-stack rows', () => {
    const names = ['postgres', 'keycloak', 'one', 'two', 'three', 'four', 'five'];
    const model = {
      services: Object.fromEntries(names.map((name) => [name, { healthcheck: {} }])),
    };
    const rows = names.map((name) => ({ Service: name, State: 'running', Health: 'healthy' }));
    const withGated = { services: { ...model.services, gated: { profiles: ['gated'] } } };
    expect(defaultServices(withGated)).toEqual(names);
    expect(healthyServices(withGated, rows)).toBe(true);
    expect(healthyServices(model, rows.slice(0, 1), true)).toBe(true);
    expect(healthyServices(model, rows)).toBe(true);
    expect(healthyServices(model, rows.slice(0, -1))).toBe(false);
    expect(
      healthyServices(model, [...rows, { Service: 'orphan', State: 'running', Health: 'healthy' }]),
    ).toBe(false);
    expect(
      healthyServices(
        model,
        rows.map((row) => ({ ...row, Health: '' })),
      ),
    ).toBe(false);
    expect(() => healthyServices({ services: {} }, [])).toThrow();
  });
});

describe('demo runtime boundaries', () => {
  it('isolates the Development API user-secret profile within the invocation', () => {
    const root = temporary();
    const env = publicDemoApiEnvironment(
      {
        ConnectionStrings__Default: 'application',
        ConnectionStrings__OutboxDispatcher: 'dispatcher',
      },
      { apiOrigin: 'http://127.0.0.1:5080', secret: 'private' },
      root,
      {
        PATH: '/bin',
        HOME: '/foreign-home',
        APPDATA: '/foreign-secrets',
        DOTNET_CLI_HOME: '/foreign',
      },
    );
    expect(env.HOME).toBe(realpathSync(root));
    expect(env.APPDATA).toBe(realpathSync(root));
    expect(env.TMPDIR).toBe(realpathSync(root));
    expect(env).not.toHaveProperty('DOTNET_CLI_HOME');
    expect(env.ConnectionStrings__Default).toBe('application');
    expect(env.ConnectionStrings__OutboxDispatcher).toBe('dispatcher');
    expect(env.ASPNETCORE_ENVIRONMENT).toBe('Development');
  });
  it('gives native web addons an owned cache without ambient credentials or diagnostics', () => {
    const root = temporary();
    const values = {
      LEARNSTACK_PUBLIC_API_ORIGIN: 'http://127.0.0.1:5080',
      LEARNSTACK_PUBLIC_HOP_SECRET: 'private',
      LEARNSTACK_PUBLIC_TLS_CERT: '/owned/cert',
      LEARNSTACK_PUBLIC_TLS_KEY: '/owned/key',
      ConnectionStrings__Migration: 'owner',
      ConnectionStrings__OutboxDispatcher: 'dispatcher',
    };
    expect(
      publicWebEnvironment(values, root, {
        PATH: '/bin',
        DEBUG: '*',
        HOME: '/foreign',
        ConnectionStrings__OutboxDispatcher: 'ambient-dispatcher',
      }),
    ).toEqual({
      PATH: '/bin',
      TMPDIR: root,
      SWC_NATIVE_BINDING_CACHE: join(realpathSync(root), 'swc-native-cache'),
      NODE_ENV: 'production',
      NEXT_TELEMETRY_DISABLED: '1',
      NODE_TLS_REJECT_UNAUTHORIZED: '1',
      ...Object.fromEntries(
        Object.entries(values).filter(([key]) => key.startsWith('LEARNSTACK_PUBLIC_')),
      ),
    });
  });
  it('passes the selected tenant issuer only in the constructed API environment', () => {
    const root = temporary();
    const key = 'Authentication__Tenant__Issuer';
    const issuer = 'https://identity.example/realms/learnstack';
    const values = {
      ConnectionStrings__Default: 'application',
      ConnectionStrings__OutboxDispatcher: 'dispatcher',
      [key]: issuer,
    };
    const configuration = { apiOrigin: 'http://127.0.0.1:5080', secret: 'private' };
    expect(publicApiEnvironment(values, configuration, {})[key]).toBe(issuer);
    expect(publicWebEnvironment(values, root, { [key]: issuer })).not.toHaveProperty(key);
    expect(publicApiEnvironment({ ...values, [key]: '' }, configuration, {})).not.toHaveProperty(
      key,
    );
    expect(
      publicApiEnvironment(
        {
          ConnectionStrings__Default: 'application',
          ConnectionStrings__OutboxDispatcher: 'dispatcher',
        },
        configuration,
        { [key]: issuer },
      ),
    ).not.toHaveProperty(key);
  });

  it('passes only explicit API credentials and hop configuration', () => {
    const env = publicApiEnvironment(
      {
        ConnectionStrings__Default: 'application',
        ConnectionStrings__OutboxDispatcher: 'dispatcher',
      },
      { apiOrigin: 'http://127.0.0.1:5080', secret: 'private' },
      {
        PATH: '/bin',
        ConnectionStrings__Migration: 'owner',
        ConnectionStrings__OutboxDispatcher: 'worker',
        LEARNSTACK_MIGRATION_PW: 'owner',
        ASPNETCORE_FORWARDEDHEADERS_ENABLED: 'true',
        Tenancy__TrustedHop__Secrets__1: 'stale',
      },
    );
    expect(env).toEqual({
      PATH: '/bin',
      ConnectionStrings__Default: 'application',
      ConnectionStrings__OutboxDispatcher: 'dispatcher',
      ASPNETCORE_ENVIRONMENT: 'Development',
      ASPNETCORE_URLS: 'http://127.0.0.1:5080',
      Tenancy__TrustedHop__Networks__0: '127.0.0.1/32',
      Tenancy__TrustedHop__Secrets__0: 'private',
    });
  });
  it.each(['ConnectionStrings__Default', 'ConnectionStrings__OutboxDispatcher'])(
    'requires explicit %s even when the ambient environment has it',
    (missing) => {
      const values: Record<string, string> = {
        ConnectionStrings__Default: 'application',
        ConnectionStrings__OutboxDispatcher: 'dispatcher',
      };
      const configuration = { apiOrigin: 'http://127.0.0.1:5080', secret: 'private' };
      const ambient = { ...values };
      delete values[missing];
      expect(() => publicApiEnvironment(values, configuration, {})).toThrow(
        'credential is missing',
      );
      expect(() => publicApiEnvironment(values, configuration, ambient)).toThrow(
        'credential is missing',
      );
      expect(() => publicDemoApiEnvironment(values, configuration, temporary(), ambient)).toThrow(
        'credential is missing',
      );
    },
  );
  it('refuses a foreign listener without closing it', async () => {
    const server = createServer();
    await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve));
    const address = server.address();
    if (address === null || typeof address === 'string') throw new Error('Missing listener');
    try {
      await expect(vacant(address.port)).rejects.toThrow('occupied');
      expect(server.listening).toBe(true);
    } finally {
      await new Promise<void>((resolve) => server.close(() => resolve()));
    }
    await expect(vacant(address.port)).resolves.toBeUndefined();
  });
  it('bounds response bodies and cancels oversized input', async () => {
    await expect(boundedBody(new Response('ready'), 5)).resolves.toBe('ready');
    await expect(boundedBody(new Response('too large'), 3)).rejects.toThrow('bound');
  });
  it('requires matching, valid TLS and keeps hostname checks with a loopback resolver', async () => {
    const root = temporary();
    const cert = join(root, 'cert.pem');
    const key = join(root, 'key.pem');
    execFileSync(
      'openssl',
      [
        'req',
        '-x509',
        '-newkey',
        'rsa:2048',
        '-nodes',
        '-keyout',
        key,
        '-out',
        cert,
        '-days',
        '1',
        '-subj',
        '/CN=demo.test',
        '-addext',
        'subjectAltName=DNS:demo.test',
      ],
      { stdio: 'ignore' },
    );
    expect(
      validateCertificate(readFileSync(cert), readFileSync(key), ['demo.test']).checkHost(
        'demo.test',
      ),
    ).toBe('demo.test');
    expect(() =>
      validateCertificate(readFileSync(cert), readFileSync(key), ['other.test']),
    ).toThrow();
    const otherKey = join(root, 'other-key.pem');
    execFileSync(
      'openssl',
      ['genpkey', '-algorithm', 'RSA', '-pkeyopt', 'rsa_keygen_bits:2048', '-out', otherKey],
      { stdio: 'ignore' },
    );
    expect(() =>
      validateCertificate(readFileSync(cert), readFileSync(otherKey), ['demo.test']),
    ).toThrow();
    expect(() =>
      validateCertificate(
        readFileSync(cert),
        readFileSync(key),
        ['demo.test'],
        Date.now() + 2 * 86400000,
      ),
    ).toThrow();
    const server = createHttpsServer(
      { cert: readFileSync(cert), key: readFileSync(key) },
      (_request, response) => response.end('ready'),
    );
    await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve));
    const address = server.address();
    if (address === null || typeof address === 'string') throw new Error('Missing TLS listener');
    const options = {
      ca: readFileSync(cert),
      signal: new AbortController().signal,
      loopback: true,
    };
    try {
      const url = `https://demo.test:${address.port}/`;
      expect((await demoRequest(url, options)).body).toBe('ready');
      await expect(demoRequest(url, { ...options, ca: [] })).rejects.toThrow();
      await expect(demoRequest(`https://other.test:${address.port}/`, options)).rejects.toThrow();
    } finally {
      server.closeAllConnections();
      await new Promise<void>((resolve) => server.close(() => resolve()));
    }
  });
});

describe('native exit and output closure ownership', () => {
  it.each(['nonzero', 'signal'])(
    'retains a native %s command failure before a later deadline despite paused stdout',
    async (kind) => {
      const processes = owner();
      let running: ReturnType<DemoProcesses['start']> | undefined;
      const start = processes.start.bind(processes);
      processes.start = (...args) => {
        running = start(...args);
        return running;
      };
      const result = processes
        .run(
          process.execPath,
          [
            '-e',
            `
        process.once('SIGUSR2', () => {
          process.stdout.write('late output\\n');
          ${kind === 'nonzero' ? 'process.exit(17);' : 'process.kill(process.pid, "SIGTERM");'}
        });
        process.stdout.write('ready\\n');
        setInterval(() => {}, 1000);
      `,
          ],
          { capture: true, label: 'Native command exit control', timeout: 5000 },
        )
        .catch((error: unknown) => error);
      if (!running || !running.child.stdout) throw new Error('Missing native exit control');
      const child = running.child;
      const stream = child.stdout!;
      await once(stream, 'data');
      const hold = () => stream.pause();
      stream.on('resume', hold);
      stream.pause();
      let closed = false;
      child.once('close', () => {
        closed = true;
      });
      try {
        const nativeExit = once(child, 'exit');
        child.kill('SIGUSR2');
        await nativeExit;
        expect(closed).toBe(false);
        expect(await result).toMatchObject({
          reason: 'exit',
          origin: 'command',
          exitCode: kind === 'nonzero' ? 17 : null,
          signalCode: kind === 'signal' ? 'SIGTERM' : null,
        });
        expect(closed).toBe(false);
      } finally {
        stream.off('resume', hold);
        stream.resume();
        await result;
        await processes.dispose();
      }
    },
    15000,
  );

  it('retains complete successful captured output through close after native exit', async () => {
    const processes = owner();
    let running: ReturnType<DemoProcesses['start']> | undefined;
    const start = processes.start.bind(processes);
    processes.start = (...args) => {
      running = start(...args);
      return running;
    };
    let settled = false;
    const result = processes
      .run(
        process.execPath,
        [
          '-e',
          `
      const keepAlive = setInterval(() => {}, 1000);
      process.once('SIGUSR2', () => {
        clearInterval(keepAlive);
        process.stdout.write('late output\\n');
      });
      process.stdout.write('ready\\n');
    `,
        ],
        { capture: true, label: 'Native success output control', timeout: 5000 },
      )
      .then((output) => {
        settled = true;
        return output;
      });
    if (!running || !running.child.stdout) throw new Error('Missing native success control');
    const child = running.child;
    const stream = child.stdout!;
    await once(stream, 'data');
    const hold = () => stream.pause();
    stream.on('resume', hold);
    stream.pause();
    try {
      const nativeExit = once(child, 'exit');
      child.kill('SIGUSR2');
      await nativeExit;
      expect(child.exitCode).toBe(0);
      await new Promise((resolve) => setTimeout(resolve, 100));
      expect(settled).toBe(false);
      stream.off('resume', hold);
      stream.resume();
      await expect(result).resolves.toBe('ready\nlate output\n');
    } finally {
      stream.off('resume', hold);
      stream.resume();
      await result;
      await processes.dispose();
    }
  });

  it.each([0, 17])(
    'latches the first daemon leader exit %s before a second daemon closes its streams',
    async (firstCode) => {
      const processes = owner();
      const daemons = [firstCode, 23].map((code) =>
        processes.daemon(
          process.execPath,
          [
            '-e',
            `
      process.once('SIGUSR2', () => {
        process.stdout.write('late output\\n');
        process.exit(${code});
      });
      process.stdout.write('ready\\n');
      setInterval(() => {}, 1000);
    `,
          ],
          { label: 'Native daemon exit control' },
        ),
      );
      const first = daemons[0]!;
      const second = daemons[1]!;
      await Promise.all(daemons.map((daemon) => once(daemon.child.stdout!, 'data')));
      const stream = first.child.stdout!;
      const hold = () => stream.pause();
      stream.on('resume', hold);
      stream.pause();
      let firstClosed = false;
      first.child.once('close', () => {
        firstClosed = true;
      });
      try {
        const nativeExit = once(first.child, 'exit');
        first.child.kill('SIGUSR2');
        await nativeExit;
        expect(firstClosed).toBe(false);
        second.child.kill('SIGUSR2');
        await once(second.child, 'close');
        const failure = await processes.failure;
        expect(failure).toMatchObject({ reason: 'exit', origin: 'daemon', exitCode: firstCode });
        let activeFailure: unknown;
        try {
          processes.assertActive();
        } catch (error) {
          activeFailure = error;
        }
        expect(activeFailure).toBe(failure);
        expect(firstClosed).toBe(false);
      } finally {
        stream.off('resume', hold);
        stream.resume();
        await processes.dispose();
      }
    },
  );
});

describe('owned real process lifecycle', () => {
  it('captures split multibyte stdout intact and bounds accumulated bytes', async () => {
    const processes = owner();
    let observed: (chunk: Buffer) => void = () => {};
    const first = new Promise<Buffer>((resolve) => {
      observed = resolve;
    });
    const running = processes.start(
      process.execPath,
      [
        '-e',
        `
      const text=Buffer.from('Ağaç duruşu');
      const keepAlive=setInterval(()=>{},1000);
      process.once('SIGUSR2',()=>{clearInterval(keepAlive);process.stdout.write(text.subarray(2));});
      process.stdout.write(text.subarray(0,2));
    `,
      ],
      { capture: true, label: 'Split UTF-8 control', observe: observed },
    );
    expect((await first).length).toBe(2);
    running.child.kill('SIGUSR2');
    expect((await running.exit).code).toBe(0);
    expect(running.result().output).toBe('Ağaç duruşu');
    await processes.dispose();
    await expect(
      owner().run(
        process.execPath,
        ['-e', "process.stdout.write(Buffer.from('ğ'.repeat(1024*1024+1)));"],
        { capture: true, label: 'Byte bound control' },
      ),
    ).rejects.toThrow();
  });
  it('routes real terminal hangup through the supervisor cancellation path', async () => {
    const processes = owner();
    let ready: () => void = () => {};
    const started = new Promise<void>((resolve) => {
      ready = resolve;
    });
    const moduleUrl = new URL('../../../../../scripts/demo-process.mjs', import.meta.url).href;
    const running = processes.start(
      process.execPath,
      [
        '--input-type=module',
        '-e',
        `
      import {installDemoCancellation} from ${JSON.stringify(moduleUrl)};
      const remove=installDemoCancellation(()=>{console.log('cancelled');remove();});
      console.log('ready');
    `,
      ],
      {
        capture: true,
        observe: (chunk) => {
          if (chunk.toString().includes('ready')) ready();
        },
      },
    );
    await started;
    running.child.kill('SIGHUP');
    expect((await running.exit).code).toBe(0);
    expect(running.result().output).toContain('cancelled');
  });
  it('cancels when the actual parent exits and leaves no owned group behind', async () => {
    const moduleUrl = new URL('../../../../../scripts/demo-process.mjs', import.meta.url).href;
    const watcher = `
      import {installDemoCancellation} from ${JSON.stringify(moduleUrl)};
      const remove=installDemoCancellation(()=>{console.log('parent-loss handled');remove();});
      process.send('ready');
    `;
    const output = await owner().run(
      process.execPath,
      [
        '--input-type=module',
        '-e',
        `
      import {spawn} from 'node:child_process';
      const child=spawn(process.execPath,['--input-type=module','-e',${JSON.stringify(watcher)}],
        {stdio:['ignore','inherit','inherit','ipc']});
      child.once('message',()=>process.exit(0));
    `,
      ],
      { capture: true, label: 'Parent-loss control', timeout: 3000 },
    );
    expect(output).toContain('parent-loss handled');
  });
  it('joins a daemon and its descendant, and closes ownership against later spawns', async () => {
    const processes = owner();
    let release: (pid: number) => void = () => {};
    const ready = new Promise<number>((resolve) => {
      release = resolve;
    });
    let output = '';
    const running = processes.daemon(
      process.execPath,
      [
        '-e',
        `
      const {spawn}=require('node:child_process');
      const child=spawn(process.execPath,['-e','setInterval(()=>{},1000)'],{stdio:'ignore'});
      let stopping=false;
      const finish=()=>{if(stopping) process.exit(0);};
      child.once('exit',finish);
      process.on('SIGTERM',()=>{
        stopping=true;
        if(child.exitCode!==null || child.signalCode!==null) finish();
        else child.kill('SIGTERM');
      });
      console.log(child.pid);
      setInterval(()=>{},1000);
    `,
      ],
      {
        label: 'tree',
        observe: (chunk) => {
          output += chunk.toString();
          if (output.includes('\n')) release(Number(output.trim()));
        },
      },
    );
    const descendant = await ready;
    expect(Number.isInteger(descendant)).toBe(true);
    expect(() => process.kill(descendant, 0)).not.toThrow();
    await processes.dispose();
    await running.exit;
    expect(running.child.exitCode).toBe(0);
    expect(() => process.kill(descendant, 0)).toThrow();
    expect(() => processes.start(process.execPath, ['-e', 'process.exit(0)'])).toThrow('closed');
  });
  it('joins deferred stdout through child close before finalizing completion counts', async () => {
    const processes = owner();
    const observer = apiCompletionObserver();
    const record = (status: number) =>
      JSON.stringify({
        SourceContext: 'Microsoft.AspNetCore.Hosting.Diagnostics',
        EventId: { Id: 2 },
        Path: '/api/v1/public/site',
        StatusCode: status,
        Method: 'GET',
      }) + '\n';
    const running = processes.start(
      process.execPath,
      [
        '-e',
        `
        const keepAlive = setInterval(() => {}, 1000);
        process.once('SIGUSR2', () => {
          clearInterval(keepAlive);
          process.stdout.write(${JSON.stringify(record(429))});
        });
        process.stdout.write(${JSON.stringify(record(200))});
      `,
      ],
      { label: 'Deferred output control', observe: observer.consume },
    );
    const stream = running.child.stdout;
    if (!stream) throw new Error('Owned child must have piped stdout');
    await once(stream, 'data');
    const beforeCleanup = structuredClone(observer.state);
    expect(beforeCleanup.total).toBe(1);
    // child_process auto-resumes at exit; retain the planted backpressure until
    // we explicitly release it, after confirming cleanup waits for EOF.
    const hold = () => stream.pause();
    stream.on('resume', hold);
    stream.pause();
    let disposed = false;
    let disposal: Promise<void> | undefined;
    try {
      const exited = once(running.child, 'exit');
      running.child.kill('SIGUSR2');
      await exited;
      disposal = processes.dispose().then(() => {
        disposed = true;
      });
      await new Promise((resolve) => setTimeout(resolve, 100));
      expect(disposed).toBe(false);
      stream.off('resume', hold);
      stream.resume();
      await disposal;
      observer.finish();
      expect(observer.state.statuses[429]).toBe(1);
      expect(() => finalCompletionCensus(beforeCleanup, observer.state, 1)).toThrow();
    } finally {
      stream.off('resume', hold);
      stream.resume();
      await disposal;
    }
  });
  it('captures a successful command and refuses its nonzero control', async () => {
    const processes = owner();
    await expect(
      processes.run(process.execPath, ['-e', 'process.stdout.write("ready")'], {
        capture: true,
        label: 'control',
      }),
    ).resolves.toBe('ready');
    await expect(
      processes.run(process.execPath, ['-e', 'process.exit(2)'], { label: 'control' }),
    ).rejects.toThrow('control failed');
  });
  it('aborts and joins a real setup child', async () => {
    const controller = new AbortController();
    const processes = owner(controller.signal);
    const running = processes.run(process.execPath, ['-e', 'setInterval(()=>{},1000)'], {
      label: 'setup',
    });
    controller.abort();
    await expect(running).rejects.toThrow();
    await expect(processes.dispose()).resolves.toBeUndefined();
  });
  it('refuses a hung command at its deadline and a daemon that exits early', async () => {
    const processes = owner();
    await expect(
      processes.run(process.execPath, ['-e', 'setInterval(()=>{},1000)'], {
        timeout: 20,
        label: 'hung',
      }),
    ).rejects.toThrow('hung failed');
    processes.daemon(process.execPath, ['-e', 'process.exit(1)'], { label: 'daemon' });
    await expect(processes.failure).resolves.toEqual(
      expect.objectContaining({ message: 'demo: daemon exited failed' }),
    );
    expect(() => processes.assertActive()).toThrow('daemon exited');
  });
});

describe('independent owned cleanup attempts', () => {
  it.each(['processes', 'observer', 'compose'])(
    'retains private state and failure after %s fails, without skipping other teardown',
    async (stage) => {
      const actions: string[] = [];
      const cause = new Error('private diagnostic sentinel');
      const attempt = (name: string) => () => {
        actions.push(name);
        if (stage === name) throw cause;
      };
      const error = await disposeDemoResources({
        processes: { dispose: async () => attempt('processes')() },
        finish: attempt('observer'),
        teardown: attempt('compose'),
        removePrivate: attempt('private'),
      }).catch((error: unknown) => error);
      expect(actions).toEqual(['processes', 'observer', 'compose']);
      expect(error).toBeInstanceOf(AggregateError);
      expect((error as AggregateError).errors).toEqual([cause]);
      expect((error as Error).message).not.toContain(cause.message);
    },
  );
  it.each([false, true])(
    'removes private state only after every applicable stage confirms cleanup (CI=%s)',
    async (ci) => {
      const actions: string[] = [];
      await disposeDemoResources({
        processes: {
          dispose: async () => {
            actions.push('processes');
          },
        },
        finish: () => {
          actions.push('observer');
        },
        teardown: ci
          ? async () => {
              actions.push('compose');
            }
          : undefined,
        removePrivate: () => {
          actions.push('private');
        },
      });
      expect(actions).toEqual(
        ci ? ['processes', 'observer', 'compose', 'private'] : ['processes', 'observer', 'private'],
      );
    },
  );
  it('retains observer causes internally without printing private output', async () => {
    const cause = new Error('private observer sentinel');
    const processes = new DemoProcesses(new AbortController().signal);
    const error = await processes
      .run(process.execPath, ['-e', 'process.stdout.write("output")'], {
        label: 'Fixed observer stage',
        observe: () => {
          throw cause;
        },
      })
      .catch((error: unknown) => error);
    expect(error).toBeInstanceOf(Error);
    expect((error as Error).message).toBe('demo: Fixed observer stage failed');
    expect(((error as Error).cause as Error).cause).toBe(cause);
    await processes.dispose();
  });
});

describe('listener bind diagnostics', () => {
  it.each(['EADDRINUSE', 'EACCES'])(
    'keeps %s distinct without exposing socket diagnostics',
    (code) => {
      const result = spawnSync(
        process.execPath,
        [
          '--input-type=module',
          '-e',
          `
      import net from 'node:net';
      import { EventEmitter } from 'node:events';
      import { syncBuiltinESMExports } from 'node:module';
      const cause = Object.assign(new Error('private bind diagnostic'), { code: process.argv[1] });
      net.createServer = () => {
        const server = new EventEmitter();
        server.listen = () => queueMicrotask(() => server.emit('error', cause));
        return server;
      };
      syncBuiltinESMExports();
      const { vacant } = await import(process.argv[2]);
      try { await vacant(3000); process.exitCode = 2; }
      catch (error) { console.log(JSON.stringify({ message: error.message, code: error.cause?.code })); }
    `,
          code,
          new URL('../../../../../scripts/demo-http.mjs', import.meta.url).href,
        ],
        {
          encoding: 'utf8',
          timeout: 5000,
        },
      );
      expect(result.status).toBe(0);
      expect(result.stderr).toBe('');
      expect(JSON.parse(result.stdout)).toEqual({
        message:
          code === 'EADDRINUSE'
            ? 'Port 3000 is occupied; stop its owner before make demo'
            : 'Demo listener bind check failed',
        code,
      });
    },
  );
});
