import { realpathSync, rmSync } from 'node:fs';
import { join } from 'node:path';

import { stopTestChild } from './stop-test-child.mjs';

const OUTPUT_BOUND = 65536;
const PRIVATE_TOKEN = /(?<![A-Za-z0-9_.-])v1\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]{43}(?![A-Za-z0-9_.=-])/;
const PRIVATE_KEYS = new Set([
  'LEARNSTACK_PUBLIC_API_ORIGIN',
  'LEARNSTACK_PUBLIC_HOP_SECRET',
  'LEARNSTACK_PUBLIC_TLS_CERT',
  'LEARNSTACK_PUBLIC_TLS_KEY',
]);

export function createPrivateScanner(secrets = []) {
  // Accepted ingress envelopes are at most 12288 bytes (ingress.ts). Retaining
  // 64 KiB also covers every fixture secret and splits at any supported boundary.
  if (
    secrets.some((secret) => typeof secret !== 'string' || !secret || secret.length > OUTPUT_BOUND)
  )
    throw new Error('Invalid fixture scanner secret');
  let tail = '';
  let leaked = false;
  const contains = (value) =>
    /x-learnstack-/i.test(value) ||
    PRIVATE_TOKEN.test(value) ||
    secrets.some((secret) => value.includes(secret));
  return {
    contains,
    push(chunk) {
      const combined = tail + chunk.toString();
      // A chunk ending is not a token boundary: the next chunk may extend the
      // MAC or add forbidden padding. Exact secrets/carriers need no boundary.
      leaked ||=
        /x-learnstack-/i.test(combined) ||
        secrets.some((secret) => combined.includes(secret)) ||
        PRIVATE_TOKEN.test(combined + '_');
      tail = combined.slice(-OUTPUT_BOUND);
    },
    finish() {
      leaked ||= contains(tail);
    },
    get leaked() {
      return leaked;
    },
    get tail() {
      return tail;
    },
  };
}

export function fixtureEnvironment({
  root,
  nodeEnv = 'production',
  nodePath,
  privateValues = {},
  debug,
}) {
  const env = {
    TMPDIR: root,
    // SWC's native carrier rejects symlink ancestors (including macOS /var).
    // Canonicalize the owned root instead of inheriting a user's cache or HOME.
    SWC_NATIVE_BINDING_CACHE: join(realpathSync(root), 'swc-native-cache'),
    NODE_ENV: nodeEnv,
    NODE_TLS_REJECT_UNAUTHORIZED: '1',
    NEXT_TELEMETRY_DISABLED: '1',
    CI: 'true',
  };
  if (process.env.PATH !== undefined) env.PATH = process.env.PATH;
  if (nodePath !== undefined) env.NODE_PATH = nodePath;
  if (debug !== undefined) env.DEBUG = debug;
  for (const [key, value] of Object.entries(privateValues)) {
    if (!PRIVATE_KEYS.has(key) || typeof value !== 'string')
      throw new Error('Unsupported fixture environment key or value');
    env[key] = value;
  }
  return env;
}

export function createFixtureOwner({ exit = (code) => process.exit(code) } = {}) {
  const roots = new Set();
  const children = new Set();
  const servers = new Set();
  const uninstall = [];
  let closed = false;
  let cleanup;
  let cancellation;
  let installed = false;
  function assertActive() {
    if (closed) throw new Error('Fixture owner is closed');
  }
  function own(collection, resource) {
    assertActive();
    collection.add(resource);
    return resource;
  }
  function closeServer(server) {
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error('Fixture listener cleanup deadline')), 5000);
      const done = (error) => {
        clearTimeout(timer);
        if (error && error.code !== 'ERR_SERVER_NOT_RUNNING') reject(error);
        else resolve();
      };
      try {
        server.closeAllConnections?.();
        server.close(done);
      } catch (error) {
        done(error);
      }
    });
  }
  async function drainOutput(child) {
    await Promise.all(
      [child.stdout, child.stderr].filter(Boolean).map((stream) => {
        if (stream.closed || stream.readableEnded || stream.destroyed) return;
        return new Promise((resolve, reject) => {
          const done = (error) => {
            clearTimeout(timer);
            stream.removeListener('end', ended);
            stream.removeListener('close', ended);
            stream.removeListener('error', done);
            if (error) reject(error);
            else resolve();
          };
          const ended = () => done();
          const timer = setTimeout(() => done(new Error('Fixture output cleanup deadline')), 5000);
          stream.once('end', ended);
          stream.once('close', ended);
          stream.once('error', done);
        });
      }),
    );
  }
  function dispose() {
    closed = true;
    cleanup ??= (async () => {
      const results = await Promise.allSettled([
        ...[...children].map(async (child) => {
          await stopTestChild(child);
          await drainOutput(child);
        }),
        ...[...servers].map(closeServer),
      ]);
      const failures = results
        .filter((result) => result.status === 'rejected')
        .map((result) => result.reason);
      // Preserve the tree if a child/listener cannot be confirmed stopped.
      // Every resource still gets an attempt, and incomplete cleanup fails.
      for (const root of failures.length ? [] : roots) {
        try {
          rmSync(root, { recursive: true, force: true });
        } catch (error) {
          failures.push(error);
        }
      }
      for (const remove of uninstall) remove();
      if (failures.length) throw new AggregateError(failures, 'Fixture cleanup failed');
    })();
    return cleanup;
  }
  function cancel() {
    closed = true;
    cancellation ??= dispose().then(
      () => exit(1),
      () => exit(1),
    );
    return cancellation;
  }
  function install({ control, event = 'close' } = {}) {
    assertActive();
    if (installed) return;
    installed = true;
    const cancelled = () => {
      void cancel();
    };
    const onOutputError = () => {
      void cancel();
    };
    for (const signal of ['SIGINT', 'SIGTERM']) {
      process.on(signal, cancelled);
      uninstall.push(() => process.removeListener(signal, cancelled));
    }
    for (const stream of [process.stdout, process.stderr]) {
      stream.on('error', onOutputError);
      uninstall.push(() => stream.removeListener('error', onOutputError));
    }
    if (control) {
      control.on(event, cancelled);
      uninstall.push(() => control.removeListener(event, cancelled));
    }
  }
  return {
    ownRoot: (path) => own(roots, path),
    ownChild: (child) => own(children, child),
    ownServer: (server) => own(servers, server),
    assertActive,
    dispose,
    cancel,
    install,
  };
}
