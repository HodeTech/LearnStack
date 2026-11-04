import { execFileSync } from 'node:child_process';
import { mkdirSync, copyFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';

import { assertFrameworkSDKProducer } from '../../../scripts/framework-installation.mjs';

const require = createRequire(import.meta.url);
const root = fileURLToPath(new URL('../', import.meta.url));
// Only the clean qualified producer may mutate this shared native SDK graph.
assertFrameworkSDKProducer(root);
execFileSync(
  process.execPath,
  [require.resolve('typescript/bin/tsc'), '--project', 'tsconfig.native.json'],
  {
    cwd: root,
    stdio: 'inherit',
  },
);
// TypeScript does not copy input declaration files into its output tree.
mkdirSync(new URL('../dist/generated/', import.meta.url), { recursive: true });
copyFileSync(
  new URL('../src/generated/schema.d.ts', import.meta.url),
  new URL('../dist/generated/schema.d.ts', import.meta.url),
);
