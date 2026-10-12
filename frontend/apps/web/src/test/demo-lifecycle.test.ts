// @vitest-environment node
import { execFileSync, spawnSync } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import { once } from 'node:events';
import { mkdirSync, mkdtempSync, readFileSync, realpathSync, rmSync, writeFileSync } from 'node:fs';
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

const roots: string[] = [];
const owners: DemoProcesses[] = [];
afterEach(async () => {
  await Promise.all(owners.splice(0).map((owner) => owner.dispose()));
  for (const root of roots.splice(0)) rmSync(root, { recursive: true, force: true });
});
function temporary() {
  const root = mkdtempSync(join(tmpdir(), 'learnstack-demo-control-'));
  roots.push(root);
  return root;
}
function owner(signal = new AbortController().signal) {
  const processes = new DemoProcesses(signal);
  owners.push(processes);
  return processes;
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
  it.each(['simple', 'semi;colon', ' leading and trailing ', 'a"double"quote', "a'single'quote"])(
    'accepts all four local roles with quoted password %s',
    (password) => {
      expect(() => validateDemoDatabaseConfiguration(databaseValues(password))).not.toThrow();
    },
  );
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
    const base = {
      PATH: '/bin',
      HOME: '/home',
      DOTNET_ROOT: '/dotnet',
      TMPDIR: '/tmp',
      APPDATA: '/profile',
      ConnectionStrings__Default: 'ambient-application',
      ConnectionStrings__Migration: 'owner',
      ConnectionStrings__OutboxDispatcher: 'dispatcher',
      CONNECTIONSTRINGS__OUTBOXDISPATCHER: 'uppercase-dispatcher',
      'ConnectionStrings:OutboxDispatcher': 'colon-dispatcher',
      LEARNSTACK_OUTBOX_PW: 'dispatcher-password',
      DEBUG: '*',
    };
    const prerequisites = {
      PATH: '/bin',
      HOME: '/home',
      DOTNET_ROOT: '/dotnet',
      TMPDIR: '/tmp',
      APPDATA: '/profile',
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
  it.each([
    'COMPOSE_PROFILES=gated',
    ' export COMPOSE_PROFILES = gated',
    'COMPOSE_FILE: foreign.yml',
    'COMPOSE_PROJECT_NAME=foreign',
    'COMPOSE_ENV_FILES=foreign.env',
  ])('refuses a selected dotenv Compose control before Docker: %s', (assignment) => {
    const root = temporary();
    writeFileSync(join(root, '.env'), `POSTGRES_PASSWORD=private\n${assignment}\n`);
    expect(() => composeSelection('dev', {}, root)).toThrow('Reserved COMPOSE_*');
    writeFileSync(join(root, '.env'), '# COMPOSE_PROFILES=gated\nPOSTGRES_PASSWORD=private\n');
    expect(() => composeSelection('dev', {}, root)).not.toThrow();
  });
  it('refuses stale ambient seed credentials before stack or database actions', () => {
    const root = temporary();
    mkdirSync(join(root, 'scripts'));
    const script = join(root, 'scripts/seed.sh');
    writeFileSync(script, readFileSync(new URL('../../../../../scripts/seed.sh', import.meta.url)));
    const migration = 'Host=localhost;Username=learnstack_migration';
    const app = 'Host=localhost;Username=learnstack_app';
    writeFileSync(
      join(root, 'selected.env'),
      `ConnectionStrings__Migration=${migration}\nConnectionStrings__Default=${app}\n`,
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
      expect(result.stderr).toContain('disagrees');
      expect(result.stderr).not.toContain('foreign');
    }
  });
  it('launches the argument-free Seeder without selected or ambient dispatcher credentials', () => {
    const root = temporary();
    for (const directory of ['scripts', 'backend', 'bin']) mkdirSync(join(root, directory));
    for (const file of ['seed.sh', 'connection-string.awk', 'demo-database.mjs'])
      writeFileSync(
        join(root, 'scripts', file),
        readFileSync(new URL(`../../../../../scripts/${file}`, import.meta.url)),
      );
    writeFileSync(
      join(root, '.env'),
      'ConnectionStrings__Migration=Username=learnstack_migration\nConnectionStrings__Default=Username=learnstack_app\nConnectionStrings__OutboxDispatcher=Username=learnstack_outbox_admin\n',
    );
    for (const [name, body] of Object.entries({
      docker: '[ "$*" = "compose version" ]',
      node: 'if [ "$*" = "scripts/wait-compose.mjs" ]; then exit 0; fi\nexec "$REAL_NODE" "$@"',
      dotnet:
        '[ "$#" = 3 ] && [ "$1" = run ] && [ "$2" = --project ] && [ "$3" = src/LearnStack.Tools.Seeder ] && [ "$(basename "$PWD")" = backend ] && [ "$ConnectionStrings__Default" = Username=learnstack_app ] && [ "$HOME" = /seed-home ] && [ -n "$PATH" ] && [ -z "${ConnectionStrings__OutboxDispatcher+x}" ] && [ -z "${CONNECTIONSTRINGS__OUTBOXDISPATCHER+x}" ] && [ -z "${LEARNSTACK_OUTBOX_PW+x}" ] && ! env | /usr/bin/grep -q "^ConnectionStrings:OutboxDispatcher=" && printf launched > ../launched',
    }))
      writeFileSync(join(root, 'bin', name), `#!/bin/sh\n${body}\n`, { mode: 0o700 });
    const launch = () =>
      spawnSync('bash', [join(root, 'scripts/seed.sh')], {
        cwd: root,
        env: {
          PATH: `${join(root, 'bin')}:${process.env.PATH}`,
          TEST_ROOT: root,
          REAL_NODE: process.execPath,
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
    expect(readFileSync(join(root, 'launched'), 'utf8')).toBe('launched');
    const script = join(root, 'scripts/seed.sh');
    writeFileSync(
      script,
      readFileSync(script, 'utf8').replace(
        "['run', '--project', 'src/LearnStack.Tools.Seeder']",
        "['run', '--project', 'src/LearnStack.Tools.Seeder', '--nologo']",
      ),
    );
    const refused = launch();
    expect(refused.status).not.toBe(0);
    expect(refused.stderr).toContain('tenant seeding failed');
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
    writeFileSync(
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
    writeFileSync(join(root, '.env'), 'POSTGRES_PASSWORD=private-file-value\n');
    const selected = composeSelection(
      'e2e',
      {
        PATH: '/bin',
        COMPOSE_PROFILES: 'gated',
        COMPOSE_FILE: 'foreign.yml',
        COMPOSE_PROJECT_NAME: 'foreign',
        POSTGRES_PASSWORD: 'ambient-shadow',
        LEARNSTACK_COMPOSE_PROJECT: 'owned-demo',
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
    expect(selected.env.PATH).toBe('/bin');
    expect(Object.isFrozen(selected.args)).toBe(true);
  });
  it('refuses missing environment, unsupported mode and unsafe project names', () => {
    const root = temporary();
    expect(() => composeSelection('dev', {}, root)).toThrow();
    writeFileSync(join(root, '.env'), 'A=B\n');
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
