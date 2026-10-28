import { describe, expect, expectTypeOf, it } from 'vitest';

import { AUTHENTICATED_GET_CONTRACTS } from './authenticated-contract';
import type {
  AccountSite,
  AccountSiteSuccess,
  AuthenticatedGetOperation,
  GetContractOwnership,
  MembershipDirectoryItem,
  MembershipDirectoryPage,
} from './authenticated-contract';
import type { AuthenticatedServerSdk } from './authenticated-server';
import type { components, operations, paths } from './generated/schema';
import { AUTHENTICATED_GET_CONTRACTS as RootContracts } from './index';
import type {
  AccountSite as RootAccountSite,
  AccountSiteSuccess as RootAccountSiteSuccess,
  AuthenticatedGetOperation as RootAuthenticatedGetOperation,
} from './index';
import { GET_CONTRACT_OWNERSHIP, PUBLIC_GET_CONTRACTS, PUBLIC_GET_WRAPPERS } from './server';
import type { ServerSdk } from './server';
import snapshot from '../../../../backend/openapi/v1.json';

type Public = typeof PUBLIC_GET_CONTRACTS;
type Authenticated = typeof AUTHENTICATED_GET_CONTRACTS;
type Ownership = GetContractOwnership<paths, operations, Public, Authenticated>;

function ownershipErrors(
  publicContracts: Readonly<Record<string, string>>,
  authenticatedContracts: Readonly<Record<string, string>>,
  generated: {
    paths: Record<string, { get?: { operationId: string }; post?: unknown }>;
  } = snapshot,
): string[] {
  const errors: string[] = [];
  const owners = [...Object.entries(publicContracts), ...Object.entries(authenticatedContracts)];
  if (new Set(owners.map(([path]) => path)).size !== owners.length) errors.push('path-overlap');
  if (new Set(owners.map(([, operation]) => operation)).size !== owners.length)
    errors.push('operation-overlap');
  const generatedGets = Object.entries(generated.paths).flatMap(([path, item]) =>
    item.get ? [[path, item.get.operationId]] : [],
  );
  if (
    owners.length !== generatedGets.length ||
    generatedGets.some(
      ([path, operation]) =>
        !owners.some(
          ([ownedPath, ownedOperation]) => ownedPath === path && ownedOperation === operation,
        ),
    )
  )
    errors.push('ownership');
  return errors;
}

