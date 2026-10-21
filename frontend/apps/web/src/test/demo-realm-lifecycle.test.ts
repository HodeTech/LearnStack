// @vitest-environment node
import { execFileSync, spawnSync } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import {
  chmodSync,
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

import { demoFailureMessage } from '../../../../../scripts/demo.mjs';

function writePrivateFileSync(path: string, source: Parameters<typeof writeFileSync>[1]) {
  writeFileSync(path, source, { mode: 0o600 });
}

const roots: string[] = [];
afterEach(() => {
  for (const root of roots.splice(0)) rmSync(root, { recursive: true, force: true });
});

function fixture() {
  const root = realpathSync(mkdtempSync(join(tmpdir(), 'learnstack-realm-lifecycle-')));
  roots.push(root);
  for (const directory of ['scripts', 'backend', 'bin', 'frontend/apps/web/.server'])
    mkdirSync(join(root, directory), { recursive: true });
  for (const name of [
    'demo',
    'seed',
    'compose',
    'demo-process',
    'demo-database',
    'demo-observation',
    'public-runtime',
    'development-realm-owner',
    'development-realm-errors',
  ])
    writeFileSync(
      join(root, `scripts/${name}.mjs`),
      readFileSync(new URL(`../../../../../scripts/${name}.mjs`, import.meta.url)),
    );
  for (const name of ['seed.sh', 'connection-string.awk'])
    writeFileSync(
      join(root, 'scripts', name),
      readFileSync(new URL(`../../../../../scripts/${name}`, import.meta.url)),
      { mode: name === 'seed.sh' ? 0o700 : 0o600 },
    );
  mkdirSync(join(root, 'frontend/apps/web/scripts'));
  writeFileSync(
    join(root, 'frontend/apps/web/scripts/stop-test-child.mjs'),
    readFileSync(new URL('../../scripts/stop-test-child.mjs', import.meta.url)),
  );
  writeFileSync(join(root, 'package.json'), '{"type":"module"}\n');
  writeFileSync(
    join(root, 'scripts/demo-http.mjs'),
    'export async function vacant() {}\nexport async function demoRequest() { throw new Error("Unexpected product request"); }\n',
  );
  writeFileSync(
    join(root, 'scripts/demo-source.mjs'),
    'export async function demoSource() { return { commit: "fixture", tree: "fixture" }; }\n',
  );
  writeFileSync(
    join(root, 'scripts/wait-compose.mjs'),
    `export function defaultServices() { return ['postgres', 'keycloak']; }
export async function waitCompose(selected, processes, signal, postgresOnly = false) {
  const f = globalThis.lifecycle;
  f.events.push(postgresOnly && f.subject === 'seed' ? 'postgres-ready' : 'ready');
  if (f.selected === undefined) f.selected = selected;
  if (f.signal === undefined) f.signal = signal;
  f.selectionIdentity &&= f.selected === selected;
  f.signalIdentity &&= f.signal === signal;
}
`,
  );
  writeFileSync(
    join(root, 'scripts/reconcile-development-realm.mjs'),
    `import { RealmError } from './development-realm-errors.mjs';
export async function reconcileDevelopmentRealm({ selected, signal }) {
  const f = globalThis.lifecycle;
  f.events.push('realm-start');
  f.selectionIdentity &&= selected === f.selected;
  f.signalIdentity &&= signal === f.signal && signal instanceof AbortSignal;
  f.selectedDescription = { project: selected.project, file: selected.file, mode: selected.mode };
  if (f.scenario === 'reconcile-failure') throw f.realmFailure;
  if (f.scenario === 'cancel') {
    setTimeout(() => f.controller.abort(), 10);
    await new Promise(resolve => signal.addEventListener('abort', resolve, { once: true }));
    f.events.push('realm-cancel');
    await new Promise(resolve => setTimeout(resolve, 30));
    f.events.push('realm-joined');
    throw new RealmError('preflight', 'cancelled');
  }
  await new Promise(resolve => setTimeout(resolve, 30));
  f.events.push('realm-complete');
  return { mutations: 0 };
}
`,
  );
  writeFileSync(
    join(root, 'frontend/apps/web/.server/ingress.js'),
    `import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { parseEnv } from 'node:util';
export const parseLocalEnvironment = parseEnv;
export function loadLocalEnvironment(root) { return parseEnv(readFileSync(join(root, '.env'), 'utf8')); }
export function publicServerConfiguration(values) {
  return { apiOrigin: 'http://127.0.0.1:5080', secret: values.LEARNSTACK_PUBLIC_HOP_SECRET,
    certificate: values.LEARNSTACK_PUBLIC_TLS_CERT, privateKey: values.LEARNSTACK_PUBLIC_TLS_KEY };
}
`,
  );
  const canaries = [randomBytes(20).toString('hex'), randomBytes(20).toString('hex')];
  const values: Record<string, string> = {
    POSTGRES_DB: 'learnstack',
    POSTGRES_PASSWORD: 'fixture-postgres',
    KEYCLOAK_ADMIN: 'fixture-admin',
    KEYCLOAK_ADMIN_PASSWORD: canaries[0]!,
    KEYCLOAK_WEB_CLIENT_SECRET: canaries[1]!,
    LEARNSTACK_PUBLIC_API_ORIGIN: 'http://127.0.0.1:5080',
    LEARNSTACK_PUBLIC_HOP_SECRET: 'fixture-hop',
  };
  for (const [suffix, role, key] of [
    ['Migration', 'learnstack_migration', 'LEARNSTACK_MIGRATION_PW'],
    ['Default', 'learnstack_app', 'LEARNSTACK_APP_PW'],
    ['PlatformAdmin', 'learnstack_platform', 'LEARNSTACK_PLATFORM_PW'],
    ['OutboxDispatcher', 'learnstack_outbox_admin', 'LEARNSTACK_OUTBOX_PW'],
  ]) {
    const password = randomBytes(20).toString('hex');
    values[key!] = password;
    // The disposable fixture generates this credential at runtime; none is committed.
    values[`ConnectionStrings__${suffix}`] =
      `Host=localhost;Port=5432;Database=learnstack;Username=${role};Password=${password}`; // leakwatch:ignore
  }
  const source =
    Object.entries(values)
      .map(([key, value]) => `${key}=${value}`)
      .join('\n') + '\n';
  writeFileSync(join(root, '.env.example'), source);
  const env = {
    PATH: `${join(root, 'bin')}:${process.env.PATH}`,
    HOME: root,
    NODE_ENV: 'test' as const,
    // This unrelated inherited secret must not enter ordinary child processes.
    Authentication__Bff__ClientSecret: randomBytes(20).toString('hex'),
    ConnectionStrings__OutboxDispatcher: 'ambient-dispatcher',
    DEBUG: '*',
  };
  for (const name of ['docker', 'dotnet'])
    writeFileSync(
      join(root, 'bin', name),
      `#!/bin/sh\nprintf unexpected >> '${join(root, 'actions')}'\nexit 19\n`,
      { mode: 0o700 },
    );
  return { root, env, values, source, canaries };
}

type Fixture = ReturnType<typeof fixture>;
type Result = {
  events: string[];
  failure: string;
  selectionIdentity: boolean;
  signalIdentity: boolean;
  selectedDescription?: { project: string; file: string; mode: string };
  commands: Array<{
    command: string;
    args: string[];
    label: string;
    confined: boolean;
    prerequisitesPreserved: boolean;
    dockerContext: string | null;
    dockerHome: string | null;
    timeout?: number;
  }>;
  migrationSelection: boolean;
};

function execute(f: Fixture, subject: 'seed' | 'demo', scenario = 'success') {
  if (subject === 'demo' && scenario !== 'preflight') {
    execFileSync(
      'openssl',
      [
        'req',
        '-x509',
        '-newkey',
        'rsa:2048',
        '-nodes',
        '-keyout',
        join(f.root, 'key.pem'),
        '-out',
        join(f.root, 'cert.pem'),
        '-days',
        '1',
        '-subj',
        '/CN=localhost',
        '-addext',
        'subjectAltName=DNS:localhost',
      ],
      { stdio: 'ignore' },
    );
  }
  const harness = join(f.root, 'scripts/fixture-harness.mjs');
  writeFileSync(
    harness,
    `import { copyFileSync } from 'node:fs';
import { join } from 'node:path';
import { DemoProcesses } from './demo-process.mjs';
import { composeSelection } from './compose.mjs';
const f = globalThis.lifecycle = { events: [], commands: [], scenario: ${JSON.stringify(scenario)},
  subject: ${JSON.stringify(subject)},
  selectionIdentity: true, signalIdentity: true, migrationSelection: false,
  controller: new AbortController(), seedFailure: new Error('seed refusal'),
  realmFailure: new Error('realm refusal'), boundary: new Error('renderer boundary') };
DemoProcesses.prototype.dispose = async function() { f.events.push('dispose'); };
DemoProcesses.prototype.daemon = function() { f.events.push('daemon'); throw new Error('Unexpected daemon'); };
DemoProcesses.prototype.run = async function(command, args, options = {}) {
  const env = options.env ?? {};
  const isCompose = command === 'docker' && args[0] === 'compose' && args.includes('--project-name');
  const startup = command === 'make' && args.includes('dev');
  const migration = command === 'make' && args.includes('migrate');
  const allowed = new Set(['PATH', 'HOME', 'DOTNET_ROOT', 'TMPDIR', 'APPDATA',
    'HTTP_PROXY', 'HTTPS_PROXY', 'ALL_PROXY', 'NO_PROXY', 'http_proxy', 'https_proxy', 'all_proxy', 'no_proxy',
    'NODE_EXTRA_CA_CERTS', 'SSL_CERT_FILE', 'SSL_CERT_DIR',
    'NUGET_PACKAGES', 'NUGET_HTTP_CACHE_PATH', 'COREPACK_HOME', 'PNPM_HOME', 'XDG_CACHE_HOME',
    'NPM_CONFIG_CACHE', 'npm_config_cache', 'NPM_CONFIG_STORE_DIR', 'npm_config_store_dir',
    ...(startup || migration || command === 'docker' ? ['USERPROFILE', 'XDG_CONFIG_HOME', 'XDG_RUNTIME_DIR', 'DOCKER_CONFIG', 'DOCKER_CONTEXT', 'DOCKER_HOST', 'DOCKER_TLS_VERIFY', 'DOCKER_CERT_PATH', 'DOCKER_DEFAULT_PLATFORM', 'DOCKER_API_VERSION'] : []),
    ...(startup || migration ? ['LEARNSTACK_COMPOSE_PROJECT', 'LEARNSTACK_COMPOSE_ENV_FILE', 'LEARNSTACK_COMPOSE_MODE'] : []),
    ...(migration ? ['ConnectionStrings__Migration'] : []),
    ...(options.label === 'Contextual seed convergence' || options.label === 'Application seed convergence' ? ['ConnectionStrings__Default'] : [])]);
  // The app seed is recognised by its real, argument-free Release DLL call too.
  if (command === 'dotnet' && args.length === 1 && args[0].endsWith('/LearnStack.Tools.Seeder.dll')) allowed.add('ConnectionStrings__Default');
  f.commands.push({ command, args, label: options.label, timeout: options.timeout,
    dockerContext: env.DOCKER_CONTEXT ?? null,
    dockerHome: env.HOME ?? null,
    prerequisitesPreserved: (command === 'docker' ? ['DOCKER_CONFIG', 'DOCKER_CONTEXT']
      : ['HTTP_PROXY', 'http_proxy', 'NUGET_PACKAGES'])
      .every(key => process.env[key] === undefined || env[key] === process.env[key]),
    confined: isCompose || Object.keys(env).every(key => allowed.has(key)) });
  if (migration) f.migrationSelection = env.LEARNSTACK_COMPOSE_PROJECT?.startsWith('learnstack-demo-')
    && env.LEARNSTACK_COMPOSE_ENV_FILE === join(${JSON.stringify(f.root)}, '.env')
    && env.LEARNSTACK_COMPOSE_MODE === 'e2e' && env.ConnectionStrings__Migration?.includes('Username=learnstack_migration');
  if (${JSON.stringify(subject)} === 'seed') {
    if (command === 'docker' && JSON.stringify(args) === JSON.stringify(['compose', 'version'])) f.events.push('compose-version');
    if (startup) f.events.push('stack-up');
    if (migration) f.events.push('migrate');
  }
  if (command === 'dotnet' && args.includes('--demo-manifest')) return JSON.stringify([{ host: 'localhost' }]);
  if (command === 'docker' && args[0] === 'context')
    return JSON.stringify([{ Endpoints: { docker: { Host: f.scenario === 'remote-context'
      || (f.scenario === 'selected-remote-context' && env.DOCKER_CONTEXT === undefined)
      || (f.scenario === 'selected-remote-home' && env.HOME === undefined)
      ? 'tcp://remote.fixture.invalid:2375' : 'unix:///var/run/docker.sock' } } }]);
  if (command === 'openssl' && args[0] === 'req') {
    copyFileSync(join(${JSON.stringify(f.root)}, 'key.pem'), args[args.indexOf('-keyout') + 1]);
    copyFileSync(join(${JSON.stringify(f.root)}, 'cert.pem'), args[args.indexOf('-out') + 1]);
  }
  if (isCompose && args.includes('config')) return JSON.stringify({ services: { postgres: {}, keycloak: {} } });
  if (isCompose && args.includes('stop')) f.events.push('stop-keycloak');
  if (command === 'dotnet' && args.length === 1 && args[0].endsWith('/LearnStack.Tools.Seeder.dll')) {
    f.events.push('appseed');
    if (f.scenario === 'seed-failure') throw f.seedFailure;
  }
  if (command === 'pnpm' && args.at(-1) === 'build') { f.events.push('renderer'); throw f.boundary; }
  return '';
};
let failure = '';
console.log = () => {};
try {
  if (${JSON.stringify(subject)} === 'seed') {
    const { seedDevelopment } = await import('./seed.mjs');
    f.selected = composeSelection('dev', { ...process.env, LEARNSTACK_COMPOSE_PROJECT: 'owned-seed', LEARNSTACK_COMPOSE_ENV_FILE: 'selected.env' });
    f.signal = f.controller.signal;
    await seedDevelopment({ selected: f.selected, signal: f.signal });
  } else {
    const { startDemo } = await import('./demo.mjs');
    await startDemo(${JSON.stringify(scenario === 'preflight' ? 'local' : 'ci')});
  }
} catch (error) { failure = error.message; }
process.stdout.write(JSON.stringify({ events: f.events, commands: f.commands, failure,
  selectionIdentity: f.selectionIdentity, signalIdentity: f.signalIdentity,
  selectedDescription: f.selectedDescription, migrationSelection: f.migrationSelection }));
`,
  );
  const environment: NodeJS.ProcessEnv = { ...f.env };
  if (subject === 'demo') delete environment.ConnectionStrings__OutboxDispatcher;
  const execution = spawnSync(process.execPath, [harness], {
    cwd: f.root,
    env: environment,
    encoding: 'utf8',
    timeout: 10000,
  });
  expect(execution.error).toBeUndefined();
  expect(execution.status, execution.stderr).toBe(0);
  for (const canary of f.canaries) {
    expect(execution.stdout).not.toContain(canary);
    expect(execution.stderr).not.toContain(canary);
  }
  return JSON.parse(execution.stdout) as Result;
}

type BoundaryEvent = {
  stage: string;
  confined: boolean;
  selected: boolean;
  parentPrivate?: boolean;
};

function makeSeedFixture() {
  const f = fixture();
  const selectedFile = join(f.root, 'selected.env');
  const events = join(f.root, 'boundary-events');
  const project = 'owned-seed-boundary';
  const dockerConfiguration = join(f.root, 'docker-config');
  writeFileSync(
    join(f.root, 'Makefile'),
    readFileSync(new URL('../../../../../Makefile', import.meta.url)),
  );
  for (const name of ['wait-compose', 'demo-http'])
    writeFileSync(
      join(f.root, `scripts/${name}.mjs`),
      readFileSync(new URL(`../../../../../scripts/${name}.mjs`, import.meta.url)),
    );
  mkdirSync(
    join(
      f.root,
      'backend/src/Modules/Tenancy/LearnStack.Modules.Tenancy.Infrastructure/Persistence/Migrations',
    ),
    { recursive: true },
  );
  const decoys = [randomBytes(20).toString('hex'), randomBytes(20).toString('hex')];
  f.canaries.push(...decoys);
  const defaultSource = f.source
    .replace(f.values.KEYCLOAK_ADMIN_PASSWORD!, decoys[0]!)
    .replace(f.values.KEYCLOAK_WEB_CLIENT_SECRET!, decoys[1]!)
    .replaceAll('Host=localhost', 'Host=127.0.0.1');
  writePrivateFileSync(join(f.root, '.env'), defaultSource);
  writePrivateFileSync(selectedFile, f.source);
  // These are command-wiring controls. The real provider proofs own Docker and .NET behavior.
  writeFileSync(
    join(f.root, 'scripts/boundary-observer.mjs'),
    `import { appendFileSync, readFileSync } from 'node:fs';
import { parseEnv } from 'node:util';
export const values = parseEnv(readFileSync(${JSON.stringify(selectedFile)}, 'utf8'));
export function record(stage, connection, selected = true) {
  const permitted = connection === undefined ? undefined : 'ConnectionStrings__' + connection;
  const confined = Object.keys(process.env).every(key =>
    !/keycloak|connectionstrings|_pw$|password|bff|debug|private/i.test(key) || key === permitted);
  const connectionMatches = permitted === undefined || process.env[permitted] === values[permitted];
  const metadataMatches = connection !== 'Migration' || (
    process.env.LEARNSTACK_COMPOSE_PROJECT === ${JSON.stringify(project)}
    && process.env.LEARNSTACK_COMPOSE_ENV_FILE === ${JSON.stringify(selectedFile)}
    && process.env.LEARNSTACK_COMPOSE_MODE === 'e2e');
  const needsDocker = ['compose-version', 'stack-up', 'postgres-readiness', 'readiness-config',
    'readiness-status', 'migration-tools', 'migration-build', 'migration-ef'].includes(stage);
  const dockerMatches = !needsDocker || (process.env.DOCKER_CONTEXT === 'fixture-context'
    && process.env.DOCKER_HOST === 'unix:///fixture/docker.sock'
    && process.env.DOCKER_CONFIG === ${JSON.stringify(dockerConfiguration)});
  appendFileSync(${JSON.stringify(events)}, JSON.stringify({ stage, confined,
    selected: selected && connectionMatches && metadataMatches && dockerMatches }) + '\\n');
}
`,
  );
  const preload = join(f.root, 'scripts/boundary-discovery.mjs');
  writeFileSync(
    preload,
    `globalThis.fetch = async function(input) {
  const url = String(input);
  if (!/^http:\\/\\/localhost:8080\\/realms\\/(learnstack|learnstack-hub)\\/\\.well-known\\/openid-configuration$/.test(url)) throw new Error('Unexpected fixture request');
  return new Response(JSON.stringify({ issuer: url.slice(0, url.indexOf('/.well-known/')) }));
};
`,
  );
  writeFileSync(
    join(f.root, 'bin/node'),
    `#!${process.execPath}
import { spawnSync } from 'node:child_process';
import { record } from '../scripts/boundary-observer.mjs';
const args = process.argv.slice(2);
if (args[0] === 'scripts/wait-compose.mjs') record('postgres-readiness', 'Migration');
const child = spawnSync(${JSON.stringify(process.execPath)}, ['--import', ${JSON.stringify(preload)}, ...args], { stdio: 'inherit', env: process.env });
process.exit(child.status ?? 19);
`,
    { mode: 0o700 },
  );
  writeFileSync(
    join(f.root, 'bin/docker'),
    `#!${process.execPath}
import { record } from '../scripts/boundary-observer.mjs';
const args = process.argv.slice(2);
if (JSON.stringify(args) === JSON.stringify(['compose', 'version'])) {
  record('compose-version');
} else if (JSON.stringify(args) === JSON.stringify(['context', 'inspect'])) {
  process.stdout.write(JSON.stringify([{ Endpoints: { docker: { Host: 'unix:///fixture/docker.sock' } } }]));
} else {
  const selected = args[0] === 'compose' && args[args.indexOf('--project-name') + 1] === ${JSON.stringify(project)}
    && args[args.indexOf('--env-file') + 1] === ${JSON.stringify(selectedFile)}
    && args.filter(arg => arg === '-f').length === 2;
  const names = ['postgres', 'keycloak', 'one', 'two', 'three', 'four', 'five'];
  if (args.includes('up') && args.at(-1) === '-d') record('stack-up', undefined, selected);
  else if (args.includes('config') && args.at(-1) === 'json') {
    record('readiness-config', undefined, selected);
    process.stdout.write(JSON.stringify({ services: Object.fromEntries(names.map(name => [name, {}])) }));
  } else if (args.includes('ps') && args.at(-1) === 'json') {
    record('readiness-status', undefined, selected);
    process.stdout.write(JSON.stringify(names.map(Service => ({ Service, State: 'running' }))));
  } else process.exit(19);
}
`,
    { mode: 0o700 },
  );
  writeFileSync(
    join(f.root, 'bin/dotnet'),
    `#!${process.execPath}
import { record, values } from '../scripts/boundary-observer.mjs';
const args = process.argv.slice(2);
if (process.cwd() !== ${JSON.stringify(join(f.root, 'backend'))}) process.exit(19);
if (JSON.stringify(args) === JSON.stringify(['tool', 'restore'])) record('migration-tools', 'Migration');
else if (JSON.stringify(args) === JSON.stringify(['build', 'src/LearnStack.Api', '--configuration', 'Release', '--nologo'])) record('migration-build', 'Migration');
else if (args[0] === 'ef' && args[1] === 'database' && args[2] === 'update') record('migration-ef', 'Migration', !args.includes('--connection') && !args.some(arg => arg.includes('Password=')));
else if (JSON.stringify(args) === JSON.stringify(['build', 'src/LearnStack.Tools.Seeder', '--configuration', 'Release', '--nologo'])) record('seeder-build');
else if (args.length === 1 && args[0] === ${JSON.stringify(join(f.root, 'backend/src/LearnStack.Tools.Seeder/bin/Release/net10.0/LearnStack.Tools.Seeder.dll'))}) record('appseed', 'Default');
else process.exit(19);
`,
    { mode: 0o700 },
  );
  writeFileSync(
    join(f.root, 'scripts/reconcile-development-realm.mjs'),
    `import { appendFileSync, readFileSync } from 'node:fs';
import { realmEnvironment } from './development-realm-owner.mjs';
export async function reconcileDevelopmentRealm({ selected, signal }) {
  const values = realmEnvironment(readFileSync(selected.file, 'utf8'));
  const parentPrivate = Object.entries(values).every(([key, value]) => process.env[key] === value);
  const selectedSource = selected.file === ${JSON.stringify(selectedFile)} && selected.project === ${JSON.stringify(project)}
    && selected.mode === 'e2e' && signal instanceof AbortSignal;
  appendFileSync(${JSON.stringify(events)}, JSON.stringify({ stage: 'realm', confined: true,
    selected: selectedSource, parentPrivate }) + '\\n');
  return { mutations: 0 };
}
`,
  );
  const environment = {
    ...f.env,
    KEYCLOAK_ADMIN: f.values.KEYCLOAK_ADMIN!,
    KEYCLOAK_ADMIN_PASSWORD: f.values.KEYCLOAK_ADMIN_PASSWORD!,
    KEYCLOAK_WEB_CLIENT_SECRET: f.values.KEYCLOAK_WEB_CLIENT_SECRET!,
    ConnectionStrings__Migration: f.values.ConnectionStrings__Migration!,
    ConnectionStrings__Default: f.values.ConnectionStrings__Default!,
    LEARNSTACK_COMPOSE_PROJECT: project,
    LEARNSTACK_COMPOSE_ENV_FILE: selectedFile,
    LEARNSTACK_COMPOSE_MODE: 'e2e',
    DOCKER_CONTEXT: 'fixture-context',
    DOCKER_HOST: 'unix:///fixture/docker.sock',
    DOCKER_CONFIG: dockerConfiguration,
    UNRELATED_PRIVATE_VALUE: randomBytes(20).toString('hex'),
  };
  const sensitiveValues = [
    ...f.canaries,
    environment.Authentication__Bff__ClientSecret,
    environment.UNRELATED_PRIVATE_VALUE,
    ...Object.entries(f.values)
      .filter(([key]) => key.startsWith('ConnectionStrings__'))
      .map(([, value]) => value),
  ];
  return {
    root: f.root,
    environment,
    selectedFile,
    sensitiveValues,
    run() {
      writeFileSync(events, '');
      const execution = spawnSync('make', ['seed'], {
        cwd: f.root,
        env: environment,
        encoding: 'utf8',
        timeout: 15000,
      });
      expect(
        sensitiveValues.some(
          (value) => execution.stdout.includes(value) || execution.stderr.includes(value),
        ),
      ).toBe(false);
      expect(execution.error).toBeUndefined();
      expect(execution.status).toBe(0);
      expect(readFileSync(join(f.root, '.env'), 'utf8') === defaultSource).toBe(true);
      expect(readFileSync(selectedFile, 'utf8') === f.source).toBe(true);
      return readFileSync(events, 'utf8')
        .trim()
        .split('\n')
        .map((line) => JSON.parse(line) as BoundaryEvent);
    },
  };
}

type ReadinessCleanupResult = {
  reason: string;
  observedDocker: boolean;
  readinessTimeouts: number[];
  sameOwner: boolean;
  childSurvivesOwnerCleanup: boolean;
  otherOwnedGroupSurvivesOwnerCleanup: boolean;
  heartbeatContinuesAfterCleanup: boolean;
  nestedCleanupStarted: boolean;
};

function readinessCleanupFixture(kind: 'cancelled' | 'deadline') {
  const f = makeSeedFixture();
  const marker = join(f.root, 'owned-docker.json');
  const heartbeat = join(f.root, 'heartbeat');
  const nestedCleanup = join(f.root, 'nested-cleanup');
  const nestedRelease = join(f.root, 'release-nested-cleanup');
  const cleanupComplete = join(f.root, 'fixture-cleanup-complete');
  const dockerRetired = join(f.root, 'docker-retired');
  const ownership = randomBytes(16).toString('hex');
  writeFileSync(
    join(f.root, 'bin/docker'),
    `#!${process.execPath}
import { appendFileSync, writeFileSync } from 'node:fs';
const args = process.argv.slice(2);
if (args.includes('config')) {
  process.on('SIGTERM', () => {});
  writeFileSync(${JSON.stringify(marker)}, JSON.stringify({ pid: process.pid, ownership: ${JSON.stringify(ownership)} }));
  writeFileSync(${JSON.stringify(heartbeat)}, '.');
  setInterval(() => appendFileSync(${JSON.stringify(heartbeat)}, '.'), 20);
} else if (args[0] === 'context') {
  process.stdout.write(JSON.stringify([{ Endpoints: { docker: { Host: 'unix:///fixture/docker.sock' } } }]));
} else if (args.includes('up') || JSON.stringify(args) === JSON.stringify(['compose', 'version'])) process.exit(0);
else process.exit(19);
`,
    { mode: 0o700 },
  );
  const stopHelper = join(f.root, 'frontend/apps/web/scripts/stop-test-child.mjs');
  writeFileSync(
    join(f.root, 'frontend/apps/web/scripts/stop-test-child-actual.mjs'),
    readFileSync(stopHelper),
  );
  // Hold only the nested CLI cleanup to expose competing owners deterministically.
  // The seed owner still executes the unchanged native stop helper and deadlines.
  writeFileSync(
    stopHelper,
    `import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import { stopTestChild as actualStop } from './stop-test-child-actual.mjs';
export async function stopTestChild(child) {
  if (process.argv[1]?.endsWith('/wait-compose.mjs')) {
    writeFileSync(${JSON.stringify(nestedCleanup)}, 'started');
    while (!existsSync(${JSON.stringify(nestedRelease)}))
      await new Promise(resolve => setTimeout(resolve, 10));
  }
  await actualStop(child);
  if (existsSync(${JSON.stringify(marker)})) {
    const value = JSON.parse(readFileSync(${JSON.stringify(marker)}, 'utf8'));
    if (value.ownership === ${JSON.stringify(ownership)} && value.pid === child.pid)
      writeFileSync(${JSON.stringify(dockerRetired)}, 'retired');
  }
}
`,
  );
  const driver = join(f.root, 'scripts/readiness-cleanup-driver.mjs');
  writeFileSync(
    driver,
    `import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import { composeSelection } from './compose.mjs';
import { DemoProcesses } from './demo-process.mjs';
import { seedDevelopment } from './seed.mjs';
import { stopTestChild } from '../frontend/apps/web/scripts/stop-test-child.mjs';
console.log = () => {};
const controller = new AbortController();
const tracked = [];
const retiredGroups = new Set();
const nativeKill = process.kill.bind(process);
// Observe the native helper's absence witness before any saved PID can recycle.
// The fixture also fails if a later observer or cleanup reuses that retired handle.
process.kill = (pid, signal) => {
  if (pid < 0 && retiredGroups.has(-pid)) throw new Error('Fixture group is already retired');
  try { return nativeKill(pid, signal); }
  catch (error) {
    if (error.code === 'ESRCH' && pid < 0 && tracked.some(child => child.pid === -pid)) retiredGroups.add(-pid);
    throw error;
  }
};
const start = DemoProcesses.prototype.start;
DemoProcesses.prototype.start = function(...args) {
  const running = start.apply(this, args);
  tracked.push(running.child);
  return running;
};
const run = DemoProcesses.prototype.run;
const readinessTimeouts = [];
const readinessDeadlines = [];
let seedOwner;
let sameOwner = true;
DemoProcesses.prototype.run = function(command, args, options = {}) {
  if (options.label === 'Docker Compose prerequisite') seedOwner = this;
  if (options.label === 'Compose readiness') {
    readinessTimeouts.push(options.timeout);
    sameOwner &&= this === seedOwner;
    if (${JSON.stringify(kind)} === 'deadline') {
      const deadline = new AbortController();
      readinessDeadlines.push(deadline);
      const timeout = AbortSignal.timeout;
      AbortSignal.timeout = milliseconds => milliseconds === options.timeout
        ? deadline.signal : timeout(milliseconds);
      try { return run.call(this, command, args, options); }
      finally { AbortSignal.timeout = timeout; }
    }
  }
  return run.call(this, command, args, options);
};
const alive = pid => { try { process.kill(pid, 0); return true; } catch (error) { if (error.code === 'ESRCH') return false; throw error; } };
let retired = false;
const ownedDockerAlive = pid => {
  retired ||= existsSync(${JSON.stringify(dockerRetired)});
  if (retired) return false;
  if (alive(pid)) return true;
  retired = true;
  writeFileSync(${JSON.stringify(dockerRetired)}, 'retired');
  return false;
};
const ownedPid = () => {
  if (!existsSync(${JSON.stringify(marker)})) return undefined;
  const value = JSON.parse(readFileSync(${JSON.stringify(marker)}, 'utf8'));
  if (value.ownership !== ${JSON.stringify(ownership)} || !Number.isSafeInteger(value.pid) || value.pid <= 0) throw new Error('Invalid fixture ownership');
  return value.pid;
};
let cancelled = false;
const poll = setInterval(() => {
  if (existsSync(${JSON.stringify(marker)}) && !cancelled) {
    cancelled = true;
    if (${JSON.stringify(kind)} === 'cancelled') controller.abort();
    else for (const deadline of readinessDeadlines) deadline.abort();
  }
}, 10);
const watchdog = setTimeout(() => controller.abort(), 9000);
let result;
try {
  let reason = '';
  try { await seedDevelopment({ selected: composeSelection('e2e'), signal: controller.signal }); }
  catch (error) { reason = error.reason ?? 'unexpected'; }
  clearInterval(poll);
  clearTimeout(watchdog);
  const pid = ownedPid();
  const childSurvived = pid !== undefined && ownedDockerAlive(pid);
  const trackedGroupAlive = () => tracked.some(child => child.pid !== undefined
    && !retiredGroups.has(child.pid) && alive(-child.pid));
  const groupSurvived = trackedGroupAlive();
  const before = existsSync(${JSON.stringify(heartbeat)}) ? readFileSync(${JSON.stringify(heartbeat)}, 'utf8') : '';
  await new Promise(resolve => setTimeout(resolve, 300));
  const after = existsSync(${JSON.stringify(heartbeat)}) ? readFileSync(${JSON.stringify(heartbeat)}, 'utf8') : '';
  result = { reason, observedDocker: pid !== undefined, readinessTimeouts, sameOwner,
    childSurvivesOwnerCleanup: childSurvived || (pid !== undefined && ownedDockerAlive(pid)),
    otherOwnedGroupSurvivesOwnerCleanup: groupSurvived || trackedGroupAlive(), heartbeatContinuesAfterCleanup: before !== after,
    nestedCleanupStarted: existsSync(${JSON.stringify(nestedCleanup)}) };
} finally {
  clearInterval(poll);
  clearTimeout(watchdog);
  writeFileSync(${JSON.stringify(nestedRelease)}, 'release');
  await Promise.all(tracked.map(child => stopTestChild(child)));
  const pid = ownedPid();
  if (pid !== undefined && ownedDockerAlive(pid)) {
    try { process.kill(-pid, 'SIGKILL'); }
    catch (error) {
      if (error.code !== 'ESRCH') throw error;
      retired = true;
      writeFileSync(${JSON.stringify(dockerRetired)}, 'retired');
    }
    const deadline = Date.now() + 3000;
    while (ownedDockerAlive(pid)) {
      if (Date.now() >= deadline) throw new Error('Fixture Docker did not stop');
      await new Promise(resolve => setTimeout(resolve, 20));
    }
  }
  writeFileSync(${JSON.stringify(cleanupComplete)}, 'complete');
  process.kill = nativeKill;
}
process.stdout.write(JSON.stringify(result));
`,
  );
  async function cleanupOrphanedDocker() {
    writeFileSync(nestedRelease, 'release');
    if (existsSync(cleanupComplete) || existsSync(dockerRetired) || !existsSync(marker)) return;
    const value = JSON.parse(readFileSync(marker, 'utf8')) as {
      pid: number;
      ownership: string;
    };
    if (value.ownership !== ownership || !Number.isSafeInteger(value.pid) || value.pid <= 0)
      throw new Error('Invalid fixture ownership');
    let retired = false;
    const groupAlive = () => {
      if (retired) return false;
      try {
        process.kill(-value.pid, 0);
        return true;
      } catch (error) {
        if ((error as NodeJS.ErrnoException).code !== 'ESRCH') throw error;
        retired = true;
        writeFileSync(dockerRetired, 'retired');
        return false;
      }
    };
    if (groupAlive()) {
      try {
        process.kill(-value.pid, 'SIGKILL');
      } catch (error) {
        if ((error as NodeJS.ErrnoException).code !== 'ESRCH') throw error;
        retired = true;
        writeFileSync(dockerRetired, 'retired');
      }
    }
    const deadline = Date.now() + 3000;
    while (groupAlive()) {
      if (Date.now() >= deadline) throw new Error('Fixture Docker did not stop');
      await new Promise((resolve) => setTimeout(resolve, 20));
    }
  }
  return {
    async run() {
      for (const path of [
        marker,
        heartbeat,
        nestedCleanup,
        nestedRelease,
        cleanupComplete,
        dockerRetired,
      ])
        rmSync(path, { force: true });
      let execution;
      try {
        execution = spawnSync(process.execPath, [driver], {
          cwd: f.root,
          env: f.environment,
          encoding: 'utf8',
          timeout: 12000,
        });
      } finally {
        // A failed driver cannot leave its one recorded fixture group behind.
        await cleanupOrphanedDocker();
      }
      expect(
        f.sensitiveValues.some(
          (value) => execution.stdout.includes(value) || execution.stderr.includes(value),
        ),
      ).toBe(false);
      expect(execution.error).toBeUndefined();
      expect(execution.status).toBe(0);
      expect(execution.stderr).toBe('');
      expect(existsSync(cleanupComplete)).toBe(true);
      return JSON.parse(execution.stdout) as ReadinessCleanupResult;
    },
    plantNestedReadiness() {
      const path = join(f.root, 'scripts/seed.mjs');
      const source = readFileSync(path, 'utf8');
      const direct = '    await waitCompose(selected, processes, signal, true);';
      expect(source.split(direct)).toHaveLength(2);
      writeFileSync(
        path,
        source.replace(
          direct,
          `    await processes.run('make', ['wait-postgres'], {
      cwd: repositoryRoot, env: selectedPrerequisites, label: 'Compose readiness', timeout: 15000,
    });`,
        ),
      );
    },
  };
}

describe('shared realm handoff lifecycle', () => {
  it.each(['cancelled', 'deadline'] as const)(
    'joins readiness Docker after %s and rejects a nested readiness owner',
    async (kind) => {
      const f = readinessCleanupFixture(kind);
      const stopped = (result: ReadinessCleanupResult) =>
        !result.childSurvivesOwnerCleanup &&
        !result.otherOwnedGroupSurvivesOwnerCleanup &&
        !result.heartbeatContinuesAfterCleanup;
      const clean = await f.run();
      expect(clean.reason).toBe(kind);
      expect(clean.observedDocker).toBe(true);
      expect(clean.readinessTimeouts).toEqual([15000]);
      expect(clean.sameOwner).toBe(true);
      expect(clean.nestedCleanupStarted).toBe(false);
      expect(clean.otherOwnedGroupSurvivesOwnerCleanup).toBe(false);
      expect(stopped(clean)).toBe(true);

      f.plantNestedReadiness();
      const nested = await f.run();
      expect(nested.reason).toBe(kind);
      expect(nested.observedDocker).toBe(true);
      expect(nested.readinessTimeouts).toEqual([15000]);
      expect(nested.nestedCleanupStarted).toBe(true);
      expect(nested.otherOwnedGroupSurvivesOwnerCleanup).toBe(false);
      expect(stopped(nested)).toBe(false);
      expect(nested.childSurvivesOwnerCleanup).toBe(true);
      expect(nested.heartbeatContinuesAfterCleanup).toBe(true);
    },
    30000,
  );

  it('confines actual make seed preparation and rejects the old prerequisite topology', () => {
    const f = makeSeedFixture();
    const confined = (events: BoundaryEvent[]) => events.every((event) => event.confined);
    const events = f.run();
    expect(events.map((event) => event.stage)).toEqual([
      'compose-version',
      'stack-up',
      'readiness-config',
      'readiness-status',
      'migration-tools',
      'migration-build',
      'migration-ef',
      'readiness-config',
      'readiness-status',
      'seeder-build',
      'appseed',
      'realm',
    ]);
    expect(confined(events)).toBe(true);
    expect(events.every((event) => event.selected)).toBe(true);
    expect(events.at(-1)?.parentPrivate).toBe(true);

    const makefile = join(f.root, 'Makefile');
    const source = readFileSync(makefile, 'utf8');
    const planted = source.replace(
      /^seed: seed-config(?=\s*##)/m,
      'seed: seed-config dev wait-postgres migrate',
    );
    expect(planted).not.toBe(source);
    writeFileSync(makefile, planted);
    const inherited = f.run();
    expect(confined(inherited)).toBe(false);
    expect(
      inherited
        .filter((event) =>
          ['migration-build', 'migration-ef', 'postgres-readiness'].includes(event.stage),
        )
        .some((event) => !event.confined),
    ).toBe(true);
  }, 45000);

  it('uses the caller selection and signal through readiness, fresh seed and awaited reconciliation', () => {
    const f = fixture();
    writePrivateFileSync(join(f.root, 'selected.env'), f.source);
    const result = execute(f, 'seed');
    expect(result.failure).toBe('');
    expect(result.events).toEqual([
      'compose-version',
      'stack-up',
      'postgres-ready',
      'migrate',
      'ready',
      'appseed',
      'realm-start',
      'realm-complete',
      'dispose',
    ]);
    expect(result.commands.slice(0, 4).map(({ command, args }) => ({ command, args }))).toEqual([
      { command: 'docker', args: ['compose', 'version'] },
      { command: 'docker', args: ['context', 'inspect'] },
      { command: 'make', args: ['dev'] },
      { command: 'make', args: ['migrate'] },
    ]);
    expect(result.commands.slice(0, 4).map(({ timeout }) => timeout)).toEqual([
      5000, 5000, 600000, 300000,
    ]);
    expect(result.selectionIdentity).toBe(true);
    expect(result.signalIdentity).toBe(true);
    expect(result.selectedDescription).toEqual({
      project: 'owned-seed',
      file: join(f.root, 'selected.env'),
      mode: 'dev',
    });
    expect(result.commands.every((command) => command.confined)).toBe(true);
    expect(readFileSync(join(f.root, 'selected.env'), 'utf8')).toBe(f.source);
  });

  it.each(['seed-failure', 'reconcile-failure'])(
    'halts the seed owner on %s and disposes its children',
    (scenario) => {
      const f = fixture();
      writePrivateFileSync(join(f.root, 'selected.env'), f.source);
      const result = execute(f, 'seed', scenario);
      expect(result.failure).toBe(scenario === 'seed-failure' ? 'seed refusal' : 'realm refusal');
      expect(result.events).toEqual(
        scenario === 'seed-failure'
          ? [
              'compose-version',
              'stack-up',
              'postgres-ready',
              'migrate',
              'ready',
              'appseed',
              'dispose',
            ]
          : [
              'compose-version',
              'stack-up',
              'postgres-ready',
              'migrate',
              'ready',
              'appseed',
              'realm-start',
              'dispose',
            ],
      );
    },
  );

  it('joins a cancelled reconciliation before disposing the seed process owner', () => {
    const f = fixture();
    writePrivateFileSync(join(f.root, 'selected.env'), f.source);
    const result = execute(f, 'seed', 'cancel');
    expect(result.failure).toBe('realm: preflight/cancelled');
    expect(result.events).toEqual([
      'compose-version',
      'stack-up',
      'postgres-ready',
      'migrate',
      'ready',
      'appseed',
      'realm-start',
      'realm-cancel',
      'realm-joined',
      'dispose',
    ]);
  });

  it('awaits reconciliation after app seed and before CI stops Keycloak or launches API/web', () => {
    const f = fixture();
    Object.assign(f.env, {
      HTTP_PROXY: 'http://upper.fixture.invalid',
      http_proxy: 'http://lower.fixture.invalid',
      NUGET_PACKAGES: join(f.root, 'nuget-cache'),
      DOCKER_CONFIG: join(f.root, 'docker-config'),
      DOCKER_CONTEXT: 'owned-fixture-context',
    });
    const result = execute(f, 'demo');
    expect(result.failure).toBe('renderer boundary');
    expect(result.events.filter((event) => event !== 'dispose')).toEqual([
      'ready',
      'ready',
      'appseed',
      'realm-start',
      'realm-complete',
      'stop-keycloak',
      'renderer',
    ]);
    expect(result.events.filter((event) => event === 'dispose')).toHaveLength(2);
    expect(result.selectionIdentity).toBe(true);
    expect(result.signalIdentity).toBe(true);
    expect(result.selectedDescription).toMatchObject({ file: join(f.root, '.env'), mode: 'e2e' });
    expect(result.selectedDescription!.project).toMatch(/^learnstack-demo-[a-f0-9]+$/);
    expect(result.commands.every((command) => command.confined)).toBe(true);
    expect(result.commands.every((command) => command.prerequisitesPreserved)).toBe(true);
    expect(result.commands.find(({ label }) => label === 'Required docker')).toMatchObject({
      command: 'docker',
      args: ['compose', 'version'],
      prerequisitesPreserved: true,
    });
    expect(result.migrationSelection).toBe(true);
    expect(existsSync(join(f.root, '.env'))).toBe(false);
  });

  it.each(['seed-failure', 'reconcile-failure'])(
    'halts the CI demo before stop/start on %s',
    (scenario) => {
      const f = fixture();
      const result = execute(f, 'demo', scenario);
      expect(result.failure).toBe(scenario === 'seed-failure' ? 'seed refusal' : 'realm refusal');
      expect(result.events).not.toContain('stop-keycloak');
      expect(result.events).not.toContain('renderer');
      expect(result.events).not.toContain('daemon');
      expect(result.events).not.toContain('realm-complete');
      if (scenario === 'seed-failure') expect(result.events).not.toContain('realm-start');
      expect(existsSync(join(f.root, '.env'))).toBe(false);
    },
  );

  it.each(['local', 'remote'])(
    'checks the selected Docker environment when the private file removes an ambient context and the default is %s',
    (endpoint) => {
      const f = fixture();
      Object.assign(f.env, { DOCKER_CONTEXT: 'owned-fixture-context' });
      writeFileSync(
        join(f.root, '.env.example'),
        f.source + 'DOCKER_CONTEXT=owned-fixture-context\n',
      );
      const result = execute(f, 'demo', `selected-${endpoint}-context`);
      const inspections = result.commands.filter(
        ({ command, args }) => command === 'docker' && args[0] === 'context',
      );
      expect(inspections.map(({ dockerContext }) => dockerContext)).toEqual([
        'owned-fixture-context',
        null,
      ]);
      const selectedCommands = result.commands.filter(({ args }) =>
        args.includes('--project-name'),
      );
      if (endpoint === 'remote') {
        expect(result.failure).toBe('realm: preflight/configuration');
        expect(selectedCommands).toEqual([]);
        expect(result.events).toEqual(['dispose']);
      } else {
        expect(result.failure).toBe('renderer boundary');
        expect(selectedCommands.some(({ args }) => args.includes('up'))).toBe(true);
        expect(selectedCommands.every(({ dockerContext }) => dockerContext === null)).toBe(true);
        expect(result.commands.indexOf(inspections[1]!)).toBeLessThan(
          result.commands.indexOf(selectedCommands[0]!),
        );
      }
      expect(existsSync(join(f.root, '.env'))).toBe(false);
    },
  );
});

describe('private configuration refuses launch before commands', () => {
  it.each(['seed', 'demo'] as const)(
    'refuses a shared private file before %s commands without repairing it',
    (subject) => {
      const f = fixture();
      const file = join(f.root, subject === 'seed' ? 'selected.env' : '.env');
      writePrivateFileSync(file, f.source);
      chmodSync(file, 0o644);
      const result = execute(f, subject, 'preflight');
      expect(result.failure).toBe('realm: preflight/configuration');
      expect(result.commands).toEqual([]);
      expect(readFileSync(file, 'utf8')).toBe(f.source);
    },
  );

  it.each(['seed', 'demo'] as const)(
    'refuses a remote Docker context before %s starts or migrates a stack',
    (subject) => {
      const f = fixture();
      if (subject === 'seed') writePrivateFileSync(join(f.root, 'selected.env'), f.source);
      const result = execute(f, subject, 'remote-context');
      expect(result.failure).toBe('realm: preflight/configuration');
      expect(
        result.commands
          .filter(({ command }) => ['make', 'dotnet', 'pnpm'].includes(command))
          .every(({ args }) => args.includes('--version')),
      ).toBe(true);
      expect(
        result.commands.some(({ label }) => /startup|migrations|seed convergence/i.test(label)),
      ).toBe(false);
      expect(result.events).not.toContain('stack-up');
      expect(result.events).not.toContain('migrate');
      expect(result.events).not.toContain('appseed');
    },
  );

  const keys = ['KEYCLOAK_ADMIN', 'KEYCLOAK_ADMIN_PASSWORD', 'KEYCLOAK_WEB_CLIENT_SECRET'];
  it('checks the selected Docker environment without reintroducing an inherited HOME', () => {
    const f = fixture();
    writePrivateFileSync(join(f.root, 'selected.env'), f.source + `HOME=${f.env.HOME}\n`);
    const result = execute(f, 'seed', 'selected-remote-home');
    expect(result.failure).toBe('realm: preflight/configuration');
    const inspection = result.commands.find(
      ({ command, args }) => command === 'docker' && args[0] === 'context',
    );
    expect(inspection?.dockerHome).toBeNull();
    expect(result.events).not.toContain('stack-up');
    expect(result.events).not.toContain('migrate');
    expect(result.events).not.toContain('appseed');
  });

  it.each(
    ['Migration', 'Default'].flatMap((suffix) =>
      ['missing', 'ambient mismatch'].map((kind) => ({ suffix, kind })),
    ),
  )('refuses owner $suffix $kind before preparation or readiness', ({ suffix, kind }) => {
    const f = fixture();
    const key = `ConnectionStrings__${suffix}`;
    let source = f.source;
    if (kind === 'missing') source = source.replace(`${key}=${f.values[key]}\n`, '');
    else Object.assign(f.env, { [key]: randomBytes(20).toString('hex') });
    writePrivateFileSync(join(f.root, 'selected.env'), source);
    const result = execute(f, 'seed');
    expect(result.failure).toBe('realm: preflight/configuration');
    expect(result.commands).toEqual([]);
    expect(result.events).toEqual([]);
  });

  it('keeps private owner preflight ahead of database disagreement and every command', () => {
    const f = fixture();
    writePrivateFileSync(
      join(f.root, 'selected.env'),
      f.source.replace(`KEYCLOAK_WEB_CLIENT_SECRET=${f.values.KEYCLOAK_WEB_CLIENT_SECRET}\n`, ''),
    );
    Object.assign(f.env, { ConnectionStrings__Migration: randomBytes(20).toString('hex') });
    const result = execute(f, 'seed');
    expect(result.failure).toBe('realm: preflight/missing_web_secret');
    expect(result.commands).toEqual([]);
    expect(result.events).toEqual([]);
  });

  it('keeps the fixed application-role refusal ahead of private configuration preflight', () => {
    const f = fixture();
    const source = f.source
      .replace('Username=learnstack_app', 'Username=learnstack_platform')
      .replace(`KEYCLOAK_WEB_CLIENT_SECRET=${f.values.KEYCLOAK_WEB_CLIENT_SECRET}\n`, '');
    writePrivateFileSync(join(f.root, '.env'), source);
    for (const args of [['--validate-config'], []]) {
      const execution = spawnSync('bash', [join(f.root, 'scripts/seed.sh'), ...args], {
        cwd: f.root,
        env: f.env,
        encoding: 'utf8',
        timeout: 5000,
      });
      expect(execution.status).not.toBe(0);
      expect(execution.stderr).toBe(
        'seed: ConnectionStrings__Default must name learnstack_app; credentials were not used.\n',
      );
      expect(existsSync(join(f.root, 'actions'))).toBe(false);
      expect(readFileSync(join(f.root, '.env'), 'utf8')).toBe(source);
    }
  });

  it('validates the generated CI private environment before selected-stack commands', () => {
    const f = fixture();
    writeFileSync(
      join(f.root, '.env.example'),
      f.source.replace(`KEYCLOAK_WEB_CLIENT_SECRET=${f.values.KEYCLOAK_WEB_CLIENT_SECRET}\n`, ''),
    );
    const result = execute(f, 'demo', 'ci-preflight');
    expect(result.failure).toBe('realm: preflight/missing_web_secret');
    expect(result.commands.some((command) => command.args.includes('--project-name'))).toBe(false);
    expect(result.events).toEqual(['dispose']);
    expect(existsSync(join(f.root, '.env'))).toBe(false);
  });

  it.each(
    keys.flatMap((key) =>
      ['missing', 'duplicate', 'alias', 'ambient mismatch'].map((kind) => ({ key, kind })),
    ),
  )(
    'refuses $key $kind in shell validation, normal seed and local demo without rewriting or leaking it',
    ({ key, kind }) => {
      const f = fixture();
      let source = f.source;
      const assignment = `${key}=${f.values[key]}\n`;
      if (kind === 'missing') source = source.replace(assignment, '');
      if (kind === 'duplicate') source += assignment;
      if (kind === 'alias') source = source.replace(assignment, `export ${assignment}`);
      if (kind === 'ambient mismatch')
        Object.assign(f.env, { [key]: randomBytes(20).toString('hex') });
      writePrivateFileSync(join(f.root, '.env'), source);
      for (const args of [['--validate-config'], []]) {
        const execution = spawnSync('bash', [join(f.root, 'scripts/seed.sh'), ...args], {
          cwd: f.root,
          env: f.env,
          encoding: 'utf8',
          timeout: 5000,
        });
        expect(execution.error).toBeUndefined();
        expect(execution.status).not.toBe(0);
        expect(execution.stderr).toContain('realm: preflight/');
        expect(existsSync(join(f.root, 'actions'))).toBe(false);
        expect(readFileSync(join(f.root, '.env'), 'utf8')).toBe(source);
        for (const canary of f.canaries) {
          expect(execution.stdout).not.toContain(canary);
          expect(execution.stderr).not.toContain(canary);
        }
      }
      const result = execute(f, 'demo', 'preflight');
      expect(result.failure).toMatch(/^realm: preflight\/(?:configuration|missing_web_secret)$/);
      expect(result.commands).toEqual([]);
      expect(readFileSync(join(f.root, '.env'), 'utf8')).toBe(source);
    },
  );

  it('keeps the seed CLI closed and validates configuration without starting work', () => {
    const f = fixture();
    writePrivateFileSync(join(f.root, '.env'), f.source);
    for (const args of [
      ['--validate-config'],
      ['--identity-manifest'],
      ['--validate-config', 'extra'],
    ]) {
      const execution = spawnSync(process.execPath, [join(f.root, 'scripts/seed.mjs'), ...args], {
        cwd: f.root,
        env: f.env,
        encoding: 'utf8',
        timeout: 5000,
      });
      expect(execution.error).toBeUndefined();
      if (args.length === 1 && args[0] === '--validate-config') expect(execution.status).toBe(0);
      else expect(execution.status).not.toBe(0);
      expect(existsSync(join(f.root, 'actions'))).toBe(false);
      expect(readFileSync(join(f.root, '.env'), 'utf8')).toBe(f.source);
    }
  });

  it('emits only fixed realm diagnostics and the bounded missing-key instruction', () => {
    const f = fixture();
    const execution = spawnSync(
      process.execPath,
      [
        '--input-type=module',
        '-e',
        `
        import { RealmError } from './scripts/development-realm-errors.mjs';
        import { demoFailureMessage } from './scripts/demo.mjs';
        process.stdout.write(JSON.stringify([
          demoFailureMessage(new RealmError('apply', 'status')),
          demoFailureMessage(new RealmError('preflight', 'missing_web_secret')),
        ]));
      `,
      ],
      { cwd: f.root, env: f.env, encoding: 'utf8', timeout: 5000 },
    );
    expect(execution.status, execution.stderr).toBe(0);
    const [ordinary, missing] = JSON.parse(execution.stdout) as [string, string];
    expect(ordinary).toBe('realm: apply/status');
    expect(missing).toContain('realm: preflight/missing_web_secret');
    expect(missing).toContain('add KEYCLOAK_WEB_CLIENT_SECRET explicitly');
    expect(missing).toContain('infra/keycloak/README.md');
    expect(
      demoFailureMessage(new Error('realm: preflight/missing_web_secret injected-private-value')),
    ).not.toContain('injected-private-value');
  });
});
