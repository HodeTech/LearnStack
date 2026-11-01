// Private runtime entry: latch and scrub before asynchronous admission/imports.
import { realpathSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

import { createFrameworkRuntimeChildControl } from '../../../scripts/framework-runtime-child-control.mjs';

let child;
try {
  child = createFrameworkRuntimeChildControl();
  const appRoot = realpathSync(fileURLToPath(new URL('../', import.meta.url)));
  if (
    process.execArgv.length ||
    process.env.NODE_OPTIONS !== undefined ||
    process.argv.length !== 2 ||
    realpathSync(process.cwd()) !== appRoot
  )
    throw new Error('Private runtime arguments refused');
  const { verifyFrameworkRuntimeChild } = await import(
    '../../../scripts/framework-installation.mjs'
  );
  const framework = verifyFrameworkRuntimeChild({ appRoot, ...child.binding });
  // Copied-proof child instrumentation is inserted after admission.
  const control = await child.admit(framework);
  if (control) {
    const { startPublicRuntime } = await import('./public-server-runtime.mjs');
    if (!control.stopRequested()) await startPublicRuntime(control);
  }
} catch {
  console.error('Framework private runtime admission failed');
  if (child) child.fail('startup-fault');
  else process.exitCode = 1;
}
