// Fixed test-owned template-reuse helper. It emits no diagnostic payloads.
import assert from 'node:assert/strict';
import { lstatSync, realpathSync } from 'node:fs';
import { basename, join, resolve } from 'node:path';

import { reuseFrameworkProof } from './framework-proof-support.mjs';

try {
  assert.equal(process.argv.length, 4);
  assert.equal(process.execArgv.length, 0);
  assert.equal(process.env.NODE_OPTIONS, undefined);
  const [app, template] = process.argv.slice(2);
  assert.equal(typeof process.env.TMPDIR, 'string');
  const fixtureRoot = realpathSync(process.env.TMPDIR);
  const appRoot = realpathSync(app);
  assert.equal(appRoot, join(fixtureRoot, 'frontend/apps/web'));
  const templateApp = realpathSync(template);
  const templateOwner = resolve(templateApp, '../../../..');
  const owner = lstatSync(templateOwner);
  assert.ok(owner.isDirectory() && !owner.isSymbolicLink());
  assert.equal(owner.uid, process.getuid());
  assert.equal(owner.mode & 0o777, 0o700);
  assert.ok(basename(templateOwner).startsWith('learnstack-ingress-controls-'));
  assert.equal(templateApp, join(templateOwner, 'prepared/frontend/apps/web'));
  const reused = reuseFrameworkProof({ app: appRoot, templateApp });
  assert.equal(typeof reused, 'boolean');
  process.exitCode = reused ? 0 : 10;
} catch {
  // Fixed refusal: no path, process environment or source diagnostic leaves this helper.
  process.exitCode = 1;
}
