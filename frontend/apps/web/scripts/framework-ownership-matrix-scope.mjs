// Expected fatal-owner experiments have a separate disposition from ordinary fixture cleanup.
import assert from 'node:assert/strict';
import {
  closeSync,
  constants,
  fstatSync,
  lstatSync,
  openSync,
  readSync,
  readdirSync,
  realpathSync,
  rmSync,
} from 'node:fs';
import { basename, join } from 'node:path';
import { fileURLToPath } from 'node:url';

import { assertNoRetainedFrameworkOwners, createFixtureOwner } from './fixture-support.mjs';
import { stopTestChild } from './stop-test-child.mjs';

/** Final leak observation only; unrelated shared readers may change concurrently. */
export function assertNoCanonicalOwnershipReaders(frontend, leases) {
  assert.ok(Array.isArray(leases) && leases.length > 0 && leases.length <= 20);
  const nonces = new Set();
  for (const lease of leases) {
    assert.deepEqual(Object.keys(lease).sort(), ['format', 'kind', 'nonce', 'pid']);
    assert.equal(lease.format, 1);
    assert.ok(Number.isSafeInteger(lease.pid) && lease.pid > 1);
    assert.ok(['production', 'development'].includes(lease.kind));
    assert.match(lease.nonce, /^[0-9a-f]{32}$/);
    assert.equal(nonces.has(lease.nonce), false, 'distinct captured lease nonce');
    nonces.add(lease.nonce);
  }
  let readers;
  try {
    readers = readdirSync(join(frontend, '.framework-state/readers'));
  } catch (error) {
    if (error.code !== 'ENOENT') throw error;
    readers = [];
  }
  assert.equal(
    readers.some((name) => nonces.has(name)),
    false,
    'own canonical reader contamination',
  );
}

/** Read finite observational files; this never creates an ownership capability. */
export function readOwnershipObservation(path, bound = 16384) {
  const before = lstatSync(path);
  assert.ok(before.isFile() && !before.isSymbolicLink() && before.nlink === 1);
  assert.equal(before.uid, process.getuid());
  assert.equal(before.mode & 0o777, 0o600);
  assert.equal(realpathSync(path), path);
  assert.ok(before.size <= bound);
  const descriptor = openSync(path, constants.O_RDONLY | constants.O_NOFOLLOW);
  try {
    for (let attempt = 0; attempt < 3; attempt++) {
      const opened = fstatSync(descriptor);
      assert.equal(before.dev, opened.dev);
      assert.equal(before.ino, opened.ino);
      assert.ok(opened.isFile() && opened.nlink === 1 && opened.size <= bound);
      assert.equal(opened.uid, before.uid);
      assert.equal(opened.mode & 0o777, 0o600);
      // Reopen-free positioned reads preserve the checked descriptor identity.
      const buffer = Buffer.alloc(opened.size);
      const bytes = readSync(descriptor, buffer, 0, opened.size, 0);
      const after = fstatSync(descriptor);
      if (
        opened.size !== after.size ||
        opened.mtimeMs !== after.mtimeMs ||
        opened.ctimeMs !== after.ctimeMs
      ) {
        // An append concurrent with observation is uncertainty, never a partial record.
        continue;
      }
      assert.equal(bytes, opened.size);
      return buffer.toString('utf8');
    }
    throw new Error('Ownership observation changed during read');
  } finally {
    closeSync(descriptor);
  }
}

