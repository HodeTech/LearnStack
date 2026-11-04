// @vitest-environment node
import { execFileSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { runInNewContext } from 'node:vm';

import ts from 'typescript';
import { describe, expect, it } from 'vitest';

import { closedFrameworkProofSync } from './framework-proof-closed-boundary';

type Observation = { surface: string; sanitized: boolean };
type Evidence = {
  consume(phase: string, messages: Observation[]): void;
  complete(messages: Observation[]): void;
};
// Exercise the exact pure ledger used by the self-executing fixture without starting it.
const source = readFileSync(new URL('../../scripts/verify-ingress.mjs', import.meta.url), 'utf8');
const file = ts.createSourceFile(
  'verify-ingress.mjs',
  source,
  ts.ScriptTarget.Latest,
  true,
  ts.ScriptKind.JS,
);
const declarations = file.statements.filter(
  (node): node is ts.FunctionDeclaration =>
    ts.isFunctionDeclaration(node) && node.name?.text === 'createUpgradeEvidence',
);
if (declarations.length !== 1) throw new Error('Ingress upgrade evidence source refused');
const createEvidence = runInNewContext(`(${declarations[0]!.getText(file)})`) as () => Evidence;
const native = (): Observation => ({ surface: 'native', sanitized: false });
const sink = (): Observation => ({ surface: 'sink', sanitized: true });
const production = () => Array.from({ length: 6 }, native);
const development = () => Array.from({ length: 4 }, native);

describe('finite ingress upgrade observation accounting', () => {
  it('consumes all four fixed phases in order, including both native/sink HMR pairs', () => {
    const proof = createEvidence();
    const messages = production();
    proof.consume('production', messages);
    messages.push(...development());
    proof.consume('development', messages);
    for (let count = 0; count < 2; count++) {
      messages.push(native(), sink());
      proof.consume('hmr', messages);
    }
    expect(() => proof.complete(messages)).not.toThrow();
  });

  it('rejects missing, extra, wrong-surface, sanitized and extra-field production frames', () => {
    const candidates = [
      production().slice(1),
      new Array<Observation>(6),
      [...production(), native()],
      [...production().slice(0, 5), sink()],
      [...production().slice(0, 5), { surface: 'native', sanitized: true }],
      [...production().slice(0, 5), { ...native(), extra: true }],
    ];
    for (const messages of candidates) {
      const proof = createEvidence();
      expect(() => proof.consume('production', messages)).toThrow(
        'Ingress upgrade observations refused',
      );
      expect(() => proof.consume('production', production())).not.toThrow();
    }
  });

  it('rejects skipped phases and malformed development or HMR slices', () => {
    const proof = createEvidence();
    const messages = production();
    expect(() => proof.consume('development', messages)).toThrow();
    proof.consume('production', messages);
    messages.push(...development().slice(1));
    expect(() => proof.consume('development', messages)).toThrow();
    messages.push(native());
    proof.consume('development', messages);
    const good = [...messages, native(), sink()];
    for (const slice of [[sink(), native()], [sink()], [native(), sink(), sink()]])
      expect(() => proof.consume('hmr', [...messages, ...slice])).toThrow();
    proof.consume('hmr', good);
    expect(() => proof.complete(good)).toThrow();
    good.push(native(), sink());
    proof.consume('hmr', good);
    expect(() => proof.complete([...good, native()])).toThrow();
    expect(() => proof.complete([sink(), ...good.slice(1)])).toThrow();
    expect(() => proof.complete(good)).not.toThrow();
  });
});

it('joins the fixed reuse helper and rejects dirty or uncertain outcomes', () =>
  closedFrameworkProofSync(() => {
    const frontend = fileURLToPath(new URL('../../../../', import.meta.url));
    const output = execFileSync(
      process.execPath,
      ['--test', join(frontend, 'scripts/framework-proof-reuse.test.mjs')],
      {
        encoding: 'utf8',
        stdio: ['ignore', 'pipe', 'pipe'],
        timeout: 10_000,
        maxBuffer: 65536,
        env: { PATH: process.env.PATH, NODE_ENV: 'production', NEXT_TELEMETRY_DISABLED: '1' },
      },
    );
    for (const [field, count] of [
      ['tests', 4],
      ['pass', 4],
      ['fail', 0],
      ['cancelled', 0],
      ['skipped', 0],
      ['todo', 0],
    ] as const)
      expect(output).toMatch(new RegExp(`^# ${field} ${count}$`, 'm'));
  }));
