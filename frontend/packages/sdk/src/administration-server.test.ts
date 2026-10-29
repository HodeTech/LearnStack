import { describe, expect, expectTypeOf, it, vi } from 'vitest';

import type { AdministrationFetch, AdministrationResult } from './administration-contract';
import { createAdministrationServerSdk } from './administration-server';
import { createAuthenticatedServerSdk } from './authenticated-server';
import { createServerSdk } from './server';

const member = '01930000-0000-7000-8000-00000000a001';
const organization = '01930000-0000-7000-8000-0000000000a1';
const rowVersion = '9007199254740993';
const options = { version: rowVersion, idempotencyKey: 'request-key-123' };
const profile = { displayName: 'İpek', preferredLocale: 'tr' };
const json = (body: unknown, status = 200, headers: Record<string, string> = {}) =>
  new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json; charset=utf-8', ...headers },
  });
const version = (status = 200, headers: Record<string, string> = {}) =>
  json({ version: rowVersion }, status, { ETag: '"' + rowVersion + '"', ...headers });
const problem = (status: number, code: string) =>
  json(
    {
      type: 'https://errors.learnstack.dev/' + code,
      title: 'Refused',
      status,
      instance: '/api/v1/identity/me/profile',
      code,
      messageKey: 'lockey_' + code,
      correlationId: 'request-123',
    },
    status,
  );

