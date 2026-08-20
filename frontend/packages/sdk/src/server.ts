import type { paths } from './generated/schema';

/**
 * Server-side SDK entry. Reads tenant + locale from request headers set by
 * `middleware.ts`, and is the path React Server Components and route handlers
 * use to reach the .NET API.
 *
 * Typed against the generated {@link paths}, which is empty until the backend
 * publishes its first operation — the pipeline is wired, the API is not yet.
 */
export type ServerSdkOptions = {
  readonly tenantId: string;
  readonly locale: string;
};

export type ServerSdk = {
  readonly [P in keyof paths]: paths[P];
};

export function createServerSdk(_options: ServerSdkOptions): ServerSdk {
  return {} as ServerSdk;
}
