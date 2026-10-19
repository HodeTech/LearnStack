import { describe, expect, expectTypeOf, it } from 'vitest';

import { AUTHENTICATED_GET_CONTRACTS } from './authenticated-contract';
import type {
  AccountSite,
  AccountSiteQuery,
  AccountSiteSuccess,
  AuthenticatedGetOperation,
  GetContractOwnership,
} from './authenticated-contract';
import type { components, operations, paths } from './generated/schema';
import { GET_CONTRACT_OWNERSHIP, PUBLIC_GET_CONTRACTS } from './server';
import type { ServerSdk, PUBLIC_GET_WRAPPERS } from './server';
import snapshot from '../../../../backend/openapi/v1.json';

type Public = typeof PUBLIC_GET_CONTRACTS;
type Authenticated = typeof AUTHENTICATED_GET_CONTRACTS;
type Ownership = GetContractOwnership<paths, operations, Public, Authenticated>;

function ownershipErrors(
  publicContracts: Readonly<Record<string, string>>,
  authenticatedContracts: Readonly<Record<string, string>>,
  generated: { paths: Record<string, { get?: { operationId: string } }> } = snapshot,
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
  it('keeps AccountSite in the separate authenticated contract and public methods credential-free', () => {
    expect(AUTHENTICATED_GET_CONTRACTS).toEqual({ '/api/v1/account/site': 'GetAccountSite' });
    expect(PUBLIC_GET_CONTRACTS).toEqual({
      '/api/v1/public/site': 'GetPublicSite',
      '/api/v1/public/courses': 'GetPublicCourses',
      '/api/v1/public/courses/{slug}': 'GetPublicCourse',
      '/api/v1/public/courses/{slug}/lessons/{lessonSlug}': 'GetPublicLesson',
    });
    expect(GET_CONTRACT_OWNERSHIP).toBe(true);
    expect(ownershipErrors(PUBLIC_GET_CONTRACTS, AUTHENTICATED_GET_CONTRACTS)).toEqual([]);
    expectTypeOf<Ownership>().toEqualTypeOf<true>();
    expectTypeOf<AuthenticatedGetOperation>().toEqualTypeOf<'GetAccountSite'>();
    expectTypeOf<AccountSiteQuery>().toEqualTypeOf<undefined>();
    expectTypeOf<AccountSite>().toEqualTypeOf<AccountSiteSuccess>();
    expectTypeOf<AccountSite['actorId']>().toEqualTypeOf<string>();
    expectTypeOf<AccountSite['tenantId']>().toEqualTypeOf<string>();
    expectTypeOf<AccountSite['organizationId']>().toEqualTypeOf<string | null>();
    expectTypeOf<AccountSite['site']>().toEqualTypeOf<components['schemas']['PublicSite']>();
    expectTypeOf<
      operations['GetAccountSite']['responses'][413]['content']['application/problem+json']
    >().toEqualTypeOf<components['schemas']['ProblemDetails']>();
    expectTypeOf<paths['/api/v1/account/site']['get']>().toEqualTypeOf<
      operations['GetAccountSite']
    >();
    expectTypeOf<
      Extract<keyof typeof PUBLIC_GET_WRAPPERS, '/api/v1/account/site'>
    >().toEqualTypeOf<never>();
    expectTypeOf<Extract<keyof ServerSdk, 'getAccountSite'>>().toEqualTypeOf<never>();
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
