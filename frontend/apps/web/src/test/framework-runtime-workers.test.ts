// @vitest-environment node
import { it } from 'vitest';

import { closedFrameworkProofSync } from './framework-proof-closed-boundary';
import { runFrameworkOwnershipGroup } from './framework-runtime-ownership';

it(
  'joins initialized Next workers after direct exits and ordered shutdown',
  () =>
    closedFrameworkProofSync(() => {
      runFrameworkOwnershipGroup('workers');
    }),
  160_000,
);
