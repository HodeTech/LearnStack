/**
 * Server-side SDK entry. Reads tenant + locale from request headers set by
 * `middleware.ts`. The real implementation lands when `/openapi/v1.json`
 * exposes its first endpoint.
 */
export type ServerSdkOptions = {
  readonly tenantId: string;
  readonly locale: string;
};

export function createServerSdk(_options: ServerSdkOptions) {
  // Placeholder — wired up alongside the first generated route.
  return {} as const;
}
