// Builtins-only launch owner. Native listener and credentials stay in its child.
import { realpathSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

import {
  acquireFrameworkLease,
  FrameworkAdmissionError,
  frameworkRuntimeEnvironment,
  prepareFrameworkNativeCache,
  verifyFrameworkBuild,
} from '../../../scripts/framework-installation.mjs';
import { assertFrameworkProcessFamilySupport } from '../../../scripts/framework-process-family.mjs';
import { runFrameworkRuntimeFamily } from '../../../scripts/framework-runtime-family.mjs';

const startup = new AbortController();
const terminate = () => {
  if (!startup.signal.aborted) startup.abort('SIGTERM');
};
const interrupt = () => {
  if (!startup.signal.aborted) startup.abort('SIGINT');
};
process.on('SIGTERM', terminate);
process.on('SIGINT', interrupt);
const appRoot = realpathSync(fileURLToPath(new URL('../', import.meta.url)));
let lease;

async function startupBoundary() {
  await new Promise((resolve) => setImmediate(resolve));
  if (startup.signal.aborted) throw new FrameworkAdmissionError('runtime_cancelled');
}

try {
  assertFrameworkProcessFamilySupport();
  if (
    process.execArgv.length ||
    process.env.NODE_OPTIONS !== undefined ||
    realpathSync(process.cwd()) !== appRoot ||
    process.argv.slice(2).some((argument) => argument !== '--dev') ||
    process.argv.slice(2).length > 1
  )
    throw new FrameworkAdmissionError('runtime_arguments');
  const mode = process.argv.includes('--dev') ? 'development' : 'production';
  process.env.NODE_ENV = mode;
  let identity;
  await startupBoundary();
  // Copied-proof owner instrumentation is inserted before generation.
  if (mode === 'development') {
    const { buildFramework } = await import('./framework-build.mjs');
    await startupBoundary();
    const development = await buildFramework({ appRoot, mode, signal: startup.signal });
    identity = development.identity;
    lease = development.lease;
  } else {
    lease = acquireFrameworkLease({ appRoot, kind: 'production' });
    identity = verifyFrameworkBuild({ appRoot }).identity;
  }
  await startupBoundary();
  prepareFrameworkNativeCache(identity, lease.record);
  await startupBoundary();
  const environment = frameworkRuntimeEnvironment(identity, mode);
  const receipt = await runFrameworkRuntimeFamily({
    entry: fileURLToPath(new URL('./public-runtime-child.mjs', import.meta.url)),
    cwd: appRoot,
    environment,
    mode,
    leaseRecord: lease.record,
    signal: startup.signal,
  });
  const graceful =
    startup.signal.aborted &&
    ['SIGTERM', 'SIGINT'].includes(startup.signal.reason) &&
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
  process.exitCode = graceful ? 0 : 1;
  if (!graceful) console.error('Framework runtime family failed');
} catch (error) {
  console.error(
    error instanceof FrameworkAdmissionError ? error.message : 'Framework runtime admission failed',
  );
  process.exitCode = 1;
} finally {
  // The runner settles only for a never-started or physically retired family.
  // No exit hook, timeout or direct-child exit releases these reservations.
  try {
    lease?.release();
  } catch {
    console.error('Framework runtime ownership cleanup failed');
    process.exitCode = 1;
  }
  process.off('SIGTERM', terminate);
  process.off('SIGINT', interrupt);
}
