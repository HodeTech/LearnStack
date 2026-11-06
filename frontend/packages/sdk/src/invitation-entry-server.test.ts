import { describe, expect, it, vi } from 'vitest';

import { createInvitationEntryServerSdk } from './invitation-entry-server.js';

const id = '019b24b6-0cf0-7000-8000-000000000011';
const input = { invitationId: id, code: id + '.' + 'A'.repeat(43) };
const accepted = {
  invitationId: id,
  version: '9007199254740993',
  membershipId: id,
  disposition: 'created',
  tenantId: id,
  organizationId: null,
};
const inspection = {
  invitationId: id,
  version: '0',
  expiresAt: '2026-12-01T00:00:00+00:00',
  tenantLabel: 'Current tenant',
  organizationLabel: null,
  roleId: id,
  roleKey: 'Learner',
  roleScope: 'Tenant',
  verifiedEmail: 'recipient@example.test',
};
function response(body: object = accepted, headers: Record<string, string> = {}) {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: {
      'Content-Type': 'application/json',
      'Cache-Control': 'no-store',
      ETag: '"' + ('version' in body ? body.version : '0') + '"',
      ...headers,
    },
  });
}
describe('closed recipient entry SDK', () => {
  it('uses exact POSTs, body-only credentials, header-only decimal version, and never retries', async () => {
    const transport = vi.fn(async () => response());
    const sdk = createInvitationEntryServerSdk(transport);
    expect((await sdk.acceptInvitation(input, { version: '9007199254740993' })).kind).toBe(
      'success',
    );
    expect(transport).toHaveBeenCalledOnce();
    const [path, init] = transport.mock.calls[0] as unknown as [string, RequestInit];
    expect(path).toBe('/api/v1/identity/invitation-entry/acceptances');
    expect(init).toMatchObject({
      method: 'POST',
      cache: 'no-store',
      headers: { 'If-Match': '"9007199254740993"' },
    });
    expect(JSON.parse(init.body as string)).toEqual(input);
    expect(new Headers(init.headers).has('Idempotency-Key')).toBe(false);
    transport.mockImplementation(async () => response(inspection));
    expect((await sdk.inspectInvitation(input)).kind).toBe('success');
    const inspectHeaders = new Headers(
      (transport.mock.calls[1] as unknown as [string, RequestInit])[1].headers,
    );
    expect(inspectHeaders.has('If-Match')).toBe(false);
    expect(inspectHeaders.has('If-None-Match')).toBe(false);
    transport.mockImplementation(async () => response());
    expect((await sdk.getOwnInvitationResult(input)).kind).toBe('success');
    expect(transport.mock.calls).toHaveLength(3);
  });
  it.each(['01', '-1', '9223372036854775808', '1e2', '"0"'])(
    'refuses malformed version %s before transport',
    async (version) => {
      const transport = vi.fn();
      expect(
        await createInvitationEntryServerSdk(transport).acceptInvitation(input, { version }),
      ).toEqual({ kind: 'invalid-request' });
      expect(transport).not.toHaveBeenCalled();
    },
  );
  it('rejects selectors, body version, alternate verifier encoding and unknown options', async () => {
    const transport = vi.fn();
    const sdk = createInvitationEntryServerSdk(transport);
    for (const extra of [{ tenantId: id }, { version: '0' }, { email: 'recipient@example.test' }])
      expect((await sdk.inspectInvitation({ ...input, ...extra })).kind).toBe('invalid-request');
    expect(
      (await sdk.inspectInvitation({ ...input, code: input.code.slice(0, -1) + 'B' })).kind,
    ).toBe('invalid-request');
    expect((await sdk.inspectInvitation(input, { version: '0' } as never)).kind).toBe(
      'invalid-request',
    );
    expect((await sdk.getOwnInvitationResult(input, { version: '0' } as never)).kind).toBe(
      'invalid-request',
    );
    for (const options of [
      { idempotencyKey: 'not-accepted' },
      { tenantId: id },
      { headers: {} },
      [],
    ])
      expect((await sdk.getOwnInvitationResult(input, options as never)).kind).toBe(
        'invalid-request',
      );
    expect(transport).not.toHaveBeenCalled();
  });
  it.each<Record<string, string>>([
    { ETag: 'W/"9007199254740993"' },
    { Location: '/elsewhere' },
    { 'Idempotency-Replayed': 'true' },
    { 'Set-Cookie': 'session=value' },
    { 'Cache-Control': 'public' },
  ])('refuses contradictory success headers', async (headers) => {
    expect(
      (
        await createInvitationEntryServerSdk(async () =>
          response(accepted, headers),
        ).getOwnInvitationResult(input)
      ).kind,
    ).toBe('invalid-response');
  });
  it('rejects cross-offer output, numeric versions, unexpected grants and oversized bodies', async () => {
    for (const body of [
      { ...accepted, invitationId: '019b24b6-0cf0-7000-8000-000000000012' },
      { ...accepted, version: 2 },
      { ...accepted, grant: 'token' },
    ])
      expect(
        (
          await createInvitationEntryServerSdk(async () => response(body)).getOwnInvitationResult(
            input,
          )
        ).kind,
      ).toBe('invalid-response');
    expect(
      (
        await createInvitationEntryServerSdk(
          async () => new Response(' '.repeat(16 * 1024 + 1)),
        ).getOwnInvitationResult(input)
      ).kind,
    ).toBe('invalid-response');
  });
  it('preserves cancellation and sanitizes transport failure without retry', async () => {
    const aborted = AbortSignal.abort();
    const transport = vi.fn(async () => {
      throw new Error(input.code);
    });
    const sdk = createInvitationEntryServerSdk(transport);
    expect(await sdk.getOwnInvitationResult(input, { signal: aborted })).toEqual({
      kind: 'cancelled',
    });
    expect(transport).not.toHaveBeenCalled();
    expect(await sdk.acceptInvitation(input, { version: '0' })).toEqual({
      kind: 'transport-error',
    });
    expect(transport).toHaveBeenCalledOnce();
  });
  it('recovers an uncertain acceptance only through the explicit result POST', async () => {
    const transport = vi.fn(async (): Promise<Response> => {
      throw new Error('Unknown acceptance outcome');
    });
    const sdk = createInvitationEntryServerSdk(transport);
    expect(await sdk.acceptInvitation(input, { version: '0' })).toEqual({
      kind: 'transport-error',
    });
    expect(transport).toHaveBeenCalledOnce();
    transport.mockImplementation(async () => response());
    expect((await sdk.getOwnInvitationResult(input)).kind).toBe('success');
    const [path, init] = transport.mock.calls[1] as unknown as [string, RequestInit];
    expect(path).toBe('/api/v1/identity/invitation-entry/results');
    expect(JSON.parse(init.body as string)).toEqual(input);
    const headers = new Headers(init.headers);
    for (const name of ['If-Match', 'If-None-Match', 'Idempotency-Key', 'Authorization', 'Cookie'])
      expect(headers.has(name)).toBe(false);
    expect(transport).toHaveBeenCalledTimes(2);
  });
  it('binds response validation to the submitted offer even if the caller mutates its object', async () => {
    const mutable = { ...input };
    const transport = vi.fn(async () => {
      mutable.invitationId = '019b24b6-0cf0-7000-8000-000000000012';
      return response();
    });
    expect(
      (await createInvitationEntryServerSdk(transport).getOwnInvitationResult(mutable)).kind,
    ).toBe('success');
  });
  it.each(['2026-02-30T00:00:00Z', '2026-12-01T00:00:00+03:00'])(
    'refuses an invalid inspection instant',
    async (expiresAt) => {
      expect(
        (
          await createInvitationEntryServerSdk(async () =>
            response({ ...inspection, expiresAt }),
          ).inspectInvitation(input)
        ).kind,
      ).toBe('invalid-response');
    },
  );
  it('accepts a stored tenant or branch name that Tenancy allows to hold control characters', async () => {
    const body = {
      ...inspection,
      roleKey: 'OrgAdmin',
      roleScope: 'Organization',
      tenantLabel: 'Merkez\n\u0301Okul',
      organizationLabel: 'Merkez\r\n\u0007Sube',
    };
    const result = await createInvitationEntryServerSdk(async () =>
      response(body),
    ).inspectInvitation(input);
    expect(result.kind).toBe('success');
    // Names the database cannot store, and malformed text, stay refused.
    for (const label of ['Bad\u0000name', '\ud800', ' \n ', 'x'.repeat(201)]) {
      expect(
        (
          await createInvitationEntryServerSdk(async () =>
            response({ ...body, tenantLabel: label }),
          ).inspectInvitation(input)
        ).kind,
      ).toBe('invalid-response');
    }
  });
  it.each(['email', 'role', 'organization', 'session'])(
    'refuses an inspection outside the exact recipient DTO: %s',
    async (mutation) => {
      const body =
        mutation === 'email'
          ? { ...inspection, verifiedEmail: 'Recipient@example.test' }
          : mutation === 'role'
            ? { ...inspection, roleKey: 'Unknown' }
            : mutation === 'organization'
              ? { ...inspection, organizationLabel: 'Unexpected organization' }
              : { ...inspection, session: 'unexpected' };
      expect(
        (await createInvitationEntryServerSdk(async () => response(body)).inspectInvitation(input))
          .kind,
      ).toBe('invalid-response');
    },
  );
  it('clears streamed chunks and joins cancellation on overflow without retry', async () => {
    const first = new Uint8Array(16 * 1024).fill(65);
    const excess = new Uint8Array([66]);
    let index = 0;
    let cancelled = false;
    const stream = new ReadableStream<Uint8Array>(
      {
        pull(controller) {
          controller.enqueue(index++ === 0 ? first : excess);
        },
        async cancel() {
          await Promise.resolve();
          cancelled = true;
        },
      },
      { highWaterMark: 0 },
    );
    const transport = vi.fn(async () => new Response(stream));
    expect(
      (await createInvitationEntryServerSdk(transport).getOwnInvitationResult(input)).kind,
    ).toBe('invalid-response');
    expect(cancelled).toBe(true);
    expect(first.every((byte) => byte === 0)).toBe(true);
    expect(excess.every((byte) => byte === 0)).toBe(true);
    expect(stream.locked).toBe(false);
    expect(transport).toHaveBeenCalledOnce();
  });
  it('cancels a pending body read, clears prior chunks, and preserves caller cancellation', async () => {
    const controller = new AbortController();
    const first = new Uint8Array([65]);
    let reads = 0;
    let cancelled = false;
    const stream = new ReadableStream<Uint8Array>(
      {
        pull(source) {
          if (reads++ === 0) source.enqueue(first);
          else controller.abort();
        },
        async cancel() {
          await Promise.resolve();
          cancelled = true;
        },
      },
      { highWaterMark: 0 },
    );
    const transport = vi.fn(async () => new Response(stream));
    expect(
      await createInvitationEntryServerSdk(transport).getOwnInvitationResult(input, {
        signal: controller.signal,
      }),
    ).toEqual({ kind: 'cancelled' });
    expect(cancelled).toBe(true);
    expect(first[0]).toBe(0);
    expect(stream.locked).toBe(false);
    expect(transport).toHaveBeenCalledOnce();
  });
  it('accepts the exact streaming limit and clears the transient payload', async () => {
    const bytes = new TextEncoder().encode(JSON.stringify(accepted).padEnd(16 * 1024, ' '));
    const stream = new ReadableStream<Uint8Array>({
      start(controller) {
        controller.enqueue(bytes);
        controller.close();
      },
    });
    const transport = vi.fn(
      async () =>
        new Response(stream, {
          headers: {
            'Content-Type': 'application/json',
            'Cache-Control': 'no-store',
            ETag: '"' + accepted.version + '"',
          },
        }),
    );
    expect(
      (await createInvitationEntryServerSdk(transport).getOwnInvitationResult(input)).kind,
    ).toBe('success');
    expect(bytes.every((byte) => byte === 0)).toBe(true);
    expect(stream.locked).toBe(false);
    expect(transport).toHaveBeenCalledOnce();
  });
  it('refuses a declared oversized response before reading any chunks', async () => {
    const pull = vi.fn();
    const cancel = vi.fn();
    const stream = new ReadableStream<Uint8Array>({ pull, cancel }, { highWaterMark: 0 });
    const transport = vi.fn(
      async () => new Response(stream, { headers: { 'Content-Length': String(16 * 1024 + 1) } }),
    );
    expect(
      (await createInvitationEntryServerSdk(transport).getOwnInvitationResult(input)).kind,
    ).toBe('invalid-response');
    expect(pull).not.toHaveBeenCalled();
    expect(cancel).toHaveBeenCalledOnce();
    expect(stream.locked).toBe(false);
  });
});
