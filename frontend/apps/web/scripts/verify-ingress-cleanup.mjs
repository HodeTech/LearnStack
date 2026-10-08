// Planted real listener failure must exit red and still remove its child/temp tree.
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { existsSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

const appRoot = fileURLToPath(new URL('../', import.meta.url));
const proof = mkdtempSync(join(tmpdir(), 'learnstack-ingress-cleanup-control-'));
function replaceOne(source, before, after) {
  assert.equal(source.split(before).length, 2, 'Each planted edit must match exactly once');
  return source.replace(before, after);
}
let runner;
let deadline;
try {
  const source = readFileSync(join(appRoot, 'scripts/verify-ingress.mjs'), 'utf8');
  writeFileSync(
    join(proof, 'stop-test-child.mjs'),
    readFileSync(join(appRoot, 'scripts/stop-test-child.mjs'), 'utf8'),
  );
  let mutant = replaceOne(
    source,
    "const appRoot = fileURLToPath(new URL('../', import.meta.url));",
    'const appRoot = ' + JSON.stringify(appRoot) + ';',
  );
  mutant = replaceOne(
    mutant,
    "assert(request.url === '/api/v1/public/site',",
    "assert(request.url === '/planted-invalid-bootstrap',",
  );
  mutant = replaceOne(
    mutant,
    'const children = [];',
    'const children = [];\nwriteFileSync(' +
      JSON.stringify(join(proof, 'fixture')) +
      ', fixtureRoot);',
  );
  mutant = replaceOne(
    mutant,
    'children.push(child);',
    'children.push(child);\nwriteFileSync(' +
      JSON.stringify(join(proof, 'pid')) +
      ', String(child.pid));',
  );
  writeFileSync(join(proof, 'mutant.mjs'), mutant);
  runner = spawn(process.execPath, [join(proof, 'mutant.mjs')], {
    stdio: ['ignore', 'ignore', 'pipe'],
  });
  let diagnostics = '';
  runner.stderr.on('data', (chunk) => {
    diagnostics = (diagnostics + chunk.toString()).slice(-8192);
  });
  const code = await new Promise((resolve, reject) => {
    deadline = setTimeout(() => {
      runner.kill('SIGKILL');
      reject(new Error('Cleanup control timed out'));
    }, 30_000);
    runner.once('error', reject);
    runner.once('exit', resolve);
  });
  clearTimeout(deadline);
  assert.equal(code, 1, 'A planted listener refusal must fail the real harness');
  assert(
    diagnostics.includes('Bootstrap fixture refused the hop'),
    'The intended listener control must cause the red outcome',
  );
  assert(existsSync(join(proof, 'pid')), 'The control must actually start a Next child');
  const pid = Number(readFileSync(join(proof, 'pid'), 'utf8'));
  assert.throws(
    () => process.kill(pid, 0),
    { code: 'ESRCH' },
    'The refused control must stop its child',
  );
  assert.throws(
    () => process.kill(-pid, 0),
    { code: 'ESRCH' },
    'The refused control must stop its entire owned group',
  );
  assert.equal(
    existsSync(readFileSync(join(proof, 'fixture'), 'utf8')),
    false,
    'The refused control must remove its temporary tree',
  );
  console.warn('Planted listener refusal: red outcome and full child/tree cleanup passed.');
} finally {
  clearTimeout(deadline);
  if (runner && runner.exitCode === null && runner.signalCode === null) runner.kill('SIGKILL');
  // Only proof-owned metadata authorizes cleanup after a failed mutant assertion.
  if (existsSync(join(proof, 'pid'))) {
    const pid = Number(readFileSync(join(proof, 'pid'), 'utf8'));
    try {
      process.kill(-pid, 'SIGKILL');
    } catch {
      /* Already stopped. */
    }
  }
  if (existsSync(join(proof, 'fixture')))
    rmSync(readFileSync(join(proof, 'fixture'), 'utf8'), { recursive: true, force: true });
  rmSync(proof, { recursive: true, force: true });
}
