/**
 * Typed API client surface.
 *
 * `src/generated/schema.d.ts` is produced by `pnpm generate` from the backend's
 * `/openapi/v1.json` and is checked in, so a reviewer can see the contract the
 * app is compiled against. Regeneration is byte-stable — the file is in
 * `.prettierignore` so nothing reformats the generator's output — which is what
 * would let `generate && git diff --exit-code` be a gate. No CI job runs it
 * yet: it needs the API up, and until the walking skeleton publishes an
 * operation there is nothing for a diff to catch. Phase 02d owns wiring it.
 *
 * Frontend code MUST route through this SDK — direct `fetch` from Client
 * Components is forbidden (Standards 03 § Forbidden) and the shared ESLint
 * preset refuses it.
 */
export type { paths, components, operations } from './generated/schema';

export * from './client';
export * from './server';
