// @vitest-environment node
import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import {
  copyFileSync,
  existsSync,
  mkdirSync,
  mkdtempSync,
  readFileSync,
  readdirSync,
  rmSync,
  writeFileSync,
} from 'node:fs';
import { createRequire } from 'node:module';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

import { afterEach, beforeAll, expect, it } from 'vitest';

type QualifiedManager = {
  command: string;
  args: string[];
  executable: string;
  version: string;
};
const frontend = fileURLToPath(new URL('../../../../', import.meta.url));
const guard = createRequire(import.meta.url)('../../../../scripts/framework-installation.mjs') as {
  resolveQualifiedPnpm(options?: { frontendRoot?: string }): QualifiedManager;
};
const roots: string[] = [];
let manager: QualifiedManager;

beforeAll(() => {
  // Probe the actual executable once. A packageManager/user-agent declaration
  // does not supply the binary used by these real pnpm commands.
  manager = guard.resolveQualifiedPnpm({ frontendRoot: frontend });
  expect(manager.version).toBe('9.12.3');
});
afterEach(() => {
  for (const root of roots.splice(0)) rmSync(root, { recursive: true, force: true });
});

function fixture() {
  const root = mkdtempSync(join(tmpdir(), 'learnstack-install-hook-'));
  roots.push(root);
  for (const path of [
    '.pnpmfile.cjs',
    '.npmrc',
    'package.json',
    'pnpm-workspace.yaml',
    'pnpm-lock.yaml',
    'apps/web/package.json',
    'packages/sdk/package.json',
    'packages/ui/package.json',
    'packages/config/package.json',
    'scripts/framework-installation.mjs',
    'scripts/install-frontend.mjs',
    'framework/next-response-cleanup/census.json',
    'framework/next-response-cleanup/pnpm.json',
  ]) {
    const target = join(root, path);
    mkdirSync(dirname(target), { recursive: true });
    copyFileSync(join(frontend, path), target);
  }
  // Existing dependency/output bytes are sentinels, not a simulated successful
  // installation. Their entire membership and contents must survive refusal.
  for (const path of [
    'node_modules/existing-package/value.txt',
    'apps/web/.next/server/existing.js',
    'apps/web/.server/existing.js',
    'packages/sdk/dist/existing.js',
  ]) {
    mkdirSync(dirname(join(root, path)), { recursive: true });
    writeFileSync(join(root, path), `existing owned bytes: ${path}\n`);
  }
  writeFileSync(join(root, 'empty-user-config'), '');
  return root;
}

function snapshot(root: string) {
  const values: Array<{ path: string; kind: string; sha256?: string }> = [];
  const visit = (directory: string, prefix: string) => {
    for (const entry of readdirSync(directory, { withFileTypes: true }).sort((a, b) =>
      a.name.localeCompare(b.name),
    )) {
      const path = prefix ? `${prefix}/${entry.name}` : entry.name;
      if (entry.isDirectory()) {
        values.push({ path, kind: 'directory' });
        visit(join(directory, entry.name), path);
      } else {
        if (!entry.isFile()) throw new Error('Unexpected install-control fixture alias');
        values.push({
          path,
          kind: 'file',
          sha256: createHash('sha256')
            .update(readFileSync(join(directory, entry.name)))
            .digest('hex'),
        });
      }
    }
  };
  visit(root, '');
  return values;
}

function execute(root: string, args: string[], token?: string) {
  const environment = { ...process.env };
  delete environment.LEARNSTACK_FRAMEWORK_INSTALL_OWNER;
  environment.npm_config_userconfig = join(root, 'empty-user-config');
  if (token !== undefined) environment.LEARNSTACK_FRAMEWORK_INSTALL_OWNER = token;
  return spawnSync(manager.command, [...manager.args, ...args], {
    cwd: root,
    env: environment,
    encoding: 'utf8',
    timeout: 10_000,
    maxBuffer: 64 * 1024,
  });
}

function expectRefusedWithoutMutation(root: string, args: string[], token?: string) {
  const before = snapshot(root);
  const result = execute(root, args, token);
  expect(result.error).toBeUndefined();
  expect(result.signal).toBeNull();
  expect(result.status).not.toBe(0);
  expect(result.stdout + result.stderr).toContain(
    'Use node frontend/scripts/install-frontend.mjs for an owned frontend restore',
  );
  expect(snapshot(root)).toEqual(before);
  expect(existsSync(join(root, 'node_modules/.pnpm'))).toBe(false);
}

it.each([
  ['frozen install', ['install', '--frozen-lockfile', '--offline']],
  ['install alias', ['i', '--frozen-lockfile', '--offline']],
  ['implicit install', []],
  [
    'filtered install',
    ['--filter', '@learnstack/web', 'install', '--frozen-lockfile', '--offline'],
  ],
  ['reporter-prefixed install', ['--reporter', 'append-only', 'install', '--offline']],
  ['add', ['add', 'is-number@7.0.0', '--offline']],
  ['unknown command', ['unrecognized-install-control-command']],
] as const)('refuses raw %s before dependency or executable-output mutation', (_name, args) => {
  expectRefusedWithoutMutation(fixture(), [...args]);
});

it('preserves a live runtime reader and refuses a raw install carrying its nonce', () => {
  const root = fixture();
  const nonce = 'a'.repeat(32);
  const state = join(root, '.framework-state');
  mkdirSync(join(state, 'readers'), { recursive: true });
  writeFileSync(join(state, 'format.json'), '{"format":1}\n');
  writeFileSync(
    join(state, 'readers', nonce),
    JSON.stringify({ format: 1, kind: 'production', pid: process.pid, nonce }) + '\n',
  );
  expectRefusedWithoutMutation(root, ['install', '--frozen-lockfile', '--offline'], nonce);
});

it('refuses a mismatched installation token before any project write', () => {
  const root = fixture();
  const state = join(root, '.framework-state');
  mkdirSync(state);
  writeFileSync(
    join(state, 'writer.json'),
    JSON.stringify({ format: 1, kind: 'install', pid: process.pid, nonce: 'b'.repeat(32) }) + '\n',
  );
  expectRefusedWithoutMutation(root, ['install', '--frozen-lockfile', '--offline'], 'c'.repeat(32));
});

it('keeps a real non-mutating exec usable without acquiring an installation writer', () => {
  const root = fixture();
  const before = snapshot(root);
  const result = execute(root, ['exec', process.execPath, '--version']);
  expect(result.error).toBeUndefined();
  expect(result.status, result.stdout + result.stderr).toBe(0);
  expect(result.stdout.trim()).toBe(`v${process.versions.node}`);
  expect(snapshot(root)).toEqual(before);
});

it('keeps the manager-path probe on the actual qualified pnpm executable without writes', () => {
  const root = fixture();
  const before = snapshot(root);
  const result = execute(root, ['--silent', 'run', 'framework:manager']);
  expect(result.error).toBeUndefined();
  expect(result.status, result.stdout + result.stderr).toBe(0);
  expect(result.stdout.trim()).toBe(manager.executable);
  expect(snapshot(root)).toEqual(before);
});
