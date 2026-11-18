// @vitest-environment node
import { describe, expect, it, vi } from 'vitest';

import {
  createNativeFederationTestApi,
  createPublicSignInReader,
} from './native-federation-client';

const connectionId = '019a154b-0000-7000-8000-000000000021';
const attempt = '019a154b-0000-7000-8000-000000000022';
const tenantId = '019a154b-0000-7000-8000-000000000023';
const configuration = {
  apiOrigin: 'https://api.example.test',
  secret: 'private-hop-'.padEnd(48, 'x'),
  nativeSourceSecret: Buffer.from(Array.from({ length: 32 }, (_, index) => index + 1)).toString(
    'base64url',
  ),
};
const binding = {
  host: 'institution.example.test:3000',
  source: '203.0.113.19',
  tenantId,
  organizationId: null,
};
const detail = (overrides: Record<string, unknown> = {}) => ({
  connection: {
    connectionId,
    alias: 'lsf-' + 'a'.repeat(32),
    version: '7',
    status: 'draft',
    ...overrides,
  },
  provider: 'matches',
});
const receipt = (overrides: Record<string, unknown> = {}) => ({
  connectionId,
  version: '8',
  outcome: 'applied',
  reason: null,
  testAttemptId: attempt,
  ...overrides,
});
const json = (value: unknown, status = 200) =>
  new Response(JSON.stringify(value), {
    status,
    headers: {
      'content-type': status < 400 ? 'application/json' : 'application/problem+json',
    },
  });
const signal = () => new AbortController().signal;
const failure = async (promise: Promise<unknown>) =>
  promise.then(
    () => null,
    (error: { reason?: string; status?: number }) => ({ code: error.reason, status: error.status }),
  );

/** The transport answers each call in order; the calls are returned for inspection. */
function sequence(...responses: Response[]) {
  const queue = [...responses];
  return vi.fn<typeof fetch>(async () => queue.shift() ?? json({}, 500));
}

