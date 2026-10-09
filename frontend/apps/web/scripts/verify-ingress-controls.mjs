// Source mutants exercise the real harness; the independent normal command
// retains every Step 2 socket/framework control.
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import {
  copyFileSync,
  existsSync,
  mkdirSync,
  mkdtempSync,
  readFileSync,
  rmSync,
  writeFileSync,
} from 'node:fs';
import { createServer } from 'node:net';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

import { createFixtureOwner, fixtureEnvironment } from './fixture-support.mjs';
import { stopTestChild } from './stop-test-child.mjs';

const appRoot = fileURLToPath(new URL('../', import.meta.url));
const original = readFileSync(join(appRoot, 'scripts/verify-ingress.mjs'), 'utf8');
const owner = createFixtureOwner();
owner.install({ control: process.connected ? process : undefined, event: 'disconnect' });
const rescueSockets = new Set();
const listenerSockets = new WeakMap();
let currentCase;
let proof;
let caseNumber = 0;

function replaceOne(source, before, after) {
  assert.equal(source.split(before).length, 2, 'A source control must match exactly once');
  return source.replace(before, after);
}

function absent(pid) {
  for (const target of process.platform === 'win32' ? [pid] : [pid, -pid]) {
    try {
      process.kill(target, 0);
    } catch (error) {
      if (error.code === 'ESRCH') continue;
      throw new Error('Owned process absence could not be proved');
    }
    throw new Error('Owned fixture child survived');
  }
}

async function listen(server, port = 0) {
  const sockets = new Set();
  listenerSockets.set(server, sockets);
  server.on('connection', (socket) => {
    sockets.add(socket);
    rescueSockets.add(socket);
    socket.once('close', () => {
      sockets.delete(socket);
      rescueSockets.delete(socket);
    });
    socket.on('error', () => {});
  });
  await new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error('Control listener bind deadline')), 2000);
    server.once('error', (error) => {
      clearTimeout(timer);
      reject(error);
    });
    server.listen({ host: '127.0.0.1', port, exclusive: true }, () => {
      clearTimeout(timer);
      resolve();
    });
  });
  return server.address().port;
}

async function closeListener(server) {
  for (const socket of listenerSockets.get(server) ?? []) socket.destroy();
  await new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error('Control listener closure deadline')), 2000);
    server.close((error) => {
      clearTimeout(timer);
      if (error) reject(error);
      else resolve();
    });
  });
}

