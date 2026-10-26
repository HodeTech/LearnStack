// @vitest-environment node
import { spawnSync } from 'node:child_process';
import {
  chmodSync,
  existsSync,
  mkdirSync,
  mkdtempSync,
  readdirSync,
  readFileSync,
  realpathSync,
  rmSync,
  symlinkSync,
  writeFileSync,
} from 'node:fs';
import { tmpdir } from 'node:os';
import { basename, dirname, join, relative } from 'node:path';
import { fileURLToPath } from 'node:url';

import { afterEach, expect, it } from 'vitest';

const repository = fileURLToPath(new URL('../../../../../', import.meta.url));
const recipe = readFileSync(join(repository, 'Makefile'), 'utf8');
const roots: string[] = [];
afterEach(() => {
  for (const root of roots.splice(0)) rmSync(root, { recursive: true, force: true });
});

function migrationProjects(directory = join(repository, 'backend/src')): string[] {
  return readdirSync(directory, { withFileTypes: true }).flatMap((entry) => {
    if (!entry.isDirectory() || ['bin', 'obj'].includes(entry.name)) return [];
    if (entry.name === 'Migrations' && basename(directory) === 'Persistence')
      return [relative(repository, dirname(directory))];
    return migrationProjects(join(directory, entry.name));
  });
}
const projects = migrationProjects().sort();
const tenancy = 'backend/src/Modules/Tenancy/LearnStack.Modules.Tenancy.Infrastructure';
const platform = 'backend/src/LearnStack.Infrastructure';
const audit = 'backend/src/Modules/Audit/LearnStack.Modules.Audit.Infrastructure';
const identity = 'backend/src/Modules/Identity/LearnStack.Modules.Identity.Infrastructure';
const extra = 'backend/src/Modules/Zeta/LearnStack.Modules.Zeta.Infrastructure';
const ordered = (chains: string[]) =>
  [...new Set([tenancy, platform, audit, identity, ...chains.slice().sort()])].filter((path) =>
    chains.includes(path),
  );

function fixture(additional = false) {
  const root = realpathSync(mkdtempSync(join(tmpdir(), 'learnstack-migration-build-')));
  roots.push(root);
  const chains = [...projects, ...(additional ? [extra] : [])];
  for (const directory of [
    'bin',
    'scripts',
    ...chains.map((path) => path + '/Persistence/Migrations'),
  ])
    mkdirSync(join(root, directory), { recursive: true });
  writeFileSync(join(root, 'Makefile'), recipe);
  writeFileSync(
    join(root, 'scripts/connection-string.awk'),
    readFileSync(join(repository, 'scripts/connection-string.awk')),
  );
  for (const name of ['migrate', 'compose', 'demo-database', 'development-realm-errors'])
    writeFileSync(
      join(root, `scripts/${name}.mjs`),
      readFileSync(join(repository, `scripts/${name}.mjs`)),
    );
  // A previous artifact must never substitute for this invocation's fresh build.
  const stale = join(root, 'backend/src/LearnStack.Api/bin/Release/net10.0');
  mkdirSync(stale, { recursive: true });
  writeFileSync(join(stale, 'LearnStack.Api.dll'), 'stale');
  const log = join(root, 'calls.jsonl');
  writeFileSync(log, '');
  writeFileSync(
    join(root, 'bin/dotnet'),
    `#!${process.execPath}\nrequire(${JSON.stringify(join(root, 'dotnet-stub.cjs'))});\n`,
    { mode: 0o700 },
  );
  const stub = join(root, 'dotnet-stub.cjs');
  writeFileSync(
    stub,
    `const { appendFileSync, existsSync, readFileSync, writeFileSync } = require('node:fs');
const { join } = require('node:path');
const args = process.argv.slice(2);
const stage = args[0] === 'tool' ? 'tool' : args[0] === 'build' ? 'build' : args[0] === 'ef' ? 'ef' : 'installer';
const behavior = JSON.parse(readFileSync(${JSON.stringify(join(root, 'behavior.json'))}, 'utf8'));
appendFileSync(${JSON.stringify(log)}, JSON.stringify({
  args, cwd: process.cwd(), connection: process.env.ConnectionStrings__Migration, environment: process.env,
}) + '\\n');
if (process.cwd() !== (stage === 'installer' ? ${JSON.stringify(root)} : ${JSON.stringify(join(root, 'backend'))})) process.exit(91);
if (behavior.MIGRATION_TEST_FAIL === stage) process.exit(17);
const built = ${JSON.stringify(join(root, 'fresh-release'))};
if (stage === 'build') writeFileSync(built, 'fresh');
if (stage === 'ef') {
  if (!existsSync(built)) process.exit(92);
  if (args[args.indexOf('--project') + 1] === behavior.MIGRATION_TEST_FAIL_PROJECT)
    process.exit(18);
}
`,
  );
  return {
    root,
    chains,
    run(environment: Record<string, string> = {}) {
      writeFileSync(join(root, 'behavior.json'), JSON.stringify(environment));
      const result = spawnSync('make', ['migrate', '.SHELLFLAGS=-c'], {
        cwd: root,
        env: {
          PATH: `${join(root, 'bin')}:${process.env.PATH}`,
          NODE_ENV: 'test',
          ConnectionStrings__Migration: 'Host=localhost;Username=learnstack_migration',
          ...environment,
        },
        encoding: 'utf8',
        timeout: 5000,
      });
      expect(result.error).toBeUndefined();
      const calls = readFileSync(log, 'utf8')
        .split('\n')
        .filter(Boolean)
        .map(
          (line) =>
            JSON.parse(line) as {
              args: string[];
              cwd: string;
              connection?: string;
              environment: Record<string, string>;
            },
        );
      return { result, calls };
    },
  };
}

