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
  '/api/auth/entry/data-requests',
  '/api/auth/entry/data-requests/export',
  '/api/auth/entry/data-requests/erasure',
  '/api/auth/entry/data-requests/closure',
  '/api/auth/entry/data-requests/cancel',
  '/api/auth/entry/data-requests/revoke',
  '/api/auth/entry/data-requests/download',
  '/api/account/data-requests/export',
  '/api/account/data-requests/erasure',
  '/api/account/data-requests/closure',
  '/api/account/data-requests/cancel',
  '/api/account/data-requests/revoke',
  '/api/account/data-requests/download',
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
  '/api/studio/data-request-export',
  '/api/studio/data-request-erasure',
  '/api/studio/data-request-cancel',
  '/api/studio/data-request-revoke',
  '/api/studio/federation-test',
  '/api/studio/federation-configure',
  '/api/studio/federation-rotate-secret',
  '/api/studio/federation-rename',
  '/api/studio/federation-activate',
  '/api/studio/federation-disable',
  '/api/studio/federation-enable',
  '/api/studio/federation-delete',
  '/api/studio/federation-resume',
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
/**
 * ADR-0106 § 9, ADR-0107 § 4: the native data-request document and its fixed actions. Only the pre-Next data-request
 * handler serves them; Next and the BFF protocol answer them `404`.
 */
export const DATA_REQUEST_DOCUMENT_PATHS = Object.freeze([
  '/account/data',
  ...BFF_POST_PATHS.filter(
    (path) =>
      path.startsWith('/api/auth/entry/data-requests') ||
      path.startsWith('/api/account/data-requests/'),
  ),
]);
export function bffPostTarget(method: string, target: string): boolean {
  return method === 'POST' && BFF_POST_PATHS.includes(target);
}

/** Closed private HTML results; the finite POST census remains the authority. */
export function bffActionDocumentTarget(target: string): boolean {
  return (
    BFF_POST_PATHS.includes(target) &&
    !DATA_REQUEST_DOCUMENT_PATHS.includes(target) &&
    (target.startsWith('/api/studio/') ||
      target.startsWith('/api/account/') ||
      target === '/api/auth/switch' ||
      target === '/api/auth/entry/select')
  );
}
