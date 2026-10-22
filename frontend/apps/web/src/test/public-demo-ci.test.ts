// @vitest-environment node
import { spawnSync } from 'node:child_process';
import { chmodSync, existsSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
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
  const configuration = join(files.root, 'apt-config');
  const captured = join(files.root, 'installed-with');
  const foreign = join(files.root, 'foreign-apt-config');
  writeFileSync(foreign, 'preserve existing runner configuration');
  for (const [name, body] of [
    ['uname', 'printf "%s\\n" "${ARCHITECTURE:-x86_64}"'],
    [
      'pnpm',
      `
      printf 'pnpm %s\\n' "$*" >> "$INVOCATION"
      if [ "$*" = 'install --frozen-lockfile' ]; then
        [ "$FAILURE" != dependencies ] || exit 19
      elif [ "$*" = 'exec playwright install --with-deps chromium' ]; then
        /bin/cp "$CONFIGURATION" "$CAPTURED"
        [ "$FAILURE" != browser ] || exit 23
      else exit 99; fi
    `,
    ],
    [
      'sudo',
      `
      printf 'sudo %s\\n' "$*" >> "$INVOCATION"
      [ "$1" = -n ] || exit 99
      shift
      case "$1" in
        mktemp)
          [ "$*" = 'mktemp /etc/apt/apt.conf.d/zzzz-learnstack-browser-XXXXXX' ] || exit 99
          [ "$FAILURE" != creation ] || exit 17
          (set -C; : > "$CONFIGURATION") || exit 98
          printf '/etc/apt/apt.conf.d/zzzz-learnstack-browser-Ab1cD2\\n'
          ;;
        tee)
          [ "$*" = 'tee /etc/apt/apt.conf.d/zzzz-learnstack-browser-Ab1cD2' ] || exit 99
          if [ "$FAILURE" = write ]; then
            IFS= read -r first_line
            printf '%s\\n' "$first_line" > "$CONFIGURATION"
            exit 18
          fi
          /bin/cat > "$CONFIGURATION"
          ;;
        apt-config)
          [ "$*" = 'apt-config shell RETRIES Acquire::Retries HTTP_TIMEOUT Acquire::http::Timeout HTTPS_TIMEOUT Acquire::https::Timeout' ] || exit 99
          if [ "$FAILURE" = readback ]; then printf "RETRIES='3'\\n";
          else printf "%s\\n" "RETRIES='1'" "HTTP_TIMEOUT='15'" "HTTPS_TIMEOUT='15'"; fi
          ;;
        rm)
          [ "$*" = 'rm -- /etc/apt/apt.conf.d/zzzz-learnstack-browser-Ab1cD2' ] || exit 99
          [ "$FAILURE" != cleanup ] || exit 29
          /bin/rm -- "$CONFIGURATION"
          ;;
        *) exit 99 ;;
      esac
    `,
    ],
  ] as const) {
    writeFileSync(join(files.root, name), '#!/bin/sh\nset -eu\n' + body + '\n');
    chmodSync(join(files.root, name), 0o700);
  }
  const step = job?.match(
    / {6}- name: Install pinned browser tools\n {8}timeout-minutes: 5\n {8}working-directory: frontend\n {8}run: \|\n((?: {10}.*\n|\n)+)/,
  );
  if (!step?.[1]) throw new Error('Missing bounded browser installation command');
  const result = spawnSync('/bin/bash', ['-euo', 'pipefail', '-c', step[1]], {
    env: {
      ...files.env,
      CONFIGURATION: configuration,
      CAPTURED: captured,
      FAILURE: failure,
      ARCHITECTURE: failure === 'architecture' ? 'aarch64' : 'x86_64',
    },
    encoding: 'utf8',
    timeout: 10000,
  });
  return { ...files, result, configuration, captured, foreign };
}

it('installs all pinned browser dependencies with verified APT bounds and removes only its fragment', () => {
  const files = browserSetup();
  expect(files.result.status).toBe(0);
  expect(readFileSync(files.captured, 'utf8')).toBe(
    'Acquire::Retries "1";\nAcquire::http::Timeout "15";\nAcquire::https::Timeout "15";\n',
  );
  expect(existsSync(files.configuration)).toBe(false);
  expect(readFileSync(files.foreign, 'utf8')).toBe('preserve existing runner configuration');
  const calls = readFileSync(files.invocation, 'utf8').trim().split('\n');
  expect(calls[0]).toBe('pnpm install --frozen-lockfile');
  expect(calls.at(-2)).toBe('pnpm exec playwright install --with-deps chromium');
  expect(calls.at(-1)).toBe('sudo -n rm -- /etc/apt/apt.conf.d/zzzz-learnstack-browser-Ab1cD2');
});

it.each([
  ['creation', 17, false],
  ['write', 18, true],
  ['readback', 1, true],
  ['browser', 23, true],
  ['cleanup', 29, true],
  ['dependencies', 19, false],
  ['architecture', 1, false],
] as const)(
  'fails closed on browser setup %s and cleans only a created fragment',
  (failure, code, created) => {
    const files = browserSetup(failure);
    expect(files.result.status).toBe(code);
    expect(readFileSync(files.foreign, 'utf8')).toBe('preserve existing runner configuration');
    expect(existsSync(files.configuration)).toBe(failure === 'cleanup');
    const calls = readFileSync(files.invocation, 'utf8');
    expect(calls.includes('sudo -n rm --')).toBe(created);
    expect(existsSync(files.captured)).toBe(['browser', 'cleanup'].includes(failure));
    if (['dependencies', 'architecture'].includes(failure)) expect(calls).not.toContain('sudo ');
  },
);

it('starts the absolute budget before checkout and reserves four minutes of the job ceiling', () => {
  const files = fixture();
  const first = job?.match(
    /steps:\n(?: {6}#.*\n)* {6}- name: Reserve demo cleanup and artifact time\n {8}run: (.+)\n/,
  );
  expect(first).not.toBeNull();
  expect(job).toContain('timeout-minutes: 20');
  if (!first?.[1]) throw new Error('Missing first-step budget reservation');
  const result = spawnSync('/bin/bash', ['-eu', '-c', first[1]], { env: files.env });
  expect(result.status).toBe(0);
  expect(readFileSync(files.environment, 'utf8')).toBe('PUBLIC_DEMO_DEADLINE=1960\n');
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