describe('closed administration server SDK', () => {
  it('keeps public and AccountSite callers separate and exposes no generic request method', () => {
    const transport = vi.fn<AdministrationFetch>(async () => version());
    expect(Object.keys(createAuthenticatedServerSdk(transport))).toEqual(['getAccountSite']);
    expect(Object.keys(createServerSdk(transport))).toEqual([
      'getSite',
      'getCourses',
      'getCourse',
      'getLesson',
    ]);
    const sdk = createAdministrationServerSdk(transport);
    expect(Object.isFrozen(sdk)).toBe(true);
    expect(Object.keys(sdk)).toHaveLength(21);
    expect(sdk).not.toHaveProperty('request');
    expectTypeOf<
      Extract<AdministrationResult<unknown, 201>, { kind: 'success' }>['status']
    >().toEqualTypeOf<201>();
    expectTypeOf<
      Extract<AdministrationResult<unknown>, { kind: 'success' }>['status']
    >().toEqualTypeOf<200>();
  });

  it('preserves unsafe-integer aggregate versions as strings in preconditions, JSON and metadata', async () => {
    const transport = vi.fn<AdministrationFetch>(async () =>
      version(200, { 'Idempotency-Replayed': 'true' }),
    );
    const result = await createAdministrationServerSdk(transport).setMembershipProfile(
      member,
      profile,
      options,
    );
    expect(result).toEqual({
      kind: 'success',
      status: 200,
      data: { version: rowVersion },
      etag: '"' + rowVersion + '"',
      location: null,
      replayed: true,
    });
    const call = transport.mock.calls[0]!;
    expect(call[0]).toBe('/api/v1/identity/memberships/' + member + '/profile');
    expect(call[1]).toEqual({
      method: 'PUT',
      cache: 'no-store',
      headers: {
        Accept: 'application/json',
        'Content-Type': 'application/json',
        'Idempotency-Key': options.idempotencyKey,
        'If-Match': '"' + rowVersion + '"',
      },
      body: JSON.stringify(profile),
      signal: undefined,
    });
  });

  it('keeps creation 201 and its same-API Location distinct from update 200', async () => {
    const transport = vi.fn<AdministrationFetch>(async () =>
      version(201, { Location: '/api/v1/tenancy/organizations/' + organization }),
    );
    const result = await createAdministrationServerSdk(transport).createOrganization(
      { slug: 'studio', displayName: 'Studio' },
      options,
    );
    expect(result.kind).toBe('success');
    if (result.kind === 'success') {
      expect(result.status).toBe(201);
      expect(result.location).toBe('/api/v1/tenancy/organizations/' + organization);
    }
    expect(transport.mock.calls[0]![1].headers).not.toHaveProperty('If-Match');
  });

  it.each([
    ['setOwnProfile', '/api/v1/identity/me/profile', 'PUT', [profile, options]],
    [
      'replaceMembershipRoles',
      '/api/v1/identity/memberships/' + member + '/role-assignments',
      'PUT',
      [member, [], options],
    ],
    [
      'changeMembershipStatus',
      '/api/v1/identity/memberships/' + member + '/status',
      'PUT',
      [member, 'Suspended', options],
    ],
    [
      'changeRoleGrants',
      '/api/v1/identity/roles/' + member + '/grants',
      'PUT',
      [member, [], options],
    ],
    [
      'adoptRoleManifest',
      '/api/v1/identity/roles/' + member + '/manifest-adoptions',
      'POST',
      [member, 2, 'a'.repeat(64), options],
    ],
    [
      'renameOrganization',
      '/api/v1/tenancy/organizations/' + organization,
      'PUT',
      [organization, 'Renamed', options],
    ],
    [
      'archiveOrganization',
      '/api/v1/tenancy/organizations/' + organization + '/archive',
      'POST',
      [organization, options],
    ],
    [
      'selectDefaultOrganization',
      '/api/v1/tenancy/default-organization',
      'PUT',
      [organization, options],
    ],
  ] as const)(
    'constructs only the declared %s mutation',
    async (method, path, verb, arguments_) => {
      const transport = vi.fn<AdministrationFetch>(async () => version());
      const sdk = createAdministrationServerSdk(transport);
      await Reflect.apply(sdk[method], undefined, arguments_);
      expect(transport).toHaveBeenCalledTimes(1);
      expect(transport.mock.calls[0]![0]).toBe(path);
      expect(transport.mock.calls[0]![1].method).toBe(verb);
      expect(transport.mock.calls[0]![1].cache).toBe('no-store');
      expect(transport.mock.calls[0]![1].headers).toHaveProperty(
        'If-Match',
        '"' + rowVersion + '"',
      );
    },
  );

  it.each(['', '01', '-1', '1.0', '1e3', '9223372036854775808', '"1"', '9007199254740993 '])(
    'refuses a noncanonical version %s before I/O',
    async (invalid) => {
      const transport = vi.fn<AdministrationFetch>(async () => version());
      expect(
        await createAdministrationServerSdk(transport).setOwnProfile(profile, {
          ...options,
          version: invalid,
        }),
      ).toEqual({ kind: 'invalid-request' });
      expect(transport).not.toHaveBeenCalled();
    },
  );

  it.each(['A', '-studio', 'studio-', 'two--studios', 'studio\n', 'x'.repeat(64)])(
    'refuses an invalid Organization slug %j before I/O',
    async (slug) => {
      const transport = vi.fn<AdministrationFetch>(async () => version());
      expect(
        await createAdministrationServerSdk(transport).createOrganization(
          { slug, displayName: 'Studio' },
          options,
        ),
      ).toEqual({ kind: 'invalid-request' });
      expect(transport).not.toHaveBeenCalled();
    },
  );

  it('refuses extra profile fields, malformed selectors, duplicate/foreign grants and duplicate Role IDs before I/O', async () => {
    const transport = vi.fn<AdministrationFetch>(async () => version());
    const sdk = createAdministrationServerSdk(transport);
    const forgedProfile = { ...profile, tenantId: organization };
    expect(await sdk.setOwnProfile(forgedProfile, options)).toEqual({ kind: 'invalid-request' });
    expect(await sdk.getMembership('../account/site')).toEqual({ kind: 'invalid-request' });
    expect(await sdk.getRole(member.toUpperCase())).toEqual({ kind: 'invalid-request' });
    expect(await sdk.replaceMembershipRoles(member, [member, member], options)).toEqual({
      kind: 'invalid-request',
    });
    expect(
      await sdk.changeRoleGrants(
        member,
        [{ scope: 'Organization', key: 'identity.role.admin' }],
        options,
      ),
    ).toEqual({ kind: 'invalid-request' });
    expect(
      await sdk.changeRoleGrants(
        member,
        Array(9).fill({ scope: 'Tenant', key: 'identity.role.admin' }),
        options,
      ),
    ).toEqual({ kind: 'invalid-request' });
    expect(await sdk.setOwnProfile({ ...profile, displayName: '\ud800' }, options)).toEqual({
      kind: 'invalid-request',
    });
    expect(transport).not.toHaveBeenCalled();
  });

  it.each([
    [201, { version: rowVersion }, { ETag: '"' + rowVersion + '"' }],
    [200, { version: 9007199254740992 }, { ETag: '"' + rowVersion + '"' }],
    [200, { version: rowVersion }, { ETag: 'W/"' + rowVersion + '"' }],
    [200, { version: rowVersion }, { ETag: '"0"' }],
    [
      200,
      { version: rowVersion },
      { ETag: '"' + rowVersion + '"', Location: 'https://other.example/' },
    ],
    [
      200,
      { version: rowVersion },
      { ETag: '"' + rowVersion + '"', 'Idempotency-Replayed': 'false' },
    ],
    [
      200,
      { version: rowVersion, displayName: 'unexpected sensitive copy' },
      { ETag: '"' + rowVersion + '"' },
    ],
  ])('withholds malformed update success %#', async (status, body, headers) => {
    const transport = vi.fn<AdministrationFetch>(async () =>
      json(body, status as number, headers as Record<string, string>),
    );
    expect(
      (await createAdministrationServerSdk(transport).setOwnProfile(profile, options)).kind,
    ).toBe('invalid-response');
    expect(transport).toHaveBeenCalledTimes(1);
  });

  it.each([
    'https://other.example/api/v1/tenancy/organizations/' + organization,
    '/api/v1/tenancy/organizations/' + organization + '?x=1',
    '/api/v1/tenancy/organizations/../identity',
  ])('refuses foreign or noncanonical creation Location %s', async (location) => {
    const transport = vi.fn<AdministrationFetch>(async () => version(201, { Location: location }));
    expect(
      (
        await createAdministrationServerSdk(transport).createOrganization(
          { slug: 'new', displayName: 'New' },
          options,
        )
      ).kind,
    ).toBe('invalid-response');
  });

  it('keeps a scoped profile 404, a conflict and dependency refusal as typed API errors without retry', async () => {
    for (const [status, code] of [
      [404, 'not_found'],
      [409, 'concurrency_conflict'],
      [503, 'dependency_unavailable'],
    ] as const) {
      const transport = vi.fn<AdministrationFetch>(async () => problem(status, code));
      const result = await createAdministrationServerSdk(transport).getOwnProfile();
      expect(result.kind).toBe('api-error');
      if (result.kind === 'api-error') expect(result.error.code).toBe(code);
      expect(transport).toHaveBeenCalledTimes(1);
    }
  });

  it('clamps paging and preserves an opaque cursor without adding an actor or scope selector', async () => {
    const transport = vi.fn<AdministrationFetch>(async () =>
      json({
        items: [],
        pageInfo: { nextCursor: null, previousCursor: null, hasNext: false, hasPrevious: false },
      }),
    );
    expect(
      (
        await createAdministrationServerSdk(transport).listOrganizationMemberships({
          cursor: 'opaque+/=',
          limit: 1000,
        })
      ).kind,
    ).toBe('success');
    expect(transport.mock.calls[0]![0]).toBe(
      '/api/v1/identity/organization-memberships?cursor=opaque%2B%2F%3D&limit=100',
    );
  });

  it('accepts current resource and own-navigation wire shapes without coercing their fields', async () => {
    const pageInfo = { nextCursor: null, previousCursor: null, hasNext: false, hasPrevious: false };
    const detail = {
      membershipId: member,
      userId: member,
      organizationId: null,
      status: 'Active',
      displayName: 'Learner',
      preferredLocale: 'tr',
      roleIds: [member],
      version: rowVersion,
    };
    const role = {
      roleId: member,
      builtInKey: 'TenantAdmin',
      capabilityScope: 'Tenant',
      version: rowVersion,
      grants: [{ scope: 'Tenant', key: 'identity.role.admin' }],
      adoptedReleaseVersion: 2,
      adoptedReleaseFingerprint: 'a'.repeat(64),
    };
    const organizationDetail = {
      organizationId: organization,
      slug: 'studio-one',
      displayName: 'Studio One',
      status: 'Active',
      isDefault: true,
      version: rowVersion,
    };
    const transport = vi.fn<AdministrationFetch>(async (path) => {
      if (path === '/api/v1/identity/memberships/' + member)
        return json(detail, 200, { ETag: '"' + rowVersion + '"' });
      if (path === '/api/v1/identity/roles/' + member)
        return json(role, 200, { ETag: '"' + rowVersion + '"' });
      if (path === '/api/v1/tenancy/organizations/' + organization)
        return json(organizationDetail, 200, { ETag: '"' + rowVersion + '"' });
      if (path === '/api/v1/tenancy/organizations')
        return json({
          items: [organizationDetail],
          defaultOrganizationId: organization,
          tenantVersion: rowVersion,
          pageInfo,
        });
      return json({
        items: [
          {
            tenantId: member,
            organizationId: organization,
            tenantName: 'School',
            organizationName: 'Studio One',
            entryOrigin: 'https://school.example:3000',
            canUseCurrentHost: false,
          },
        ],
        pageInfo,
      });
    });
    const sdk = createAdministrationServerSdk(transport);
    for (const result of [
      await sdk.getMembership(member),
      await sdk.getRole(member),
      await sdk.getOrganization(organization),
      await sdk.listOrganizations(),
      await sdk.getOwnContexts(),
    ])
      expect(result.kind).toBe('success');
  });

  it('rejects nested internal data, invalid page coherence and foreign entry origins', async () => {
    const contexts = {
      items: [
        {
          tenantId: member,
          organizationId: null,
          tenantName: 'School',
          organizationName: null,
          entryOrigin: 'https://school.example',
          canUseCurrentHost: false,
        },
      ],
      pageInfo: { nextCursor: null, previousCursor: null, hasNext: false, hasPrevious: false },
    };
    const transport = vi.fn<AdministrationFetch>(async () => json(contexts));
    const sdk = createAdministrationServerSdk(transport);
    expect((await sdk.getOwnContexts()).kind).toBe('success');
    for (const body of [
      { ...contexts, items: [{ ...contexts.items[0], subjectKey: 'private' }] },
      { ...contexts, items: [{ ...contexts.items[0], entryOrigin: 'http://school.example' }] },
      {
        ...contexts,
        items: [{ ...contexts.items[0], entryOrigin: 'https://school.example/path' }],
      },
      {
        ...contexts,
        items: [{ ...contexts.items[0], organizationName: 'Unexpected branch name' }],
      },
      { ...contexts, pageInfo: { ...contexts.pageInfo, hasNext: true } },
      { ...contexts, pageInfo: { ...contexts.pageInfo, previousCursor: 'cursor' } },
    ]) {
      transport.mockImplementation(async () => json(body));
      expect((await sdk.getOwnContexts()).kind).toBe('invalid-response');
    }
  });

  it('rejects a stale seven-flag preview and a resource ETag on current capabilities', async () => {
    const flags = {
      canReadTenantMembers: true,
      canReadOrganizationMembers: false,
      canWriteMemberProfiles: true,
      canManageMemberships: true,
      canReadRoles: true,
      canChangeRoleGrants: true,
      canAdoptRoleRelease: true,
      canWriteOwnProfile: true,
      canManageOrganizations: true,
    };
    const transport = vi.fn<AdministrationFetch>(async () => json(flags));
    expect((await createAdministrationServerSdk(transport).getOwnCapabilities()).kind).toBe(
      'success',
    );
    transport.mockImplementation(async () => json(flags, 200, { ETag: '"0"' }));
    expect((await createAdministrationServerSdk(transport).getOwnCapabilities()).kind).toBe(
      'invalid-response',
    );
    const { canChangeRoleGrants: _change, canAdoptRoleRelease: _adopt, ...seven } = flags;
    transport.mockImplementation(async () => json(seven));
    expect((await createAdministrationServerSdk(transport).getOwnCapabilities()).kind).toBe(
      'invalid-response',
    );
  });

  it('classifies transport and malformed JSON without retaining provider details', async () => {
    const transport = vi.fn<AdministrationFetch>(async (): Promise<Response> => {
      throw new Error('private provider detail');
    });
    const sdk = createAdministrationServerSdk(transport);
    expect(await sdk.getOwnProfile()).toEqual({ kind: 'transport-error' });
    transport.mockImplementation(
      async () => new Response('{', { headers: { 'Content-Type': 'application/json' } }),
    );
    expect(await sdk.getOwnProfile()).toEqual({
      kind: 'invalid-response',
      reason: 'malformed-json',
    });
    transport.mockImplementation(async () => json({ payload: 'x'.repeat(256 * 1024) }));
    expect(await sdk.getOwnProfile()).toEqual({
      kind: 'invalid-response',
      reason: 'invalid-success',
    });
  });

  it('waits for actual body cancellation settlement instead of treating aborted wait as cleanup', async () => {
    let release!: () => void;
    let observed!: () => void;
    const cancelled = new Promise<void>((resolve) => {
      observed = resolve;
    });
    const held = new Promise<void>((resolve) => {
      release = resolve;
    });
    const body = new ReadableStream<Uint8Array>({
      cancel() {
        observed();
        return held;
      },
    });
    const transport = vi.fn<AdministrationFetch>(
      async () => new Response(body, { headers: { 'Content-Type': 'application/json' } }),
    );
    const controller = new AbortController();
    let settled = false;
    const result = createAdministrationServerSdk(transport).getOwnProfile({
      signal: controller.signal,
    });
    void result.then(() => {
      settled = true;
    });
    await vi.waitFor(() => expect(body.locked).toBe(true));
    controller.abort();
    await cancelled;
    await new Promise((resolve) => setTimeout(resolve, 20));
    expect(settled).toBe(false);
    release();
    expect(await result).toEqual({ kind: 'cancelled' });
    expect(body.locked).toBe(false);
    expect(transport).toHaveBeenCalledTimes(1);
  });
});
