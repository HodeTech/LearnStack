import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  appendFileSync,
  chmodSync,
  linkSync,
  mkdirSync,
  mkdtempSync,
  readFileSync,
  realpathSync,
  renameSync,
  rmSync,
  symlinkSync,
  writeFileSync,
} from 'node:fs';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { pathToFileURL } from 'node:url';
import { createCopiedOverlapHost } from '../apps/web/scripts/framework-overlap-proof-host.mjs';
const source = readFileSync(
  new URL('../apps/web/scripts/framework-overlap-proof-host.mjs', import.meta.url),
  'utf8',
);
const codec = readFileSync(
  new URL('../apps/web/scripts/framework-overlap-proof-rows.mjs', import.meta.url),
  'utf8',
);
const line = (n, event) =>
  JSON.stringify({ version: 1, pid: process.pid, sequence: n, event, state: null }) + '\n';
function fixture() {
  const root = mkdtempSync(join(realpathSync(tmpdir()), 'overlap-host-control-'));
  const app = join(root, 'frontend/apps/web');
  mkdirSync(app, { recursive: true });
  const path = join(root, '.overlap-proof.events');
  writeFileSync(path, line(1, 'armed'), { mode: 0o600 });
  const lease = Object.freeze({ pid: 1, nonce: 'bound' });
  const family = Object.freeze({
    pid: process.pid,
    group: process.pid,
    parentPid: 1,
    leaseNonce: 'bound',
    incarnation: 'a'.repeat(32),
  });
  let healthy = true,
    observed = family;
  const broker = {
    assertHealthy() {
      if (!healthy) throw new Error('unhealthy');
    },
    capture: { family: () => observed, lease: () => lease },
    records: () => [
      { path: join(app, '.framework-state/writer.json') },
      { path: join(root, 'reader') },
      { path: join(root, 'owner') },
    ],
  };
  return {
    root,
    app,
    path,
    broker,
    breakBinding() {
      observed = Object.freeze({ ...family });
    },
    unhealthy() {
      healthy = false;
    },
    cleanup() {
      rmSync(root, { recursive: true, force: true });
    },
  };
}
function usingFixture(callback) {
  const f = fixture();
  try {
    callback(f);
  } finally {
    f.cleanup();
  }
}
test('fixed descriptor accepts append, refuses rewriting and changing family', () =>
  usingFixture((f) => {
    const host = createCopiedOverlapHost(f);
    assert.equal(host.events().length, 1);
    appendFileSync(f.path, line(2, 'api-entered'));
    assert.equal(host.events().length, 2);
    writeFileSync(f.path, line(1, 'track-b') + line(2, 'api-entered'));
    assert.throws(() => host.events());
    f.breakBinding();
    assert.throws(() => host.events());
    host.close();
  }));
test('fixed descriptor refuses replacement, private-mode drift and shrink', () => {
  for (const mutation of ['replace', 'mode', 'shrink'])
    usingFixture((f) => {
      const host = createCopiedOverlapHost(f);
      assert.equal(host.events().length, 1);
      if (mutation === 'replace') {
        writeFileSync(f.path + '.new', line(1, 'armed'), { mode: 0o600 });
        renameSync(f.path + '.new', f.path);
      }
      if (mutation === 'mode') chmodSync(f.path, 0o644);
      if (mutation === 'shrink') writeFileSync(f.path, '');
      assert.throws(() => host.events(), mutation);
      host.close();
    });
});
test('hard-linked event file and unhealthy broker refuse before observation', () => {
  usingFixture((f) => {
    linkSync(f.path, f.path + '.link');
    assert.throws(() => createCopiedOverlapHost(f));
  });
  usingFixture((f) => {
    f.unhealthy();
    assert.throws(() => createCopiedOverlapHost(f));
  });
});
test('app aliases and owned-root drift refuse with the fixed error', () => {
  usingFixture((f) => {
    const alias = f.app + '-alias';
    symlinkSync(f.app, alias);
    assert.throws(() => createCopiedOverlapHost({ app: alias, broker: f.broker }), {
      message: 'Copied overlap evidence refused',
    });
    const host = createCopiedOverlapHost(f);
    chmodSync(f.root, 0o755);
    assert.throws(() => host.events(), { message: 'Copied overlap evidence refused' });
    chmodSync(f.root, 0o700);
    renameSync(f.app, f.app + '-moved');
    symlinkSync(f.app + '-moved', f.app);
    assert.throws(() => host.events(), { message: 'Copied overlap evidence refused' });
    host.close();
  });
});
test('same-open-descriptor retry accepts one append but refuses continual mutation', async () => {
  const dir = mkdtempSync(join(realpathSync(tmpdir()), 'overlap-reader-injection-'));
  try {
    const needle = 'const count = readSync(fd, bytes, 0, bytes.length, 0);';
    assert.equal(source.split(needle).length, 2);
    const patched = source.replace(
      needle,
      needle + "\n        globalThis[Symbol.for('overlap.private.read.test')]?.();",
    );
    writeFileSync(join(dir, 'host.mjs'), patched);
    writeFileSync(join(dir, 'framework-overlap-proof-rows.mjs'), codec);
    const { createCopiedOverlapHost: injected } = await import(
      pathToFileURL(join(dir, 'host.mjs')).href
    );
    for (const continuous of [false, true])
      usingFixture((f) => {
        const host = injected(f);
        let calls = 0;
        globalThis[Symbol.for('overlap.private.read.test')] = () => {
          calls++;
          if (continuous || calls === 1) appendFileSync(f.path, line(calls + 1, 'track-b'));
        };
        try {
          if (continuous) assert.throws(() => host.events());
          else assert.equal(host.events().length, 2);
        } finally {
          delete globalThis[Symbol.for('overlap.private.read.test')];
          host.close();
        }
      });
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});
