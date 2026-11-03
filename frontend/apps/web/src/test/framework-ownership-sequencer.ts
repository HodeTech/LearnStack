import { resolve } from 'node:path';

import { BaseSequencer, type TestSpecification } from 'vitest/node';

/** Start the sequential native ownership spec before ordinary suite work. */
export default class FrameworkOwnershipSequencer extends BaseSequencer {
  override async sort(files: TestSpecification[]): Promise<TestSpecification[]> {
    const first = resolve(this.ctx.config.root, 'src/test/framework-runtime-ownership.test.ts');
    const sorted = await super.sort(files);
    return [
      ...sorted.filter((file) => file.moduleId === first),
      ...sorted.filter((file) => file.moduleId !== first),
    ];
  }
}
