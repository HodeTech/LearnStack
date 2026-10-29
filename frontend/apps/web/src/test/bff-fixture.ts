import { randomBytes } from 'node:crypto';

import { vi } from 'vitest';

import type { BffHttpRequest } from '../server/auth/http-input';
import { createBffHttpProtocol } from '../server/auth/http-protocol';
import type { NativeSessionApi } from '../server/auth/native-contract';
import type { NativeAdministrationCaller } from '../server/auth/native-operation-contract';
import type { ProviderIdentity } from '../server/auth/provider';
import type { SessionDependencies } from '../server/auth/session-contract';
import { InMemoryBffSessionStore } from '../server/auth/session-store';

export const bffHost = 'institution.example.test:3000';
export const bffSource = '203.0.113.71';
export const bffOpaque = () => randomBytes(32).toString('base64url');
export function bffFixture(options: { account?: SessionDependencies['account'] } = {}) {
  let now = 0;
  const identity: ProviderIdentity = {
    issuer: 'https://identity.example.test/realms/learnstack',
    subject: 'opaque-subject',
    actorId: '019a154b-0000-7000-8000-000000000011',
    tenantId: '10000000-0000-4000-8000-000000000001',
    organizationId: null,
    displayLabel: 'Confirmed account',
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
    actorId: identity.actorId!,
    tenantId: identity.tenantId!,
    organizationId: null,
    displayName: 'Institution',
  }));
  const scope = { tenantId: identity.tenantId!, organizationId: null };
  const association = {
    authorityId: '10000000-0000-4000-8000-000000000099',
    subjectKey: 'a'.repeat(64),
    actorId: identity.actorId!,
  };
  const native = {
    self: vi.fn<NativeSessionApi['self']>(async () => ({ state: 'active', association })),
    bootstrap: vi.fn<NativeSessionApi['bootstrap']>(async () => association),
    contexts: vi.fn<NativeSessionApi['contexts']>(async (_token, binding) => ({
      items: [
        {
          ...scope,
          tenantName: 'Institution',
          organizationName: null,
          entryOrigin: 'https://' + binding.host,
          canUseCurrentHost: true,
        },
      ],
      endCursor: null,
      hasNextPage: false,
    })),
    admit: vi.fn<NativeSessionApi['admit']>(async (_token, binding) => ({
      ...association,
      tenantId: binding.tenantId,
      organizationId: binding.organizationId,
    })),
    verifyAssociation: vi.fn<NativeSessionApi['verifyAssociation']>(),
  };
  const administration = vi.fn<NativeAdministrationCaller>(async (_token, binding, operation) => {
    if (operation.kind === 'getOwnContexts')
      return {
        kind: 'success',
        status: 200,
        etag: null,
        location: null,
        replayed: false,
        data: {
          items: [
            {
              tenantId: identity.tenantId!,
              organizationId: scope.organizationId,
              tenantName: 'Institution',
              organizationName: null,
              entryOrigin: 'https://' + binding.host,
              canUseCurrentHost: true,
            },
          ],
          pageInfo: { nextCursor: null, previousCursor: null, hasNext: false, hasPrevious: false },
        },
      };
    return {
      kind: 'success',
      status: 200,
      data: { version: '1' },
      etag: '"1"',
      location: null,
      replayed: false,
    };
  });
  const store = new InMemoryBffSessionStore({
    provider,
    native,
    administration,
    account: options.account ?? account,
    clock: { monotonic: () => now, utc: () => 1_800_000_000_000 + now },
  });
  const http = createBffHttpProtocol(store);
  async function enter(extra: Partial<Parameters<typeof store.start>[0]> = {}) {
    const started = await store.start({
      host: bffHost,
      source: bffSource,
      returnPath: '/studio',
      ...extra,
    });
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
      cookie: '__Host-ls-entry=' + finished.handle + '; __Host-ls-browser=' + finished.browser,
    };
  }
  async function login() {
    const entry = await enter();
    const finished = await store.select(
      entry.binding,
      bffSource,
      entry.finished.entry.csrfToken,
      scope,
    );
    if (finished.kind !== 'session') throw new Error('Fixture expected same-host session');
    return {
      started: entry.started,
      finished,
      binding: { host: bffHost, browser: finished.browser, handle: finished.handle },
      cookie: '__Host-ls-session=' + finished.handle + '; __Host-ls-browser=' + finished.browser,
    };
  }
  return {
    store,
    http,
    provider,
    account,
    login,
    enter,
    native,
    administration,
    scope,
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
