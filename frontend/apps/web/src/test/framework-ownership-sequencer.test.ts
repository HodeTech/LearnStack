// @vitest-environment node
import assert from 'node:assert/strict';
import { resolve } from 'node:path';

import { it } from 'vitest';
import { BaseSequencer, type TestSpecification, type Vitest } from 'vitest/node';

import FrameworkOwnershipSequencer from './framework-ownership-sequencer';

const root = resolve('inert-sequencer-app');
const names = [
  'framework-runtime-ownership.test.ts',
  'framework-runtime-workers.test.ts',
  'framework-runtime-recovery.test.ts',
  'framework-router-faults.test.ts',
];

// These inert public API records exercise sorting only; no project or worker starts.
function specification(name: string): TestSpecification {
  return {
    moduleId: resolve(root, 'src/test', name),
    project: { name: '', config: { sequence: { groupOrder: 0 }, isolate: true } },
  } as unknown as TestSpecification;
}
function context(results = new Map<string, { duration: number; failed: boolean }>()): Vitest {
  return {
    config: { root },
    cache: {
      getFileTestResults: (key: string) => results.get(key),
      getFileStats: () => ({ size: 100 }),
    },
  } as unknown as Vitest;
}

it('starts exactly the four app ownership groups without dropping or replacing specifications', async () => {
  const priority = names.map(specification);
  const unrelated = specification('ordinary.test.ts');
  const outside = {
    ...specification(names[0]!),
    moduleId: resolve('another-inert-app', 'src/test', names[0]!),
  } as TestSpecification;
  const files = [outside, unrelated, ...priority.toReversed()];
  const original = [...files];
  const ctx = context();
  const baseline = await new BaseSequencer(ctx).sort(files);
  assert.equal(baseline[0], outside, 'the default would start unrelated work first');
  const actual = await new FrameworkOwnershipSequencer(ctx).sort(files);
  assert.deepEqual(new Set(actual.slice(0, 4)), new Set(priority));
  assert.deepEqual(
    actual.slice(4),
    baseline.filter((file) => file === outside || file === unrelated),
  );
  assert.equal(actual.length, files.length);
  assert.deepEqual(new Set(actual), new Set(files));
  assert.deepEqual(files, original);
});

it('preserves the installed default order and duplicate identities within both partitions', async () => {
  const priority = specification(names[0]!);
  const duplicate = { ...priority } as TestSpecification;
  const failed = specification('failed.test.ts');
  const longest = specification('longest.test.ts');
  const ctx = context(
    new Map([
      [':src/test/' + names[0], { failed: false, duration: 10 }],
      [':src/test/failed.test.ts', { failed: true, duration: 1 }],
      [':src/test/longest.test.ts', { failed: false, duration: 100 }],
    ]),
  );
  const files = [longest, duplicate, failed, priority];
  assert.deepEqual(await new BaseSequencer(ctx).sort(files), [
    failed,
    longest,
    duplicate,
    priority,
  ]);
  assert.deepEqual(await new FrameworkOwnershipSequencer(ctx).sort(files), [
    duplicate,
    priority,
    failed,
    longest,
  ]);
});

it('leaves filtered and empty runs to the default sequencer without inventing absent groups', async () => {
  const files = ['one.test.ts', 'two.test.ts'].map(specification);
  const ctx = context();
  assert.deepEqual(
    await new FrameworkOwnershipSequencer(ctx).sort(files),
    await new BaseSequencer(ctx).sort(files),
  );
  assert.deepEqual(await new FrameworkOwnershipSequencer(ctx).sort([]), []);
});
