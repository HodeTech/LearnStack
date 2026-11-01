// This bootstrap deliberately imports no application, SDK or Next runtime.
import { realpathSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

import {
  acquireFrameworkLease,
  FrameworkAdmissionError,
  frameworkRuntimeEnvironment,
  prepareFrameworkNativeCache,
  verifyFrameworkBuild,
} from '../../../scripts/framework-installation.mjs';

const appRoot = realpathSync(fileURLToPath(new URL('../', import.meta.url)));
let lease;
try {
  if (
    realpathSync(process.cwd()) !== appRoot ||
    process.argv.slice(2).some((argument) => argument !== '--dev') ||
    process.argv.slice(2).length > 1
  )
    throw new FrameworkAdmissionError('runtime_arguments');
  const dev = process.argv.includes('--dev');
  process.env.NODE_ENV = dev ? 'development' : 'production';
  let identity;
  if (dev) {
    const { buildFramework } = await import('./framework-build.mjs');
    const development = await buildFramework({ appRoot, mode: 'development' });
    identity = development.identity;
    lease = development.lease;
  } else {
    // Own output before checking it. A supported producer cannot mutate the
    // native/SDK/Next graph after verification while this listener is alive.
    lease = acquireFrameworkLease({ appRoot, kind: 'production' });
    identity = verifyFrameworkBuild({ appRoot }).identity;
  }
  prepareFrameworkNativeCache(identity, lease.record);
  let released = false;
  const release = () => {
    if (released) return;
    released = true;
    try {
      lease.release();
    } catch {
      console.error('Framework runtime ownership cleanup failed');
      process.exitCode = 1;
    }
  };
  process.once('exit', release);
  Object.defineProperty(globalThis, Symbol.for('learnstack.framework.runtime'), {
    configurable: false,
    enumerable: false,
    writable: false,
    value: Object.freeze({ identity, mode: dev ? 'development' : 'production' }),
  });
  // Runtime secrets remain a finite native projection. Ambient compiler
  // selectors never reach Next's production/config or development/HMR loaders.
  const environment = frameworkRuntimeEnvironment(identity, dev ? 'development' : 'production');
  for (const key of Object.keys(process.env))
    if (!Object.hasOwn(environment, key)) delete process.env[key];
  Object.assign(process.env, environment);
  await import('./public-server-runtime.mjs');
} catch (error) {
  try {
    lease?.release();
  } catch {
    console.error('Framework runtime ownership cleanup failed');
  }
  console.error(
    error instanceof FrameworkAdmissionError ? error.message : 'Framework runtime admission failed',
  );
  process.exitCode = 1;
}