describe('generated GET contract ownership', () => {
  it('keeps AccountSite and the directory contracts authenticated without adding native directory transport', () => {
    expect(AUTHENTICATED_GET_CONTRACTS).toEqual({
      '/api/v1/account/site': 'GetAccountSite',
      '/api/v1/identity/memberships': 'ListMemberships',
      '/api/v1/identity/self': 'GetIdentitySelf',
      '/api/v1/identity/self/contexts': 'ListIdentitySelfContexts',
    });
    expect(PUBLIC_GET_CONTRACTS).toEqual({
      '/api/v1/public/site': 'GetPublicSite',
      '/api/v1/public/courses': 'GetPublicCourses',
      '/api/v1/public/courses/{slug}': 'GetPublicCourse',
      '/api/v1/public/courses/{slug}/lessons/{lessonSlug}': 'GetPublicLesson',
    });
    expect(GET_CONTRACT_OWNERSHIP).toBe(true);
    expect(ownershipErrors(PUBLIC_GET_CONTRACTS, AUTHENTICATED_GET_CONTRACTS)).toEqual([]);
    expectTypeOf<Ownership>().toEqualTypeOf<true>();
    expectTypeOf<AuthenticatedGetOperation>().toEqualTypeOf<
      'GetAccountSite' | 'ListMemberships' | 'GetIdentitySelf' | 'ListIdentitySelfContexts'
    >();
    expect(RootContracts).toBe(AUTHENTICATED_GET_CONTRACTS);
    expectTypeOf<RootAccountSite>().toEqualTypeOf<AccountSite>();
    expectTypeOf<RootAccountSiteSuccess>().toEqualTypeOf<AccountSiteSuccess>();
    expectTypeOf<RootAuthenticatedGetOperation>().toEqualTypeOf<AuthenticatedGetOperation>();
    expectTypeOf<AccountSite>().toEqualTypeOf<AccountSiteSuccess>();
    expectTypeOf<AccountSite['actorId']>().toEqualTypeOf<string>();
    expectTypeOf<AccountSite['tenantId']>().toEqualTypeOf<string>();
    expectTypeOf<AccountSite['organizationId']>().toEqualTypeOf<string | null>();
    expectTypeOf<AccountSite['site']>().toEqualTypeOf<components['schemas']['PublicSite']>();
    expectTypeOf<
      operations['GetAccountSite']['responses'][413]['content']['application/problem+json']
    >().toEqualTypeOf<components['schemas']['ProblemDetails']>();
    expectTypeOf<
      operations['GetAccountSite']['responses'][413]['headers']['Cache-Control']
    >().toEqualTypeOf<'no-store' | undefined>();
    expectTypeOf<
      operations['GetPublicSite']['responses'][413]['headers']['Cache-Control']
    >().toEqualTypeOf<'no-store' | undefined>();
    expectTypeOf<
      operations['GetAccountSite']['responses'][200]['headers']['Cache-Control']
    >().toEqualTypeOf<'no-store'>();
    expectTypeOf<paths['/api/v1/account/site']['get']>().toEqualTypeOf<
      operations['GetAccountSite']
    >();
    expectTypeOf<
      Extract<keyof typeof PUBLIC_GET_WRAPPERS, '/api/v1/account/site'>
    >().toEqualTypeOf<never>();
    expectTypeOf<Extract<keyof ServerSdk, 'getAccountSite'>>().toEqualTypeOf<never>();
    expectTypeOf<keyof AuthenticatedServerSdk>().toEqualTypeOf<'getAccountSite'>();
    expectTypeOf<
      Extract<keyof typeof PUBLIC_GET_WRAPPERS, '/api/v1/identity/memberships'>
    >().toEqualTypeOf<never>();
    expectTypeOf<paths['/api/v1/identity/memberships']['get']>().toEqualTypeOf<
      operations['ListMemberships']
    >();
    expectTypeOf<operations['ListMemberships']['parameters']['query']>().toEqualTypeOf<
      { cursor?: string; limit?: number } | undefined
    >();
    expectTypeOf<MembershipDirectoryPage['items']>().toEqualTypeOf<MembershipDirectoryItem[]>();
    expectTypeOf<keyof MembershipDirectoryItem>().toEqualTypeOf<
      'membershipId' | 'userId' | 'organizationId' | 'status' | 'displayName' | 'preferredLocale'
    >();
    expectTypeOf<MembershipDirectoryItem['membershipId']>().toEqualTypeOf<string>();
    expectTypeOf<MembershipDirectoryItem['userId']>().toEqualTypeOf<string>();
    expectTypeOf<MembershipDirectoryItem['organizationId']>().toEqualTypeOf<string | null>();
    expectTypeOf<MembershipDirectoryItem['status']>().toEqualTypeOf<'Active' | 'Suspended'>();
    expectTypeOf<MembershipDirectoryItem['displayName']>().toEqualTypeOf<string | null>();
    expectTypeOf<MembershipDirectoryItem['preferredLocale']>().toEqualTypeOf<string | null>();
    expectTypeOf<
      operations['ListMemberships']['responses'][403]['content']['application/problem+json']
    >().toEqualTypeOf<components['schemas']['ProblemDetails']>();
    expectTypeOf<
      operations['ListMemberships']['responses'][200]['headers']['Cache-Control']
    >().toEqualTypeOf<'no-store'>();
  });

  it('freezes contract and wrapper ownership at runtime', () => {
    for (const map of [PUBLIC_GET_CONTRACTS, AUTHENTICATED_GET_CONTRACTS, PUBLIC_GET_WRAPPERS]) {
      expect(Object.isFrozen(map)).toBe(true);
      expect(Reflect.set(map, '/api/v1/future', 'GetUnowned')).toBe(false);
      expect(map).not.toHaveProperty('/api/v1/future');
    }
  });

  it('refuses unowned, overlapping and incorrectly paired paths and operation ids', () => {
    const missing = { ...PUBLIC_GET_CONTRACTS } as Record<string, string>;
    delete missing['/api/v1/public/site'];
    expect(ownershipErrors(missing, AUTHENTICATED_GET_CONTRACTS)).toContain('ownership');
    expect(
      ownershipErrors(
        { ...PUBLIC_GET_CONTRACTS, ...AUTHENTICATED_GET_CONTRACTS },
        AUTHENTICATED_GET_CONTRACTS,
      ),
    ).toContain('path-overlap');
    expect(
      ownershipErrors(
        { ...PUBLIC_GET_CONTRACTS, '/api/v1/future': 'GetAccountSite' },
        AUTHENTICATED_GET_CONTRACTS,
      ),
    ).toContain('operation-overlap');
    expect(
      ownershipErrors(
        { ...PUBLIC_GET_CONTRACTS, '/api/v1/public/site': 'GetPublicCourses' },
        AUTHENTICATED_GET_CONTRACTS,
      ),
    ).toContain('ownership');
    expect(
      ownershipErrors(PUBLIC_GET_CONTRACTS, AUTHENTICATED_GET_CONTRACTS, {
        paths: { ...snapshot.paths, '/api/v1/future': { get: { operationId: 'GetUnowned' } } },
      }),
    ).toContain('ownership');
    expect(
      ownershipErrors(PUBLIC_GET_CONTRACTS, AUTHENTICATED_GET_CONTRACTS, {
        paths: {
          ...snapshot.paths,
          '/api/v1/account/site': { get: { operationId: 'GetRenamedAccountSite' } },
        },
      }),
    ).toContain('ownership');

    expectTypeOf<
      GetContractOwnership<paths, operations, Omit<Public, '/api/v1/public/site'>, Authenticated>
    >().toEqualTypeOf<false>();
    expectTypeOf<
      GetContractOwnership<paths, operations, Public & Authenticated, Authenticated>
    >().toEqualTypeOf<false>();
    expectTypeOf<
      GetContractOwnership<
        paths,
        operations,
        Omit<Public, '/api/v1/public/site'> & { '/api/v1/public/site': 'GetPublicCourses' },
        Authenticated
      >
    >().toEqualTypeOf<false>();
    expectTypeOf<
      GetContractOwnership<
        paths & { '/api/v1/future': { get: operations['GetAccountSite'] } },
        operations,
        Public,
        Authenticated
      >
    >().toEqualTypeOf<false>();
    expectTypeOf<
      GetContractOwnership<
        paths,
        operations & { GetUnowned: operations['GetAccountSite'] },
        Public,
        Authenticated
      >
    >().toEqualTypeOf<false>();
  });
});
