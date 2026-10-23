// @vitest-environment node
import { once } from 'node:events';
import http from 'node:http';

import { exportJWK, generateKeyPair, SignJWT } from 'jose';
import { beforeAll, describe, expect, it, vi } from 'vitest';

import { createTenantProvider } from './provider';
import { BffProviderError } from './provider-error';
import { MAX_PROVIDER_BODY_BYTES, PROVIDER_DEADLINE_MS } from './provider-transport';

const issuer = 'https://identity.example.test/realms/learnstack';
const callback = 'https://school.example.test/api/auth/callback/keycloak';
const privateCanary = 'private-provider-response-canary';
const actorId = '019a154b-0000-7000-8000-000000000011';
const tenantId = '10000000-0000-4000-8000-000000000001';
let keys: Awaited<ReturnType<typeof generateKeyPair>>;
let otherKeys: Awaited<ReturnType<typeof generateKeyPair>>;
beforeAll(async () => {
  keys = await generateKeyPair('RS256');
  otherKeys = await generateKeyPair('RS256');
});

async function fixture(issuer = 'https://identity.example.test/realms/learnstack') {
  const calls: { url: string; method: string; body: URLSearchParams }[] = [];
  let nonce: string | undefined;
  let claims: Record<string, unknown> = {};
  let tokenPatch: Record<string, unknown> = {};
  let signingKey: CryptoKey | undefined;
  let refused = false;
  let metadataPatch: Record<string, unknown> = {};
  let response: (() => Response) | undefined;
  const publicKey = await exportJWK(keys.publicKey);
  const transport: typeof fetch = vi.fn(async (input, init) => {
    const url = String(input);
    const body = new URLSearchParams(String(init?.body ?? ''));
    calls.push({ url, method: init?.method ?? 'GET', body });
    expect(init?.redirect).toBe('error');
    expect(init?.credentials).toBe('omit');
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
    expect(url).toBe(`${issuer}/protocol/openid-connect/token`);
    expect(body.get('client_id')).toBe('learnstack-web');
    expect(body.get('client_secret')).toBe(privateCanary);
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
    const { transaction, redirectUrl, result } = await f.login();
    const authorization = new URL(redirectUrl);
    expect(authorization.origin).toBe(new URL(issuer).origin);
    expect(authorization.searchParams.get('code_challenge_method')).toBe('S256');
    expect(authorization.searchParams.get('nonce')).toBe(transaction.nonce);
    expect(authorization.searchParams.get('state')).toBe(transaction.state);
    expect(result.identity).toEqual({
      issuer,
      subject: 'external-subject',
      actorId,
      tenantId,
      organizationId: null,
    });
    expect(result.accessToken).toBe(privateCanary);
    expect(result.refreshExpiresAt).toBeGreaterThan(performance.now() + 1790_000);
    const refreshed = await f.provider.refresh(result.refreshToken!, result.identity);
    expect(refreshed.identity).toEqual(result.identity);
    expect(f.calls.filter((call) => call.url.endsWith('/token'))).toHaveLength(2);
    expect(f.calls.filter((call) => call.url.endsWith('/certs')).length).toBeGreaterThan(0);
    expect(f.calls.find((call) => call.url.endsWith('/token'))?.body.get('code_verifier')).toBe(
      transaction.verifier,
    );
    const before = f.calls.length;
    const logout = new URL(
      f.provider.logoutUrl(result.idToken, 'https://school.example.test/', 'owned-return'),
    );
    expect(logout.searchParams.get('id_token_hint')).toBe(result.idToken);
    expect(f.calls).toHaveLength(before);
  });

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
    ['invalid actor UUID', { user_id: tenantId }],
    ['missing tenant', { tenant_id: null }],
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

  it('rejects wrong signatures at both code and refresh processing', async () => {
    const f = await fixture();
    const { result } = await f.login();
    f.setWrongKey();
    await expect(f.provider.refresh(result.refreshToken!, result.identity)).rejects.toMatchObject({
      reason: 'response',
    });
    await expect(f.login()).rejects.toMatchObject({ reason: 'response' });
  });

  for (const patch of [
    { sub: 'replacement-subject' },
    { user_id: '019a154b-0000-7000-8000-000000000012' },
    { tenant_id: '20000000-0000-4000-8000-000000000002' },
    { organization_id: tenantId },
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
        expect(timeout).toHaveBeenCalledExactlyOnceWith(PROVIDER_DEADLINE_MS);
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
