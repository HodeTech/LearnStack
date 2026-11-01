// Real-process controls for the short metadata mutex, separate from ownership.
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import {
  existsSync,
  mkdirSync,
  mkdtempSync,
  readdirSync,
  rmSync,
  rmdirSync,
  symlinkSync,
  writeFileSync,
} from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';
import { fileURLToPath } from 'node:url';

import { acquireFrameworkLease } from './framework-installation.mjs';

const script = fileURLToPath(import.meta.url);
const MODES = ['compatible', 'uncleared', 'incompatible', 'retirement'];
function startChild(args) {
  const child = spawn(process.execPath, [script, ...args], {
    env: { PATH: process.env.PATH, NODE_ENV: 'production' },
    stdio: ['ignore', 'ignore', 'ignore', 'ipc'],
  });
  const messages = [];
  const waiting = new Map();
  child.on('message', (message) => {
    const resolve = waiting.get(message.event);
    if (resolve) {
      waiting.delete(message.event);
      resolve(message);
    } else messages.push(message);
  });
  const closed = new Promise((resolve) =>
    child.once('close', (code, signal) => resolve({ code, signal })),
  );
  return {
    child,
    closed,
    send: (message) => child.send(message),
    async next(event) {
      const index = messages.findIndex((message) => message.event === event);
      if (index >= 0) return messages.splice(index, 1)[0];
      let timer;
      try {
        return await new Promise((resolve, reject) => {
          waiting.set(event, resolve);
          timer = setTimeout(
            () => reject(new Error('Metadata control acknowledgement missing')),
            2000,
          );
        });
      } finally {
        clearTimeout(timer);
        waiting.delete(event);
      }
    },
    has: (event) => messages.some((message) => message.event === event),
    async stop() {
      if (child.exitCode !== null) return closed;
      if (child.connected) child.send('stop');
      let timer;
      try {
        return await Promise.race([
          closed,
          new Promise((_resolve, reject) => {
            timer = setTimeout(
              () => reject(new Error('Metadata control child remained live')),
              2000,
            );
          }),
        ]);
      } catch (error) {
        child.kill('SIGKILL');
        await closed;
        throw error;
      } finally {
        clearTimeout(timer);
      }
    },
  };
}
async function reader(frontendRoot, appRoot, kind) {
  let lease;
  process.send({ event: 'ready' });
  process.on('message', (message) => {
    if (message === 'go') {
      const began = performance.now();
      try {
        lease = acquireFrameworkLease({ frontendRoot, appRoot, kind });
        process.send({
          event: 'attempt',
          acquired: true,
          elapsedMilliseconds: performance.now() - began,
        });
      } catch (error) {
        process.send({
          event: 'attempt',
          acquired: false,
          reason: error.reason,
          elapsedMilliseconds: performance.now() - began,
        });
      }
    } else if (message === 'release') {
      process.send({ event: 'releasing' });
      lease?.release();
      lease = undefined;
      process.send({ event: 'released' });
    } else if (message === 'stop') {
      lease?.release();
      process.disconnect();
    }
  });
}
async function holder(gate) {
  mkdirSync(gate);
  process.send({ event: 'held' });
  let held = true;
  const release = () => {
    if (!held) return;
    rmdirSync(gate);
    held = false;
    process.send({ event: 'physically-released' });
  };
  process.on('message', (message) => {
    if (message === 'short-release') setTimeout(release, 75);
    else if (message === 'release') release();
    else if (message === 'stop') {
      release();
      process.disconnect();
    }
  });
}

