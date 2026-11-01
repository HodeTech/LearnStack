// Inert actual-process controls; no Next, shared outputs, or network dependencies.
import assert from 'node:assert/strict';
import { mkdtempSync, writeFileSync, existsSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';
import { runFrameworkRuntimeFamily as executeRuntimeFamily } from './framework-runtime-family.mjs';
import { runFrameworkProcessFamily as executeCommandFamily } from './framework-process-family.mjs';

const controlUrl = new URL('./framework-runtime-child-control.mjs', import.meta.url).href;
const nonce = 'a'.repeat(32);
const pendingFamilies = new Map();
function ownFamily(root, operation) {
  pendingFamilies.set(root, (pendingFamilies.get(root) ?? 0) + 1);
  return Promise.resolve()
    .then(operation)
    .finally(() => {
      pendingFamilies.set(root, pendingFamilies.get(root) - 1);
    });
}
function runFrameworkRuntimeFamily(options) {
  return ownFamily(options.cwd, () => executeRuntimeFamily(options));
}
function runFrameworkProcessFamily(command, args, options) {
  return ownFamily(options.cwd, () => executeCommandFamily(command, args, options));
}
function removeFixture(fixture) {
  if (pendingFamilies.get(fixture.root)) {
    process.stderr.write('Runtime-family fixture retained: actual work is still unjoined\n');
    return;
  }
  pendingFamilies.delete(fixture.root);
  rmSync(fixture.root, { recursive: true, force: true });
}
async function waitUntil(predicate, ms = 3000) {
  const end = Date.now() + ms;
  while (!predicate()) {
    if (Date.now() > end) throw new Error('inert control timed out');
    await delay(10);
  }
}
function fixture() {
  const root = mkdtempSync(join(tmpdir(), 'learnstack-runtime-family-'));
  const entry = join(root, 'child.mjs');
  const marker = join(root, 'ready');
  writeFileSync(
    entry,
    `
import { createServer } from 'node:net';
import { writeFileSync, existsSync } from 'node:fs';
import { spawn } from 'node:child_process';
import { createFrameworkRuntimeChildControl } from ${JSON.stringify(controlUrl)};
const challenge = process.env.LEARNSTACK_RUNTIME_CHALLENGE;
const incarnation = process.env.LEARNSTACK_RUNTIME_INCARNATION;
const owner = createFrameworkRuntimeChildControl();
if (process.env.TEST_MODE === 'wrong-hello')
  process.send({tag:'learnstack-runtime-v1',kind:'hello',challenge:'wrong'});
if (process.env.TEST_MODE === 'premature-frame') process.send({tag:'unrecognized'});
if (process.env.TEST_MODE === 'delayed-hello')
  await new Promise(resolve => setTimeout(resolve, 250));
if (process.env.TEST_MODE === 'prehello-held') {
  writeFileSync(process.env.TEST_MARKER + '.installed', 'installed');
  await new Promise(resolve => setTimeout(resolve, 6500));
}
if (process.env.TEST_MODE === 'late-hello-race') {
  let stops = 0;
  process.on('message', (frame) => {
    if (frame?.kind === 'stop') writeFileSync(process.env.TEST_MARKER + '.stops', String(++stops));
    if (frame?.kind === 'start') writeFileSync(process.env.TEST_MARKER + '.start', 'start');
  });
  await new Promise(resolve => setTimeout(resolve, 250));
  process.send({tag:'learnstack-runtime-v1',kind:'hello',challenge,pid:process.pid,
    parentPid:owner.binding.parentPid,mode:owner.binding.mode,incarnation});
  await new Promise(resolve => setTimeout(resolve, 200));
}
let watchdogHook;
let proofReadyObservation;
const proof = ['proof','proof-receive-async','proof-drain','proof-drain-reject','watchdog-proof'].includes(process.env.TEST_MODE) ? {
  attach({send,incarnation}) {
    if (process.env.TEST_MODE === 'watchdog-proof') proofReadyObservation = () =>
      send({tag:'learnstack-proof-v1',incarnation,payload:{kind:'listener-ready-observed'}});
    send({tag:'learnstack-proof-v1',incarnation,payload:{kind:'child-ready'}});
    return { receive(frame) {
      if (frame.payload?.kind !== 'parent-ack') throw new Error('invalid proof reply');
      if (process.env.TEST_MODE === 'proof-receive-async')
        return new Promise(resolve => setTimeout(resolve, 600));
      writeFileSync(process.env.TEST_MARKER + '.proof', 'proof');
    }, emit() {}, onCommand() {}, onWatchdog(fn) { watchdogHook = fn; },
    watchdog() { watchdogHook?.(); }, drain() {
      if (!['proof-drain','proof-drain-reject'].includes(process.env.TEST_MODE)) return;
      return new Promise((resolve, reject) => setTimeout(() => {
        if (process.env.TEST_MODE === 'proof-drain-reject') reject(new Error('proof work failed'));
        else resolve();
      }, 600));
    } };
  }
} : process.env.TEST_MODE === 'proof-async' ? {
  attach() { return new Promise(resolve => setTimeout(resolve, 600)); }
} : undefined;
const control = await owner.admit(Object.freeze({mode:owner.binding.mode,identity:Object.freeze({})}), {proof});
if (control) {
  if (process.env.TEST_MODE === 'pre-ready-zero') process.exit(0);
  if (['proof','proof-receive-async','proof-drain','proof-drain-reject','watchdog-proof'].includes(process.env.TEST_MODE)) {
    if (!Object.isFrozen(control.proof) || 'receive' in control.proof || 'drain' in control.proof)
      throw new Error('proof handoff was not confined');
  } else if (Object.hasOwn(control, 'proof')) throw new Error('unexpected proof capability');
  if (process.env.TEST_MODE === 'unknown-frame') process.send({tag:'unknown'});
  if (process.env.TEST_MODE === 'registration-held') {
    writeFileSync(process.env.TEST_MARKER, 'before registration');
    await new Promise(resolve => setTimeout(resolve, 6500));
  }
  if (process.env.TEST_MODE === 'descendant')
    spawn(process.execPath, ['-e', 'setTimeout(() => {}, 1600)'], {stdio:'ignore'});
  const server = createServer();
  if (process.env.TEST_MODE === 'watchdog-proof') {
    if (typeof control.proof.onWatchdog === 'function')
      control.proof.onWatchdog(() => writeFileSync(process.env.TEST_MARKER + '.watchdog', 'watchdog'));
    else writeFileSync(process.env.TEST_MARKER + '.missing-hook', 'missing');
  }
  control.registerShutdown(async () => {
    if (['watchdog','watchdog-proof'].includes(process.env.TEST_MODE)) await new Promise(() => {});
    await new Promise((resolve, reject) => server.close(error => error ? reject(error) : resolve()));
  });
  if (process.env.TEST_MODE === 'prebind-held') {
    writeFileSync(process.env.TEST_MARKER + '.prebind', 'prebind');
    const deadline = Date.now() + 3000;
    while (!existsSync(process.env.TEST_MARKER + '.release') && Date.now() < deadline)
      Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, 5);
    if (!existsSync(process.env.TEST_MARKER + '.release')) throw new Error('missing release');
  }
  if (process.env.TEST_MODE !== 'pre-ready') server.listen(0, '127.0.0.1', () => {
    if (control.listenerReady(server)) writeFileSync(process.env.TEST_MARKER, 'ready');
    proofReadyObservation?.();
    if (process.env.TEST_MODE === 'unrequested-zero') process.exit(0);
  });
  else writeFileSync(process.env.TEST_MARKER, 'prepared');
}
`,
  );
  const options = (signal, mode = 'ready', onFamily) => ({
    entry,
    cwd: root,
    environment: { TEST_MODE: mode, TEST_MARKER: marker },
    mode: 'production',
    leaseRecord: { format: 1, kind: 'production', pid: process.pid, nonce },
    signal,
    onFamily,
  });
  return { root, entry, marker, options };
}

test('pre-aborted runtime and generic command do not spawn', async () => {
  const f = fixture();
  try {
    const abort = new AbortController();
    abort.abort();
    await assert.rejects(runFrameworkRuntimeFamily(f.options(abort.signal)));
    await assert.rejects(
      runFrameworkProcessFamily(process.execPath, [f.entry], {
        cwd: f.root,
        environment: {},
        stdio: 'ignore',
        signal: abort.signal,
      }),
    );
    assert.equal(existsSync(f.marker), false);
  } finally {
    removeFixture(f);
  }
});

test('authenticated real listener and graceful stop have a retired final receipt', async () => {
  const f = fixture();
  try {
    const abort = new AbortController();
    let handle;
    const result = runFrameworkRuntimeFamily(
      f.options(abort.signal, 'ready', (value) => {
        handle = value;
      }),
    );
    await waitUntil(() => existsSync(f.marker));
    assert.ok(Object.isFrozen(handle));
    assert.equal(handle.pid, handle.group);
    assert.equal(handle.parentPid, process.pid);
    assert.equal(handle.leaseNonce, nonce);
    assert.equal('challenge' in handle, false);
    abort.abort();
    const receipt = await result;
    assert.deepEqual(receipt, {
      code: 0,
      signal: null,
      handshakeValidated: true,
      listenerReady: true,
      requestedStop: true,
      startupCancelled: false,
      parentLost: false,
      fault: null,
      directChildClosed: true,
      groupAbsent: true,
    });
    assert.ok(Object.isFrozen(receipt));
  } finally {
    removeFixture(f);
  }
});

test('stop before a listener is a failed startup even after actual retirement', async () => {
  const f = fixture();
  try {
    const abort = new AbortController();
    const result = runFrameworkRuntimeFamily(f.options(abort.signal, 'pre-ready'));
    await waitUntil(() => existsSync(f.marker));
    abort.abort();
    const receipt = await result;
    assert.equal(receipt.startupCancelled, true);
    assert.equal(receipt.listenerReady, false);
    assert.equal(receipt.groupAbsent, true);
    assert.notEqual(receipt.code, 0);
  } finally {
    removeFixture(f);
  }
});

test('first-stop watchdog fails stuck shutdown and notifies the copied-only observer', async () => {
  const fixtures = [fixture(), fixture()];
  try {
    const aborts = fixtures.map(() => new AbortController());
    let readyObserved = false;
    const proof = {
      attach({ send, incarnation }) {
        return {
          receive(frame) {
            if (frame.payload.kind === 'listener-ready-observed') {
              readyObserved = true;
              return;
            }
            assert.equal(frame.payload.kind, 'child-ready');
            send({ tag: 'learnstack-proof-v1', incarnation, payload: { kind: 'parent-ack' } });
          },
        };
      },
    };
    const results = [
      runFrameworkRuntimeFamily(fixtures[0].options(aborts[0].signal, 'watchdog')),
      runFrameworkRuntimeFamily({
        ...fixtures[1].options(aborts[1].signal, 'watchdog-proof'),
        proof,
      }),
    ];
    await waitUntil(
      () =>
        fixtures.every((f) => existsSync(f.marker)) &&
        existsSync(fixtures[1].marker + '.proof') &&
        readyObserved,
    );
    const start = Date.now();
    for (const abort of aborts) abort.abort();
    const receipts = await Promise.all(results);
    assert.ok(Date.now() - start >= 4700);
    for (const receipt of receipts) {
      assert.notEqual(receipt.code, 0);
      assert.equal(receipt.directChildClosed, true);
      assert.equal(receipt.groupAbsent, true);
      assert.notEqual(receipt.fault, null);
    }
    // The same child channel delivers actual ready before its proof observation.
    // A missing terminal must not erase that already accepted readiness.
    assert.equal(receipts[1].listenerReady, true);
    assert.equal(receipts[1].requestedStop, true);
    assert.equal(receipts[1].startupCancelled, false);
    assert.equal(existsSync(fixtures[1].marker + '.watchdog'), true);
    assert.equal(existsSync(fixtures[1].marker + '.missing-hook'), false);
  } finally {
    for (const f of fixtures) removeFixture(f);
  }
});

test('stop before registration uses the original watchdog deadline', async () => {
  const f = fixture();
  try {
    const abort = new AbortController();
    const result = runFrameworkRuntimeFamily(f.options(abort.signal, 'registration-held'));
    await waitUntil(() => existsSync(f.marker));
    const start = Date.now();
    abort.abort();
    const receipt = await result;
    const elapsed = Date.now() - start;
    assert.ok(elapsed >= 4700 && elapsed < 6400, `watchdog took ${elapsed} ms`);
    assert.notEqual(receipt.code, 0);
    assert.equal(receipt.startupCancelled, true);
    assert.equal(receipt.groupAbsent, true);
  } finally {
    removeFixture(f);
  }
});

test('a real descendant keeps the final receipt pending after the direct stop', async () => {
  const f = fixture();
  try {
    const abort = new AbortController();
    let completed = false;
    const result = runFrameworkRuntimeFamily(f.options(abort.signal, 'descendant')).then(
      (receipt) => {
        completed = true;
        return receipt;
      },
    );
    await waitUntil(() => existsSync(f.marker));
    abort.abort();
    await delay(100);
    assert.equal(completed, false);
    const receipt = await result;
    assert.equal(receipt.code, 0);
    assert.equal(receipt.groupAbsent, true);
    assert.equal(receipt.fault, null);
  } finally {
    removeFixture(f);
  }
});

test('generic family observer is synchronous and cancellation still fails', async () => {
  const f = fixture();
  try {
    const entry = join(f.root, 'generic.mjs');
    writeFileSync(entry, 'setInterval(() => {}, 1000);\n');
    let handle;
    await assert.rejects(
      runFrameworkProcessFamily(process.execPath, [entry], {
        cwd: f.root,
        environment: {},
        stdio: 'ignore',
        onFamily(value) {
          handle = value;
          return Promise.resolve();
        },
      }),
    );
    assert.ok(Object.isFrozen(handle));
    assert.equal(handle.pid, handle.group);
    assert.equal(handle.parentPid, process.pid);
    assert.equal('nonce' in handle, false);
  } finally {
    removeFixture(f);
  }
});

test('generic cancellation remains failed after a zero exit and held descendant', async () => {
  const f = fixture();
  try {
    const grandchild = join(f.root, 'generic-descendant.mjs');
    const entry = join(f.root, 'generic-parent.mjs');
    writeFileSync(
      grandchild,
      `
import { writeFileSync } from 'node:fs';
process.on('SIGTERM', () => {});
writeFileSync(process.env.TEST_MARKER, 'alive');
setTimeout(() => {}, 1600);
`,
    );
    writeFileSync(
      entry,
      `
import { spawn } from 'node:child_process';
spawn(process.execPath, [${JSON.stringify(grandchild)}], {stdio:'ignore'});
process.on('SIGTERM', () => process.exit(0));
setInterval(() => {}, 1000);
`,
    );
    const abort = new AbortController();
    let settled = false;
    const result = runFrameworkProcessFamily(process.execPath, [entry], {
      cwd: f.root,
      environment: { TEST_MARKER: f.marker },
      stdio: 'ignore',
      signal: abort.signal,
    }).then(
      () => {
        settled = true;
        return false;
      },
      () => {
        settled = true;
        return true;
      },
    );
    await waitUntil(() => existsSync(f.marker));
    abort.abort();
    await delay(100);
    assert.equal(settled, false);
    assert.equal(await result, true);
  } finally {
    removeFixture(f);
  }
});

test('a fixed proof adapter routes its separately tagged incarnation-bound frames', async () => {
  const f = fixture();
  try {
    const abort = new AbortController();
    const proof = {
      attach({ send, incarnation }) {
        return {
          receive(frame) {
            assert.equal(frame.payload.kind, 'child-ready');
            send({ tag: 'learnstack-proof-v1', incarnation, payload: { kind: 'parent-ack' } });
          },
        };
      },
    };
    const result = runFrameworkRuntimeFamily({ ...f.options(abort.signal, 'proof'), proof });
    await waitUntil(() => existsSync(f.marker) && existsSync(f.marker + '.proof'));
    abort.abort();
    const receipt = await result;
    assert.equal(receipt.code, 0);
    assert.equal(receipt.fault, null);
  } finally {
    removeFixture(f);
  }
});

test('an unknown ownership-channel message fails and still joins', async () => {
  const f = fixture();
  try {
    const result = runFrameworkRuntimeFamily(f.options(undefined, 'unknown-frame'));
    const receipt = await result;
    assert.equal(receipt.fault, 'protocol-fault');
    assert.equal(receipt.groupAbsent, true);
  } finally {
    removeFixture(f);
  }
});

test('runtime observer failure requests stop and joins before returning failure', async () => {
  const f = fixture();
  try {
    const receipt = await runFrameworkRuntimeFamily(
      f.options(undefined, 'ready', () => {
        throw new Error('observer failure');
      }),
    );
    assert.equal(receipt.fault, 'observer-fault');
    assert.equal(receipt.startupCancelled, true);
    assert.equal(receipt.directChildClosed, true);
    assert.equal(receipt.groupAbsent, true);
    assert.equal(existsSync(f.marker), false);
  } finally {
    removeFixture(f);
  }
});

test('malformed pre-hello frames stop the exact child and still join', async () => {
  for (const mode of ['wrong-hello', 'premature-frame']) {
    const f = fixture();
    try {
      const receipt = await runFrameworkRuntimeFamily(f.options(undefined, mode));
      assert.equal(receipt.fault, 'protocol-fault');
      assert.equal(receipt.handshakeValidated, false);
      assert.equal(receipt.directChildClosed, true);
      assert.equal(receipt.groupAbsent, true);
      assert.equal(existsSync(f.marker), false);
    } finally {
      removeFixture(f);
    }
  }
});

test('a legitimate stop before hello latches before admission and fails startup', async () => {
  const f = fixture();
  try {
    const abort = new AbortController();
    const result = runFrameworkRuntimeFamily(f.options(abort.signal, 'delayed-hello'));
    await delay(30);
    abort.abort();
    const receipt = await result;
    assert.equal(receipt.handshakeValidated, false);
    assert.equal(receipt.startupCancelled, true);
    assert.equal(receipt.fault, null);
    assert.equal(receipt.groupAbsent, true);
    assert.equal(existsSync(f.marker), false);
  } finally {
    removeFixture(f);
  }
});

test('uncancelled delayed hello permits actual readiness and graceful stop', async () => {
  const f = fixture();
  try {
    const abort = new AbortController();
    const result = runFrameworkRuntimeFamily(f.options(abort.signal, 'delayed-hello'));
    await waitUntil(() => existsSync(f.marker));
    abort.abort();
    const receipt = await result;
    assert.equal(receipt.handshakeValidated, true);
    assert.equal(receipt.listenerReady, true);
    assert.equal(receipt.startupCancelled, false);
    assert.equal(receipt.code, 0);
    assert.equal(receipt.fault, null);
    assert.equal(receipt.groupAbsent, true);
  } finally {
    removeFixture(f);
  }
});

test('a late authenticated hello receives the pending stop again and never starts', async () => {
  const f = fixture();
  try {
    const abort = new AbortController();
    const result = runFrameworkRuntimeFamily(f.options(abort.signal, 'late-hello-race'));
    await delay(30);
    abort.abort();
    const receipt = await result;
    assert.equal(receipt.handshakeValidated, true);
    assert.equal(receipt.startupCancelled, true);
    assert.equal(receipt.groupAbsent, true);
    assert.equal(readFileSync(f.marker + '.stops', 'utf8'), '2');
    assert.equal(existsSync(f.marker + '.start'), false);
    assert.equal(existsSync(f.marker), false);
  } finally {
    removeFixture(f);
  }
});

test('runtime observer returned work remains joined after the family stops', async () => {
  const f = fixture();
  let release;
  try {
    const work = new Promise((resolve) => {
      release = resolve;
    });
    let called = false;
    let settled = false;
    const result = runFrameworkRuntimeFamily(
      f.options(undefined, 'ready', () => {
        called = true;
        return work;
      }),
    ).then((receipt) => {
      settled = true;
      return receipt;
    });
    await waitUntil(() => called);
    await delay(150);
    assert.equal(settled, false);
    release();
    const receipt = await result;
    assert.equal(receipt.fault, 'observer-fault');
    assert.equal(receipt.groupAbsent, true);
  } finally {
    release?.();
    removeFixture(f);
  }
});

test('generic observer returned work remains joined after the family stops', async () => {
  const f = fixture();
  let release;
  try {
    const work = new Promise((resolve) => {
      release = resolve;
    });
    const entry = join(f.root, 'generic-async-observer.mjs');
    writeFileSync(entry, 'setInterval(() => {}, 1000);\n');
    let called = false;
    let settled = false;
    const result = runFrameworkProcessFamily(process.execPath, [entry], {
      cwd: f.root,
      environment: {},
      stdio: 'ignore',
      onFamily() {
        called = true;
        return work;
      },
    }).then(
      () => {
        settled = true;
        return false;
      },
      () => {
        settled = true;
        return true;
      },
    );
    await waitUntil(() => called);
    await delay(150);
    assert.equal(settled, false);
    release();
    assert.equal(await result, true);
  } finally {
    release?.();
    removeFixture(f);
  }
});

test('child async proof attachment finishes its work before normal failure', async () => {
  const f = fixture();
  try {
    const start = Date.now();
    const receipt = await runFrameworkRuntimeFamily(f.options(undefined, 'proof-async'));
    assert.ok(Date.now() - start >= 550);
    assert.notEqual(receipt.code, 0);
    assert.equal(receipt.fault, 'protocol-fault');
    assert.equal(receipt.groupAbsent, true);
  } finally {
    removeFixture(f);
  }
});

test('parent async proof attachment remains joined after the family stops', async () => {
  const f = fixture();
  let release;
  try {
    const work = new Promise((resolve) => {
      release = resolve;
    });
    let called = false;
    let settled = false;
    const proof = {
      attach() {
        called = true;
        return work;
      },
    };
    const result = runFrameworkRuntimeFamily({ ...f.options(undefined), proof }).then((receipt) => {
      settled = true;
      return receipt;
    });
    await waitUntil(() => called);
    await delay(150);
    assert.equal(settled, false);
    release();
    const receipt = await result;
    assert.equal(receipt.fault, 'protocol-fault');
    assert.equal(receipt.groupAbsent, true);
  } finally {
    release?.();
    removeFixture(f);
  }
});

test('parent async proof receive remains joined after the family stops', async () => {
  const f = fixture();
  let release;
  try {
    const work = new Promise((resolve) => {
      release = resolve;
    });
    let called = false;
    let settled = false;
    const proof = {
      attach() {
        return {
          receive() {
            called = true;
            return work;
          },
        };
      },
    };
    const result = runFrameworkRuntimeFamily({ ...f.options(undefined, 'proof'), proof }).then(
      (receipt) => {
        settled = true;
        return receipt;
      },
    );
    await waitUntil(() => called);
    await delay(150);
    assert.equal(settled, false);
    release();
    const receipt = await result;
    assert.equal(receipt.fault, 'protocol-fault');
    assert.equal(receipt.groupAbsent, true);
  } finally {
    release?.();
    removeFixture(f);
  }
});

test('child async proof receive completes before its failure terminal', async () => {
  const f = fixture();
  try {
    const proof = {
      attach({ send, incarnation }) {
        return {
          receive(frame) {
            assert.equal(frame.payload.kind, 'child-ready');
            send({ tag: 'learnstack-proof-v1', incarnation, payload: { kind: 'parent-ack' } });
          },
        };
      },
    };
    const start = Date.now();
    const receipt = await runFrameworkRuntimeFamily({
      ...f.options(undefined, 'proof-receive-async'),
      proof,
    });
    assert.ok(Date.now() - start >= 550);
    assert.equal(receipt.fault, 'protocol-fault');
    assert.equal(receipt.groupAbsent, true);
  } finally {
    removeFixture(f);
  }
});

test('private proof drain joins held command work and preserves its failure', async () => {
  for (const mode of ['proof-drain', 'proof-drain-reject']) {
    const f = fixture();
    try {
      const abort = new AbortController();
      const proof = {
        attach({ send, incarnation }) {
          return {
            receive(frame) {
              assert.equal(frame.payload.kind, 'child-ready');
              send({ tag: 'learnstack-proof-v1', incarnation, payload: { kind: 'parent-ack' } });
            },
          };
        },
      };
      let settled = false;
      const result = runFrameworkRuntimeFamily({ ...f.options(abort.signal, mode), proof }).then(
        (receipt) => {
          settled = true;
          return receipt;
        },
      );
      await waitUntil(() => existsSync(f.marker) && existsSync(f.marker + '.proof'));
      const start = Date.now();
      abort.abort();
      await delay(150);
      assert.equal(settled, false);
      const receipt = await result;
      assert.ok(Date.now() - start >= 550);
      assert.equal(receipt.code, mode === 'proof-drain' ? 0 : 1);
      assert.equal(receipt.fault, mode === 'proof-drain' ? null : 'protocol-fault');
      assert.equal(receipt.directChildClosed, true);
      assert.equal(receipt.groupAbsent, true);
    } finally {
      removeFixture(f);
    }
  }
});

test('zero exit before readiness or without requested stop remains a failure observation', async () => {
  for (const mode of ['pre-ready-zero', 'unrequested-zero']) {
    const f = fixture();
    try {
      const receipt = await runFrameworkRuntimeFamily(f.options(undefined, mode));
      assert.equal(receipt.code, 0);
      assert.equal(receipt.listenerReady, mode === 'unrequested-zero');
      assert.equal(receipt.requestedStop, false);
      assert.notEqual(receipt.fault, null);
      assert.equal(receipt.directChildClosed, true);
      assert.equal(receipt.groupAbsent, true);
    } finally {
      removeFixture(f);
    }
  }
});

test('pre-HELLO SIGTERM and SIGINT start the first-stop watchdog while validation is held', async () => {
  const fixtures = [fixture(), fixture()];
  const signals = ['SIGTERM', 'SIGINT'];
  const handlers = [];
  let results;
  try {
    const aborts = fixtures.map(() => new AbortController());
    const promises = fixtures.map((f, index) =>
      runFrameworkRuntimeFamily(f.options(aborts[index].signal, 'prehello-held')),
    );
    results = Promise.all(promises);
    await waitUntil(() => fixtures.every((f) => existsSync(f.marker + '.installed')));
    for (let index = 0; index < signals.length; index++) {
      const handler = () => aborts[index].abort(signals[index]);
      handlers.push(handler);
      process.once(signals[index], handler);
    }
    const start = Date.now();
    for (const signal of signals) process.kill(process.pid, signal);
    let timer;
    const beforeDeadline = await Promise.race([
      results.then(() => true),
      new Promise((resolve) => {
        timer = setTimeout(() => resolve(false), 5900);
      }),
    ]);
    clearTimeout(timer);
    const receipts = await results; // Even the red case must join both actual children.
    assert.equal(beforeDeadline, true, 'pre-HELLO stop did not start the original watchdog');
    assert.ok(Date.now() - start >= 4700);
    for (const receipt of receipts) {
      assert.equal(receipt.handshakeValidated, false);
      assert.equal(receipt.startupCancelled, true);
      assert.notEqual(receipt.code, 0);
      assert.equal(receipt.directChildClosed, true);
      assert.equal(receipt.groupAbsent, true);
    }
    assert.ok(fixtures.every((f) => !existsSync(f.marker)));
  } finally {
    for (let index = 0; index < handlers.length; index++)
      process.removeListener(signals[index], handlers[index]);
    if (results) await results.catch(() => {});
    for (const f of fixtures) removeFixture(f);
  }
});

test('pre-bind parent stop remains startup cancellation after a later real bind', async () => {
  const f = fixture();
  let result;
  try {
    const abort = new AbortController();
    result = runFrameworkRuntimeFamily(f.options(abort.signal, 'prebind-held'));
    await waitUntil(() => existsSync(f.marker + '.prebind'));
    abort.abort('SIGTERM');
    await delay(40);
    writeFileSync(f.marker + '.release', 'release');
    const receipt = await result;
    assert.equal(receipt.listenerReady, true, 'actual loopback listener bound after the stop');
    assert.equal(receipt.startupCancelled, true);
    assert.equal(receipt.directChildClosed, true);
    assert.equal(receipt.groupAbsent, true);
    const graceful =
      receipt.handshakeValidated &&
      receipt.listenerReady &&
      receipt.requestedStop &&
      !receipt.startupCancelled &&
      !receipt.parentLost &&
      receipt.fault === null &&
      receipt.code === 0 &&
      receipt.signal === null &&
      receipt.directChildClosed &&
      receipt.groupAbsent;
    assert.equal(graceful, false);
  } finally {
    writeFileSync(f.marker + '.release', 'release');
    if (result) await result.catch(() => {});
    removeFixture(f);
  }
});