try {
  proof = owner.ownRoot(mkdtempSync(join(tmpdir(), 'learnstack-ingress-controls-')));
  // A live control socket is only a finally-path rescue. The cleanup oracle runs
  // first, so this channel cannot make a missing owner cleanup appear green.
  const rescue = owner.ownServer(
    createServer((socket) => {
      rescueSockets.add(socket);
      socket.on('close', () => rescueSockets.delete(socket));
      socket.on('error', () => {});
    }),
  );
  const rescuePort = await listen(rescue);
  let base = replaceOne(
    original,
    "const appRoot = fileURLToPath(new URL('../', import.meta.url));",
    'const appRoot = ' + JSON.stringify(appRoot) + ';',
  );
  base = replaceOne(
    base,
    "  app = join(fixtureRoot, 'frontend/apps/web');",
    "  app = join(fixtureRoot, 'frontend/apps/web');\n  process.send?.({ kind: 'resource', fixtureRoot });",
  );
  base = replaceOne(
    base,
    "  import { Server } from 'node:net';",
    "  import { Server } from 'node:net';\n  import { connect as connectCleanup } from 'node:net';\n" +
      "  const cleanupControl = connectCleanup({ host: '127.0.0.1', port: " +
      rescuePort +
      ' });\n' +
      "  cleanupControl.once('close', () => process.exit(1));\n  cleanupControl.once('error', () => process.exit(1));",
  );
  base = replaceOne(
    base,
    '  children.push(child);',
    "  children.push(child);\n  process.send?.({ kind: 'child', pid: child.pid });",
  );
  const brokenPipeSource = replaceOne(
    base,
    "owner.install({ control: process.connected ? process : undefined, event: 'disconnect' });",
    "owner.install({ control: process.connected ? process : undefined, event: 'disconnect' });\n" +
      "process.on('message', (message) => { if (message?.kind === 'output') process.stdout.write('p'.repeat(1024 * 1024)); });",
  );

  async function runCase(name, source, { stage, action, expectedCode = 1, expectedMessage } = {}) {
    owner.assertActive();
    currentCase = name;
    const directory = join(proof, String(++caseNumber));
    mkdirSync(directory);
    for (const file of ['fixture-support.mjs', 'stop-test-child.mjs'])
      copyFileSync(join(appRoot, 'scripts', file), join(directory, file));
    const script = join(directory, 'fixture.mjs');
    writeFileSync(script, source);
    const runner = owner.ownChild(
      spawn(process.execPath, [script], {
        cwd: appRoot,
        // Deliberately poison the harness parent. Its owned Next children must
        // receive a fresh environment and explicit TLS verification instead.
        env: {
          ...fixtureEnvironment({ root: directory }),
          NODE_TLS_REJECT_UNAUTHORIZED: '0',
          NODE_OPTIONS: '--no-warnings',
          HTTP_PROXY: 'http://inert.invalid',
          INGRESS_ENV_CANARY: 'must-not-reach-child',
        },
        stdio: ['ignore', 'pipe', 'pipe', 'ipc'],
        detached: process.platform !== 'win32',
      }),
    );
    let root;
    let pids = [];
    let diagnostics = '';
    let triggered = false;
    let timer;
    let actionError;
    runner.stdout.on('data', () => {});
    runner.stderr.on('data', (chunk) => {
      diagnostics = (diagnostics + chunk.toString()).slice(-16384);
    });
    runner.on('message', (message) => {
      if (message.kind === 'resource') root = message.fixtureRoot;
      if (message.kind === 'child') pids = [...new Set([...pids, message.pid])];
      if (message.kind !== 'checkpoint') return;
      root = message.fixtureRoot;
      pids = [...new Set([...pids, ...message.children])];
      if (message.stage === stage) {
        triggered = true;
        Promise.resolve()
          .then(() => action(runner))
          .catch((error) => {
            actionError = error;
            runner.disconnect();
          });
      } else runner.send({ kind: 'continue', stage: message.stage });
    });
    try {
      const result = await new Promise((resolve, reject) => {
        timer = setTimeout(() => reject(new Error('Ingress control deadline: ' + name)), 40000);
        runner.once('error', reject);
        runner.once('exit', (code, signal) => resolve({ code, signal }));
      });
      // Node 22 does not reliably emit the aggregate ChildProcess close after
      // explicit IPC disconnect. Drain each diagnostic pipe after owned exit.
      await Promise.all(
        [runner.stdout, runner.stderr].map(
          (stream) =>
            new Promise((resolve, reject) => {
              if (stream.destroyed || stream.readableEnded) {
                resolve();
                return;
              }
              const deadline = setTimeout(
                () => reject(new Error('Control output drainage deadline')),
                2000,
              );
              const done = () => {
                clearTimeout(deadline);
                resolve();
              };
              stream.once('end', done);
              stream.once('close', done);
            }),
        ),
      );
      if (actionError) throw actionError;
      assert.equal(result.signal, null, name + ' must exit through the harness');
      assert.equal(result.code, expectedCode, name + ' exited with the wrong result');
      if (stage) assert(triggered, name + ' did not reach its owned checkpoint');
      if (name === 'broken output pipe')
        assert(!diagnostics.includes('write EPIPE'), 'EPIPE escaped owner cleanup');
      if (expectedMessage)
        assert(diagnostics.includes(expectedMessage), name + ' failed for the wrong reason');
      assert(root, name + ' did not create its owned temporary tree');
      assert.equal(existsSync(root), false, 'Temporary fixture tree survived');
      for (const pid of pids) absent(pid);
      if (caseNumber % 5 === 0) console.warn('Ingress controls checked: ' + caseNumber);
      return { root, pids };
    } finally {
      clearTimeout(timer);
      // The handle remains live until confirmed absent; no saved PID authorizes
      // any later signal. Nested children also hold the finally-only rescue pipe.
      try {
        await stopTestChild(runner);
      } finally {
        for (const socket of rescueSockets) socket.destroy();
        if (root) rmSync(root, { recursive: true, force: true });
      }
    }
  }

  const opensslFailure = replaceOne(
    base,
    "  try {\n    execFileSync(\n      'openssl',",
    "  try {\n    execFileSync(\n      'learnstack-planted-unavailable-openssl',",
  );
  await runCase('early OpenSSL setup failure', opensslFailure, {
    expectedMessage: 'Fixture TLS certificate setup failed',
  });

  const listenerFailure = replaceOne(
    base,
    "assert(request.url === '/api/v1/public/site',",
    "assert(request.url === '/planted-invalid-bootstrap',",
  );
  await runCase('asynchronous listener refusal', listenerFailure, {
    expectedMessage: 'Bootstrap fixture refused the hop',
  });

  const setupFailure = replaceOne(
    base,
    "  await checkpoint('setup');",
    "  throw new Error('Planted setup failure');",
  );
  await runCase('setup failure clean cleanup', setupFailure, {
    expectedMessage: 'Planted setup failure',
  });
  const omittedCleanup = replaceOne(
    setupFailure,
    '  await owner.dispose();',
    '  // Planted omitted cleanup.',
  );
  await assert.rejects(
    runCase('omitted cleanup mutant', omittedCleanup, { expectedMessage: 'Planted setup failure' }),
    /Temporary fixture tree survived/,
    'Omitted cleanup must fail the independent cleanup oracle',
  );

  for (const signal of ['SIGINT', 'SIGTERM'])
    await runCase(signal, base, { stage: 'native', action: (runner) => runner.kill(signal) });
  await runCase('explicit parent IPC disconnect', base, {
    stage: 'native',
    action: (runner) => runner.disconnect(),
  });
  await runCase('broken output pipe', brokenPipeSource, {
    stage: 'native',
    action: async (runner) => {
      runner.stdout.destroy();
      await new Promise((resolve, reject) =>
        runner.send({ kind: 'output' }, (error) => (error ? reject(error) : resolve())),
      );
    },
  });

  const shortTls = replaceOne(
    base,
    "  assert.equal(bootstrapCalls, beforeTls, 'Rejected TLS reached bootstrap');",
    "  assert.equal(bootstrapCalls, beforeTls, 'Rejected TLS reached bootstrap');\n" +
      '  const state = await observe(native);\n' +
      "  assert.equal(state.environmentSafe, true, 'Child environment was not isolated');\n" +
      "  assert.equal(diagnosticsLeaked(), false, 'Private material entered diagnostics');\n  return;",
  );
  await runCase('TLS and environment clean control', shortTls, { expectedCode: 0 });
  const disabledTls = replaceOne(
    shortTls,
    'rejectUnauthorized: true, ...tlsOptions',
    'rejectUnauthorized: false, ...tlsOptions',
  );
  await runCase('disabled TLS mutant', disabledTls, {
    expectedMessage: 'TLS must reject untrusted CA and wrong hostname',
  });

  function emitted(source, expression) {
    return replaceOne(
      source,
      'const configuration = publicServerConfiguration(values, root);',
      'const configuration = publicServerConfiguration(values, root);\n' +
        '  const emitted = ' +
        expression +
        ';\n' +
        '  process.stderr.write(emitted.slice(0, 13));\n' +
        "  setTimeout(() => process.stderr.write(emitted.slice(13) + '\\n' + 'z'.repeat(70000)), 10);",
    );
  }
  // Patch the disposable launcher source, leaving every fixture check intact.
  function launcherOutput(source, expression) {
    return replaceOne(
      source,
      "const launcher = readFileSync(join(appRoot, 'scripts/public-server.mjs'), 'utf8');",
      'const launcher = ' +
        JSON.stringify(
          emitted(readFileSync(join(appRoot, 'scripts/public-server.mjs'), 'utf8'), expression),
        ) +
        ';',
    );
  }
  await runCase(
    'benign version text and large output clean control',
    launcherOutput(shortTls, "'v1.release.notes'"),
    { expectedCode: 0 },
  );
  await runCase(
    'early split secret with later large output mutant',
    launcherOutput(shortTls, 'configuration.secret'),
    { expectedMessage: 'Private material entered diagnostics' },
  );
  await runCase(
    'early split structural envelope mutant',
    launcherOutput(
      shortTls,
      "'v1.' + Buffer.from('{}').toString('base64url') + '.' + 'a'.repeat(43)",
    ),
    { expectedMessage: 'Private material entered diagnostics' },
  );

  const shutdownLauncher = replaceOne(
    readFileSync(join(appRoot, 'scripts/public-server.mjs'), 'utf8'),
    'const configuration = publicServerConfiguration(values, root);',
    'const configuration = publicServerConfiguration(values, root);\n' +
      "  process.prependOnceListener('SIGTERM', () => process.stderr.write(configuration.secret + '\\n'));",
  );
  await runCase(
    'shutdown-only secret mutant',
    replaceOne(
      shortTls,
      "const launcher = readFileSync(join(appRoot, 'scripts/public-server.mjs'), 'utf8');",
      'const launcher = ' + JSON.stringify(shutdownLauncher) + ';',
    ),
    { expectedMessage: 'Private material entered shutdown diagnostics' },
  );

  // A stale healthy listener must be left alone and must never satisfy readiness.
  const foreign = owner.ownServer(
    createServer((socket) => {
      socket.end(
        'HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 20\r\n\r\n{"status":"healthy"}',
      );
    }),
  );
  await listen(foreign, 3011);
  await runCase('stale listener preflight', base, {
    expectedMessage: 'Fixture port 3011 is already in use',
  });
  const readinessOnly = replaceOne(
    replaceOne(shortTls, 'for (const port of [3000, 3011])', 'for (const port of [3000])'),
    '  return;\n  const nativeState',
    "  const staleStock = start([join(appRoot, 'node_modules/next/dist/bin/next'), 'start', '--hostname', '127.0.0.1', '--port', '3011']);\n" +
      '  await ready(staleStock, false, 3011);\n  return;\n  const nativeState',
  );
  await runCase('stale listener cannot replace child bind', readinessOnly, {
    expectedMessage: 'Fixture startup failed',
  });
  assert.equal(foreign.listening, true, 'Preflight must preserve a foreign listener');
  await closeListener(foreign);

  const bindRace = owner.ownServer(createServer((socket) => socket.destroy()));
  await runCase('post-preflight bind failure', base, {
    stage: 'setup',
    action: async (runner) => {
      await listen(bindRace, 3000);
      runner.send({ kind: 'continue', stage: 'setup' });
    },
    expectedMessage: 'Fixture startup failed',
  });
  assert.equal(bindRace.listening, true, 'Bind failure must preserve a foreign listener');
  await closeListener(bindRace);

  const upgradeOnly = replaceOne(
    shortTls,
    '  return;\n  const nativeState',
    "  await callUpgrade('Host: localhost:3000');\n  await observe(native);\n  return;\n  const nativeState",
  );
  await runCase('unsupported upgrade clean closure', upgradeOnly, { expectedCode: 0 });
  const unclosedLauncher = replaceOne(
    readFileSync(join(appRoot, 'scripts/public-server.mjs'), 'utf8'),
    'if (!dev || !admitIncomingRequest(request, configuration.secret)) return socket.destroy();',
    'if (!dev || !admitIncomingRequest(request, configuration.secret)) return;',
  );
  const unclosedUpgrade = replaceOne(
    upgradeOnly,
    "const launcher = readFileSync(join(appRoot, 'scripts/public-server.mjs'), 'utf8');",
    'const launcher = ' + JSON.stringify(unclosedLauncher) + ';',
  );
  await runCase('unclosed upgrade mutant', unclosedUpgrade, {
    expectedMessage: 'Server did not close unsupported upgrade',
  });
  console.warn(
    'Ingress controls: ' +
      caseNumber +
      ' setup/cleanup, signal/IPC/EPIPE, TLS/environment, leak, listener/bind and upgrade cases passed.',
  );
} catch (error) {
  console.error('Ingress control failed: ' + currentCase);
  throw error;
} finally {
  for (const socket of rescueSockets) socket.destroy();
  await owner.dispose();
}
