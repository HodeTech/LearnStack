// @vitest-environment node
import { spawnSync } from 'node:child_process';
import {
  existsSync,
  mkdirSync,
  mkdtempSync,
  readdirSync,
  readFileSync,
  realpathSync,
  rmSync,
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
const extra = 'backend/src/Modules/Zeta/LearnStack.Modules.Zeta.Infrastructure';
const ordered = (chains: string[]) =>
  [...new Set([tenancy, platform, ...chains.slice().sort()])].filter((path) =>
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
  // A previous artifact must never substitute for this invocation's fresh build.
  const stale = join(root, 'backend/src/LearnStack.Api/bin/Release/net10.0');
  mkdirSync(stale, { recursive: true });
  writeFileSync(join(stale, 'LearnStack.Api.dll'), 'stale');
  const log = join(root, 'calls.jsonl');
  writeFileSync(log, '');
  writeFileSync(
    join(root, 'bin/dotnet'),
    '#!/bin/sh\nexec "$MIGRATION_TEST_NODE" "$MIGRATION_TEST_STUB" "$@"\n',
    { mode: 0o700 },
  );
  const stub = join(root, 'dotnet-stub.cjs');
  writeFileSync(
    stub,
    `const { appendFileSync, existsSync, writeFileSync } = require('node:fs');
const { join } = require('node:path');
const args = process.argv.slice(2);
const stage = args[0] === 'tool' ? 'tool' : args[0] === 'build' ? 'build' : 'ef';
appendFileSync(process.env.MIGRATION_TEST_LOG, JSON.stringify({
  args, cwd: process.cwd(), connection: process.env.ConnectionStrings__Migration,
}) + '\\n');
if (process.cwd() !== join(process.env.MIGRATION_TEST_ROOT, 'backend')) process.exit(91);
if (process.env.MIGRATION_TEST_FAIL === stage) process.exit(17);
const built = join(process.env.MIGRATION_TEST_ROOT, 'fresh-release');
if (stage === 'build') writeFileSync(built, 'fresh');
if (stage === 'ef') {
  if (!existsSync(built)) process.exit(92);
  if (args[args.indexOf('--project') + 1] === process.env.MIGRATION_TEST_FAIL_PROJECT)
    process.exit(18);
}
`,
  );
  return {
    root,
    chains,
    run(environment: Record<string, string> = {}) {
      const result = spawnSync('make', ['migrate', '.SHELLFLAGS=-c'], {
        cwd: root,
        env: {
          PATH: `${join(root, 'bin')}:${process.env.PATH}`,
          NODE_ENV: 'test',
          ConnectionStrings__Migration: 'Host=localhost;Username=learnstack_migration',
          MIGRATION_TEST_NODE: process.execPath,
          MIGRATION_TEST_STUB: stub,
          MIGRATION_TEST_LOG: log,
          MIGRATION_TEST_ROOT: root,
          ...environment,
        },
        encoding: 'utf8',
        timeout: 5000,
      });
      expect(result.error).toBeUndefined();
      const calls = readFileSync(log, 'utf8')
        .split('\n')
        .filter(Boolean)
        .map((line) => JSON.parse(line) as { args: string[]; cwd: string; connection?: string });
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
