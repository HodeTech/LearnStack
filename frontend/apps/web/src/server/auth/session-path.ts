import type { NativeReturnPath } from './session-contract.js';

const UUID_V7 = '[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}';
const DATA_REQUEST_ERASURE = new RegExp(
  '^/studio/data-requests/erasure\\?membership=(' + UUID_V7 + ')$',
);
const DATA_REQUEST_RESULT = new RegExp(
  '^/studio/data-requests/' + UUID_V7 + '\\?result=(?:export|erasure|cancelled|revoked)$',
);

/** Canonical local screens only; queries, fragments and arbitrary paths never return authority. */
export function isNativeReturnPath(value: unknown): value is NativeReturnPath {
  return (
    typeof value === 'string' &&
    ([
      '/',
      '/studio',
      '/studio/members',
      '/studio/roles',
      '/studio/custom-fields',
      '/studio/invitations',
      '/studio/invitation-policy',
      '/studio/federation',
      '/studio/data-requests',
      '/studio/data-requests/erasure',
      '/portal',
      '/portal/profile',
    ].includes(value) ||
      /^\/studio\/(?:members|roles|invitations|custom-fields|data-requests)\/[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/.test(
        value,
      ))
  );
}

/**
 * ADR-0106 § 3: the erasure page of one Revoked Membership. A Revoked Membership has no member page, so its identifier
 * travels in the one query this page admits; the caller supplies a canonical UUIDv7.
 */
export function dataRequestErasureTarget(membershipId: string): string {
  const target = '/studio/data-requests/erasure?membership=' + membershipId;
  if (!DATA_REQUEST_ERASURE.test(target)) throw new Error('Erasure target unavailable');
  return target;
}

/**
 * The fixed GET a native action document recovers to: a canonical screen, or the erasure page of one Membership,
 * the only protected document whose recovery needs its query.
 */
export function isBffRecoveryTarget(value: unknown): value is string {
  return (
    isNativeReturnPath(value) || (typeof value === 'string' && DATA_REQUEST_ERASURE.test(value))
  );
}

/** A same-host 303 the native protocol may answer: a recovery target, or a data-request page with its closed result. */
export function isBffRedirectTarget(value: unknown): value is string {
  return (
    isBffRecoveryTarget(value) || (typeof value === 'string' && DATA_REQUEST_RESULT.test(value))
  );
}
