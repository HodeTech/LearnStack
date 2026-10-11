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
