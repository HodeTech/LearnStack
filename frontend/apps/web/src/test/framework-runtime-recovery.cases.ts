// @vitest-environment node
import { it } from 'vitest';

import { closedFrameworkProofSync } from './framework-proof-closed-boundary';
import { runFrameworkOwnershipGroup } from './framework-runtime-ownership';

export function registerRuntimeRecoveryCases() {
  it(
    'preserves fatal outcomes and reconciles only exact retired Next families',
    () =>
      closedFrameworkProofSync(() => {
        runFrameworkOwnershipGroup('recovery');
      }),
    220_000,
  );
}
