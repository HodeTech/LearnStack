export const AUTH_INPUT_LIMIT = 16 * 1024;

/** The only native POST exemptions, gated off until the complete BFF is installed. */
export const BFF_POST_PATHS = Object.freeze([
  '/api/auth/invitation/capture',
  '/api/auth/invitation/inspect',
  '/api/auth/invitation/verify',
  '/api/auth/invitation/accept',
  '/api/auth/invitation/result',
  '/api/auth/invitation/cancel',
  '/api/auth/entry/consents',
  '/api/auth/entry/consents/withdraw',
  '/api/studio/custom-field-create',
  '/api/studio/custom-field-revise',
  '/api/studio/custom-field-publish',
  '/api/studio/custom-field-retire',
  '/api/studio/custom-field-discard',
  '/api/studio/membership-custom-fields',
  '/api/account/custom-fields',
  '/api/account/consent-grant',
  '/api/account/consent-withdraw',
  '/api/studio/invitation-create',
  '/api/studio/invitation-revoke',
  '/api/studio/invitation-reissue',
  '/api/studio/invitation-policy',
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
      target.startsWith('/api/account/') ||
      target === '/api/auth/switch' ||
      target === '/api/auth/entry/select')
  );
}
