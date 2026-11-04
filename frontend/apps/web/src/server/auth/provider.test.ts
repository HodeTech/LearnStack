// @vitest-environment node
import { createHash } from 'node:crypto';
import { once } from 'node:events';
import http from 'node:http';

import { exportJWK, generateKeyPair, SignJWT } from 'jose';
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';

import { createTenantProvider } from './provider';
import { BffProviderError } from './provider-error';
import { MAX_PROVIDER_BODY_BYTES, providerOperation } from './provider-transport';

const issuer = 'https://identity.example.test/realms/learnstack';
const callback = 'https://school.example.test/api/auth/callback/keycloak';
const privateCanary = 'private-provider-response-canary';
const actorId = '019a154b-0000-7000-8000-000000000011';
const tenantId = '10000000-0000-4000-8000-000000000001';
let keys: Awaited<ReturnType<typeof generateKeyPair>>;
let otherKeys: Awaited<ReturnType<typeof generateKeyPair>>;
type WireCall = Readonly<{
  url: string;
  method: string;
  body: URLSearchParams;
  redirect: RequestRedirect | undefined;
  credentials: RequestCredentials | undefined;
  cache: RequestCache | undefined;
}>;
const wires: { issuer: string; calls: WireCall[] }[] = [];

afterEach(() => {
  // Assertions outside customFetch cannot be mistaken for an expected transport
  // rejection by the production adapter's closed error conversion.
  for (const { issuer, calls } of wires.splice(0))
    for (const call of calls) {
      expect(call.redirect).toBe('error');
      expect(call.credentials).toBe('omit');
      expect(call.cache).toBe('no-store');
      const token = `${issuer}/protocol/openid-connect/token`;
      expect([
        `${issuer}/.well-known/openid-configuration`,
        `${issuer}/protocol/openid-connect/certs`,
        token,
      ]).toContain(call.url);
      expect(call.method).toBe(call.url === token ? 'POST' : 'GET');
      if (call.url === token) {
        expect(call.body.getAll('client_id')).toEqual(['learnstack-web']);
        expect(call.body.getAll('client_secret')).toEqual([privateCanary]);
      } else expect([...call.body]).toEqual([]);
    }
});
beforeAll(async () => {
  keys = await generateKeyPair('RS256');
  otherKeys = await generateKeyPair('RS256');
});

async function fixture(issuer = 'https://identity.example.test/realms/learnstack') {
  const calls: WireCall[] = [];
  wires.push({ issuer, calls });
  let nonce: string | undefined;
  let claims: Record<string, unknown> = {};
  let tokenPatch: Record<string, unknown> = {};
  let signingKey: CryptoKey | undefined;
  let refused = false;
  let failedPath: string | undefined;
  let metadataPatch: Record<string, unknown> = {};
  let response: (() => Response) | undefined;
  const publicKey = await exportJWK(keys.publicKey);
  const transport: typeof fetch = vi.fn(async (input, init) => {
    const url = String(input);
    const body = new URLSearchParams(String(init?.body ?? ''));
    calls.push({
      url,
      method: init?.method ?? 'GET',
      body,
      redirect: init?.redirect,
      credentials: init?.credentials,
      cache: init?.cache,
    });
    if (failedPath && url.endsWith(failedPath)) throw new Error(privateCanary);
    if (response) return response();
    if (url.endsWith('/.well-known/openid-configuration'))
      return Response.json({
        issuer,
        authorization_endpoint: `${issuer}/protocol/openid-connect/auth`,
        token_endpoint: `${issuer}/protocol/openid-connect/token`,
        jwks_uri: `${issuer}/protocol/openid-connect/certs`,
        end_session_endpoint: `${issuer}/protocol/openid-connect/logout`,
        ...metadataPatch,
      });
    if (url.endsWith('/certs'))
      return Response.json({
        keys: [{ ...publicKey, kid: 'owned-key', use: 'sig', alg: 'RS256' }],
      });
    if (refused)
      return Response.json(
        { error: 'invalid_grant', error_description: privateCanary },
        { status: 400 },
      );
    const now = Math.floor(Date.now() / 1000);
    const id = await new SignJWT({
      sub: 'external-subject',
      iss: issuer,
      aud: 'learnstack-web',
      iat: now,
      exp: now + 900,
      ...(nonce ? { nonce } : {}),
      user_id: actorId,
      tenant_id: tenantId,
      ...claims,
    })
      .setProtectedHeader({ alg: 'RS256', kid: 'owned-key' })
      .sign(signingKey ?? keys.privateKey);
    return Response.json({
      access_token: privateCanary,
      token_type: 'Bearer',
      id_token: id,
      refresh_token: privateCanary,
      expires_in: 900,
      refresh_expires_in: 1800,
      ...tokenPatch,
    });
  });
  const provider = createTenantProvider(
    { issuer, clientSecret: privateCanary, allowLoopbackHttp: true },
    transport,
  );
  async function login() {
    const start = await provider.begin(callback);
    nonce = start.transaction.nonce;
    const url = new URL(callback);
    url.searchParams.set('code', 'owned-code');
    url.searchParams.set('state', start.transaction.state);
    const result = await provider.redeem(start.transaction, url.href);
    return { ...start, result };
  }
  return {
    provider,
    calls,
    transport,
    login,
    setClaims(value: Record<string, unknown>) {
      claims = value;
    },
    setTokens(value: Record<string, unknown>) {
      tokenPatch = value;
    },
    setMetadata(value: Record<string, unknown>) {
      metadataPatch = value;
    },
    setResponse(value: () => Response) {
      response = value;
    },
    setTransportFailure(path: string) {
      failedPath = path;
    },
    setRefused() {
      refused = true;
    },
    setWrongKey() {
      signingKey = otherKeys.privateKey;
    },
  };
}