it.each([false, true])(
  'builds fresh Release once before every discovered migration, including an added chain (%s)',
  (additional) => {
    expect(projects.length).toBeGreaterThan(0);
    const files = fixture(additional);
    const { result, calls } = files.run();
    expect(result.status, result.stderr).toBe(0);
    expect(calls.map((call) => call.args)).toEqual([
      ['tool', 'restore'],
      ['build', 'src/LearnStack.Api', '--configuration', 'Release', '--nologo'],
      ...ordered(files.chains).map((project) => [
        'ef',
        'database',
        'update',
        '--project',
        '../' + project,
        '--startup-project',
        'src/LearnStack.Api',
        '--configuration',
        'Release',
        '--no-build',
      ]),
    ]);
    for (const call of calls) {
      expect(call.cwd).toBe(join(files.root, 'backend'));
      expect(call.connection).toBe(
        call.args[0] === 'ef' ? 'Host=localhost;Username=learnstack_migration' : undefined,
      );
      expect(call.args).not.toContain('Host=localhost;Username=learnstack_migration');
      expect(call.args).not.toContain('--connection');
    }
  },
);

it('confines a selected-file migration credential to EF after credential-free restore and build', () => {
  const files = fixture();
  const connection = 'Host=localhost;Username=learnstack_migration';
  writeFileSync(join(files.root, 'selected.env'), `ConnectionStrings__Migration=${connection}\n`, {
    mode: 0o600,
  });
  const { result, calls } = files.run({
    ConnectionStrings__Migration: '',
    LEARNSTACK_COMPOSE_ENV_FILE: join(files.root, 'selected.env'),
  });
  expect(result.status, result.stderr).toBe(0);
  expect(calls.length).toBeGreaterThan(2);
  expect(calls.slice(0, 2).every((call) => call.connection === undefined)).toBe(true);
  expect(calls.slice(2).every((call) => call.connection === connection)).toBe(true);
  expect(result.stdout).not.toContain(connection);
  expect(result.stderr).not.toContain(connection);
});

const authority = {
  Identity__AuthorityId: '019a1929-5450-7bbb-8df1-516e8e002501',
  Authentication__Tenant__Issuer: 'https://identity.example/realms/learnstack',
};
const migrationConnection =
  'Host=deployment.example;Username=learnstack_migration;Password=private-test-credential'; // leakwatch:ignore -- synthetic input; dotnet is a local stub
