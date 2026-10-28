// @vitest-environment node
import { execFileSync, spawnSync } from 'node:child_process';
import {
  chmodSync,
  existsSync,
  linkSync,
  lstatSync,
  mkdirSync,
  mkdtempSync,
  readFileSync,
  realpathSync,
  rmSync,
  symlinkSync,
  writeFileSync,
} from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { parseEnv } from 'node:util';

import { afterEach, describe, expect, it } from 'vitest';

const roots: string[] = [];
const canary = 'private-renderer-file-canary-4826';
const secret = 'preserved_private_renderer_secret_4826_abcdefgh';
const defaults = [
  'LEARNSTACK_PUBLIC_API_ORIGIN=http://127.0.0.1:5080',
  'LEARNSTACK_PUBLIC_TLS_CERT=.data/tls/public.pem',
  'LEARNSTACK_PUBLIC_TLS_KEY=.data/tls/public-key.pem',
].join('\n');

afterEach(() => {
  for (const root of roots.splice(0)) rmSync(root, { recursive: true, force: true });
});

function fixture() {
  const root = realpathSync(mkdtempSync(join(tmpdir(), 'learnstack-public-local-file-')));
  roots.push(root);
  mkdirSync(join(root, 'scripts'));
  mkdirSync(join(root, 'frontend/apps/web/.server'), { recursive: true });
  writeFileSync(join(root, 'package.json'), '{"type":"module"}\n');
  for (const name of ['public-local.mjs', 'public-runtime.mjs', 'demo-database.mjs'])
    writeFileSync(
      join(root, 'scripts', name),
      readFileSync(new URL(`../../../../../scripts/${name}`, import.meta.url)),
    );
  const parsed = join(root, 'parsed');
  writeFileSync(
    join(root, 'frontend/apps/web/.server/ingress.js'),
    `import { appendFileSync } from 'node:fs';
import { parseEnv } from 'node:util';
export function loadLocalEnvironment() { throw new Error('Unexpected API launch'); }
export function parseLocalEnvironment(source) {
  appendFileSync(${JSON.stringify(parsed)}, 'parsed\\n');
  return parseEnv(source);
}
export function validSecret(value) { return typeof value === 'string' && /^[A-Za-z0-9_-]{43,}$/.test(value); }
export function publicServerConfiguration(values) {
  if (!validSecret(values.LEARNSTACK_PUBLIC_HOP_SECRET)
    || !values.LEARNSTACK_PUBLIC_API_ORIGIN || !values.LEARNSTACK_PUBLIC_TLS_CERT
    || !values.LEARNSTACK_PUBLIC_TLS_KEY) throw new Error('Invalid fixture configuration');
  return {};
}
`,
  );
  return { root, path: join(root, '.env'), parsed };
}

function prepare(root: string) {
  return spawnSync(process.execPath, [join(root, 'scripts/public-local.mjs'), 'prepare'], {
    cwd: root,
    env: { NODE_ENV: 'test', PATH: process.env.PATH },
    encoding: 'utf8',
    timeout: 3000,
    killSignal: 'SIGKILL',
  });
}

function refused(f: ReturnType<typeof fixture>) {
  const result = prepare(f.root);
  expect(result.error).toBeUndefined();
  expect(result.signal).toBe(null);
  expect(result.status).toBe(1);
  expect(result.stdout).toBe('');
  expect(result.stderr).toBe('Local public configuration or launch failed\n');
  expect(result.stderr).not.toContain(canary);
  expect(existsSync(f.parsed)).toBe(false);
}

