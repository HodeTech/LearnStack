// @vitest-environment node
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

import { it } from 'vitest';

it('observes actual copied request and pool owners through independent completion gates', () => {
  execFileSync(
    process.execPath,
    [
      fileURLToPath(
        new URL('../../scripts/test-bff-proof-runtime-observation.mjs', import.meta.url),
      ),
    ],
    {
      timeout: 30_000,
      maxBuffer: 65_536,
      stdio: 'pipe',
    },
  );
}, 35_000);