const environmentText = (values: Record<string, string>) =>
  Object.entries(values)
    .map(([key, value]) => `${key}='${value}'`)
    .join('\n') + '\n';

it.each(['ambient', 'file'] as const)(
  'installs one complete %s selection with confined child environments',
  (source) => {
    const files = fixture();
    const complete = { ConnectionStrings__Migration: migrationConnection, ...authority };
    writeFileSync(
      join(files.root, '.env'),
      environmentText(
        source === 'file'
          ? complete
          : {
              ConnectionStrings__Migration: 'Host=decoy;Username=learnstack_migration',
              ...authority,
              Authentication__Tenant__Issuer: 'http://localhost:8080/realms/learnstack',
            },
      ),
      { mode: 0o600 },
    );
    const { result, calls } = files.run({
      ...(source === 'ambient' ? complete : { ConnectionStrings__Migration: '' }),
      KEYCLOAK_ADMIN_PASSWORD: 'private-keycloak-canary',
      ConnectionStrings__Default: 'private-app-canary',
      UNRELATED_PRIVATE_VALUE: 'private-unrelated-canary',
    });
    expect(result.status, result.stderr).toBe(0);
    expect(calls.filter((call) => call.args[0] === 'build').map((call) => call.args[1])).toEqual([
      'src/LearnStack.Api',
      'src/LearnStack.Tools.IdentityAuthorityInstaller',
    ]);
    const installed = calls.at(-1)!;
    expect(installed.args).toEqual([
      'backend/src/LearnStack.Tools.IdentityAuthorityInstaller/bin/Release/net10.0/LearnStack.Tools.IdentityAuthorityInstaller.dll',
    ]);
    for (const call of calls) {
      expect(call.environment.KEYCLOAK_ADMIN_PASSWORD).toBeUndefined();
      expect(call.environment.ConnectionStrings__Default).toBeUndefined();
      expect(call.environment.UNRELATED_PRIVATE_VALUE).toBeUndefined();
      expect(call.connection).toBe(
        call.args[0] === 'ef' || call === installed ? migrationConnection : undefined,
      );
      for (const [key, value] of Object.entries(authority))
        expect(call.environment[key]).toBe(call === installed ? value : undefined);
      expect(call.args.join(' ')).not.toContain(migrationConnection);
    }
    expect(result.stdout + result.stderr).not.toContain('private-');
  },
);

it.each<{ name: string; ambient: Record<string, string>; file: Record<string, string> }>([
  { name: 'ambient connection with file pair', ambient: {}, file: authority },
  {
    name: 'ambient authority with file issuer',
    ambient: { Identity__AuthorityId: authority.Identity__AuthorityId },
    file: { Authentication__Tenant__Issuer: authority.Authentication__Tenant__Issuer },
  },
  {
    name: 'ambient issuer with file authority',
    ambient: { Authentication__Tenant__Issuer: authority.Authentication__Tenant__Issuer },
    file: { Identity__AuthorityId: authority.Identity__AuthorityId },
  },
  {
    name: 'ambient pair with file connection',
    ambient: { ConnectionStrings__Migration: '', ...authority },
    file: { ConnectionStrings__Migration: migrationConnection },
  },
  {
    name: 'file authority without issuer',
    ambient: { ConnectionStrings__Migration: '' },
    file: {
      ConnectionStrings__Migration: migrationConnection,
      Identity__AuthorityId: authority.Identity__AuthorityId,
    },
  },
  {
    name: 'file issuer without authority',
    ambient: { ConnectionStrings__Migration: '' },
    file: {
      ConnectionStrings__Migration: migrationConnection,
      Authentication__Tenant__Issuer: authority.Authentication__Tenant__Issuer,
    },
  },
])('refuses $name before every dotnet child', ({ ambient, file }) => {
  const files = fixture();
  writeFileSync(join(files.root, '.env'), environmentText(file), { mode: 0o600 });
  const { result, calls } = files.run(ambient);
  expect(result.status).not.toBe(0);
  expect(calls).toEqual([]);
  expect(result.stdout + result.stderr).toContain('one source');
  expect(result.stdout + result.stderr).not.toContain(migrationConnection);
});

