import { describe, expect, it, vi } from 'vitest';

import type {
  AdministrationFetch,
  InvitationInput,
  InvitationPolicyWriteOptions,
} from './administration-contract.js';
import { createAdministrationServerSdk } from './administration-server.js';

const invitationId = '01930000-0000-7000-8000-000000000001';
const roleId = '01930000-0000-7000-8000-000000000002';
const version = '9223372036854775807';
const key = 'invitation-exact-key';
const input = { email: 'Person+training@EXAMPLE.invalid', roleId, roleVersion: version };
const row = {
  invitationId,
  email: 'person+training@example.invalid',
  organizationId: null,
  roleId,
  roleVersion: version,
  status: 'Pending',
  expiresAt: '2026-11-18T12:00:00.0000000+00:00',
  version,
  deliveryStatus: 'Queued',
};
const pageInfo = { nextCursor: null, previousCursor: null, hasNext: false, hasPrevious: false };

function json(body: unknown, status = 200, headers: Record<string, string> = {}): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json', ...headers },
  });
}

describe('bounded invitation administration SDK', () => {
  it('creates through the exact path and preserves Int64 versions without a precondition or secret response', async () => {
    const transport = vi.fn<AdministrationFetch>(async () =>
      json({ version }, 201, {
        ETag: '"' + version + '"',
        Location: '/api/v1/identity/invitations/' + invitationId,
      }),
    );
    const result = await createAdministrationServerSdk(transport).createInvitation(input, {
      idempotencyKey: key,
    });
    expect(result).toMatchObject({
      kind: 'success',
      status: 201,
      data: { version },
      replayed: false,
    });
    expect(transport).toHaveBeenCalledTimes(1);
    const [path, init] = transport.mock.calls[0]!;
    expect(path).toBe('/api/v1/identity/invitations');
    expect(init.method).toBe('POST');
    expect(init.cache).toBe('no-store');
    expect(JSON.parse(init.body as string)).toEqual(input);
    const headers = new Headers(init.headers);
    expect(headers.get('Idempotency-Key')).toBe(key);
    expect(headers.has('If-Match')).toBe(false);
    expect(headers.has('If-None-Match')).toBe(false);
  });

  it('rejects another creation family Location and additional response credentials', async () => {
    const transport = vi.fn<AdministrationFetch>(async () =>
      json({ version }, 201, {
        ETag: '"' + version + '"',
        Location: '/api/v1/tenancy/organizations/' + invitationId,
      }),
    );
    const sdk = createAdministrationServerSdk(transport);
    expect((await sdk.createInvitation(input, { idempotencyKey: key })).kind).toBe(
      'invalid-response',
    );
    transport.mockImplementation(async () =>
      json({ version, code: 'not-a-public-contract' }, 201, {
        ETag: '"' + version + '"',
        Location: '/api/v1/identity/invitations/' + invitationId,
      }),
    );
    expect((await sdk.createInvitation(input, { idempotencyKey: key })).kind).toBe(
      'invalid-response',
    );
  });

  it('refuses malformed mailboxes, version numbers and authority selectors before transport', async () => {
    const transport = vi.fn<AdministrationFetch>();
    const sdk = createAdministrationServerSdk(transport);
    for (const body of [
      { ...input, email: 'A Name <person@example.invalid>' },
      { ...input, email: 'person\r\n@example.invalid' },
      { ...input, email: 'pérson@example.invalid' },
      { ...input, email: 'person..name@example.invalid' },
      { ...input, email: 'person@-example.invalid' },
      { ...input, roleVersion: 42 },
      { ...input, roleVersion: '00' },
      { ...input, roleVersion: '9223372036854775808' },
      { ...input, organizationId: invitationId },
      { ...input, inviterUserId: invitationId },
    ])
      expect(await sdk.createInvitation(body as InvitationInput, { idempotencyKey: key })).toEqual({
        kind: 'invalid-request',
      });
    expect(transport).not.toHaveBeenCalled();
  });

  it('keeps policy absence separate from a stored version-zero row and checks ETag coherence', async () => {
    const absent = { days: 14, present: false, version: null, canEdit: false };
    const stored = { days: 21, present: true, version: '0', canEdit: true };
    const transport = vi.fn<AdministrationFetch>(async () => json(absent));
    const sdk = createAdministrationServerSdk(transport);
    expect((await sdk.getInvitationPolicy()).kind).toBe('success');
    transport.mockImplementation(async () => json(stored, 200, { ETag: '"0"' }));
    expect((await sdk.getInvitationPolicy()).kind).toBe('success');
    for (const [body, headers] of [
      [absent, { ETag: '"0"' }],
      [{ ...absent, days: 21 }, {}],
      [{ ...absent, version: '0' }, {}],
      [stored, {}],
      [stored, { ETag: '"1"' }],
      [{ ...stored, version: 0 }, { ETag: '"0"' }],
      [{ ...stored, privateSetting: 'value' }, { ETag: '"0"' }],
    ] as const) {
      transport.mockImplementation(async () => json(body, 200, headers));
      expect((await sdk.getInvitationPolicy()).kind).toBe('invalid-response');
    }
  });

  it('writes absent and present policy with distinct mutually exclusive preconditions', async () => {
    const transport = vi.fn<AdministrationFetch>(async () =>
      json({ version: '0' }, 200, { ETag: '"0"', 'Idempotency-Replayed': 'true' }),
    );
    const sdk = createAdministrationServerSdk(transport);
    for (const precondition of [{ kind: 'absent' }, { kind: 'present', version: '0' }] as const) {
      expect(
        await sdk.setInvitationLifetime(14, { idempotencyKey: key, precondition }),
      ).toMatchObject({ kind: 'success', status: 200, replayed: true });
    }
    const first = new Headers(transport.mock.calls[0]![1].headers);
    const second = new Headers(transport.mock.calls[1]![1].headers);
    expect(first.get('If-None-Match')).toBe('*');
    expect(first.has('If-Match')).toBe(false);
    expect(second.get('If-Match')).toBe('"0"');
    expect(second.has('If-None-Match')).toBe(false);
    for (const call of transport.mock.calls) {
      expect(call[0]).toBe('/api/v1/tenancy/settings/invitation-policy');
      expect(call[1].method).toBe('PUT');
      expect(JSON.parse(call[1].body as string)).toEqual({ days: 14 });
    }
  });

  it('refuses incomplete or contradictory policy conditions and noninteger/out-of-range days', async () => {
    const transport = vi.fn<AdministrationFetch>();
    const sdk = createAdministrationServerSdk(transport);
    for (const precondition of [
      undefined,
      {},
      { kind: 'present' },
      { kind: 'absent', version: '0' },
      { kind: 'present', version: '00' },
    ]) {
      expect(
        await sdk.setInvitationLifetime(14, {
          idempotencyKey: key,
          precondition,
        } as InvitationPolicyWriteOptions),
      ).toEqual({ kind: 'invalid-request' });
    }
    for (const days of [0, 31, 1.5, Number.NaN]) {
      expect(
        await sdk.setInvitationLifetime(days, {
          idempotencyKey: key,
          precondition: { kind: 'absent' },
        }),
      ).toEqual({ kind: 'invalid-request' });
    }
    expect(transport).not.toHaveBeenCalled();
  });

  it('reads a finite list/detail and preserves opaque cursors while clamping page size', async () => {
    const transport = vi.fn<AdministrationFetch>(async (path) =>
      path.includes('?')
        ? json({ items: [row], pageInfo })
        : json(row, 200, { ETag: '"' + version + '"' }),
    );
    const sdk = createAdministrationServerSdk(transport);
    expect((await sdk.listInvitations({ cursor: 'opaque_cursor', limit: 101 })).kind).toBe(
      'success',
    );
    expect(transport.mock.calls[0]![0]).toBe(
      '/api/v1/identity/invitations?cursor=opaque_cursor&limit=100',
    );
    expect((await sdk.getInvitation(invitationId)).kind).toBe('success');
    for (const body of [
      { ...row, verifierDigest: 'private' },
      { ...row, email: 'PERSON@example.invalid' },
      { ...row, status: 'Delivered' },
      { ...row, deliveryStatus: 'Delivered' },
      { ...row, expiresAt: '2026-02-30T12:00:00Z' },
      { ...row, roleVersion: 1 },
      { ...row, version: 1 },
    ]) {
      transport.mockImplementation(async () => json(body, 200, { ETag: '"' + version + '"' }));
      expect((await sdk.getInvitation(invitationId)).kind).toBe('invalid-response');
    }
  });

  it('accepts a delegation-limited Role subset and rejects duplicate or expanded grant summaries', async () => {
    const role = { roleId, builtInKey: 'Learner', capabilityScope: 'Organization', version };
    const transport = vi.fn<AdministrationFetch>(async () => json({ items: [role] }));
    const sdk = createAdministrationServerSdk(transport);
    expect((await sdk.listInvitableRoles()).kind).toBe('success');
    for (const items of [
      [role, role],
      [{ ...role, grants: [] }],
      [{ ...role, capabilityScope: 'Platform' }],
    ]) {
      transport.mockImplementation(async () => json({ items }));
      expect((await sdk.listInvitableRoles()).kind).toBe('invalid-response');
    }
    transport.mockImplementation(async () => json({ items: [] }));
    expect((await sdk.listInvitableRoles()).kind).toBe('success');
  });

  it('revokes/reissues only via the exact target commands and version receipts', async () => {
    const transport = vi.fn<AdministrationFetch>(async () =>
      json({ version }, 200, { ETag: '"' + version + '"' }),
    );
    const sdk = createAdministrationServerSdk(transport);
    const options = { idempotencyKey: key, version };
    expect((await sdk.revokeInvitation(invitationId, options)).kind).toBe('success');
    expect((await sdk.reissueInvitation(invitationId, options)).kind).toBe('success');
    expect(transport.mock.calls.map(([path]) => path)).toEqual([
      '/api/v1/identity/invitations/' + invitationId + '/revocations',
      '/api/v1/identity/invitations/' + invitationId + '/reissues',
    ]);
    for (const [, init] of transport.mock.calls) {
      expect(init.method).toBe('POST');
      expect(init.body).toBe('{}');
      expect(new Headers(init.headers).get('If-Match')).toBe('"' + version + '"');
    }
  });
});
