// @vitest-environment node
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

import { expect, it } from 'vitest';

it('preserves source-map coordinates, duplicate positions and metadata across UTF-16 edits', () => {
  const controls = fileURLToPath(
    new URL('../../../../scripts/next-response-patch/map-controls.mjs', import.meta.url),
  );
  const output = execFileSync(process.execPath, [controls], {
    encoding: 'utf8',
    timeout: 15_000,
    maxBuffer: 4096,
  });
  expect(output.trim()).toBe('source-map controls passed');
});