export function createOwnershipMatrixScope(root) {
  assert.equal(realpathSync(root), root);
  assert.equal(fileURLToPath(new URL('../../../../../', import.meta.url)), root + '/');
  assert.ok(basename(root).startsWith('learnstack-framework-ownership-'));
  const rootEntry = lstatSync(root);
  assert.equal(rootEntry.uid, process.getuid());
  assert.equal(rootEntry.mode & 0o777, 0o700);
  const ordinary = createFixtureOwner();
  const runtimes = new Map();
  let closed = false;
  let failed = false;
  let disposal;
  function active() {
    assert.equal(closed, false, 'Ownership matrix scope is active');
    ordinary.assertActive();
  }
  function entry(server) {
    const owned = runtimes.get(server.process);
    assert.ok(owned && owned.server === server, 'Actual captured runtime belongs to this scope');
    return owned;
  }
  const owner = Object.freeze({
    assertActive: active,
    ownServer: ordinary.ownServer,
    ownChild(child) {
      active();
      if (child.spawnargs.slice(1).includes('scripts/public-server.mjs')) {
        assert.equal(runtimes.has(child), false);
        runtimes.set(child, { child, server: null, result: null, disposition: null });
        return child;
      }
      return ordinary.ownChild(child);
    },
  });
  return Object.freeze({
    owner,
    track(server) {
      const owned = runtimes.get(server.process);
      assert.ok(owned && owned.server === null && server.broker);
      owned.server = server;
    },
    async retire(server, expectedCode) {
      const owned = entry(server);
      assert.equal(owned.disposition, null);
      const result = await stopTestChild(server.process);
      const actual = await server.completed;
      assert.equal(result.forced, false);
      assert.equal(result.ownerCode, expectedCode);
      assert.equal(result.ownerSignal, null);
      assert.deepEqual(actual, { code: expectedCode, signal: null });
      assert.equal(server.broker.capture.retired(), true);
      owned.result = Object.freeze({ ...result });
      owned.disposition = 'retired';
      return owned.result;
    },
    async refusedBeforeLease(server) {
      const owned = entry(server);
      assert.equal(owned.disposition, null);
      assert.deepEqual(await server.completed, { code: 1, signal: null });
      assert.equal(server.broker.capture.lease(), null);
      assert.equal(server.broker.capture.family(), null);
      assert.deepEqual(server.broker.capture.producers(), []);
      assert.match(server.scanner.tail, /Framework admission refused: owner_busy/);
      assert.throws(() => process.kill(-server.process.pid, 0), { code: 'ESRCH' });
      owned.result = Object.freeze({ code: 1, signal: null, leaseAcquired: false });
      owned.disposition = 'refused-before-lease';
      return owned.result;
    },
    async observeExpectedOwnerDeath(server) {
      const owned = entry(server);
      assert.equal(owned.disposition, null);
      const result = await stopTestChild(server.process);
      assert.equal(result.forced, true);
      assert.equal(result.ownerCode, null);
      assert.equal(result.ownerSignal, 'SIGKILL');
      assert.deepEqual(await server.completed, { code: null, signal: 'SIGKILL' });
      assert.equal(server.broker.capture.retired(), true);
      owned.result = Object.freeze({ ...result }); // The fatal launch outcome is never cleared.
      owned.disposition = 'fatal-retired-unreconciled';
      return owned.result;
    },
    reconcileExpectedOwnerDeath(server) {
      const owned = entry(server);
      assert.equal(owned.disposition, 'fatal-retired-unreconciled');
      assert.equal(server.broker.reconcile(), 'reconciled');
      owned.disposition = 'fatal-retired-reconciled';
      return owned.result;
    },
    async retainPreHello(server, ownerKilled) {
      const owned = entry(server);
      assert.equal(owned.disposition, null);
      assert.equal(server.broker.capture.family(), null);
      await assert.rejects(stopTestChild(server.process), /Runtime fixture family handle missing/);
      const actual = await server.completed;
      assert.deepEqual(
        actual,
        ownerKilled ? { code: null, signal: 'SIGKILL' } : { code: 1, signal: null },
      );
      assert.throws(() => server.broker.reconcile(), /evidence refused/);
      owned.result = Object.freeze({ ...actual, missingAuthenticatedFamily: true });
      owned.disposition = 'retained-prehello-missing-family';
      return owned.result;
    },
    dispose() {
      if (disposal) return disposal;
      closed = true;
      disposal = (async () => {
        for (const owned of runtimes.values()) {
          if (owned.disposition === null || owned.disposition === 'fatal-retired-unreconciled') {
            failed = true;
            try {
              await stopTestChild(owned.child);
            } catch {
              /* Retain every uncertain scope. */
            }
          }
        }
        try {
          await ordinary.dispose();
        } catch {
          failed = true;
        }
        if (failed) throw new Error('Ownership matrix cleanup unconfirmed');
        const retained = [...runtimes.values()].some(
          (owned) => owned.disposition === 'retained-prehello-missing-family',
        );
        if (!retained) {
          assertNoRetainedFrameworkOwners(root);
          rmSync(root, { recursive: true });
        }
        return Object.freeze({
          disposition: retained ? 'retained-missing-family' : 'removed',
          launchOutcomes: [...runtimes.values()].map((owned) => ({
            disposition: owned.disposition,
            result: owned.result,
          })),
        });
      })();
      return disposal;
    },
  });
}
