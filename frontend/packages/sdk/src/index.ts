/**
 * Typed API client surface.
 *
 * `src/generated/schema.d.ts` is produced by `pnpm generate` from the backend's
 * `/openapi/v1.json` and is checked in, so a reviewer can see the contract the
 * app is compiled against and CI can fail when it drifts from the running API.
 *
 * Frontend code MUST route through this SDK — direct `fetch` from Client
 * Components is forbidden (Standards 03 § Forbidden) and the shared ESLint
 * preset refuses it.
 */
export type { paths, components, operations } from './generated/schema';

export * from './client';
export * from './server';
