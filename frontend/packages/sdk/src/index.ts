/**
 * Typed API client surface. The actual client is generated from the backend's
 * `/openapi/v1.json` during CI; this file re-exports the generated symbols.
 *
 * Frontend code MUST route through this SDK — direct `fetch` from Client
 * Components is forbidden (Standards 03 § Forbidden).
 */
export * from './client';
export * from './server';
