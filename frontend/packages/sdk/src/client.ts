/**
 * Client-side SDK entry. Reads tenant + locale from request-scoped context
 * established by `middleware.ts`. The real implementation lands when
 * `/openapi/v1.json` exposes its first endpoint.
 */
export type ClientSdkOptions = {
  readonly tenantId: string;
  readonly locale: string;
};

export function createClientSdk(_options: ClientSdkOptions) {
  // Placeholder — wired up alongside the first generated route.
  return {} as const;
}
