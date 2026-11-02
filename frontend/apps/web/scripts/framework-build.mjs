// Supported clean producer. No application/framework import precedes admission.
import { realpathSync } from 'node:fs';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

import { produceFrameworkBuild } from '../../../scripts/framework-installation.mjs';

export const buildFramework = produceFrameworkBuild;

function argumentsFromCLI(values) {
  const options = {};
  for (let index = 0; index < values.length; index++) {
    const option = values[index];
    if (option === '--development' && options.mode === undefined) options.mode = 'development';
    else if (option === '--native-only' && options.nativeOnly === undefined)
      options.nativeOnly = true;
    else if (['--app-root', '--frontend-root'].includes(option) && values[index + 1]) {
      const name = option === '--app-root' ? 'appRoot' : 'frontendRoot';
      if (options[name] !== undefined) throw new Error('Duplicate framework build option');
      options[name] = resolve(values[++index]);
    } else throw new Error('Unsupported framework build option');
  }
  return options;
}
if (process.argv[1] && realpathSync(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const controller = new AbortController();
  const stop = () => controller.abort();
  process.on('SIGTERM', stop);
  process.on('SIGINT', stop);
  try {
    const options = argumentsFromCLI(process.argv.slice(2));
    if (options.mode === 'development')
      throw new Error('Development ownership belongs to the native launcher');
    await buildFramework({ ...options, signal: controller.signal });
  } catch {
    console.error('Framework clean build refused or failed');
    process.exitCode = 1;
  } finally {
    process.removeListener('SIGTERM', stop);
    process.removeListener('SIGINT', stop);
  }
}
