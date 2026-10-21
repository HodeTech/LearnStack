// @vitest-environment node
import { execFileSync, spawnSync } from 'node:child_process';
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

import { demoFailureMessage } from '../../../../../scripts/demo.mjs';

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
export async function waitCompose(selected, processes, signal) {
  const f = globalThis.lifecycle;
  f.events.push('ready');
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
  commands: Array<{ command: string; args: string[]; label: string; confined: boolean }>;
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
  selectionIdentity: true, signalIdentity: true, migrationSelection: false,
  controller: new AbortController(), seedFailure: new Error('seed refusal'),
  realmFailure: new Error('realm refusal'), boundary: new Error('renderer boundary') };
DemoProcesses.prototype.dispose = async function() { f.events.push('dispose'); };
DemoProcesses.prototype.daemon = function() { f.events.push('daemon'); throw new Error('Unexpected daemon'); };
DemoProcesses.prototype.run = async function(command, args, options = {}) {
  const env = options.env ?? {};
  const isCompose = command === 'docker' && args[0] === 'compose' && args.includes('--project-name');
  const migration = command === 'make' && args[0] === 'migrate';
  const allowed = new Set(['PATH', 'HOME', 'DOTNET_ROOT', 'TMPDIR', 'APPDATA',
    ...(migration ? ['ConnectionStrings__Migration', 'LEARNSTACK_COMPOSE_PROJECT', 'LEARNSTACK_COMPOSE_ENV_FILE', 'LEARNSTACK_COMPOSE_MODE'] : []),
    ...(options.label === 'Contextual seed convergence' || options.label === 'Application seed convergence' ? ['ConnectionStrings__Default'] : [])]);
  // The app seed is recognised by its real, argument-free Release DLL call too.
  if (command === 'dotnet' && args.length === 1 && args[0].endsWith('/LearnStack.Tools.Seeder.dll')) allowed.add('ConnectionStrings__Default');
  f.commands.push({ command, args, label: options.label,
    confined: isCompose || Object.keys(env).every(key => allowed.has(key)) });
  if (migration) f.migrationSelection = env.LEARNSTACK_COMPOSE_PROJECT?.startsWith('learnstack-demo-')
    && env.LEARNSTACK_COMPOSE_ENV_FILE === join(${JSON.stringify(f.root)}, '.env')
    && env.LEARNSTACK_COMPOSE_MODE === 'e2e' && env.ConnectionStrings__Migration?.includes('Username=learnstack_migration');
  if (command === 'dotnet' && args.includes('--demo-manifest')) return JSON.stringify([{ host: 'localhost' }]);
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

describe('shared realm handoff lifecycle', () => {
  it('uses the caller selection and signal through readiness, fresh seed and awaited reconciliation', () => {
    const f = fixture();
    writeFileSync(join(f.root, 'selected.env'), f.source);
    const result = execute(f, 'seed');
    expect(result.failure).toBe('');
    expect(result.events).toEqual(['ready', 'appseed', 'realm-start', 'realm-complete', 'dispose']);
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
      writeFileSync(join(f.root, 'selected.env'), f.source);
      const result = execute(f, 'seed', scenario);
      expect(result.failure).toBe(scenario === 'seed-failure' ? 'seed refusal' : 'realm refusal');
      expect(result.events).toEqual(
        scenario === 'seed-failure'
          ? ['ready', 'appseed', 'dispose']
          : ['ready', 'appseed', 'realm-start', 'dispose'],
      );
    },
  );

  it('joins a cancelled reconciliation before disposing the seed process owner', () => {
    const f = fixture();
    writeFileSync(join(f.root, 'selected.env'), f.source);
    const result = execute(f, 'seed', 'cancel');
    expect(result.failure).toBe('realm: preflight/cancelled');
    expect(result.events).toEqual([
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
});

describe('private configuration refuses launch before commands', () => {
  const keys = ['KEYCLOAK_ADMIN', 'KEYCLOAK_ADMIN_PASSWORD', 'KEYCLOAK_WEB_CLIENT_SECRET'];
  it('keeps the fixed application-role refusal ahead of private configuration preflight', () => {
    const f = fixture();
    const source = f.source
      .replace('Username=learnstack_app', 'Username=learnstack_platform')
      .replace(`KEYCLOAK_WEB_CLIENT_SECRET=${f.values.KEYCLOAK_WEB_CLIENT_SECRET}\n`, '');
    writeFileSync(join(f.root, '.env'), source);
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
      writeFileSync(join(f.root, '.env'), source);
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
    writeFileSync(join(f.root, '.env'), f.source);
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