describe('the BFF-only federation test owner', () => {
  it('reads the connection, then begins its window under the version it read', async () => {
    const transport = sequence(json(detail()), json(receipt()));
    const started = await createNativeFederationTestApi(configuration, transport).start(
      'admin-token',
      binding,
      signal(),
    );
    expect(started).toEqual({
      alias: 'lsf-' + 'a'.repeat(32),
      attemptId: attempt,
      version: '8',
      connectionId,
    });
    expect(transport).toHaveBeenCalledTimes(2);
    const [readUrl, readInit] = transport.mock.calls[0]!;
    expect(readUrl).toBe('https://api.example.test/api/v1/identity/federation');
    expect(readInit!.method).toBe('GET');
    const [startUrl, startInit] = transport.mock.calls[1]!;
    expect(startUrl).toBe('https://api.example.test/api/v1/identity/federation/test-starts');
    expect(startInit!.method).toBe('POST');
    expect(startInit!.body).toBe('{}');
    expect(new Headers(startInit!.headers).get('If-Match')).toBe('"7"');
    expect(new Headers(startInit!.headers).get('Authorization')).toBe('Bearer admin-token');
  });

  it('refuses without a connection (404) or any state but Draft (409) and writes nothing', async () => {
    for (const [body, status] of [
      [{ connection: null, provider: 'matches' }, 404],
      [detail({ status: 'active' }), 409],
      [detail({ status: 'test_open' }), 409],
    ] as const) {
      const transport = sequence(json(body));
      const result = await failure(
        createNativeFederationTestApi(configuration, transport).start('t', binding, signal()),
      );
      expect(result).toEqual({ code: 'provider_refused', status });
      expect(transport).toHaveBeenCalledTimes(1);
    }
  });

  it('refuses a malformed detail before any write', async () => {
    // A bad connection object reads as a missing one (404); a bad envelope is an unreadable response.
    for (const [body, status] of [
      [detail({ alias: 'not-an-alias' }), 404],
      [detail({ version: '07' }), 404],
      [detail({ version: 7 }), 404],
      [detail({ connectionId: 'not-a-uuid' }), 404],
      [{ ...detail(), extra: true }, undefined],
      [{ connection: detail().connection }, undefined],
    ] as const) {
      const transport = sequence(json(body));
      const result = await failure(
        createNativeFederationTestApi(configuration, transport).start('t', binding, signal()),
      );
      expect(result).toEqual({ code: 'response', status });
      expect(transport).toHaveBeenCalledTimes(1);
    }
  });

  it('returns no attempt unless the start applied and names the connection it read', async () => {
    for (const body of [
      receipt({ outcome: 'uncertain', testAttemptId: null }),
      receipt({ outcome: 'pending', testAttemptId: null }),
      // An outcome that did not apply never yields an attempt, whatever the body also carries.
      receipt({ outcome: 'pending' }),
      receipt({ outcome: 'drift' }),
      receipt({ testAttemptId: null }),
      receipt({ connectionId: '019a154b-0000-7000-8000-000000000099' }),
    ]) {
      const result = await failure(
        createNativeFederationTestApi(configuration, sequence(json(detail()), json(body))).start(
          't',
          binding,
          signal(),
        ),
      );
      expect(result).toEqual({ code: 'provider_refused', status: 409 });
    }
  });

  it('classifies a refusal by its closed status and never surfaces a problem body', async () => {
    const refused = await failure(
      createNativeFederationTestApi(
        configuration,
        sequence(json({ title: 'secret-detail', detail: 'private' }, 403)),
      ).start('t', binding, signal()),
    );
    expect(refused).toEqual({ code: 'provider_refused', status: 403 });
    const created = await failure(
      createNativeFederationTestApi(
        configuration,
        sequence(json(detail()), json(receipt(), 201)),
      ).start('t', binding, signal()),
    );
    expect(created).toEqual({ code: 'response', status: undefined });
  });

  it('records a closed result under the version and attempt it holds', async () => {
    const transport = sequence(json(receipt({ outcome: 'recorded', testAttemptId: null })));
    const recorded = await createNativeFederationTestApi(configuration, transport).record(
      'test-record-token',
      binding,
      { version: '8', attemptId: attempt, passed: true },
      signal(),
    );
    expect(recorded).toEqual({ outcome: 'recorded' });
    const [url, init] = transport.mock.calls[0]!;
    expect(url).toBe('https://api.example.test/api/v1/identity/federation/test-results');
    expect(init!.method).toBe('POST');
    expect(JSON.parse(init!.body as string)).toEqual({ testAttemptId: attempt, passed: true });
    expect(new Headers(init!.headers).get('If-Match')).toBe('"8"');
    expect(new Headers(init!.headers).get('Authorization')).toBe('Bearer test-record-token');
  });

  it('refuses a malformed record input before the transport is called', async () => {
    for (const input of [
      { version: '8', attemptId: attempt },
      { version: '8', attemptId: attempt, passed: 'true' },
      { version: '08', attemptId: attempt, passed: true },
      { version: '8', attemptId: 'not-a-uuid', passed: true },
      { version: '8', attemptId: attempt, passed: true, extra: 1 },
    ]) {
      const transport = sequence(json(receipt()));
      const result = await failure(
        createNativeFederationTestApi(configuration, transport).record(
          't',
          binding,
          input as never,
          signal(),
        ),
      );
      expect(result).toEqual({ code: 'configuration', status: undefined });
      expect(transport).not.toHaveBeenCalled();
    }
  });

  it('refuses a receipt that is not exactly the closed shape', async () => {
    for (const body of [
      { ...receipt(), extra: true },
      { connectionId, version: '8', outcome: 'recorded', reason: null },
      receipt({ outcome: 'accepted' }),
      receipt({ version: 8 }),
      receipt({ reason: 7 }),
      receipt({ testAttemptId: 'not-a-uuid' }),
    ]) {
      const result = await failure(
        createNativeFederationTestApi(configuration, sequence(json(body))).record(
          't',
          binding,
          { version: '8', attemptId: attempt, passed: false },
          signal(),
        ),
      );
      expect(result).toEqual({ code: 'response', status: undefined });
    }
  });
});

describe('the anonymous public sign-in reader', () => {
  const alias = 'lsf-' + 'b'.repeat(32);
  const read = (response: Response | Error) =>
    createPublicSignInReader(
      configuration,
      vi.fn<typeof fetch>(async () => {
        if (response instanceof Error) throw response;
        return response;
      }),
    )(binding, signal());

  it('returns exactly the alias and display name of the Active option', async () => {
    expect(await read(json({ federation: { alias, displayName: 'Acme SSO' } }))).toEqual({
      alias,
      displayName: 'Acme SSO',
    });
  });

  it('hides the option on a refusal, a transport failure or anything not exactly shaped', async () => {
    expect(await read(json({ federation: null }))).toBeNull();
    expect(await read(json({}))).toBeNull();
    expect(await read(json({ federation: { alias, displayName: 'Acme SSO' } }, 503))).toBeNull();
    expect(await read(new Error('network'))).toBeNull();
    for (const federation of [
      { alias: 'not-an-alias', displayName: 'Acme SSO' },
      { alias, displayName: 7 },
      { alias },
      { alias, displayName: 'Acme SSO', clientId: 'leaked' },
      'ls',
    ])
      expect(await read(json({ federation }))).toBeNull();
  });
});
