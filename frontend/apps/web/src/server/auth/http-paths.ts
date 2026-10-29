export const AUTH_INPUT_LIMIT = 16 * 1024;

/** The only native POST exemptions, gated off until the complete BFF is installed. */
export const BFF_POST_PATHS = Object.freeze([
  '/api/auth/signin/keycloak',
  '/api/auth/signout',
  '/api/auth/extend',
  '/api/auth/entry/create',
  '/api/auth/entry/select',
  '/api/auth/switch',
  '/api/studio/membership-profile',
  '/api/studio/membership-roles',
  '/api/studio/membership-status',
  '/api/studio/role-grants',
  '/api/studio/role-manifest-adoption',
  '/api/account/profile',
]);
export function bffPostTarget(method: string, target: string): boolean {
  return method === 'POST' && BFF_POST_PATHS.includes(target);
}

/** Closed private HTML results; the finite POST census remains the authority. */
export function bffActionDocumentTarget(target: string): boolean {
  return (
    BFF_POST_PATHS.includes(target) &&
    (target.startsWith('/api/studio/') ||
      target === '/api/account/profile' ||
      target === '/api/auth/switch' ||
      target === '/api/auth/entry/select')
  );
}
