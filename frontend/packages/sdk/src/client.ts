import type { paths } from './generated/schema';

/**
 * Client-side SDK entry. Reads tenant + locale from request-scoped context
 * established by `middleware.ts`.
 *
 * The surface is typed against the generated {@link paths}, which is empty
 * today: the backend's v1 document has no operations until the walking
 * skeleton lands its first endpoints. The generation pipeline is wired and
 * runs — that emptiness is the API's, not the scaffold's.
 */
export type ClientSdkOptions = {
  readonly tenantId: string;
  readonly locale: string;
};

export type ClientSdk = {
  readonly [P in keyof paths]: paths[P];
};

export function createClientSdk(_options: ClientSdkOptions): ClientSdk {
  // No operations to bind yet. The shape is the generated one so that adding
  // an endpoint to the backend is what changes this type, rather than a
  // hand-maintained mirror drifting from it.
  return {} as ClientSdk;
}
