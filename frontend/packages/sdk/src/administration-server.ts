import type {
  AdministrationCreateOptions,
  AdministrationFetch,
  AdministrationPageOptions,
  AdministrationReadOptions,
  AdministrationResult,
  AdministrationVersion,
  AdministrationWriteOptions,
  GrantInput,
  MembershipDetail,
  MembershipPage,
  MembershipState,
  OrganizationDetail,
  OrganizationDirectory,
  OwnCapabilities,
  OwnMembershipContexts,
  OwnMembershipProfile,
  PermissionRegistry,
  ProfileInput,
  RoleDetail,
  RoleDirectory,
} from './administration-contract.js';
import {
  displayName as validDisplayName,
  fields,
  grants,
  identifier,
  isAdministrationSuccess,
  object,
  profileInput,
  slug,
  text,
  version,
} from './administration-policy.js';
import type { AdministrationShape } from './administration-policy.js';
import { appError, parseProblem } from './response-policy.js';

export type * from './administration-contract.js';

class BodyLimit extends Error {
  constructor() {
    super('Administration response exceeds its limit');
  }
}

/** Reads a bounded UTF-8 body and joins actual cancellation before releasing its reader. */
async function json(response: Response, signal?: AbortSignal): Promise<unknown> {
  const reader = response.body?.getReader();
  if (!reader) throw new SyntaxError('Administration response is empty');
  let cancelling: Promise<void> | undefined;
  const cancel = () => {
    cancelling ??= reader.cancel().catch(() => undefined);
  };
  signal?.addEventListener('abort', cancel, { once: true });
  const chunks: Uint8Array[] = [];
  let bytes = 0;
  try {
    if (signal?.aborted) cancel();
    for (;;) {
      const next = await reader.read();
      if (signal?.aborted) throw new DOMException('Cancelled', 'AbortError');
      if (next.done) break;
      bytes += next.value.byteLength;
      if (bytes > 256 * 1024) {
        cancel();
        throw new BodyLimit();
      }
      chunks.push(next.value);
    }
    const body = new Uint8Array(bytes);
    let position = 0;
    for (const chunk of chunks) {
      body.set(chunk, position);
      position += chunk.byteLength;
    }
    try {
      return JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(body)) as unknown;
    } catch {
      throw new SyntaxError('Administration response JSON is invalid');
    }
  } finally {
    signal?.removeEventListener('abort', cancel);
    if (cancelling) await cancelling;
    reader.releaseLock();
  }
}

function resource(prefix: string, id: string): string | undefined {
  return identifier(id) ? prefix + '/' + id : undefined;
}
function page(path: string, options?: AdministrationPageOptions): string | undefined {
  if (options?.cursor !== undefined && !text(options.cursor, 1024)) return undefined;
  if (
    options?.limit !== undefined &&
    (!Number.isInteger(options.limit) || options.limit < 1 || options.limit > 2147483647)
  )
    return undefined;
  const query = new URLSearchParams();
  if (options?.cursor !== undefined) query.set('cursor', options.cursor);
  if (options?.limit !== undefined) query.set('limit', String(Math.min(options.limit, 100)));
  return path + (query.size ? '?' + query.toString() : '');
}
function write(
  options: AdministrationCreateOptions | undefined,
  needsVersion: boolean,
): Record<string, string> | undefined {
  if (
    !options ||
    typeof options.idempotencyKey !== 'string' ||
    !/^[\x21-\x7e]{8,128}$/.test(options.idempotencyKey)
  )
    return undefined;
  const headers: Record<string, string> = {
    'Content-Type': 'application/json',
    'Idempotency-Key': options.idempotencyKey,
  };
  if (needsVersion) {
    if (!('version' in options) || !version(options.version)) return undefined;
    headers['If-Match'] = '"' + options.version + '"';
  }
  return headers;
}