describe('inactive tenant RP boundary', () => {
  it('contacts nothing at construction; real pinned code/refresh validate signed ID tokens and return server-only credentials', async () => {
    const f = await fixture();
    expect(f.calls).toHaveLength(0);
    const beforeGrant = performance.now();
    const { transaction, redirectUrl, result } = await f.login();
    const afterGrant = performance.now();
    const authorization = new URL(redirectUrl);
    expect(authorization.origin).toBe(new URL(issuer).origin);
    expect(authorization.pathname).toBe('/realms/learnstack/protocol/openid-connect/auth');
    expect([...authorization.searchParams.keys()].sort()).toEqual([
      'client_id',
      'code_challenge',
      'code_challenge_method',
      'nonce',
      'redirect_uri',
      'response_type',
      'scope',
      'state',
    ]);
    expect(authorization.searchParams.get('client_id')).toBe('learnstack-web');
    expect(authorization.searchParams.get('redirect_uri')).toBe(callback);
    expect(authorization.searchParams.get('response_type')).toBe('code');
    expect(authorization.searchParams.get('scope')).toBe('openid profile email');
    expect(authorization.searchParams.get('code_challenge_method')).toBe('S256');
    expect(authorization.searchParams.get('code_challenge')).toBe(
      createHash('sha256').update(transaction.verifier).digest('base64url'),
    );
    expect(authorization.searchParams.get('code_challenge')).not.toBe(transaction.verifier);
    expect(authorization.searchParams.get('nonce')).toBe(transaction.nonce);
    expect(authorization.searchParams.get('state')).toBe(transaction.state);
    expect(redirectUrl).not.toContain(privateCanary);
    expect(result.identity).toEqual({
      issuer,
      subject: 'external-subject',
      actorId,
      tenantId,
      organizationId: null,
      displayLabel: null,
    });
    expect(result.accessToken).toBe(privateCanary);
    expect(result.accessExpiresAt).toBeGreaterThanOrEqual(beforeGrant + 900_000);
    expect(result.accessExpiresAt).toBeLessThanOrEqual(afterGrant + 900_000);
    expect(result.refreshExpiresAt).toBeGreaterThanOrEqual(beforeGrant + 1_800_000);
    expect(result.refreshExpiresAt).toBeLessThanOrEqual(afterGrant + 1_800_000);
    const beforeRefresh = performance.now();
    const refreshed = await f.provider.refresh(result.refreshToken!, result.identity);
    const afterRefresh = performance.now();
    expect(refreshed.accessExpiresAt).toBeGreaterThanOrEqual(beforeRefresh + 900_000);
    expect(refreshed.accessExpiresAt).toBeLessThanOrEqual(afterRefresh + 900_000);
    expect(refreshed.refreshExpiresAt).toBeGreaterThanOrEqual(beforeRefresh + 1_800_000);
    expect(refreshed.refreshExpiresAt).toBeLessThanOrEqual(afterRefresh + 1_800_000);
    expect(refreshed.identity).toEqual(result.identity);
    expect(f.calls.filter((call) => call.url.endsWith('/token'))).toHaveLength(2);
    expect(f.calls.filter((call) => call.url.endsWith('/certs')).length).toBeGreaterThan(0);
    const tokenCalls = f.calls.filter((call) => call.url.endsWith('/token'));
    expect([...tokenCalls[0]!.body]).toHaveLength(6);
    expect(Object.fromEntries(tokenCalls[0]!.body)).toEqual({
      client_id: 'learnstack-web',
      client_secret: privateCanary,
      code: 'owned-code',
      code_verifier: transaction.verifier,
      grant_type: 'authorization_code',
      redirect_uri: callback,
    });
    expect(Object.fromEntries(tokenCalls[1]!.body)).toEqual({
      client_id: 'learnstack-web',
      client_secret: privateCanary,
      grant_type: 'refresh_token',
      refresh_token: result.refreshToken,
    });
    expect([...tokenCalls[1]!.body]).toHaveLength(4);
    const before = f.calls.length;
    const logout = new URL(
      f.provider.logoutUrl(result.idToken, 'https://school.example.test/', 'owned-return'),
    );
    expect(logout.origin).toBe(new URL(issuer).origin);
    expect(logout.pathname).toBe('/realms/learnstack/protocol/openid-connect/logout');
    expect(Object.fromEntries(logout.searchParams)).toEqual({
      client_id: 'learnstack-web',
      id_token_hint: result.idToken,
      post_logout_redirect_uri: 'https://school.example.test/',
      state: 'owned-return',
    });
    expect([...logout.searchParams]).toHaveLength(4);
    expect(logout.href).not.toContain(privateCanary);
    expect(f.calls).toHaveLength(before);
  });

  it('creates independent unpredictable state, nonce and verifier values per transaction', async () => {
    const f = await fixture();
    const transactions = await Promise.all([
      f.provider.begin(callback),
      f.provider.begin(callback),
    ]);
    const values = transactions.flatMap(({ transaction }) => [
      transaction.state,
      transaction.nonce,
      transaction.verifier,
    ]);
    for (const value of values) expect(value).toMatch(/^[A-Za-z0-9_-]{43}$/);
    expect(new Set(values).size).toBe(6);
  });

  it('accepts a nonnil actor UUID independently of its version, matching the API profile', async () => {
    const f = await fixture();
    const versionFourActor = 'FA22AEF7-7175-4CF3-8AD3-A300E2204776';
    f.setClaims({ user_id: versionFourActor });
    const { result } = await f.login();
    expect(result.identity.actorId).toBe(versionFourActor.toLowerCase());
    expect((await f.provider.refresh(result.refreshToken!, result.identity)).identity.actorId).toBe(
      versionFourActor.toLowerCase(),
    );
  });

  it.each([
    [{ user_id: undefined, tenant_id: undefined }, null, null, null],
    [{ tenant_id: undefined }, actorId, null, null],
    [{ user_id: undefined, organization_id: actorId }, null, tenantId, actorId],
  ] as const)(
    'accepts optional compatibility identity and a complete optional scope (%#)',
    async (claims, actor, tenant, organization) => {
      const f = await fixture();
      f.setClaims(claims);
      const { result } = await f.login();
      expect(result.identity).toEqual({
        issuer,
        subject: 'external-subject',
        actorId: actor,
        tenantId: tenant,
        organizationId: organization,
        displayLabel: null,
      });
      expect((await f.provider.refresh(result.refreshToken!, result.identity)).identity).toEqual(
        result.identity,
      );
    },
  );

  it.each([
    [{ name: 'Full Name', preferred_username: 'username' }, 'Full Name'],
    [{ preferred_username: 'username' }, 'username'],
    [{ name: 'n'.repeat(128) }, 'n'.repeat(128)],
    [{ name: 'n'.repeat(129) }, null],
    [{ name: ['not-scalar'] }, null],
    [{ name: '\0private' }, null],
    [{ name: '\ud800' }, null],
    [{ name: '   ' }, null],
    [{ name: '\u0085' }, null],
    [{}, null],
  ] as const)(
    'keeps optional bounded display text separate from binding and refresh continuity (%#)',
    async (claims, label) => {
      const f = await fixture();
      f.setClaims(claims);
      const { result } = await f.login();
      expect(result.identity.displayLabel).toBe(label);
      f.setClaims({ name: 'Updated Name' });
      const refreshed = await f.provider.refresh(result.refreshToken!, result.identity);
      expect(refreshed.identity).toEqual({ ...result.identity, displayLabel: 'Updated Name' });
    },
  );

  it('retains the qualified loopback allowance for logout and closes malformed return errors', async () => {
    const f = await fixture('http://127.0.0.1:8099/realms/learnstack');
    const { result } = await f.login();
    const calls = f.calls.length;
    const logout = new URL(
      f.provider.logoutUrl(result.idToken, 'https://school.example.test/', 'owned-return'),
    );
    expect(logout.origin).toBe('http://127.0.0.1:8099');
    expect(logout.pathname).toBe('/realms/learnstack/protocol/openid-connect/logout');
    expect(logout.searchParams.get('id_token_hint')).toBe(result.idToken);
    expect(logout.searchParams.get('state')).toBe('owned-return');
    expect(f.calls).toHaveLength(calls);
    expect(() => f.provider.logoutUrl(result.idToken, privateCanary, 'owned-return')).toThrow(
      BffProviderError,
    );
  });

  for (const [name, patch] of [
    ['wrong issuer', { iss: 'https://foreign.example.test/realms/learnstack' }],
    ['wrong audience', { aud: 'another-client' }],
    ['expired ID token', { exp: 1 }],
    ['wrong nonce', { nonce: 'wrong' }],
    ['non-scalar actor', { user_id: [actorId] }],
    ['invalid actor UUID', { user_id: 'not-a-uuid' }],
    ['nil actor UUID', { user_id: '00000000-0000-0000-0000-000000000000' }],
    ['null actor', { user_id: null }],
    ['null tenant', { tenant_id: null }],
    ['organization without tenant', { tenant_id: undefined, organization_id: actorId }],
    ['invalid organization', { organization_id: 'private-profile-canary' }],
  ] as const) {
    it(`rejects ${name} through the actual signed grant without leaking upstream details`, async () => {
      const f = await fixture();
      f.setClaims(patch);
      await expect(f.login()).rejects.toBeInstanceOf(BffProviderError);
      await f.login().catch((error: Error) => {
        expect(error.stack).not.toContain(privateCanary);
        expect(error.stack).not.toContain('private-profile-canary');
      });
    });
  }

  it.each([255, 256])(
    'enforces the shared opaque subject bound at %i characters',
    async (length) => {
      const f = await fixture();
      const subject = 's'.repeat(length);
      f.setClaims({ sub: subject });
      if (length === 255) {
        const { result } = await f.login();
        expect(result.identity.subject).toBe(subject);
      } else {
        await expect(f.login()).rejects.toMatchObject({ reason: 'identity' });
      }
    },
  );

  it('rejects wrong signatures at both code and refresh processing', async () => {
    const f = await fixture();
    const { result } = await f.login();
    f.setWrongKey();
    await expect(f.provider.refresh(result.refreshToken!, result.identity)).rejects.toMatchObject({
      reason: 'response',
    });
    await expect(f.login()).rejects.toMatchObject({ reason: 'response' });
  });

  it.each([
    ' ',
    '\u0085\u3000',
    'private\0subject',
    'private\ud800',
    'private\udc00',
    '🙂'.repeat(128),
  ])(
    'refuses nonstorable, whitespace-only or over-bound subjects at code and refresh processing (%#)',
    async (subject) => {
      const f = await fixture();
      const { result } = await f.login();
      f.setClaims({ sub: subject });
      await expect(f.login()).rejects.toMatchObject({ reason: 'identity' });
      await expect(f.provider.refresh(result.refreshToken!, result.identity)).rejects.toMatchObject(
        { reason: 'identity' },
      );
    },
  );

  it.each(['  I/ı/🙂  ', '🙂'.repeat(127) + 'a'])(
    'preserves exact non-ASCII subject text at both grants (%#)',
    async (subject) => {
      const f = await fixture();
      f.setClaims({ sub: subject });
      const { result } = await f.login();
      expect(result.identity.subject).toBe(subject);
      expect(
        (await f.provider.refresh(result.refreshToken!, result.identity)).identity.subject,
      ).toBe(subject);
    },
  );

  for (const patch of [
    { sub: 'replacement-subject' },
    { user_id: '019a154b-0000-7000-8000-000000000012' },
    { tenant_id: '20000000-0000-4000-8000-000000000002' },
    { organization_id: tenantId },
    { user_id: undefined },
    { tenant_id: undefined },
  ]) {
    it(`refuses refresh identity reassignment of ${Object.keys(patch)[0]}`, async () => {
      const f = await fixture();
      const { result } = await f.login();
      f.setClaims(patch);
      await expect(f.provider.refresh(result.refreshToken!, result.identity)).rejects.toMatchObject(
        { reason: 'identity' },
      );
    });
  }

  it('rejects state mismatch before any discovery or code exchange', async () => {
    const f = await fixture();
    await expect(
      f.provider.redeem(
        { callbackUri: callback, state: 'expected', verifier: 'v', nonce: 'n' },
        `${callback}?code=secret-code&state=wrong`,
      ),
    ).rejects.toMatchObject({ reason: 'response' });
    expect(f.calls).toHaveLength(0);
  });

  it('does not let discovery point a credential-bearing exchange at another origin/path', async () => {
    const f = await fixture();
    f.setMetadata({ token_endpoint: 'https://foreign.example.test/collect' });
    await expect(f.login()).rejects.toMatchObject({ reason: 'configuration' });
    expect(f.calls).toHaveLength(1);
    expect(f.calls[0]?.body.toString()).not.toContain(privateCanary);
  });

  for (const path of ['/token', '/certs']) {
    it(`preserves the closed transport reason through the library wrapper at ${path}`, async () => {
      const f = await fixture();
      f.setTransportFailure(path);
      const error = await f.login().catch((value: unknown) => value);
      expect(error).toBeInstanceOf(BffProviderError);
      expect(error).toMatchObject({ reason: 'transport', message: 'BFF provider: transport' });
      expect((error as Error).cause).toBeUndefined();
      expect((error as Error).stack).not.toContain(privateCanary);
      expect(f.calls.filter((call) => call.url.endsWith(path))).toHaveLength(1);
    });
  }

  it('retains network uncertainty during refresh without retry or an upstream cause', async () => {
    const f = await fixture();
    const { result } = await f.login();
    const before = f.calls.filter((call) => call.url.endsWith('/token')).length;
    f.setTransportFailure('/token');
    const error = await f.provider
      .refresh(result.refreshToken!, result.identity)
      .catch((value: unknown) => value);
    expect(error).toBeInstanceOf(BffProviderError);
    expect(error).toMatchObject({ reason: 'transport', message: 'BFF provider: transport' });
    expect((error as Error).cause).toBeUndefined();
    expect((error as Error).stack).not.toContain(privateCanary);
    expect(f.calls.filter((call) => call.url.endsWith('/token'))).toHaveLength(before + 1);
  });

  it('maps invalid_grant without retrying or preserving provider descriptions', async () => {
    const f = await fixture();
    const { result } = await f.login();
    f.setRefused();
    const before = f.calls.filter((call) => call.url.endsWith('/token')).length;
    await expect(f.provider.refresh(result.refreshToken!, result.identity)).rejects.toMatchObject({
      reason: 'invalid_grant',
      message: 'BFF provider: invalid_grant',
    });
    expect(f.calls.filter((call) => call.url.endsWith('/token'))).toHaveLength(before + 1);
  });

  for (const [name, value] of [
    ['missing refresh lifetime', undefined],
    ['unbounded refresh lifetime', 0],
    ['malformed refresh lifetime', '1800'],
  ]) {
    it(`refuses ${name}`, async () => {
      const f = await fixture();
      f.setTokens({ refresh_expires_in: value });
      await expect(f.login()).rejects.toMatchObject({ reason: 'response' });
    });
  }

  it('returns rotated refresh credentials and uses their replacement for the next signed grant', async () => {
    const f = await fixture();
    const { result } = await f.login();
    const replacement = 'rotated-private-refresh-canary';
    f.setTokens({ refresh_token: replacement });
    const rotated = await f.provider.refresh(result.refreshToken!, result.identity);
    expect(rotated.refreshToken).toBe(replacement);
    await f.provider.refresh(rotated.refreshToken!, rotated.identity);
    const refreshes = f.calls.filter((call) => call.body.get('grant_type') === 'refresh_token');
    expect(refreshes).toHaveLength(2);
    expect(refreshes.map((call) => call.body.get('refresh_token'))).toEqual([
      result.refreshToken,
      replacement,
    ]);
  });

  it('allows omitted replacement refresh token only during refresh, never initial custody', async () => {
    const f = await fixture();
    const { result } = await f.login();
    f.setTokens({ refresh_token: undefined });
    expect(
      (await f.provider.refresh(result.refreshToken!, result.identity)).refreshToken,
    ).toBeUndefined();
    await expect(f.login()).rejects.toMatchObject({ reason: 'response' });
  });

  for (const [name, response] of [
    ['HTML', () => new Response(privateCanary, { headers: { 'content-type': 'text/html' } })],
    [
      'redirect',
      () =>
        new Response(null, { status: 302, headers: { location: 'https://foreign.example.test/' } }),
    ],
    [
      'oversized decoded body',
      () => Response.json({ padding: 'x'.repeat(MAX_PROVIDER_BODY_BYTES) }),
    ],
    [
      'malformed JSON',
      () => new Response('{', { headers: { 'content-type': 'application/json' } }),
    ],
  ] as const) {
    it(`bounds ${name} before library processing`, async () => {
      const f = await fixture();
      f.setResponse(response);
      await expect(f.login()).rejects.toBeInstanceOf(BffProviderError);
      expect(f.calls).toHaveLength(1);
    });
  }

  it.each(['media type', 'redirect', 'decoded byte limit'] as const)(
    'independently rejects %s before returning an otherwise valid discovery document',
    async (defect) => {
      const document = JSON.stringify({
        issuer,
        authorization_endpoint: `${issuer}/protocol/openid-connect/auth`,
        token_endpoint: `${issuer}/protocol/openid-connect/token`,
        jwks_uri: `${issuer}/protocol/openid-connect/certs`,
        end_session_endpoint: `${issuer}/protocol/openid-connect/logout`,
      });
      for (const planted of [false, true]) {
        let cancelled = false;
        let sent = false;
        const text =
          planted && defect === 'decoded byte limit' ? document.padEnd(262_145, ' ') : document;
        const transport = vi.fn(
          async () =>
            new Response(
              new ReadableStream<Uint8Array>(
                {
                  pull(controller) {
                    if (sent) controller.close();
                    else {
                      sent = true;
                      controller.enqueue(new TextEncoder().encode(text));
                    }
                  },
                  cancel() {
                    cancelled = true;
                  },
                },
                { highWaterMark: 0 },
              ),
              {
                status: planted && defect === 'redirect' ? 302 : 200,
                headers: {
                  'content-type':
                    planted && defect === 'media type' ? 'text/html' : 'application/json',
                },
              },
            ),
        );
        const operation = providerOperation(issuer, undefined, transport);
        const received = operation.fetch(`${issuer}/.well-known/openid-configuration`);
        if (planted) {
          await expect(received).rejects.toMatchObject({ reason: 'response' });
          if (defect === 'decoded byte limit') expect(cancelled).toBe(true);
        } else {
          expect(await (await received).json()).toEqual(JSON.parse(document));
        }
        expect(transport).toHaveBeenCalledTimes(1);
      }
    },
  );

  it('accepts the literal decoded-body ceiling through the actual provider discovery path', async () => {
    const document = JSON.stringify({
      issuer,
      authorization_endpoint: `${issuer}/protocol/openid-connect/auth`,
      token_endpoint: `${issuer}/protocol/openid-connect/token`,
      jwks_uri: `${issuer}/protocol/openid-connect/certs`,
      end_session_endpoint: `${issuer}/protocol/openid-connect/logout`,
    });
    const f = await fixture();
    f.setResponse(
      () =>
        new Response(document.padEnd(262_144, ' '), {
          headers: { 'content-type': 'application/json' },
        }),
    );
    await expect(f.provider.begin(callback)).resolves.toHaveProperty('transaction');
    expect(f.calls).toHaveLength(1);
  });

  it('uses one operation deadline and distinguishes it from caller cancellation without raw causes', async () => {
    for (const cancelled of [false, true]) {
      const deadline = new AbortController();
      const caller = new AbortController();
      const timeout = vi.spyOn(AbortSignal, 'timeout').mockReturnValue(deadline.signal);
      const fetcher: typeof fetch = vi.fn(
        async (_input, init) =>
          new Promise<Response>((_resolve, reject) => {
            init?.signal?.addEventListener('abort', () => reject(new Error(privateCanary)), {
              once: true,
            });
            (cancelled ? caller : deadline).abort();
          }),
      );
      try {
        const provider = createTenantProvider({ issuer, clientSecret: privateCanary }, fetcher);
        await expect(provider.begin(callback, caller.signal)).rejects.toMatchObject({
          reason: cancelled ? 'cancelled' : 'deadline',
        });
        expect(timeout).toHaveBeenCalledExactlyOnceWith(10_000);
      } finally {
        timeout.mockRestore();
      }
    }
  });

  it('rejects unconfigured realm and non-loopback HTTP before any credential dispatch', () => {
    const fetcher = vi.fn();
    for (const value of [
      'http://identity.example.test/realms/learnstack',
      'https://identity.example.test/realms/learnstack-hub',
      'https://user:pass@identity.example.test/realms/learnstack',
      `${issuer}?private=value`,
    ])
      expect(() =>
        createTenantProvider(
          { issuer: value, clientSecret: privateCanary, allowLoopbackHttp: true },
          fetcher,
        ),
      ).toThrow(BffProviderError);
    expect(fetcher).not.toHaveBeenCalled();
  });

  it('cancels a real HTTP token response while its body is still incomplete', async () => {
    let localIssuer = '';
    let tokenSeen: () => void = () => {};
    const reachedToken = new Promise<void>((resolve) => {
      tokenSeen = resolve;
    });
    const server = http.createServer((request, response) => {
      response.setHeader('content-type', 'application/json');
      if (request.url?.endsWith('/.well-known/openid-configuration')) {
        response.end(
          JSON.stringify({
            issuer: localIssuer,
            authorization_endpoint: `${localIssuer}/protocol/openid-connect/auth`,
            token_endpoint: `${localIssuer}/protocol/openid-connect/token`,
            jwks_uri: `${localIssuer}/protocol/openid-connect/certs`,
            end_session_endpoint: `${localIssuer}/protocol/openid-connect/logout`,
          }),
        );
      } else {
        request.resume();
        response.write('{"access_token":"');
        tokenSeen();
      }
    });
    const caller = new AbortController();
    let pending: Promise<unknown> | undefined;
    let timer: ReturnType<typeof setTimeout> | undefined;
    try {
      server.listen(0, '127.0.0.1');
      await once(server, 'listening');
      const address = server.address();
      if (!address || typeof address === 'string') throw new Error('Owned HTTP stub has no port');
      localIssuer = `http://127.0.0.1:${address.port}/realms/learnstack`;
      const provider = createTenantProvider(
        { issuer: localIssuer, clientSecret: privateCanary, allowLoopbackHttp: true },
        globalThis.fetch,
      );
      const start = await provider.begin(callback);
      pending = provider.redeem(
        start.transaction,
        `${callback}?code=owned&state=${start.transaction.state}`,
        caller.signal,
      );
      await Promise.race([
        reachedToken,
        new Promise<never>((_resolve, reject) => {
          timer = setTimeout(() => reject(new Error('Owned token body was not reached')), 2000);
        }),
      ]);
      caller.abort();
      await expect(pending).rejects.toMatchObject({
        reason: 'cancelled',
        message: 'BFF provider: cancelled',
      });
    } finally {
      if (timer) clearTimeout(timer);
      caller.abort();
      await pending?.catch(() => {});
      const closed = new Promise<void>((resolve) => server.close(() => resolve()));
      server.closeAllConnections();
      await closed;
    }
  });
});
