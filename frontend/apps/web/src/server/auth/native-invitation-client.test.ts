// @vitest-environment node
import { describe, expect, it, vi } from 'vitest';

import { createNativeInvitationApi } from './native-invitation-client';
import type { InvitationInspection, InvitationInput } from './native-invitation-contract';

const invitationId = '019a154b-0000-7000-8000-000000000031';
const input = {
  invitationId,
  code: invitationId + '.' + Buffer.alloc(32, 7).toString('base64url'),
};
const configuration = {
  apiOrigin: 'http://127.0.0.1:5100',
  secret: 'private-hop-'.padEnd(48, 'x'),
  nativeSourceSecret: Buffer.from(Array.from({ length: 32 }, (_, index) => index + 1)).toString(
    'base64url',
  ),
};
const binding = { host: 'institution.example.test:3000', source: '203.0.113.19' };
const inspection: InvitationInspection = {
  invitationId,
  version: '9007199254740993',
  expiresAt: '2026-11-20T15:30:00.0000000+00:00',
  tenantLabel: 'Institution',
  organizationLabel: 'Branch',
  roleId: '019a154b-0000-7000-8000-000000000032',
  roleKey: 'Learner',
  roleScope: 'Organization',
  verifiedEmail: 'recipient@example.test',
};
const accepted = {
  invitationId,
  version: '9007199254740994',
  membershipId: '019a154b-0000-7000-8000-000000000033',
  disposition: 'already_member',
  tenantId: '019a154b-0000-7000-8000-000000000034',
  organizationId: '019a154b-0000-7000-8000-000000000035',
};
const signal = () => new AbortController().signal;
const json = (body: unknown, status = 200, headers: Record<string, string> = {}) =>
  new Response(JSON.stringify(body), {
    status,
    headers: {
      'Content-Type': status < 400 ? 'application/json' : 'application/problem+json',
      'Cache-Control': 'no-store',
      ...(status === 200 ? { ETag: '"' + (body as { version: string }).version + '"' } : {}),
      ...headers,
    },
  });
const problem = (code: string, status: number) => ({
  type: 'https://errors.learnstack.dev/' + code,
  title: 'private-provider-diagnostic',
  status,
  instance: '/api/v1/identity/invitation-entry/inspections',
  code,
  messageKey: 'lockey_' + code,
  correlationId: 'private-correlation',
  errors: { code: [{ key: 'lockey_private_field' }] },
});

