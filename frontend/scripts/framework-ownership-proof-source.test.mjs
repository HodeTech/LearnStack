import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { test } from 'node:test';

import { assertNoCanonicalOwnershipReaders } from '../apps/web/scripts/framework-ownership-matrix-scope.mjs';
import { instrumentFrameworkOwnershipMatrix } from '../apps/web/scripts/framework-ownership-matrix-source.mjs';
import { instrumentFrameworkOwnershipProof } from '../apps/web/scripts/framework-ownership-proof-source.mjs';

test('the emitted recorders write actual finite JSONL records and bind the dynamic matrix root', () => {
  const root = mkdtempSync(join(tmpdir(), 'learnstack-ownership-recorder-'));
  const app = join(root, 'frontend/apps/web');
  const records = join(root, 'ownership-control/events.jsonl');
  mkdirSync(app, { recursive: true });
  mkdirSync(join(root, 'ownership-control'), { mode: 0o700 });
  writeFileSync(records, '', { mode: 0o600 });
  try {
    // This inert anchor carrier never executes; only the emitted recorder does.
    const runtime = `// INERT_RUNTIME_BOUNDARY
    stopping = true;
    await ownSetup(app.prepare());
    if (app) ownShutdown(() => app.close());
          server.listen(0, '127.0.0.1', () => {`;
    const generated = instrumentFrameworkOwnershipProof({
      ownershipCase: 'prepare-sigterm',
      configuration: 'export default nextConfig;',
      runtime,
    }).runtime;
    const recorder = generated.slice(0, generated.indexOf('// INERT_RUNTIME_BOUNDARY'));
    const result = spawnSync(process.execPath, ['--input-type=module'], {
      cwd: app,
      input: recorder + "recordOwnership('prepare-called'); recordOwnership('prepare-settled');",
      encoding: 'utf8',
      timeout: 5000,
    });
    assert.equal(result.status, 0, result.stderr);
    assert.equal(result.signal, null);
    assert.equal(result.stdout, '');
    assert.equal(result.stderr, '');
    const bytes = readFileSync(records, 'utf8');
    assert.equal(bytes.endsWith('\n'), true);
    const lines = bytes.split('\n');
    assert.equal(lines.length, 3);
    assert.equal(lines.pop(), '');
    const events = lines.map((line) => JSON.parse(line));
    assert.deepEqual(
      events.map((event) => event.event),
      ['prepare-called', 'prepare-settled'],
    );
    for (const event of events) {
      assert.deepEqual(Object.keys(event).sort(), ['event', 'parentPid', 'pid']);
      assert.ok(Number.isSafeInteger(event.pid) && event.pid > 1);
      assert.equal(event.parentPid, process.pid);
    }
    assert.equal(events[0].pid, events[1].pid);

    const matrixInput = {
      configuration: 'export default nextConfig;',
      runtime:
        '// INERT_RUNTIME_BOUNDARY\n' +
        readFileSync(
          new URL('../apps/web/scripts/public-server-runtime.mjs', import.meta.url),
          'utf8',
        )
          // The matrix transform follows the BFF copy's ephemeral listener transform.
          .replace(
            "server.listen(PUBLIC_HTTPS_PORT, '127.0.0.1', () => {",
            "server.listen(0, '127.0.0.1', () => {",
          ),
      child: '  child = createFrameworkRuntimeChildControl();',
      owner: '  const graceful =',
      rootPage: 'export default function Page() { return <main>Public</main>; }',
    };
    const matrix = instrumentFrameworkOwnershipMatrix(matrixInput);
    assert.ok(
      matrix.rootPage.includes(
        "export const dynamic = 'force-dynamic';\nexport const revalidate = 0;",
      ),
    );
    assert.ok(matrix.rootPage.includes('framework.waitUntil(work);'));
    assert.equal(
      matrix.page.includes('force-dynamic'),
      false,
      'real static paths worker route stays static',
    );
    assert.ok(matrix.runtime.includes('if (!control.stopRequested()) throw'));
    assert.throws(
      () =>
        instrumentFrameworkOwnershipMatrix({
          ...matrixInput,
          rootPage: matrixInput.rootPage + '\n',
        }),
      /exact copied ownership root page/,
    );
    writeFileSync(records, '', { mode: 0o600 });
    writeFileSync(join(root, 'ownership-control/scenario'), 'prepare-term', { mode: 0o600 });
    const matrixRecorder = matrix.runtime.slice(
      0,
      matrix.runtime.indexOf('// INERT_RUNTIME_BOUNDARY'),
    );
    const matrixResult = spawnSync(process.execPath, ['--input-type=module'], {
      cwd: app,
      input:
        matrixRecorder + "ownershipEvent('prepare-called'); ownershipEvent('prepare-settled');",
      encoding: 'utf8',
      timeout: 5000,
    });
    assert.equal(matrixResult.status, 0, matrixResult.stderr);
    assert.equal(matrixResult.signal, null);
    assert.equal(matrixResult.stdout, '');
    assert.equal(matrixResult.stderr, '');
    const matrixLines = readFileSync(records, 'utf8').split('\n');
    assert.equal(matrixLines.length, 3);
    assert.equal(matrixLines.pop(), '');
    const matrixEvents = matrixLines.map((line) => JSON.parse(line));
    assert.deepEqual(
      matrixEvents.map((event) => event.event),
      ['prepare-called', 'prepare-settled'],
    );
    for (const event of matrixEvents) {
      assert.deepEqual(Object.keys(event).sort(), [
        'case',
        'event',
        'milliseconds',
        'parentPid',
        'pid',
      ]);
      assert.equal(event.case, 'prepare-term');
      assert.ok(Number.isSafeInteger(event.pid) && event.pid > 1);
      assert.equal(event.parentPid, process.pid);
      assert.ok(
        Number.isSafeInteger(event.milliseconds) &&
          event.milliseconds >= 0 &&
          event.milliseconds < 5000,
      );
    }
    assert.equal(matrixEvents[0].pid, matrixEvents[1].pid);

    const readers = join(root, '.framework-state/readers');
    mkdirSync(readers, { recursive: true, mode: 0o700 });
    const captured = [{ format: 1, pid: process.pid, kind: 'production', nonce: 'a'.repeat(32) }];
    writeFileSync(join(readers, 'b'.repeat(32)), 'unrelated first reader', { mode: 0o600 });
    assertNoCanonicalOwnershipReaders(root, captured);
    rmSync(join(readers, 'b'.repeat(32)));
    writeFileSync(join(readers, 'c'.repeat(32)), 'unrelated replacement reader', { mode: 0o600 });
    assertNoCanonicalOwnershipReaders(root, captured);
    assert.throws(() => assertNoCanonicalOwnershipReaders(root, []));
    writeFileSync(join(readers, captured[0].nonce), 'own contamination', { mode: 0o600 });
    assert.throws(
      () => assertNoCanonicalOwnershipReaders(root, captured),
      /own canonical reader contamination/,
    );
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});
