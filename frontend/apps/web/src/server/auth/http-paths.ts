export const AUTH_INPUT_LIMIT = 16 * 1024;

/** The only native POST exemptions, gated off until the complete BFF is installed. */
export const BFF_POST_PATHS = Object.freeze([
  '/api/auth/signin/keycloak',
  '/api/auth/signout',
  '/api/auth/extend',
]);
export function bffPostTarget(method: string, target: string): boolean {
  return method === 'POST' && BFF_POST_PATHS.includes(target);
}
