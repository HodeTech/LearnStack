// `@testing-library/jest-dom` adds the DOM matchers (`toBeInTheDocument`,
// `toHaveAccessibleName`, …) to Vitest's `expect`. The `/vitest` entry point is
// the one that registers against Vitest rather than Jest.
import '@testing-library/jest-dom/vitest';
import { cleanup } from '@testing-library/react';
import { afterEach } from 'vitest';

// Testing Library auto-cleans only when it detects a global `afterEach`, which
// depends on `globals: true` staying on. Doing it explicitly keeps the harness
// correct if that flag is ever turned off.
afterEach(cleanup);
