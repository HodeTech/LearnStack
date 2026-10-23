import { randomBytes } from 'node:crypto';

import { vi } from 'vitest';

import type { BffHttpRequest } from '../server/auth/http-input';
import { createBffHttpProtocol } from '../server/auth/http-protocol';
import type { ProviderIdentity } from '../server/auth/provider';
import { InMemoryBffSessionStore } from '../server/auth/session-store';

export const bffHost = 'institution.example.test:3000';
export const bffSource = '203.0.113.71';
export const bffOpaque = () => randomBytes(32).toString('base64url');
export function bffFixture() {
  let now = 0;
  const identity: ProviderIdentity = {
    issuer: 'https://identity.example.test/realms/learnstack',
    subject: 'opaque-subject',
    actorId: '019a154b-0000-7000-8000-000000000011',
    tenantId: '10000000-0000-4000-8000-000000000001',
    organizationId: null,
  };
  const credentials = () => ({
    accessToken: 'private-access-canary',
    idToken: 'private-id-canary',
    refreshToken: 'private-refresh-canary',
    identity,
    accessExpiresAt: now + 900_000,
    refreshExpiresAt: now + 1800_000,
  });
  const provider = {
    begin: vi.fn(async (callbackUri: string) => ({
      transaction: { state: bffOpaque(), nonce: bffOpaque(), verifier: bffOpaque(), callbackUri },
      redirectUrl: 'https://identity.example.test/authorize',
    })),
    redeem: vi.fn(async () => credentials()),
    refresh: vi.fn(async () => credentials()),
    logoutUrl: vi.fn(() => 'https://identity.example.test/logout'),
  };
  const account = vi.fn(async () => ({
    actorId: identity.actorId,
    tenantId: identity.tenantId,
    organizationId: null,
    displayName: 'Institution',
  }));
  const store = new InMemoryBffSessionStore({
    provider,
    account,
    clock: { monotonic: () => now, utc: () => 1_800_000_000_000 + now },
  });
  const http = createBffHttpProtocol(store);
  async function login() {
    const started = await store.start({ host: bffHost, source: bffSource, returnPath: '/studio' });
    const finished = await store.complete({
      host: bffHost,
      source: bffSource,
      browser: started.browser,
      state: started.state,
      proof: started.proof,
      responseUrl:
        'https://' + bffHost + '/api/auth/callback/keycloak?state=' + started.state + '&code=owned',
    });
    const binding = { host: bffHost, browser: finished.browser, handle: finished.handle };
    return {
      started,
      finished,
      binding,
      cookie: '__Host-ls-session=' + finished.handle + '; __Host-ls-browser=' + finished.browser,
    };
  }
  return {
    store,
    http,
    provider,
    account,
    login,
    advance: (ms: number) => {
      now += ms;
    },
  };
}
export function bffRequest(
  target: string,
  options: {
    method?: string;
    cookie?: string;
    form?: string;
    headers?: HeadersInit;
    signal?: AbortSignal;
    host?: string;
    source?: string;
  } = {},
): BffHttpRequest {
  const headers = new Headers(options.headers);
  const host = options.host ?? bffHost;
  if (options.cookie) headers.set('cookie', options.cookie);
  if (options.form !== undefined) {
    if (!headers.has('content-type'))
      headers.set('content-type', 'application/x-www-form-urlencoded');
    if (!headers.has('origin')) headers.set('origin', 'https://' + host);
  }
  const form = options.form;
  return {
    host,
    source: options.source ?? bffSource,
    target,
    method: options.method ?? 'GET',
    headers,
    signal: options.signal ?? new AbortController().signal,
    body:
      form === undefined
        ? null
        : new ReadableStream({
            start(controller) {
              controller.enqueue(new TextEncoder().encode(form));
              controller.close();
            },
          }),
  };
}
