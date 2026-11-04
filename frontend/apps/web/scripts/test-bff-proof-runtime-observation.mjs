// Exercise the exact copied-runtime instrumentation without building Next or modifying product sources.
import assert from 'node:assert/strict';
import { ChildProcess } from 'node:child_process';
import { mkdtempSync, mkdirSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

import ts from 'typescript';

import { instrumentBffProofRequestRuntime } from './bff-proof-host.mjs';
import { observeBffProofShutdown } from './bff-proof-support.mjs';

const web = fileURLToPath(new URL('../', import.meta.url));
const source = join(web, 'src/server/auth/request-runtime.ts');
const original = readFileSync(source, 'utf8');
const instrumented = instrumentBffProofRequestRuntime(original);
assert.throws(() =>
  instrumentBffProofRequestRuntime(original.replace('  const active = new Set<Frame>();', '')),
);
assert.throws(() =>
  instrumentBffProofRequestRuntime(original + '\n  const active = new Set<Frame>();'),
);

// Type-check the instrumented current file using the server's real compiler settings.
const configPath = join(web, 'tsconfig.server.json');
const config = ts.readConfigFile(configPath, ts.sys.readFile);
assert.equal(config.error, undefined);
const parsed = ts.parseJsonConfigFileContent(config.config, ts.sys, web, { noEmit: true });
assert.deepEqual(parsed.errors, []);
const host = ts.createCompilerHost(parsed.options);
host.getCurrentDirectory = () => web;
const read = host.readFile.bind(host);
host.readFile = (path) => (resolve(path) === source ? instrumented : read(path));
const program = ts.createProgram([source], parsed.options, host);
assert.equal(program.getSourceFile(source)?.text, instrumented);
const diagnostics = ts.getPreEmitDiagnostics(program);
assert.equal(
  diagnostics.length,
  0,
  ts.formatDiagnosticsWithColorAndContext(diagnostics, {
    getCurrentDirectory: () => web,
    getCanonicalFileName: (name) => name,
    getNewLine: () => '\n',
  }),
);

const root = mkdtempSync(join(tmpdir(), 'learnstack-bff-observation-'));
try {
  writeFileSync(join(root, 'package.json'), '{"type":"module"}');
  const compile = (input, output, text = readFileSync(input, 'utf8')) => {
    const result = ts.transpileModule(text, {
      reportDiagnostics: true,
      compilerOptions: {
        target: ts.ScriptTarget.ES2022,
        module: ts.ModuleKind.ESNext,
      },
    });
    assert.equal(result.diagnostics?.length ?? 0, 0);
    mkdirSync(dirname(output), { recursive: true });
    writeFileSync(output, result.outputText);
  };
  // Only the fixture copy is written. Its dependencies are the current source, not workstation build output.
  for (const directory of ['', 'auth']) {
    const input = join(web, 'src/server', directory);
    for (const name of readdirSync(input)) {
      if (!name.endsWith('.ts') || /\.(?:test|spec|d)\.ts$/.test(name)) continue;
      const path = join(input, name);
      compile(
        path,
        join(root, 'server', directory, name.replace(/\.ts$/, '.js')),
        path === source ? instrumented : readFileSync(path, 'utf8'),
      );
    }
  }
  compile(
    join(web, 'src/test/page-response-fixture.ts'),
    join(root, 'test/page-response-fixture.js'),
  );
  const { createBffRequestRuntime } = await import(
    pathToFileURL(join(root, 'server/auth/request-runtime.js'))
  );
  const { SnapshotWire, snapshotBinding, snapshotRequest, heldWork } = await import(
    pathToFileURL(join(root, 'test/page-response-fixture.js'))
  );
  const site = {
    displayName: 'School',
    defaultLocale: 'en',
    enabledLocales: ['en'],
    theme: null,
    showPlatformAttribution: true,
  };
  const entry = { site, locale: 'en', view: { kind: 'entry', entry: null, cursor: null } };
  const ticks = async () => {
    for (let i = 0; i < 8; ++i) await Promise.resolve();
  };
  const controller = new AbortController();
  const runtime = createBffRequestRuntime({
    read: () => ({ site, signal: controller.signal, assertActive() {} }),
  });
  const wire = new SnapshotWire();
  const handler = heldWork(),
    framework = heldWork(),
    pool = heldWork();
  const running = runtime.run(snapshotBinding, snapshotRequest(), wire.native, async () => {
    const lease = runtime.preparation.reserve(runtime.preparation.current(snapshotBinding), {});
    lease.track(pool.promise);
    lease.pin(() => true, 30_000);
    lease.publishEntry(entry);
    runtime.framework.get().waitUntil(framework.promise);
    wire.native.write('admitted');
    await handler.promise;
  });
  assert.deepEqual(runtime.proofCounts(), [1, 1, 1, 1, 1]);
  let stopped = false;
  const stopping = runtime.stop().then(() => {
    stopped = true;
  });
  assert.deepEqual(runtime.proofCounts(), [1, 0, 1, 1, 1]);

  // A malformed observer frame cannot release or replace any actual stop owner.
  const child = new ChildProcess(),
    observer = observeBffProofShutdown(child);
  child.emit('message', {
    kind: 'proof-shutdown',
    entered: true,
    watchdog: false,
    listener: 'pending',
    next: 'pending',
    sessions: 'pending',
    requests: 'pending',
    milestone: 'entered',
    sample: [0, 'PRIVATE-CANARY', 0, 0, 0],
  });
  assert.equal(observer.sample(), 'invalid');
  assert.ok(!observer.sample().includes('PRIVATE-CANARY'));
  assert.ok(!observer.snapshot().includes('PRIVATE-CANARY'));
  await ticks();
  assert.equal(stopped, false);
  assert.deepEqual(runtime.proofCounts(), [1, 0, 1, 1, 1]);
  child.emit('close', null, 'SIGTERM');

  handler.finish();
  await running;
  await ticks();
  assert.deepEqual(runtime.proofCounts(), [0, 0, 1, 1, 1]);
  framework.finish();
  await ticks();
  assert.deepEqual(runtime.proofCounts(), [0, 0, 1, 0, 1]);
  wire.drainCallbacks();
  await ticks();
  assert.deepEqual(runtime.proofCounts(), [0, 0, 0, 0, 1]);
  assert.equal(stopped, false);
  pool.finish();
  await stopping;
  assert.deepEqual(runtime.proofCounts(), [0, 0, 0, 0, 0]);
  assert.equal(runtime.counts(), 0);
  assert.equal(runtime.snapshotCounts().active, 0);

  // A real active frame remains counted until a terminal response, independent of the handler.
  const terminalRuntime = createBffRequestRuntime();
  const terminalWire = new SnapshotWire();
  terminalRuntime.run(snapshotBinding, snapshotRequest(), terminalWire.native, () => {});
  await ticks();
  assert.deepEqual(terminalRuntime.proofCounts(), [0, 1, 0, 0, 0]);
  terminalWire.destroy();
  await ticks();
  assert.deepEqual(terminalRuntime.proofCounts(), [0, 0, 0, 0, 0]);
  await terminalRuntime.stop();

  // Saturate frame counts with 257 actual outstanding handlers and response lifetimes.
  const many = createBffRequestRuntime(),
    gate = heldWork(),
    wires = [],
    pending = [];
  for (let i = 0; i < 257; ++i) {
    const current = new SnapshotWire();
    wires.push(current);
    pending.push(many.run(snapshotBinding, snapshotRequest(), current.native, () => gate.promise));
  }
  assert.deepEqual(many.proofCounts(), [256, 256, 0, 0, 0]);
  for (const current of wires) current.destroy();
  gate.finish();
  await Promise.all(pending);
  await ticks();
  assert.deepEqual(many.proofCounts(), [0, 0, 0, 0, 0]);
  await many.stop();

  // Saturate real write callbacks and actual framework promises, then join each.
  const workRuntime = createBffRequestRuntime(),
    workWire = new SnapshotWire(),
    waits = [];
  workRuntime.run(snapshotBinding, snapshotRequest(), workWire.native, () => {
    const context = workRuntime.framework.get();
    for (let i = 0; i < 257; ++i) {
      const work = heldWork();
      waits.push(work);
      context.waitUntil(work.promise);
      workWire.native.write('x');
    }
  });
  assert.deepEqual(workRuntime.proofCounts(), [0, 1, 256, 256, 0]);
  workWire.destroy();
  for (const work of waits) work.finish();
  workWire.drainCallbacks();
  await ticks();
  assert.deepEqual(workRuntime.proofCounts(), [0, 0, 0, 0, 0]);
  await workRuntime.stop();
} finally {
  rmSync(root, { recursive: true, force: true });
}