it.each(['invalid mode', 'symlink'])(
  'preserves private-file guidance for a selected file with %s before every dotnet child',
  (kind) => {
    const files = fixture();
    const source = join(files.root, 'private-source.env');
    const selected = join(files.root, 'selected.env');
    writeFileSync(
      source,
      environmentText({ ConnectionStrings__Migration: migrationConnection, ...authority }),
      { mode: 0o600 },
    );
    if (kind === 'symlink') symlinkSync(source, selected);
    else chmodSync(source, 0o644);
    const { result, calls } = files.run({
      ConnectionStrings__Migration: '',
      LEARNSTACK_COMPOSE_ENV_FILE: kind === 'symlink' ? selected : source,
    });
    expect(result.status).not.toBe(0);
    expect(calls).toEqual([]);
    const output = result.stdout + result.stderr;
    expect(output).toContain('realm: preflight/private_file');
    expect(output).toContain('regular, single-link file owned by the current user');
    expect(output).toContain('owner-only permissions; use chmod 600 on that file');
    for (const privateValue of [source, selected, migrationConnection, ...Object.values(authority)])
      expect(output).not.toContain(privateValue);
  },
);

it.each([
  `Identity__AuthorityId=${authority.Identity__AuthorityId}\n`,
  `export Identity__AuthorityId=${authority.Identity__AuthorityId}\n`,
  ` Authentication__Tenant__Issuer=${authority.Authentication__Tenant__Issuer}\n`,
])(
  'refuses duplicate or aliased authority assignments without launching dotnet (%s)',
  (extraLine) => {
    const files = fixture();
    writeFileSync(
      join(files.root, '.env'),
      environmentText({ ConnectionStrings__Migration: migrationConnection, ...authority }) +
        extraLine,
      { mode: 0o600 },
    );
    const { result, calls } = files.run({ ConnectionStrings__Migration: '' });
    expect(result.status).not.toBe(0);
    expect(calls).toEqual([]);
  },
);

it('does not install authority after any migration chain fails', () => {
  const files = fixture();
  const { result, calls } = files.run({
    ...authority,
    MIGRATION_TEST_FAIL_PROJECT: '../' + tenancy,
  });
  expect(result.status).not.toBe(0);
  expect(calls.some((call) => call.args[0]?.endsWith('.dll'))).toBe(false);
});

it.each(['tool', 'build'])(
  'refuses %s failure before any EF operation despite an existing Release DLL and without shell errexit',
  (stage) => {
    const files = fixture();
    const { result, calls } = files.run({ MIGRATION_TEST_FAIL: stage });
    expect(result.status).not.toBe(0);
    expect(calls.map((call) => call.args[0])).toEqual(
      stage === 'tool' ? ['tool'] : ['tool', 'build'],
    );
    expect(existsSync(join(files.root, 'fresh-release'))).toBe(false);
  },
);

it.each(['learnstack_app', 'learnstack_migration_readonly'])(
  'refuses role %s before tool restore, build or EF',
  (role) => {
    const { result, calls } = fixture().run({
      ConnectionStrings__Migration: `Host=localhost;Username=${role}`,
    });
    expect(result.status).not.toBe(0);
    expect(calls).toEqual([]);
  },
);

it.each(['first', 'middle', 'last'])(
  'retains a %s chain failure when subsequent chains succeed',
  (position) => {
    const files = fixture(true);
    const expected = ordered(files.chains);
    const index = position === 'first' ? 0 : position === 'last' ? expected.length - 1 : 2;
    const { result, calls } = files.run({
      MIGRATION_TEST_FAIL_PROJECT: '../' + expected[index],
    });
    expect(result.status).not.toBe(0);
    expect(calls.filter((call) => call.args[0] === 'ef').map((call) => call.args[4])).toEqual(
      expected.map((project) => '../' + project),
    );
  },
);