describe('public-local prepare private file ownership', () => {
  it.each(['absent', 'empty'])(
    'prepares a private file with an %s hop secret through the actual copied CLI',
    (state) => {
      const f = fixture();
      const source = `PRIVATE_CANARY=${canary}\n${state === 'empty' ? 'LEARNSTACK_PUBLIC_HOP_SECRET=\n' : ''}`;
      writeFileSync(f.path, source, { mode: 0o600 });
      const before = lstatSync(f.path);
      const result = prepare(f.root);
      expect(result.error).toBeUndefined();
      expect(result.status, result.stderr).toBe(0);
      expect(result.stdout).toBe('');
      expect(result.stderr).toBe(
        'Private renderer configuration prepared; TLS and hosts remain manual.\n',
      );
      expect(result.stderr).not.toContain(canary);
      const prepared = readFileSync(f.path, 'utf8');
      const values = parseEnv(prepared);
      expect(values.PRIVATE_CANARY).toBe(canary);
      expect(values.LEARNSTACK_PUBLIC_HOP_SECRET).toMatch(/^[A-Za-z0-9_-]{43}$/);
      expect(prepared).toContain(defaults);
      expect(prepared.endsWith('\n')).toBe(true);
      expect(lstatSync(f.path).ino).toBe(before.ino);
      expect(lstatSync(f.path).mode & 0o777).toBe(0o600);
    },
  );

  it('preserves an existing private secret and truncates normalized trailing bytes before a no-op rerun', () => {
    const f = fixture();
    const normalized = `PRIVATE_CANARY=${canary}\nLEARNSTACK_PUBLIC_HOP_SECRET=${secret}\n${defaults}\n`;
    writeFileSync(f.path, normalized + '\n\n\n', { mode: 0o600 });
    const before = lstatSync(f.path);
    for (let index = 0; index < 2; index++) {
      const result = prepare(f.root);
      expect(result.error).toBeUndefined();
      expect(result.status, result.stderr).toBe(0);
      expect(result.stdout).toBe('');
      expect(result.stderr).not.toContain(secret);
      expect(readFileSync(f.path, 'utf8')).toBe(normalized);
      expect(lstatSync(f.path).ino).toBe(before.ino);
      expect(lstatSync(f.path).mode & 0o777).toBe(0o600);
    }
  });

  it.each(['symlink', 'hardlink'])(
    'refuses a selected %s before parsing or changing its target',
    (kind) => {
      const f = fixture();
      const target = join(f.root, 'private-target');
      const source = `PRIVATE_CANARY=${canary}\n`;
      writeFileSync(target, source, { mode: 0o600 });
      if (kind === 'symlink') symlinkSync(target, f.path);
      else linkSync(target, f.path);
      const before = lstatSync(target);
      const selected = lstatSync(f.path);
      refused(f);
      expect(readFileSync(target, 'utf8')).toBe(source);
      expect(lstatSync(target).mode).toBe(before.mode);
      expect(lstatSync(target).ino).toBe(before.ino);
      expect(lstatSync(target).nlink).toBe(before.nlink);
      expect(lstatSync(f.path).ino).toBe(selected.ino);
    },
  );

  it.each([0o640, 0o644, 0o660])(
    'refuses shared mode %s without reading, repairing or rewriting it',
    (mode) => {
      const f = fixture();
      const source = `PRIVATE_CANARY=${canary}\n`;
      writeFileSync(f.path, source, { mode: 0o600 });
      chmodSync(f.path, mode);
      const before = lstatSync(f.path);
      refused(f);
      expect(readFileSync(f.path, 'utf8')).toBe(source);
      expect(lstatSync(f.path).mode).toBe(before.mode);
      expect(lstatSync(f.path).ino).toBe(before.ino);
    },
  );

  it('refuses a FIFO without waiting for a writer or changing its metadata', () => {
    const f = fixture();
    execFileSync('mkfifo', [f.path], { stdio: 'ignore' });
    chmodSync(f.path, 0o600);
    const before = lstatSync(f.path);
    refused(f);
    const after = lstatSync(f.path);
    expect(after.isFIFO()).toBe(true);
    expect(after.mode).toBe(before.mode);
    expect(after.ino).toBe(before.ino);
  });

  it('refuses an oversized regular private file before parsing or modifying it', () => {
    const f = fixture();
    const source = `# ${canary}${'x'.repeat(256 * 1024)}\n`;
    writeFileSync(f.path, source, { mode: 0o600 });
    const before = lstatSync(f.path);
    refused(f);
    expect(readFileSync(f.path, 'utf8')).toBe(source);
    expect(lstatSync(f.path).mode).toBe(before.mode);
    expect(lstatSync(f.path).size).toBe(before.size);
    expect(lstatSync(f.path).ino).toBe(before.ino);
  });
});
