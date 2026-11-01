import { resolve } from 'node:path';

import { BaseSequencer, type TestSpecification } from 'vitest/node';

/** Start the three bounded native ownership groups before ordinary suite work. */
export default class FrameworkOwnershipSequencer extends BaseSequencer {
  override async sort(files: TestSpecification[]): Promise<TestSpecification[]> {
    const first = new Set(
      [
        'framework-runtime-ownership.test.ts',
        'framework-runtime-workers.test.ts',
        'framework-runtime-recovery.test.ts',
      ].map((name) => resolve(this.ctx.config.root, 'src/test', name)),
    );
    const sorted = await super.sort(files);
    return [
      ...sorted.filter((file) => first.has(file.moduleId)),
      ...sorted.filter((file) => !first.has(file.moduleId)),
    ];
  }
}
