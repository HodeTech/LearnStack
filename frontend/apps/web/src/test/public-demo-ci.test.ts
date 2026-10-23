// @vitest-environment node
import { spawnSync } from 'node:child_process';
import { chmodSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { afterEach, expect, it } from 'vitest';

const job = readFileSync(
  new URL('../../../../../.github/workflows/ci.yml', import.meta.url),
  'utf8',
)
  .split('  public-demo:\n')[1]
  ?.split('\n  meta:')[0];
const roots: string[] = [];
afterEach(() => {
  for (const root of roots.splice(0)) rmSync(root, { recursive: true, force: true });
});

function fixture() {
  const root = mkdtempSync(join(tmpdir(), 'learnstack-ci-budget-'));
  roots.push(root);
  const environment = join(root, 'environment');
  const invocation = join(root, 'invocation');
  for (const [name, body] of [
    ['date', 'printf "1000\\n"'],
    ['timeout', 'printf "%s\\n" "$@" > "$INVOCATION"'],
  ] as const) {
    writeFileSync(join(root, name), '#!/bin/sh\n' + body + '\n');
    chmodSync(join(root, name), 0o700);
  }
  const env = {
    PATH: root,
    NODE_ENV: 'test' as const,
    GITHUB_ENV: environment,
    INVOCATION: invocation,
  };
  return { root, environment, invocation, env };
}

function proofScript() {
  expect(job).toBeDefined();
  const script = job?.match(/ {8}id: demo\n {8}run: \|\n((?: {10}.*\n|\n)+)/)?.[1];
  expect(script).toBeDefined();
  if (!script) throw new Error('Missing real workflow proof command');
  return script;
}

function browserSetup(failure = '') {
  const files = fixture();
  for (const [name, body] of [
    [
      'pnpm',
      `
      printf 'pnpm %s\\n' "$*" >> "$INVOCATION"
      if [ "$*" = 'install --frozen-lockfile' ]; then
        [ "$FAILURE" != dependencies ] || exit 19
      elif [ "$*" = 'exec playwright install chromium' ]; then
        [ "$FAILURE" != browser ] || exit 23
      else exit 99; fi
    `,
    ],
    // Any privileged or APT call would leave a trace and fail the command.
    ...['sudo', 'apt', 'apt-get', 'apt-config'].map(
      (tool) => [tool, `printf '${tool} %s\\n' "$*" >> "$INVOCATION"\nexit 99`] as const,
    ),
  ] as const) {
    writeFileSync(join(files.root, name), '#!/bin/sh\nset -eu\n' + body + '\n');
    chmodSync(join(files.root, name), 0o700);
  }
  const step = job?.match(
    / {6}- name: Install pinned browser tools\n {8}timeout-minutes: 5\n {8}working-directory: frontend\n {8}run: \|\n((?: {10}.*\n|\n)+)/,
  );
  if (!step?.[1]) throw new Error('Missing bounded browser installation command');
  const result = spawnSync('/bin/bash', ['-euo', 'pipefail', '-c', step[1]], {
    env: { ...files.env, FAILURE: failure },
    encoding: 'utf8',
    timeout: 10000,
  });
  return { ...files, result };
}

it('installs only the pinned Chromium build without any APT mirror dependency', () => {
  const files = browserSetup();
  expect(files.result.status).toBe(0);
  expect(readFileSync(files.invocation, 'utf8').trim().split('\n')).toEqual([
    'pnpm install --frozen-lockfile',
    'pnpm exec playwright install chromium',
  ]);
});

it.each([
  ['dependencies', 19, ['pnpm install --frozen-lockfile']],
  ['browser', 23, ['pnpm install --frozen-lockfile', 'pnpm exec playwright install chromium']],
] as const)('fails closed on browser setup %s', (failure, code, calls) => {
  const files = browserSetup(failure);
  expect(files.result.status).toBe(code);
  expect(readFileSync(files.invocation, 'utf8').trim().split('\n')).toEqual(calls);
});

it('starts the absolute budget before checkout and reserves three minutes of the job ceiling', () => {
  const files = fixture();
  const first = job?.match(
    /steps:\n(?: {6}#.*\n)* {6}- name: Reserve demo cleanup and artifact time\n {8}run: (.+)\n/,
  );
  expect(first).not.toBeNull();
  expect(job).toContain('timeout-minutes: 20');
  if (!first?.[1]) throw new Error('Missing first-step budget reservation');
  const result = spawnSync('/bin/bash', ['-eu', '-c', first[1]], { env: files.env });
  expect(result.status).toBe(0);
  expect(readFileSync(files.environment, 'utf8')).toBe('PUBLIC_DEMO_DEADLINE=2020\n');
});

it('restores the existing NuGet cache without adding a cache-save post step', () => {
  const cache = job?.match(
    / {6}- name: Cache NuGet packages\n([\s\S]*?) {6}- name: Install pinned browser tools\n/,
  )?.[1];
  expect(cache).toContain('uses: actions/cache/restore@0057852bfaa89a56745cba8c7296529d2fc39830');
  expect(cache).toContain('path: ~/.nuget/packages');
  expect(cache).toContain("hashFiles('backend/**/*.csproj', 'backend/Directory.Packages.props')");
  expect(cache).toContain('restore-keys:');
  expect(cache).not.toContain('uses: actions/cache@');
});

it.each([
  ['sandbox', 0],
  ['sandbox', 1],
  ['sandbox', 124],
  ['sandbox', 137],
  ['receipt', 0],
  ['receipt', 1],
  ['receipt', 124],
  ['receipt', 137],
] as const)('bounds the actual %s tail shell and preserves exit %s', (kind, status) => {
  const files = fixture();
  writeFileSync(
    join(files.root, 'timeout'),
    '#!/bin/sh\nprintf "%s\\n" "$@" > "$INVOCATION"\nexit "$FORCED_EXIT"\n',
  );
  const script =
    kind === 'sandbox'
      ? job?.match(
          / {6}- name: Remove only the owned Chromium sandbox profile\n {8}if: always\(\)\n {8}timeout-minutes: 1\n {8}run: (.+)\n/,
        )?.[1]
      : job?.match(
          / {6}- name: Record bounded workflow outcome\n {8}if: always\(\)\n {8}timeout-minutes: 1\n {8}env:\n[\s\S]*? {8}run: \|\n((?: {10}.*\n|\n)+)/,
        )?.[1];
  if (!script) throw new Error('Missing actual bounded tail command');
  const result = spawnSync('/bin/bash', ['-eu', '-c', script], {
    env: { ...files.env, FORCED_EXIT: String(status) },
    timeout: 10000,
  });
  expect(result.status).toBe(status);
  expect(readFileSync(files.invocation, 'utf8').split('\n')).toEqual([
    '--signal=TERM',
    '--kill-after=5s',
    '15s',
    ...(kind === 'sandbox'
      ? ['node', 'scripts/ci-public-chromium.mjs', 'cleanup']
      : ['python3', '-']),
    '',
  ]);
});

it('bounds the real workflow command by the remaining budget and requests owned SIGINT cleanup', () => {
  const files = fixture();
  const result = spawnSync('/bin/bash', ['-eu', '-c', proofScript()], {
    env: { ...files.env, PUBLIC_DEMO_DEADLINE: '1123' },
  });
  expect(result.status).toBe(0);
  expect(readFileSync(files.invocation, 'utf8').split('\n')).toEqual([
    '--signal=INT',
    '--kill-after=35s',
    '123s',
    'make',
    'demo',
    'DEMO_MODE=ci',
    '',
  ]);
});

it.each(['1000', '999'])(
  'refuses an exhausted absolute budget before starting owned resources: %s',
  (deadline) => {
    const files = fixture();
    const result = spawnSync('/bin/bash', ['-eu', '-c', proofScript()], {
      env: { ...files.env, PUBLIC_DEMO_DEADLINE: deadline },
      encoding: 'utf8',
    });
    expect(result.status).toBe(1);
    expect(result.stderr).toContain('Demo setup exhausted');
    expect(() => readFileSync(files.invocation)).toThrow();
  },
);

it('uploads only named public reports, screenshots and sanitized receipts', () => {
  const paths = (source: string) => {
    const block = source.match(/ {10}path: \|\n((?: {12}.+\n)+)/)?.[1];
    if (!block) throw new Error('Missing actual workflow artifact allowlist');
    return block
      .trim()
      .split('\n')
      .map((path) => path.trim());
  };
  const expected = [
    'artifacts/public-demo/receipt.json',
    'artifacts/public-demo/audit-*.json',
    'artifacts/public-demo/audit-*.html',
    'artifacts/public-demo/browser-*.png',
    'artifacts/public-demo-ci/workflow.json',
  ];
  expect(paths(job!)).toEqual(expected);
  expect(paths(job!.replace('audit-*.json', '*.json'))).not.toEqual(expected);
});
