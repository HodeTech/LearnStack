// Actual Next preparation and initialized worker controls in one private fixture builder.
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import {
  existsSync,
  mkdirSync,
  mkdtempSync,
  readFileSync,
  realpathSync,
  writeFileSync,
  writeSync,
} from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { prepareBffProof } from './bff-proof-host.mjs';
import { assertBffPrivateOutput } from './bff-proof-support.mjs';
import { createFixtureOwner } from './fixture-support.mjs';
import { runFrameworkOwnershipMatrix } from './framework-ownership-matrix.mjs';
import { stopTestChild } from './stop-test-child.mjs';

const selected = process.argv[2];
try {
  if (['startup', 'workers', 'recovery'].includes(selected)) {
    const privateEntry = process.argv[3] === '--qualified-private';
    assert.equal(process.argv.length, privateEntry ? 4 : 3, 'Closed ownership matrix arguments');
    await runFrameworkOwnershipMatrix(selected, privateEntry);
  } else {
    await runInitialOwnershipControl();
  }
} catch {
  process.stderr.write('Framework ownership proof failed: arguments\n');
  process.exitCode = 1;
}

async function runInitialOwnershipControl() {
  const parentPipe = process.argv[3] === '--parent-pipe';
  let parentEOF = false;
  if (parentPipe)
    process.stdin.once('end', () => {
      parentEOF = true;
    });
  let pipeEvents;
  const owner = createFixtureOwner({
    exit(code) {
      if (parentPipe) {
        const actual = running?.server;
        const complete =
          parentEOF &&
          code === 1 &&
          actual?.process.exitCode === 1 &&
          actual.process.signalCode === null &&
          actual.broker.capture.retired() &&
          !existsSync(root) &&
          JSON.stringify(pipeEvents?.map((event) => event.event)) ===
            JSON.stringify([
              'prepare-called',
              'configuration-entered',
              'stop-consumed',
              'configuration-released',
              'prepare-settled',
              'next-close-called',
              'next-close-settled',
            ]) &&
          pipeEvents.every(
            (event) =>
              event.pid === actual.broker.capture.family()?.pid &&
              event.parentPid === actual.process.pid,
          );
        try {
          writeSync(
            1,
            JSON.stringify(
              complete
                ? { protocol: 1, phase: 'closed', ownerCode: 1, retired: true, cleanup: true }
                : { protocol: 1, phase: 'failed' },
            ) + '\n',
          );
        } catch {
          /* The unchanged cancellation exit still fails. */
        }
      }
      process.exit(code);
    },
  });
  owner.install(parentPipe ? { control: process.stdin, event: 'end' } : {});
  const root = owner.ownRoot(
    realpathSync(mkdtempSync(join(tmpdir(), 'learnstack-framework-ownership-'))),
  );
  const directory = join(root, 'ownership-control');
  mkdirSync(directory, { mode: 0o700 });
  writeFileSync(join(directory, 'events.jsonl'), '', { mode: 0o600 });
  const secret = randomBytes(32).toString('base64url');
  const marker = randomBytes(32).toString('base64url');
  const ownershipCase =
    process.argv[2] === 'prepare-parent-eof' ? 'prepare-sigterm' : process.argv[2];
  const pending = [];
  let stage = 'arguments';
  let running;
  let released = false;
  const deadline = setTimeout(() => void owner.cancel(), 120_000);
  deadline.unref();
  const own = (promise) => {
    pending.push(promise);
    void promise.catch(() => {});
    return promise;
  };
  function control(name) {
    writeFileSync(join(directory, name), '', { flag: 'wx', mode: 0o600 });
  }
  function events() {
    const contents = readFileSync(join(directory, 'events.jsonl'), 'utf8');
    assert.ok(contents.length <= 4096, 'bounded ownership observations');
    const observed = contents.trim()
      ? contents
          .trim()
          .split('\n')
          .map((line) => JSON.parse(line))
      : [];
    const allowed = [
      'prepare-called',
      'prepare-settled',
      'configuration-entered',
      'configuration-released',
      'stop-consumed',
      'next-close-called',
      'next-close-settled',
      'native-listen',
      'worker-entered',
      'worker-returned',
    ];
    for (const event of observed) {
      assert.deepEqual(Object.keys(event).sort(), ['event', 'parentPid', 'pid']);
      assert.ok(allowed.includes(event.event), 'finite ownership event');
      assert.ok(Number.isSafeInteger(event.pid) && event.pid > 1);
      assert.ok(Number.isSafeInteger(event.parentPid) && event.parentPid > 0);
    }
    return observed;
  }
  async function until(predicate) {
    while (!predicate()) {
      owner.assertActive();
      if (running)
        assert.equal(running.server.process.exitCode, null, 'live owner until observation');
      await new Promise((resolve) => setTimeout(resolve, 10));
    }
  }
  function release() {
    if (released) return;
    released = true;
    control(ownershipCase === 'prepare-sigterm' ? 'prepare.release' : 'worker.release');
  }
  function absent(pid) {
    try {
      process.kill(pid, 0);
      return false;
    } catch (error) {
      if (error.code === 'ESRCH') return true;
      throw error;
    }
  }
  try {
    assert.equal(process.argv.length, parentPipe ? 4 : 3, 'one closed ownership case');
    assert.equal(
      parentPipe,
      process.argv[2] === 'prepare-parent-eof',
      'closed parent pipe selector',
    );
    assert.ok(['prepare-sigterm', 'worker-positive'].includes(ownershipCase));
    if (parentPipe)
      await new Promise((resolve, reject) => {
        let input = '';
        let started = false;
        process.stdin.on('data', (chunk) => {
          if (started) {
            void owner.cancel();
            return;
          }
          input += chunk.toString('utf8');
          if (input.length > 128) {
            reject(new Error('Parent start refused'));
            return;
          }
          if (!input.includes('\n')) return;
          if (input !== '{"protocol":1,"command":"start"}\n') {
            reject(new Error('Parent start refused'));
            return;
          }
          started = true;
          resolve();
        });
        process.stdin.resume();
      });
    stage = 'prepare-fixture';
    const prepared = await prepareBffProof({
      owner,
      root,
      privateValues: [secret, marker],
      secret,
      marker,
      ownershipCase,
      devMode: ownershipCase === 'worker-positive',
      isolatedProtocol: true,
      mark: (value) => {
        stage = value;
      },
    });
    if (ownershipCase === 'prepare-sigterm') control('prepare.arm');
    running = prepared.launch();
    own(running.ready);
    const { server } = running;
    let workerObserved = false;
    let workerIdentity = null;
    let settlement;
    if (ownershipCase === 'prepare-sigterm') {
      stage = 'held-real-prepare';
      await until(() => events().some((event) => event.event === 'configuration-entered'));
      const family = server.broker.capture.family();
      assert.ok(family, 'authenticated family precedes held preparation');
      assert.equal(
        events().some((event) => event.event === 'prepare-settled'),
        false,
      );
      if (parentPipe) {
        own(
          server.completed.then(() => {
            pipeEvents = events();
          }),
        );
        own(
          (async () => {
            // Cancellation owns disposal; this observer releases only after the child consumes stop.
            while (!events().some((event) => event.event === 'stop-consumed'))
              await new Promise((resolve) => setTimeout(resolve, 10));
            release();
          })(),
        );
        process.stdout.write('{"protocol":1,"phase":"prepare-held"}\n');
        await new Promise(() => {}); // Parent EOF or the existing overall cancellation owns exit.
      }
      stage = 'child-consumed-stop';
      const stopping = own(stopTestChild(server.process));
      await until(() => events().some((event) => event.event === 'stop-consumed'));
      assert.equal(
        events().some((event) => event.event === 'prepare-settled'),
        false,
      );
      release();
      stage = 'prepare-and-close-join';
      settlement = await stopping;
      await assert.rejects(running.ready);
      const observed = events();
      const expected = [
        'prepare-called',
        'configuration-entered',
        'stop-consumed',
        'configuration-released',
        'prepare-settled',
        'next-close-called',
        'next-close-settled',
      ];
      assert.deepEqual(
        observed.map((event) => event.event),
        expected,
      );
      assert.ok(
        observed.every(
          (event) => event.pid === family.pid && event.parentPid === server.process.pid,
        ),
      );
      assert.equal(settlement.ownerCode, 1, 'pre-ready stop remains failed startup');
    } else {
      stage = 'real-listener';
      await running.ready;
      const response = await own(running.get('/'));
      assert.equal(response.status, 200);
      assert.ok(response.body.includes('Public'));
      control('worker.arm');
      stage = 'initialized-static-paths-worker';
      let requestSettled = false;
      const request = own(
        running.get('/en/courses/ownership').finally(() => {
          requestSettled = true;
        }),
      );
      await until(() => events().some((event) => event.event === 'worker-entered'));
      const worker = events().find((event) => event.event === 'worker-entered');
      const family = server.broker.capture.family();
      assert.ok(Number.isSafeInteger(worker.pid) && worker.pid > 1 && worker.pid !== family.pid);
      assert.equal(worker.parentPid, family.pid, 'initialized worker is an actual native child');
      const processIdentity = execFileSync(
        '/bin/ps',
        ['-o', 'pid=,ppid=,pgid=', '-p', String(worker.pid)],
        { encoding: 'utf8', timeout: 5000 },
      )
        .trim()
        .split(/\s+/)
        .map(Number);
      assert.deepEqual(
        processIdentity,
        [worker.pid, family.pid, family.group],
        'worker inherits the captured native family',
      );
      workerIdentity = processIdentity;
      assert.equal(requestSettled, false, 'actual request waits for initialized worker');
      workerObserved = true;
      release();
      stage = 'worker-result-and-exit';
      const page = await request;
      assert.equal(page.status, 200);
      assert.ok(page.body.includes('Owned static paths worker'));
      assert.equal(
        events().filter((event) => event.event === 'worker-returned' && event.pid === worker.pid)
          .length,
        1,
      );
      await until(() => absent(worker.pid));
      stage = 'ready-owner-stop';
      settlement = await own(stopTestChild(server.process));
      assert.equal(settlement.ownerCode, 0, 'ready owner stops successfully');
    }
    stage = 'all-family-retired';
    assert.equal(settlement.forced, false);
    assert.equal(settlement.ownerSignal, null);
    assert.equal(server.broker.capture.retired(), true);
    const closed = await server.completed;
    assert.equal(closed.code, settlement.ownerCode);
    assert.equal(closed.signal, null);
    server.broker.assertHealthy();
    assertBffPrivateOutput(running.scanners);
    const eventEvidence = events(); // Preserve actual bounded records before owner disposal removes them.
    await owner.dispose();
    assert.equal(existsSync(root), false, 'confirmed cleanup removes the private fixture');
    assertBffPrivateOutput(running.scanners);
    clearTimeout(deadline);
    process.stdout.write(
      JSON.stringify({
        case: ownershipCase,
        ownerCode: settlement.ownerCode,
        forced: false,
        familyRetired: true,
        initializedWorker: workerObserved,
        workerIdentity,
        events: eventEvidence,
        privateCleanup: true,
      }) + '\n',
    );
  } catch {
    let clean = true;
    let eventEvidence;
    try {
      eventEvidence = events();
    } catch {
      /* Invalid observations are never emitted. */
    }
    try {
      if (running && !released) release();
    } catch {
      clean = false;
    }
    try {
      await owner.dispose();
    } catch {
      clean = false;
    }
    await Promise.allSettled(pending);
    clearTimeout(deadline);
    process.stderr.write('Framework ownership proof failed: ' + stage + '\n');
    if (eventEvidence)
      process.stderr.write('Framework ownership events: ' + JSON.stringify(eventEvidence) + '\n');
    if (!clean) process.stderr.write('Framework ownership cleanup failed.\n');
    process.exitCode = 1;
  }
}