describe('native recipient invitation client', () => {
  it('uses the three exact SDK POSTs with scope-null source proof and version only on acceptance', async () => {
    const transport = vi
      .fn<typeof fetch>()
      .mockResolvedValueOnce(json(inspection))
      .mockResolvedValueOnce(json(accepted))
      .mockResolvedValueOnce(json(accepted));
    const api = createNativeInvitationApi(configuration, transport);
    const offer = await api.inspect('held-token', binding, input, signal());
    expect(offer).toEqual(inspection);
    expect(Object.isFrozen(offer)).toBe(true);
    expect(
      await api.accept('held-token', binding, { ...input, version: inspection.version }, signal()),
    ).toEqual(accepted);
    expect(await api.result('held-token', binding, input, signal())).toEqual(accepted);
    for (const [index, action] of ['inspections', 'acceptances', 'results'].entries()) {
      const [url, init] = transport.mock.calls[index]!;
      expect(url).toBe(configuration.apiOrigin + '/api/v1/identity/invitation-entry/' + action);
      expect(init).toMatchObject({
        method: 'POST',
        cache: 'no-store',
        credentials: 'omit',
        redirect: 'error',
      });
      expect(JSON.parse(init!.body as string)).toEqual(input);
      const headers = new Headers(init!.headers);
      expect(headers.get('Authorization')).toBe('Bearer held-token');
      expect(headers.get('If-Match')).toBe(index === 1 ? '"' + inspection.version + '"' : null);
      for (const name of [
        'Cookie',
        'Idempotency-Key',
        'If-None-Match',
        'X-LearnStack-Native-Tenant-Id',
        'X-LearnStack-Native-Organization-Id',
      ])
        expect(headers.has(name)).toBe(false);
    }
  });

  it.each([
    { ...input, code: input.code + '=' },
    { ...input, code: input.code.slice(37) },
    { ...input, invitationId: inspection.roleId },
    { ...input, code: input.code.slice(0, -1) + 'B' },
    { ...input, path: '/other' },
  ])('refuses malformed or expanded input before I/O', async (value) => {
    const transport = vi.fn<typeof fetch>();
    await expect(
      createNativeInvitationApi(configuration, transport).inspect(
        'token',
        binding,
        value as InvitationInput,
        signal(),
      ),
    ).rejects.toMatchObject({ reason: 'configuration' });
    expect(transport).not.toHaveBeenCalled();
  });

  it.each([
    { ...inspection, version: 9007199254740992 },
    { ...inspection, version: '9223372036854775808' },
    { ...inspection, verifiedEmail: 'Recipient@example.test' },
    { ...inspection, organizationLabel: null },
    { ...inspection, roleKey: 'TenantAdmin' },
    { ...inspection, code: input.code },
    { ...inspection, invitationId: inspection.roleId },
  ])(
    'rejects broadened or contradictory inspection without returning private data',
    async (value) => {
      await expect(
        createNativeInvitationApi(configuration, async () => json(value)).inspect(
          'token',
          binding,
          input,
          signal(),
        ),
      ).rejects.toMatchObject({ reason: 'response' });
    },
  );

  const invalidHeaders: Record<string, string>[] = [
    { ETag: 'W/"9007199254740993"' },
    { 'Cache-Control': 'private' },
    { Location: '/api/v1/identity/invitations/' + invitationId },
    { 'Idempotency-Replayed': 'true' },
    { 'Set-Cookie': 'untrusted=private' },
  ];
  it.each(invalidHeaders)('rejects invalid receipt headers', async (headers) => {
    await expect(
      createNativeInvitationApi(configuration, async () => json(inspection, 200, headers)).inspect(
        'token',
        binding,
        input,
        signal(),
      ),
    ).rejects.toMatchObject({ reason: 'response' });
  });

  it.each([
    ['forbidden', 403],
    ['not_found', 404],
    ['concurrency_conflict', 409],
    ['rate_limited', 429],
    ['dependency_unavailable', 503],
  ] as const)(
    'retains finite %s recovery without upstream diagnostics or automatic retry',
    async (code, status) => {
      const transport = vi.fn<typeof fetch>(async () =>
        json(problem(code, status), status, { 'Retry-After': '30' }),
      );
      let captured: unknown;
      try {
        await createNativeInvitationApi(configuration, transport).accept(
          'token',
          binding,
          { ...input, version: inspection.version },
          signal(),
        );
      } catch (error) {
        captured = error;
      }
      expect(captured).toMatchObject({
        code,
        status,
        reason: 'provider_refused',
        retryAfterSeconds: status === 429 ? 30 : undefined,
      });
      expect(JSON.stringify(captured)).not.toContain('private');
      expect(String(captured)).not.toContain(input.code);
      expect(transport).toHaveBeenCalledTimes(1);
    },
  );

  it('maps unknown or status-inconsistent classifications to unknown without forwarding their value', async () => {
    for (const [code, status] of [
      ['private-code-canary', 403],
      ['forbidden', 503],
    ] as const) {
      await expect(
        createNativeInvitationApi(configuration, async () =>
          json(problem(code, status), status),
        ).inspect('token', binding, input, signal()),
      ).rejects.toMatchObject({ code: 'unknown', status });
    }
  });

  it('keeps an abort-insensitive transport owned until it really returns', async () => {
    let deliver!: (response: Response) => void;
    const pending = new Promise<Response>((resolve) => {
      deliver = resolve;
    });
    const controller = new AbortController();
    let settled = false;
    const operation = createNativeInvitationApi(configuration, async () => pending)
      .inspect('token', binding, input, controller.signal)
      .finally(() => {
        settled = true;
      });
    const refused = expect(operation).rejects.toMatchObject({ reason: 'cancelled' });
    controller.abort();
    await Promise.resolve();
    expect(settled).toBe(false);
    deliver(json(inspection));
    await refused;
  });
});
