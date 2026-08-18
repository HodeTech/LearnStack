// `@testing-library/jest-dom` adds the DOM matchers (`toBeInTheDocument`,
// `toHaveAccessibleName`, …) to Vitest's `expect`. The `/vitest` entry point is
// the one that registers against Vitest rather than Jest.
import '@testing-library/jest-dom/vitest';
import { cleanup } from '@testing-library/react';
import { afterEach } from 'vitest';

// Load-bearing, not belt-and-braces: Testing Library registers its own cleanup
// only when it detects a global `afterEach`, and `globals` is off in
// vitest.config.ts. Comment this line out and the suite goes red — measured on
// Node 20.11.0, the version CI pins.
afterEach(cleanup);
