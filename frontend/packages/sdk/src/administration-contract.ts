import type { ApiResult } from './api-result.js';
import type { components } from './generated/schema.js';
import type { AppError } from './response-policy.js';

export type MembershipDetail = components['schemas']['MembershipDetailResponse'];
export type OwnMembershipProfile = components['schemas']['OwnMembershipProfileResponse'];
export type RoleDirectory = components['schemas']['RoleDirectoryResponse'];
export type RoleDetail = components['schemas']['RoleDetailResponse'];
export type PermissionRegistry = components['schemas']['PermissionRegistryResponse'];
export type OwnCapabilities = components['schemas']['OwnCapabilitiesDto'];
export type OwnMembershipContexts = components['schemas']['OwnMembershipContextPage'];
export type OrganizationDirectory = components['schemas']['OrganizationDirectoryResponse'];
export type OrganizationDetail = components['schemas']['OrganizationResponse'];
export type AdministrationVersion = components['schemas']['AdministrationVersionReceipt'];
export type MembershipPage = components['schemas']['PageOfMembershipDirectoryItem'];

export type AdministrationError =
  | Exclude<AppError, { code: 'concurrency_conflict' }>
  | {
      readonly code: 'concurrency_conflict';
      readonly latestVersion?: string;
    };

/** Aggregate versions never pass through a JavaScript number, including error hints. */
export type AdministrationResult<T, Status extends 200 | 201 = 200> =
  | Exclude<ApiResult<T>, { kind: 'success' } | { kind: 'api-error' }>
  | {
      readonly kind: 'api-error';
      readonly status: number;
      readonly error: AdministrationError;
      readonly problem: Extract<ApiResult<T>, { kind: 'api-error' }>['problem'];
    }
  | {
      readonly kind: 'success';
      readonly status: Status;
      readonly data: T;
      readonly etag: string | null;
      readonly location: string | null;
      readonly replayed: boolean;
    };

export type AdministrationFetch = (path: string, init: RequestInit) => Promise<Response>;
export type AdministrationReadOptions = Readonly<{ signal?: AbortSignal }>;
export type AdministrationPageOptions = Readonly<{
  cursor?: string;
  limit?: number;
  signal?: AbortSignal;
}>;
export type AdministrationWriteOptions = Readonly<{
  version: string;
  idempotencyKey: string;
  signal?: AbortSignal;
}>;
export type AdministrationCreateOptions = Readonly<{
  idempotencyKey: string;
  signal?: AbortSignal;
}>;
export type ProfileInput = Readonly<{ displayName: string | null; preferredLocale: string | null }>;
export type MembershipState = 'Active' | 'Suspended' | 'Revoked';
export type GrantInput = Readonly<{ scope: 'Tenant' | 'Organization'; key: string }>;

/** Exact wire ownership; these declarations grant no native caller or browser transport. */
export const ADMINISTRATION_GET_CONTRACTS = Object.freeze({
  '/api/v1/identity/organization-memberships': 'ListOrganizationMemberships',
  '/api/v1/identity/memberships/{id}': 'GetMembershipDetail',
  '/api/v1/identity/me/profile': 'GetOwnMembershipProfile',
  '/api/v1/identity/me/contexts': 'ListOwnMembershipContexts',
  '/api/v1/identity/me/capabilities': 'GetOwnCapabilities',
  '/api/v1/identity/roles': 'ListRoles',
  '/api/v1/identity/roles/{id}': 'GetRoleDetail',
  '/api/v1/identity/permissions': 'GetPermissionRegistry',
  '/api/v1/tenancy/organizations': 'ListOrganizations',
  '/api/v1/tenancy/organizations/{id}': 'GetOrganizationDetail',
} as const);