/** Timers bound observations or schedule an actual rmdir; they acknowledge no lease. */
export async function runFrameworkMetadataControl(mode) {
  assert.ok(MODES.includes(mode));
  const root = mkdtempSync(join(tmpdir(), 'learnstack-framework-metadata-'));
  const frontendRoot = join(root, 'frontend');
  const sdk = join(root, 'sdk');
  mkdirSync(frontendRoot);
  mkdirSync(sdk);
  writeFileSync(join(sdk, 'package.json'), JSON.stringify({ name: '@learnstack/sdk' }));
  for (const name of ['one', 'two']) {
    const app = join(frontendRoot, name);
    mkdirSync(join(app, 'node_modules/@learnstack'), { recursive: true });
    symlinkSync(sdk, join(app, 'node_modules/@learnstack/sdk'), 'dir');
  }
  const children = [];
  const spawnReader = async (name, kind = 'production') => {
    const value = startChild(['--reader', frontendRoot, join(frontendRoot, name), kind]);
    children.push(value);
    await value.next('ready');
    return value;
  };
  const spawnHolder = async () => {
    const value = startChild(['--holder', join(frontendRoot, '.framework-state/gate')]);
    children.push(value);
    await value.next('held');
    return value;
  };
  const readerCount = () => readdirSync(join(frontendRoot, '.framework-state/readers')).length;
  const sdkReaderCount = () => readdirSync(join(sdk, '.framework-state/readers')).length;
  let held;
  try {
    const first = await spawnReader('one');
    first.send('go');
    assert.equal((await first.next('attempt')).acquired, true);
    if (mode === 'retirement') {
      held = await spawnHolder();
      first.send('release');
      await first.next('releasing');
      await delay(30);
      assert.equal(
        first.has('released'),
        false,
        'retirement cannot finish while the metadata gate remains held',
      );
      assert.equal(readerCount(), 1, 'the remaining reader is not retired by elapsed time');
      held.send('release');
      await held.next('physically-released');
      await first.next('released');
      assert.equal(readerCount(), 0);
      return { mode, result: 'pass', observed: 'retirement joined actual gate release' };
    }
    const second = await spawnReader('two', mode === 'incompatible' ? 'install' : 'production');
    if (mode !== 'incompatible') held = await spawnHolder();
    second.send('go');
    if (mode === 'compatible') held.send('short-release');
    const attempt = await second.next('attempt');
    if (mode === 'compatible') {
      await held.next('physically-released');
      assert.equal(attempt.acquired, true, 'compatible readers serialize metadata access');
      assert.equal(readerCount(), 2);
      assert.equal(sdkReaderCount(), 2);
    } else {
      assert.equal(attempt.acquired, false);
      assert.equal(attempt.reason, 'owner_busy');
      assert.equal(readerCount(), 1);
      assert.equal(sdkReaderCount(), 1);
      assert.equal(existsSync(join(frontendRoot, '.framework-state/writer.json')), false);
      if (mode === 'uncleared') {
        assert.ok(
          attempt.elapsedMilliseconds >= 240,
          'an uncleared metadata gate reaches its existing bound',
        );
        assert.ok(attempt.elapsedMilliseconds < 1500, 'the metadata attempt stays bounded');
        assert.equal(held.has('physically-released'), false);
        held.send('release');
        await held.next('physically-released');
      } else
        assert.ok(
          attempt.elapsedMilliseconds < 200,
          'an actual owner conflict is refused without a metadata wait',
        );
    }
    return { mode, result: 'pass', attempt };
  } finally {
    if (held?.child.connected) {
      held.send('release');
      await held.stop();
    }
    for (const value of children.toReversed())
      if (value !== held) assert.deepEqual(await value.stop(), { code: 0, signal: null });
    rmSync(root, { recursive: true, force: true });
  }
}

if (process.argv[1] === script) {
  if (process.argv[2] === '--reader') await reader(...process.argv.slice(3));
  else if (process.argv[2] === '--holder') await holder(process.argv[3]);
  else if (process.argv[2] === '--all') {
    for (const mode of MODES)
      process.stdout.write(`${JSON.stringify(await runFrameworkMetadataControl(mode))}\n`);
  } else throw new Error('Unsupported metadata control argument');
}