/** Closed operation wrappers. The injected server caller owns credentials, origin and lifetime. */
export function createAdministrationServerSdk(transport: AdministrationFetch) {
  async function request<T, Status extends 200 | 201 = 200>(
    shape: AdministrationShape,
    build: () =>
      | {
          path: string | undefined;
          method?: string;
          body?: unknown;
          headers?: Record<string, string>;
        }
      | undefined,
    expected: Status,
    options?: AdministrationReadOptions,
  ): Promise<AdministrationResult<T, Status>> {
    const signal = options?.signal;
    if (signal?.aborted) return { kind: 'cancelled' };
    let plan: ReturnType<typeof build>;
    let body: string | undefined;
    try {
      plan = build();
      if (!plan?.path) return { kind: 'invalid-request' };
      body = plan.body === undefined ? undefined : JSON.stringify(plan.body);
      if (body !== undefined && new TextEncoder().encode(body).length > 16 * 1024)
        return { kind: 'invalid-request' };
    } catch {
      return { kind: 'invalid-request' };
    }
    let response: Response;
    try {
      response = await transport(plan.path, {
        method: plan.method ?? 'GET',
        cache: 'no-store',
        headers: { Accept: 'application/json', ...plan.headers },
        ...(body === undefined ? {} : { body }),
        signal,
      });
    } catch {
      return { kind: signal?.aborted ? 'cancelled' : 'transport-error' };
    }
    if (response.status !== expected && response.status < 400) {
      try {
        await response.body?.cancel();
      } catch {
        /* No provider payload leaves the SDK. */
      }
      return signal?.aborted
        ? { kind: 'cancelled' }
        : { kind: 'invalid-response', reason: 'unexpected-status' };
    }
    let value: unknown;
    try {
      value = await json(response, signal);
    } catch (error) {
      if (signal?.aborted) return { kind: 'cancelled' };
      if (error instanceof SyntaxError)
        return { kind: 'invalid-response', reason: 'malformed-json' };
      if (error instanceof BodyLimit)
        return {
          kind: 'invalid-response',
          reason: response.status === expected ? 'invalid-success' : 'invalid-problem',
        };
      return { kind: 'transport-error' };
    }
    if (signal?.aborted) return { kind: 'cancelled' };
    if (response.status !== expected) {
      const problem = parseProblem(value, response.status);
      if (!problem) return { kind: 'invalid-response', reason: 'invalid-problem' };
      const error = appError(problem, response.headers.get('Retry-After'));
      return {
        kind: 'api-error',
        status: response.status,
        problem,
        error: error.code === 'concurrency_conflict' ? { code: 'concurrency_conflict' } : error,
      };
    }
    const etag = response.headers.get('ETag');
    const location = response.headers.get('Location');
    const replay = response.headers.get('Idempotency-Replayed');
    const versioned =
      shape === 'version' ||
      shape === 'member' ||
      shape === 'profile' ||
      shape === 'role' ||
      shape === 'organization';
    const contentType = response.headers
      .get('Content-Type')
      ?.split(';', 1)[0]
      ?.trim()
      .toLowerCase();
    if (
      contentType !== 'application/json' ||
      !isAdministrationSuccess(shape, value) ||
      (versioned ? !object(value) || etag !== '"' + value.version + '"' : etag !== null) ||
      (expected === 201
        ? location === null ||
          !/^\/api\/v1\/tenancy\/organizations\/[0-9a-f-]+$/.test(location) ||
          !identifier(location.slice('/api/v1/tenancy/organizations/'.length))
        : location !== null) ||
      (replay !== null && (shape !== 'version' || replay !== 'true'))
    )
      return { kind: 'invalid-response', reason: 'invalid-success' };
    return {
      kind: 'success',
      status: expected,
      data: value as T,
      etag,
      location,
      replayed: replay === 'true',
    };
  }
  function mutation<T = AdministrationVersion, Status extends 200 | 201 = 200>(
    path: string | undefined,
    method: 'PUT' | 'POST',
    body: unknown,
    valid: boolean,
    options: AdministrationCreateOptions,
    expected: Status = 200 as Status,
  ): Promise<AdministrationResult<T, Status>> {
    return request<T, Status>(
      'version',
      () => {
        const headers = write(options, expected !== 201);
        return valid && headers ? { path, method, headers, body } : undefined;
      },
      expected,
      options,
    );
  }
  return Object.freeze({
    listMemberships: (options?: AdministrationPageOptions) =>
      request<MembershipPage>(
        'members',
        () => ({ path: page('/api/v1/identity/memberships', options) }),
        200,
        options,
      ),
    listOrganizationMemberships: (options?: AdministrationPageOptions) =>
      request<MembershipPage>(
        'members',
        () => ({ path: page('/api/v1/identity/organization-memberships', options) }),
        200,
        options,
      ),
    getMembership: (id: string, options?: AdministrationReadOptions) =>
      request<MembershipDetail>(
        'member',
        () => ({ path: resource('/api/v1/identity/memberships', id) }),
        200,
        options,
      ),
    getOwnProfile: (options?: AdministrationReadOptions) =>
      request<OwnMembershipProfile>(
        'profile',
        () => ({ path: '/api/v1/identity/me/profile' }),
        200,
        options,
      ),
    getOwnContexts: (options?: AdministrationPageOptions) =>
      request<OwnMembershipContexts>(
        'contexts',
        () => ({ path: page('/api/v1/identity/me/contexts', options) }),
        200,
        options,
      ),
    getOwnCapabilities: (options?: AdministrationReadOptions) =>
      request<OwnCapabilities>(
        'capabilities',
        () => ({ path: '/api/v1/identity/me/capabilities' }),
        200,
        options,
      ),
    listRoles: (options?: AdministrationReadOptions) =>
      request<RoleDirectory>('roles', () => ({ path: '/api/v1/identity/roles' }), 200, options),
    getRole: (id: string, options?: AdministrationReadOptions) =>
      request<RoleDetail>(
        'role',
        () => ({ path: resource('/api/v1/identity/roles', id) }),
        200,
        options,
      ),
    getPermissions: (options?: AdministrationReadOptions) =>
      request<PermissionRegistry>(
        'permissions',
        () => ({ path: '/api/v1/identity/permissions' }),
        200,
        options,
      ),
    listOrganizations: (options?: AdministrationPageOptions) =>
      request<OrganizationDirectory>(
        'organizations',
        () => ({ path: page('/api/v1/tenancy/organizations', options) }),
        200,
        options,
      ),
    getOrganization: (id: string, options?: AdministrationReadOptions) =>
      request<OrganizationDetail>(
        'organization',
        () => ({ path: resource('/api/v1/tenancy/organizations', id) }),
        200,
        options,
      ),
    setMembershipProfile: (id: string, input: ProfileInput, options: AdministrationWriteOptions) =>
      mutation(
        resource('/api/v1/identity/memberships', id)?.concat('/profile'),
        'PUT',
        input,
        profileInput(input),
        options,
      ),
    setOwnProfile: (input: ProfileInput, options: AdministrationWriteOptions) =>
      mutation('/api/v1/identity/me/profile', 'PUT', input, profileInput(input), options),
    replaceMembershipRoles: (
      id: string,
      roleIds: readonly string[],
      options: AdministrationWriteOptions,
    ) =>
      mutation(
        resource('/api/v1/identity/memberships', id)?.concat('/role-assignments'),
        'PUT',
        { roleIds },
        Array.isArray(roleIds) &&
          roleIds.length <= 8 &&
          roleIds.every(identifier) &&
          new Set(roleIds).size === roleIds.length,
        options,
      ),
    changeMembershipStatus: (
      id: string,
      status: MembershipState,
      options: AdministrationWriteOptions,
    ) =>
      mutation(
        resource('/api/v1/identity/memberships', id)?.concat('/status'),
        'PUT',
        { status },
        ['Active', 'Suspended', 'Revoked'].includes(status),
        options,
      ),
    changeRoleGrants: (
      id: string,
      input: readonly GrantInput[],
      options: AdministrationWriteOptions,
    ) =>
      mutation(
        resource('/api/v1/identity/roles', id)?.concat('/grants'),
        'PUT',
        { grants: input },
        grants(input),
        options,
      ),
    adoptRoleManifest: (
      id: string,
      manifestVersion: number,
      manifestFingerprint: string,
      options: AdministrationWriteOptions,
    ) =>
      mutation(
        resource('/api/v1/identity/roles', id)?.concat('/manifest-adoptions'),
        'POST',
        { manifestVersion, manifestFingerprint },
        manifestVersion === 2 &&
          typeof manifestFingerprint === 'string' &&
          /^[0-9a-f]{64}$/.test(manifestFingerprint),
        options,
      ),
    createOrganization: (
      input: Readonly<{ slug: string; displayName: string }>,
      options: AdministrationCreateOptions,
    ) =>
      mutation<AdministrationVersion, 201>(
        '/api/v1/tenancy/organizations',
        'POST',
        input,
        fields(input, ['slug', 'displayName']) &&
          slug(input.slug) &&
          validDisplayName(input.displayName),
        options,
        201,
      ),
    renameOrganization: (id: string, displayName: string, options: AdministrationWriteOptions) =>
      mutation(
        resource('/api/v1/tenancy/organizations', id),
        'PUT',
        { displayName },
        validDisplayName(displayName),
        options,
      ),
    archiveOrganization: (id: string, options: AdministrationWriteOptions) =>
      mutation(
        resource('/api/v1/tenancy/organizations', id)?.concat('/archive'),
        'POST',
        {},
        true,
        options,
      ),
    selectDefaultOrganization: (organizationId: string, options: AdministrationWriteOptions) =>
      mutation(
        '/api/v1/tenancy/default-organization',
        'PUT',
        { organizationId },
        identifier(organizationId),
        options,
      ),
  });
}

export type AdministrationServerSdk = ReturnType<typeof createAdministrationServerSdk>;
