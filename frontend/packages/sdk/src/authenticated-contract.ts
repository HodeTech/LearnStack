import type { components, operations } from './generated/schema.js';

/** Authenticated wire ownership; transport admission is separately closed per caller. */
export const AUTHENTICATED_GET_CONTRACTS = Object.freeze({
  '/api/v1/account/site': 'GetAccountSite',
  '/api/v1/identity/memberships': 'ListMemberships',
  '/api/v1/identity/self': 'GetIdentitySelf',
  '/api/v1/identity/self/contexts': 'ListIdentitySelfContexts',
} as const satisfies {
  '/api/v1/account/site': 'GetAccountSite';
  '/api/v1/identity/memberships': 'ListMemberships';
  '/api/v1/identity/self': 'GetIdentitySelf';
  '/api/v1/identity/self/contexts': 'ListIdentitySelfContexts';
});

export type AuthenticatedGetOperation =
  (typeof AUTHENTICATED_GET_CONTRACTS)[keyof typeof AUTHENTICATED_GET_CONTRACTS];
export type AccountSite = components['schemas']['AccountSite'];
export type AccountSiteSuccess =
  operations['GetAccountSite']['responses'][200]['content']['application/json'];
export type MembershipDirectoryItem = components['schemas']['MembershipDirectoryItem'];
export type MembershipDirectoryPage =
  operations['ListMemberships']['responses'][200]['content']['application/json'];

type Same<A, B> =
  (<T>() => T extends A ? 1 : 2) extends <T>() => T extends B ? 1 : 2 ? true : false;
type GetPaths<P> = { [K in keyof P]: P[K] extends { get: unknown } ? K : never }[keyof P];
type GetAt<P, K extends keyof P> = P[K] extends { get: infer G } ? G : never;
type GetOperationNames<P, O> = {
  [N in keyof O]: true extends {
    [K in GetPaths<P>]: Same<O[N], GetAt<P, K>>;
  }[GetPaths<P>]
    ? N
    : never;
}[keyof O];
type PairChecks<P, O, C> = {
  [K in keyof C]: K extends keyof P
    ? C[K] extends keyof O
      ? Same<GetAt<P, K>, O[C[K]]>
      : false
    : false;
}[keyof C];

/** Fails closed on missing, overlapping, extra or incorrectly paired GET ownership. */
export type GetContractOwnership<P, O, Public, Authenticated> = false extends
  | Same<GetPaths<P>, keyof Public | keyof Authenticated>
  | Same<Extract<keyof Public, keyof Authenticated>, never>
  | Same<GetOperationNames<P, O>, Public[keyof Public] | Authenticated[keyof Authenticated]>
  | Same<Extract<Public[keyof Public], Authenticated[keyof Authenticated]>, never>
  | PairChecks<P, O, Public>
  | PairChecks<P, O, Authenticated>
  ? false
  : true;
