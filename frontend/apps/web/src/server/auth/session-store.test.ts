// @vitest-environment node
import { randomBytes } from 'node:crypto';

import { afterEach, describe, expect, it, vi } from 'vitest';

import type { NativeAdministrationCaller } from './native-operation-contract';
import type { ProviderCredentials, ProviderIdentity } from './provider';
import { BffProviderError } from './provider-error';
import { BffSessionError } from './session-contract';
import type {
  AccountWitness,
  SessionBinding,
  SessionFailure,
  SessionProvider,
} from './session-contract';
import { BFF_LIMITS, bffSecretEquals, InMemoryBffSessionStore } from './session-store';

const host = 'institution.example.test:3000';
const secondHost = 'second.example.test:3000';
const source = '203.0.113.1';
const secret = () => randomBytes(32).toString('base64url');
const privateCanary = 'private-credential-canary';
const identity: ProviderIdentity = {
  issuer: 'https://identity.example.test/realms/learnstack',
  subject: 'opaque-subject',
  actorId: '019a154b-0000-7000-8000-000000000011',
  tenantId: '10000000-0000-4000-8000-000000000001',
  organizationId: null,
  displayLabel: 'Confirmed account',
};
function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (reason: unknown) => void;
  const promise = new Promise<T>((yes, no) => {
    resolve = yes;
    reject = no;
  });
  return { promise, resolve, reject };
}
const stores: InMemoryBffSessionStore[] = [];
const releases: (() => void)[] = [];
afterEach(async () => {
  const stopped = stores.splice(0).map((store) => store.stop());
  for (const release of releases.splice(0)) release();
  await Promise.all(stopped);
});

function fixture(options: { initialTime?: number; utcJitter?: () => number } = {}) {
  let now = options.initialTime ?? 0;
  let currentIdentity = identity;
  let sequence = 0;
  let tokenPatch: Partial<ProviderCredentials> = {};
  let accountPatch: Partial<AccountWitness> = {};
  let refreshing: ReturnType<typeof deferred<void>> | undefined;
  let redeeming: ReturnType<typeof deferred<void>> | undefined;
  let accounting: ReturnType<typeof deferred<void>> | undefined;
  const tokens = new Map<string, ProviderIdentity>();
  const diagnostics: SessionFailure[] = [];
  const diagnostic = vi.fn((reason: SessionFailure) => diagnostics.push(reason));
  function credentials(): ProviderCredentials {
    const accessToken = privateCanary + '-' + ++sequence;
    const result = {
      accessToken,
      refreshToken: privateCanary,
      idToken: privateCanary,
      identity: currentIdentity,
      accessExpiresAt: now + 900_000,
      refreshExpiresAt: now + 1800_000,
      ...tokenPatch,
    };
    tokens.set(result.accessToken, result.identity);
    return result;
  }
  const provider: SessionProvider = {
    begin: vi.fn(async (uri) => ({
      transaction: { callbackUri: uri, state: secret(), nonce: secret(), verifier: secret() },
      redirectUrl: 'https://identity.example.test/authorize',
    })),
    redeem: vi.fn(async () => {
      await redeeming?.promise;
      return credentials();
    }),
    refresh: vi.fn(async () => {
      await refreshing?.promise;
      return credentials();
    }),
    logoutUrl: vi.fn(() => 'https://identity.example.test/logout?hint=private-credential-canary'),
  };
  const account = vi.fn(async (token: string, _binding: unknown, _signal: AbortSignal) => {
    await accounting?.promise;
    const validated = tokens.get(token)!;
    return {
      actorId: validated.actorId ?? identity.actorId!,
      tenantId: validated.tenantId ?? identity.tenantId!,
      organizationId: validated.organizationId,
      displayName: 'Institution',
      ...accountPatch,
    };
  });
  const association = (token: string) => ({
    authorityId: '10000000-0000-4000-8000-000000000099',
    subjectKey: 'a'.repeat(64),
    actorId: tokens.get(token)!.actorId ?? identity.actorId!,
  });
  const native = {
    self: vi.fn(async (token: string, _binding: unknown, _signal: AbortSignal) => ({
      state: 'active' as const,
      association: association(token),
    })),
    bootstrap: vi.fn(async (token: string, _binding: unknown, _signal: AbortSignal) =>
      association(token),
    ),
    contexts: vi.fn(
      async (_token: string, binding: { host: string }, _page: unknown, _signal: AbortSignal) => ({
        items: [
          {
            tenantId: identity.tenantId!,
            organizationId: accountPatch.organizationId ?? currentIdentity.organizationId,
            tenantName: 'Institution',
            organizationName: null,
            entryOrigin: 'https://' + binding.host,
            canUseCurrentHost: true,
          },
        ],
        endCursor: null,
        hasNextPage: false,
      }),
    ),
    admit: vi.fn(
      async (
        token: string,
        binding: { tenantId: string; organizationId: string | null },
        _signal: AbortSignal,
      ) => ({
        ...association(token),
        tenantId: binding.tenantId,
        organizationId: binding.organizationId,
      }),
    ),
    verifyAssociation: vi.fn((value: ProviderIdentity, witness: { actorId: string }) => {
      if (value.actorId !== null && value.actorId !== witness.actorId)
        throw new BffSessionError('identity');
    }),
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
              organizationId: accountPatch.organizationId ?? currentIdentity.organizationId,
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
    account,
    native,
    administration,
    clock: {
      monotonic: () => now,
      utc: () => 1_800_000_000_000 + now + (options.utcJitter?.() ?? 0),
    },
    diagnostic,
  });
  stores.push(store);
  async function start(extra: Partial<Parameters<typeof store.start>[0]> = {}) {
    return store.start({ host, source, returnPath: '/studio', ...extra });
  }
  async function enter(
    login: Awaited<ReturnType<typeof start>>,
    atHost = host,
    callbackSource = source,
    signal?: AbortSignal,
  ) {
    return store.complete({
      host: atHost,
      source: callbackSource,
      browser: login.browser,
      state: login.state,
      proof: login.proof,
      responseUrl:
        'https://' +
        atHost +
        '/api/auth/callback/keycloak?state=' +
        login.state +
        '&code=owned-code',
      signal,
    });
  }
  async function complete(
    login: Awaited<ReturnType<typeof start>>,
    atHost = host,
    callbackSource = source,
    signal?: AbortSignal,
  ) {
    const entry = await enter(login, atHost, callbackSource, signal);
    const selected = await store.select(
      { host: atHost, browser: entry.browser, handle: entry.handle },
      callbackSource,
      entry.entry.csrfToken,
      {
        tenantId: identity.tenantId!,
        organizationId: accountPatch.organizationId ?? currentIdentity.organizationId,
      },
      undefined,
      signal,
    );
    if (selected.kind !== 'session') throw new Error('Fixture expected a same-host session');
    return selected;
  }
  async function login(extra: Partial<Parameters<typeof store.start>[0]> = {}) {
    const started = await start(extra);
    const finished = await complete(started, extra.host ?? host);
    return {
      started,
      finished,
      binding: { host: extra.host ?? host, browser: finished.browser, handle: finished.handle },
    };
  }
  return {
    store,
    provider,
    account,
    native,
    administration,
    identity: () => currentIdentity,
    diagnostics,
    diagnostic,
    start,
    complete,
    enter,
    login,
    credentials,
    advance(ms: number) {
      now += ms;
    },
    setIdentity(value: Partial<ProviderIdentity>) {
      currentIdentity = { ...identity, ...value };
    },
    setTokens(value: Partial<ProviderCredentials>) {
      tokenPatch = value;
    },
    setAccount(value: Partial<AccountWitness>) {
      accountPatch = value;
    },
    blockRefresh() {
      refreshing = deferred<void>();
      releases.push(() => refreshing!.resolve());
      return refreshing;
    },
    blockRedeem() {
      redeeming = deferred<void>();
      releases.push(() => redeeming!.resolve());
      return redeeming;
    },
    blockAccount() {
      accounting = deferred<void>();
      releases.push(() => accounting!.resolve());
      return accounting;
    },
  };
}
async function flush() {
  for (let i = 0; i < 12; ++i) await Promise.resolve();
}

describe('Bff_Session_Refresh_And_Revocation_Have_One_Owner', () => {
  it('keeps session CSRF and logout proofs independent from handles and all other opaque values', async () => {
    const f = fixture();
    const values: string[] = [];
    for (let i = 0; i < 2; ++i) {
      const { started, binding, finished } = await f.login();
      const logout = f.store.signout(binding, finished.session.csrfToken);
      values.push(
        started.state,
        started.proof,
        binding.browser,
        binding.handle,
        finished.session.csrfToken,
        logout.state,
        logout.proof,
      );
    }
    for (const value of values) expect(value).toMatch(/^[A-Za-z0-9_-]{43}$/);
    expect(new Set(values).size).toBe(14);
  });

  it('propagates cancelled sign-in to the actual provider owner and retains credit until settlement', async () => {
    const f = fixture();
    const held = deferred<void>();
    releases.push(() => held.resolve());
    const begin = vi.mocked(f.provider.begin).getMockImplementation()!;
    vi.mocked(f.provider.begin).mockImplementationOnce(async (...args) => {
      await held.promise;
      return begin(...args);
    });
    const controller = new AbortController();
    const pending = f.start({ signal: controller.signal });
    const refused = expect(pending).rejects.toMatchObject({ reason: 'cancelled' });
    await vi.waitFor(() => expect(f.provider.begin).toHaveBeenCalledTimes(1));
    const owner = vi.mocked(f.provider.begin).mock.calls[0]![1]!;
    expect(owner.aborted).toBe(false);
    controller.abort();
    expect(owner.aborted).toBe(true);
    expect(f.store.counts()).toMatchObject({ owners: 1, pending: 1, operations: 1 });
    expect(f.provider.redeem).not.toHaveBeenCalled();
    held.resolve();
    await refused;
    expect(Object.values(f.store.counts()).every((count) => count === 0)).toBe(true);
  });

  it.each(['redeem', 'account'] as const)(
    'propagates %s cancellation through callback or selection without publishing or releasing unfinished work',
    async (stage) => {
      const f = fixture();
      const started = await f.start();
      const held = stage === 'redeem' ? f.blockRedeem() : f.blockAccount();
      const controller = new AbortController();
      const pending = f.complete(started, host, source, controller.signal);
      const refused = expect(pending).rejects.toMatchObject({
        reason: 'cancelled',
        clearTransaction: stage === 'redeem' ? started.state : null,
      });
      await vi.waitFor(() =>
        expect(stage === 'redeem' ? f.provider.redeem : f.account).toHaveBeenCalledTimes(1),
      );
      const owner =
        stage === 'redeem'
          ? vi.mocked(f.provider.redeem).mock.calls[0]![2]!
          : f.account.mock.calls[0]![2];
      expect(owner.aborted).toBe(false);
      controller.abort();
      expect(owner.aborted).toBe(true);
      expect(f.store.counts()).toMatchObject({
        owners: 1,
        pending: 1,
        operations: 1,
        credentialRecords: stage === 'redeem' ? 0 : 1,
      });
      held.resolve();
      await refused;
      if (stage === 'redeem')
        expect(Object.values(f.store.counts()).every((count) => count === 0)).toBe(true);
      else
        expect(f.store.counts()).toMatchObject({
          owners: 1,
          pending: 1,
          operations: 0,
          credentialRecords: 1,
        });
    },
  );

  it('stores credentials privately and projects only closed immutable display/deadline/CSRF fields', async () => {
    const f = fixture();
    expect(f.provider.begin).not.toHaveBeenCalled();
    const { binding, finished } = await f.login();
    const projection = f.store.peek(binding)!;
    expect(projection).toEqual({
      state: 'authenticated',
      displayName: 'Institution',
      expiresAt: 1_800_001_800_000,
      limitedByAbsolute: false,
      csrfToken: expect.stringMatching(/^[A-Za-z0-9_-]{43}$/),
    });
    expect(Object.isFrozen(projection)).toBe(true);
    expect(JSON.stringify(projection)).not.toContain(privateCanary);
    expect(JSON.stringify(projection)).not.toContain(binding.handle);
    expect(JSON.stringify(projection)).not.toContain(binding.browser);
    expect(finished.returnPath).toBe('/studio');
    expect(f.store.counts()).toMatchObject({ owners: 1, pending: 0, groups: 1, subjects: 1 });
    f.advance(10_000);
    expect(f.store.peek(binding)!.expiresAt).toBe(projection.expiresAt);
    expect(f.provider.refresh).not.toHaveBeenCalled();
  });

  for (const field of ['host', 'browser', 'handle'] as const) {
    it('requires the original independent ' + field + ' binding', async () => {
      const f = fixture();
      const { binding } = await f.login();
      const wrong = { ...binding, [field]: field === 'host' ? secondHost : secret() };
      expect(f.store.peek(wrong)).toBeNull();
      await expect(f.store.admit(wrong, source, 'read')).rejects.toMatchObject({
        reason: 'revoked',
      });
      expect(f.store.peek(binding)).not.toBeNull();
    });
  }

  for (const field of ['host', 'browser', 'proof', 'state'] as const) {
    it('leaves a valid pending transaction untouched on mismatched ' + field, async () => {
      const f = fixture();
      const login = await f.start();
      const input = {
        host,
        source,
        browser: login.browser,
        state: login.state,
        proof: login.proof,
        responseUrl: 'https://' + host + '/api/auth/callback/keycloak',
      };
      await expect(
        f.store.complete({ ...input, [field]: field === 'host' ? secondHost : secret() }),
      ).rejects.toMatchObject({ reason: 'transaction', clearTransaction: null });
      expect(f.provider.redeem).not.toHaveBeenCalled();
      await expect(f.complete(login)).resolves.toHaveProperty('handle');
      await expect(f.complete(login)).rejects.toMatchObject({
        reason: 'transaction',
        clearTransaction: null,
      });
      expect(f.provider.redeem).toHaveBeenCalledTimes(1);
    });
  }

  for (const patch of [
    { actorId: '019a154b-0000-7000-8000-000000000012' },
    { tenantId: '20000000-0000-4000-8000-000000000002' },
    { organizationId: undefined },
  ]) {
    it(
      'refuses an independently witnessed API identity/scope conflict: ' + Object.keys(patch)[0],
      async () => {
        const f = fixture();
        f.setAccount(patch);
        const login = await f.start();
        await expect(f.complete(login)).rejects.toMatchObject({
          reason: 'identity',
          clearTransaction: null,
        });
        await flush();
        expect(f.store.counts()).toMatchObject({ owners: 1, groups: 1, pending: 1 });
        expect(f.store.counts().loginStates).toBe(0);
      },
    );
  }

  it('permits API-selected organization for tenantless identity and refuses conflicting complete provider scope', async () => {
    const f = fixture();
    const organization = '30000000-0000-4000-8000-000000000003';
    f.setIdentity({ tenantId: null });
    f.setAccount({ organizationId: organization });
    const first = await f.login();
    expect(f.store.peek(first.binding)).not.toBeNull();
    f.setIdentity({
      tenantId: identity.tenantId,
      organizationId: '40000000-0000-4000-8000-000000000004',
    });
    await expect(f.login()).rejects.toMatchObject({ reason: 'identity' });
    expect(f.store.peek(first.binding)).not.toBeNull();
  });

  it('separates constant-time proof comparison from ordinary malformed inputs', () => {
    const value = secret();
    expect(bffSecretEquals(value, value)).toBe(true);
    for (const wrong of [secret(), '', value + '=', [], null, 'a'.repeat(16_385)])
      expect(bffSecretEquals(value, wrong)).toBe(false);
  });
});

describe('source, subject and global credits', () => {
  it('refuses source 17 before reservation/provider work and preserves all 16 existing owners', async () => {
    const f = fixture();
    for (let i = 0; i < 16; ++i) await f.start({ host: i % 2 ? host : secondHost });
    await expect(f.start()).rejects.toMatchObject({ reason: 'source_capacity' });
    expect(f.provider.begin).toHaveBeenCalledTimes(16);
    expect(f.store.counts()).toMatchObject({ owners: 16, pending: 16 });
    await expect(f.start({ source: '203.0.113.2' })).resolves.toHaveProperty('state');
    expect(f.diagnostics).toEqual(['source_capacity']);
  });

  it('refuses pending 129 across eight distributed sources before provider work', async () => {
    const f = fixture();
    for (let i = 0; i < 128; ++i)
      await f.start({ source: '203.0.113.' + (1 + Math.floor(i / 16)) });
    await expect(f.start({ source: '203.0.113.99' })).rejects.toMatchObject({
      reason: 'pending_capacity',
    });
    expect(f.provider.begin).toHaveBeenCalledTimes(128);
    expect(f.store.counts().pending).toBe(128);
    f.advance(BFF_LIMITS.transactionMs);
    await expect(f.start()).resolves.toHaveProperty('state');
    expect(f.store.counts()).toMatchObject({ owners: 1, pending: 1, groups: 1 });
  });

  it('retains expired pending/source credit until actual code exchange settles', async () => {
    const f = fixture();
    const started = await f.start();
    const gate = f.blockRedeem();
    const work = f.complete(started);
    const refused = expect(work).rejects.toBeInstanceOf(BffSessionError);
    await flush();
    f.advance(BFF_LIMITS.transactionMs);
    f.store.sweep();
    expect(f.store.counts()).toMatchObject({ owners: 1, pending: 1, operations: 1 });
    gate.resolve();
    await refused;
    await flush();
    expect(f.store.counts()).toMatchObject({ owners: 0, pending: 0, groups: 0 });
  });

  it('refuses a ninth subject across hosts after validation without evicting existing sessions', async () => {
    const f = fixture();
    const bindings: SessionBinding[] = [];
    for (let i = 0; i < 8; ++i)
      bindings.push((await f.login({ host: i % 2 ? host : secondHost })).binding);
    await expect(f.login()).rejects.toMatchObject({ reason: 'subject_capacity' });
    expect(f.provider.redeem).toHaveBeenCalledTimes(9);
    for (const binding of bindings) expect(f.store.peek(binding)).not.toBeNull();
    expect(f.store.counts()).toMatchObject({ owners: 8, subjects: 8 });
  });

  it('counts logout returns against the subject cap and never needs a new pending slot', async () => {
    const f = fixture();
    const returns: { host: string; browser: string; state: string; proof: string }[] = [];
    let browser: string | undefined;
    for (let i = 0; i < 8; ++i) {
      const { binding } = await f.login({ browser });
      browser = binding.browser;
      const output = f.store.signout(binding, f.store.peek(binding)!.csrfToken);
      expect(f.store.peek(binding)).toBeNull();
      returns.push({ host, browser: binding.browser, ...output });
    }
    expect(f.store.counts()).toMatchObject({ owners: 8, pending: 0, subjects: 8 });
    await expect(f.login()).rejects.toMatchObject({ reason: 'subject_capacity' });
    expect(f.store.consumeLogout(returns[0]!)).toBe(true);
    expect(f.store.consumeLogout(returns[0]!)).toBe(false);
    await expect(f.login()).resolves.toHaveProperty('binding');
    expect(f.store.counts().subjects).toBe(8);
  });

  it('bounds all 1024 owners and fully reclaims expiry before global refusal', async () => {
    const f = fixture();
    let retained!: SessionBinding;
    for (let i = 0; i < 1024; ++i) {
      f.setIdentity({ subject: 'subject-' + i });
      retained = (await f.login()).binding;
    }
    const calls = vi.mocked(f.provider.begin).mock.calls.length;
    await expect(f.start()).rejects.toMatchObject({ reason: 'global_capacity' });
    expect(f.provider.begin).toHaveBeenCalledTimes(calls);
    expect(f.store.peek(retained)).not.toBeNull();
    f.advance(BFF_LIMITS.idleMs);
    await expect(f.start()).resolves.toHaveProperty('state');
    expect(f.store.counts()).toMatchObject({ owners: 1, pending: 1, groups: 1, subjects: 0 });
  });
});

describe('browser elections and explicit replacement', () => {
  it('publishes only the first validated tab and never consumes/clears a different transaction proof', async () => {
    const f = fixture();
    const first = await f.start();
    const second = await f.start({ browser: first.browser });
    const gate = f.blockRedeem();
    const a = f.complete(first);
    const b = f.complete(second);
    const rejected = expect(b).rejects.toMatchObject({
      reason: 'transaction',
      clearTransaction: null,
    });
    await flush();
    gate.resolve();
    const output = await a;
    await rejected;
    expect(f.store.peek({ host, browser: output.browser, handle: output.handle })).not.toBeNull();
    expect(f.store.counts()).toMatchObject({ owners: 1, pending: 0, groups: 1 });
  });

  it('keeps the old session on failed replacement validation or new-subject capacity refusal', async () => {
    const f = fixture();
    const old = await f.login();
    for (let i = 0; i < 8; ++i) {
      f.setIdentity({ subject: 'other-subject' });
      await f.login();
    }
    await expect(
      f.login({ browser: old.binding.browser, handle: old.binding.handle }),
    ).rejects.toMatchObject({ reason: 'subject_capacity' });
    expect(f.store.peek(old.binding)).not.toBeNull();
    f.setIdentity({ subject: identity.subject });
    f.setAccount({ tenantId: '20000000-0000-4000-8000-000000000002' });
    await expect(
      f.login({ browser: old.binding.browser, handle: old.binding.handle }),
    ).rejects.toMatchObject({ reason: 'identity' });
    expect(f.store.peek(old.binding)).not.toBeNull();
  });

  it('replaces only its verified same-browser primary, holding subject credit until old I/O settles', async () => {
    const f = fixture();
    const old = await f.login();
    for (let i = 0; i < 6; ++i) await f.login();
    const gate = f.blockRefresh();
    const refresh = f.store.admit(old.binding, source, 'extend');
    const ended = expect(refresh).rejects.toBeInstanceOf(BffSessionError);
    await flush();
    const login = await f.start({ browser: old.binding.browser, handle: old.binding.handle });
    let published = false;
    const replacement = f.complete(login).then((value) => {
      published = true;
      return value;
    });
    await vi.waitFor(() => expect(f.store.peek(old.binding)).toBeNull());
    expect(published).toBe(false);
    expect(f.store.peek(old.binding)).toBeNull();
    expect(f.store.counts()).toMatchObject({ owners: 8, pending: 1, subjects: 8 });
    await expect(f.login()).rejects.toMatchObject({ reason: 'subject_capacity' });
    gate.resolve();
    await ended;
    const next = await replacement;
    expect(f.store.peek({ host, browser: next.browser, handle: next.handle })).not.toBeNull();
    expect(f.store.peek(old.binding)).toBeNull();
    expect(f.store.counts()).toMatchObject({ owners: 7, subjects: 7 });
  });

  it('a browser binding without its primary cookie cannot retire that primary', async () => {
    const f = fixture();
    const old = await f.login();
    const next = await f.login({ browser: old.binding.browser });
    expect(next.binding.browser).not.toBe(old.binding.browser);
    expect(f.store.peek(old.binding)).not.toBeNull();
    expect(f.store.peek(next.binding)).not.toBeNull();
    expect(f.store.counts()).toMatchObject({ owners: 2, groups: 2 });
  });

  it('logout fences earlier tabs; a later sign-in and late logout return leave the new session live', async () => {
    const f = fixture();
    const old = await f.login();
    const pending = await f.start({ browser: old.binding.browser, handle: old.binding.handle });
    const logout = f.store.signout(old.binding, f.store.peek(old.binding)!.csrfToken);
    await expect(f.complete(pending)).rejects.toMatchObject({
      reason: 'transaction',
      clearTransaction: null,
    });
    const next = await f.login({ browser: old.binding.browser });
    expect(
      f.store.consumeLogout({ host, browser: old.binding.browser, ...logout, proof: secret() }),
    ).toBe(false);
    expect(f.store.consumeLogout({ host, browser: old.binding.browser, ...logout })).toBe(true);
    expect(f.store.peek(next.binding)).not.toBeNull();
    expect(f.store.peek(old.binding)).toBeNull();
  });
});

describe('effective deadlines and one refresh owner', () => {
  for (const reason of ['transport', 'deadline', 'response', 'provider_refused'] as const) {
    it(
      'denies a failed ordinary API read without revoking or touching the live session: ' + reason,
      async () => {
        const f = fixture();
        const { binding } = await f.login();
        const before = f.store.peek(binding);
        f.advance(10_000);
        f.account.mockRejectedValueOnce(new BffProviderError(reason));
        await expect(f.store.admit(binding, source, 'read')).rejects.toMatchObject({ reason });
        expect(f.store.peek(binding)).toEqual(before);
        expect(f.store.counts()).toMatchObject({ owners: 1, subjects: 1, operations: 0 });
        expect(f.provider.refresh).not.toHaveBeenCalled();
        expect(f.diagnostics).toEqual([reason]);
        await expect(f.store.admit(binding, source, 'read')).resolves.toMatchObject({
          state: 'authenticated',
        });
        expect(f.store.peek(binding)).not.toBeNull();
      },
    );

    it(
      'revokes if the same API failure prevents validation of an already consumed refresh grant: ' +
        reason,
      async () => {
        const f = fixture();
        const { binding } = await f.login();
        f.account.mockRejectedValueOnce(new BffProviderError(reason));
        await expect(f.store.admit(binding, source, 'extend')).rejects.toMatchObject({ reason });
        expect(f.provider.refresh).toHaveBeenCalledTimes(1);
        expect(f.store.peek(binding)).toBeNull();
        await expect(f.store.admit(binding, source, 'read')).rejects.toMatchObject({
          reason: 'revoked',
        });
        expect(f.provider.refresh).toHaveBeenCalledTimes(1);
      },
    );
  }

  it('revokes an ordinary read on a proven API identity refusal', async () => {
    const f = fixture();
    const { binding } = await f.login();
    f.account.mockRejectedValueOnce(new BffProviderError('identity'));
    await expect(f.store.admit(binding, source, 'read')).rejects.toMatchObject({
      reason: 'identity',
    });
    expect(f.store.peek(binding)).toBeNull();
    expect(f.store.counts().owners).toBe(0);
  });

  it('uses the rotated refresh token for the next grant and preserves it only when replacement is omitted', async () => {
    const f = fixture();
    const { binding } = await f.login();
    const rotated = privateCanary + '-rotated';
    f.setTokens({ refreshToken: rotated });
    await f.store.admit(binding, source, 'extend');
    f.setTokens({ refreshToken: undefined });
    await f.store.admit(binding, source, 'extend');
    await f.store.admit(binding, source, 'extend');
    expect(vi.mocked(f.provider.refresh).mock.calls.map((call) => call[0])).toEqual([
      privateCanary,
      rotated,
      rotated,
    ]);
    expect(JSON.stringify(f.store.peek(binding))).not.toContain(rotated);
  });

  it('passive reads do not touch idle or call API/provider, while access-expired live activity refreshes once', async () => {
    const f = fixture();
    const { binding } = await f.login();
    const expiry = f.store.peek(binding)!.expiresAt;
    const calls = f.account.mock.calls.length;
    f.advance(901_000);
    await f.store.admit(binding, source, 'passive');
    expect(f.provider.refresh).not.toHaveBeenCalled();
    expect(f.account).toHaveBeenCalledTimes(calls);
    expect(f.store.peek(binding)!.expiresAt).toBe(expiry);
    await f.store.admit(binding, source, 'read');
    expect(f.provider.refresh).toHaveBeenCalledTimes(1);
    expect(f.store.peek(binding)!.expiresAt).toBeGreaterThan(expiry);
  });

  it('extension refreshes even fresh access; concurrent extension/read callers share one grant', async () => {
    const f = fixture();
    const { binding } = await f.login();
    f.advance(10_000);
    const gate = f.blockRefresh();
    const calls = f.account.mock.calls.length;
    const requests = Array.from({ length: 32 }, (_, i) =>
      f.store.admit(binding, source, i % 2 ? 'read' : 'extend'),
    );
    await flush();
    expect(f.provider.refresh).toHaveBeenCalledTimes(1);
    gate.resolve();
    const projections = await Promise.all(requests);
    expect(new Set(projections.map((value) => value.expiresAt)).size).toBe(1);
    expect(f.account).toHaveBeenCalledTimes(calls + 1);
    expect(projections[0]!.expiresAt).toBe(1_800_001_810_000);
  });

  it('cancelling one waiter leaves the shared refresh owner and other callers intact', async () => {
    const f = fixture();
    const { binding } = await f.login();
    const gate = f.blockRefresh();
    const controller = new AbortController();
    const a = f.store.admit(binding, source, 'extend', controller.signal);
    const ended = expect(a).rejects.toMatchObject({ reason: 'cancelled' });
    const b = f.store.admit(binding, source, 'extend');
    await flush();
    controller.abort();
    await ended;
    expect(vi.mocked(f.provider.refresh).mock.calls[0]![2]!.aborted).toBe(false);
    expect(f.store.counts().operations).toBe(1);
    gate.resolve();
    await expect(b).resolves.toHaveProperty('state', 'authenticated');
    expect(f.provider.refresh).toHaveBeenCalledTimes(1);
  });

  for (const reason of [
    'invalid_grant',
    'transport',
    'deadline',
    'response',
    'identity',
  ] as const) {
    it('revokes uncertain refresh exactly once without replay: ' + reason, async () => {
      const f = fixture();
      const { binding } = await f.login();
      vi.mocked(f.provider.refresh).mockRejectedValue(new BffProviderError(reason));
      await expect(f.store.admit(binding, source, 'extend')).rejects.toMatchObject({ reason });
      expect(f.store.peek(binding)).toBeNull();
      await expect(f.store.admit(binding, source, 'read')).rejects.toMatchObject({
        reason: 'revoked',
      });
      expect(f.provider.refresh).toHaveBeenCalledTimes(1);
      expect(f.diagnostics).toEqual([reason]);
    });
  }

  for (const patch of [
    { subject: 'other-subject' },
    { issuer: 'https://other.example.test/realms/learnstack' },
    { actorId: '019a154b-0000-7000-8000-000000000012' },
    { tenantId: '20000000-0000-4000-8000-000000000002' },
    { organizationId: '30000000-0000-4000-8000-000000000003' },
  ]) {
    it('does not publish refresh identity reassignment of ' + Object.keys(patch)[0], async () => {
      const f = fixture();
      const { binding } = await f.login();
      f.setIdentity(patch);
      await expect(f.store.admit(binding, source, 'extend')).rejects.toMatchObject({
        reason: 'identity',
      });
      expect(f.store.peek(binding)).toBeNull();
    });
  }

  it('preserves raw organization absence and independently refuses resolved-organization drift', async () => {
    const f = fixture();
    const { binding } = await f.login();
    f.setAccount({ organizationId: '30000000-0000-4000-8000-000000000003' });
    await expect(f.store.admit(binding, source, 'extend')).rejects.toMatchObject({
      reason: 'identity',
    });
    expect(f.store.peek(binding)).toBeNull();
  });

  it('preserves an omitted replacement refresh token without resetting the original absolute deadline', async () => {
    const f = fixture();
    const { binding } = await f.login();
    f.setTokens({ refreshToken: undefined });
    for (let i = 0; i < 31; ++i) {
      f.advance(15 * 60_000);
      await f.store.admit(binding, source, 'extend');
    }
    const final = f.store.peek(binding)!;
    expect(final.expiresAt).toBe(1_800_000_000_000 + BFF_LIMITS.absoluteMs);
    expect(final.limitedByAbsolute).toBe(true);
    expect(vi.mocked(f.provider.refresh).mock.calls.at(-1)![0]).toBe(privateCanary);
    f.advance(15 * 60_000);
    expect(f.store.peek(binding)).toBeNull();
    await expect(f.store.admit(binding, source, 'extend')).rejects.toMatchObject({
      reason: 'revoked',
    });
  });

  it('never extends the provider deadline by touching local idle alone', async () => {
    const f = fixture();
    f.setTokens({ refreshExpiresAt: 120_000 });
    const { binding } = await f.login();
    f.advance(60_000);
    await f.store.admit(binding, source, 'read');
    expect(f.store.peek(binding)!.expiresAt).toBe(1_800_000_120_000);
    f.advance(60_000);
    expect(f.store.peek(binding)).toBeNull();
    expect(f.provider.refresh).not.toHaveBeenCalled();
  });

  it('refuses late refresh after effective expiry even if the new provider bundle is live', async () => {
    const f = fixture();
    const { binding } = await f.login();
    const gate = f.blockRefresh();
    const work = f.store.admit(binding, source, 'extend');
    const refused = expect(work).rejects.toMatchObject({ reason: 'expired' });
    await flush();
    f.advance(BFF_LIMITS.idleMs);
    gate.resolve();
    await refused;
    expect(f.store.peek(binding)).toBeNull();
  });
});

describe('logout, actual settlement and shutdown', () => {
  it('reports one closed local signout transition without logging invalid or repeated attempts', async () => {
    const f = fixture();
    const { binding, finished } = await f.login();
    expect(() => f.store.signout(binding, secret())).toThrow(BffSessionError);
    expect(f.diagnostic).not.toHaveBeenCalled();
    const logout = f.store.signout(binding, finished.session.csrfToken);
    expect(f.diagnostic.mock.calls).toEqual([['revoked']]);
    expect(f.store.counts()).toMatchObject({ credentialRecords: 0, logoutStates: 1 });
    expect(() => f.store.signout(binding, finished.session.csrfToken)).toThrow(BffSessionError);
    expect(f.store.consumeLogout({ ...binding, ...logout })).toBe(true);
    expect(f.store.consumeLogout({ ...binding, ...logout })).toBe(false);
    f.advance(BFF_LIMITS.transactionMs);
    f.store.sweep();
    expect(f.diagnostic.mock.calls).toEqual([['revoked']]);
  });

  it('retains logout-return subject credit through aborted refresh, return consumption and actual settlement', async () => {
    const f = fixture();
    const { binding } = await f.login();
    const gate = f.blockRefresh();
    const refresh = f.store.admit(binding, source, 'extend');
    const ended = expect(refresh).rejects.toMatchObject({ reason: 'revoked' });
    await flush();
    const logout = f.store.signout(binding, f.store.peek(binding)!.csrfToken);
    expect(f.store.peek(binding)).toBeNull();
    expect(f.store.counts()).toMatchObject({ owners: 1, subjects: 1, operations: 1 });
    expect(f.store.consumeLogout({ host, browser: binding.browser, ...logout })).toBe(true);
    expect(f.store.counts()).toMatchObject({ owners: 1, subjects: 1, operations: 1 });
    gate.resolve();
    await ended;
    await flush();
    expect(f.store.counts()).toMatchObject({ owners: 0, subjects: 0, operations: 0 });
  });

  it('an aborted refresh cannot discard an unconsumed logout credit before its five-minute expiry', async () => {
    const f = fixture();
    const { binding } = await f.login();
    const gate = f.blockRefresh();
    const work = f.store.admit(binding, source, 'extend');
    const ended = expect(work).rejects.toBeInstanceOf(BffSessionError);
    await flush();
    const logout = f.store.signout(binding, f.store.peek(binding)!.csrfToken);
    gate.resolve();
    await ended;
    await flush();
    expect(f.store.counts()).toMatchObject({ owners: 1, subjects: 1, operations: 0 });
    f.advance(BFF_LIMITS.transactionMs);
    f.store.sweep();
    expect(f.store.counts()).toMatchObject({ owners: 0, subjects: 0 });
    expect(f.store.consumeLogout({ host, browser: binding.browser, ...logout })).toBe(false);
  });

  it('a provider logout URL failure still revokes locally, drops credentials and keeps only the bounded return', async () => {
    const f = fixture();
    const { binding } = await f.login();
    vi.mocked(f.provider.logoutUrl).mockImplementation(() => {
      throw new Error(privateCanary);
    });
    const output = f.store.signout(binding, f.store.peek(binding)!.csrfToken);
    expect(output.redirectUrl).toBeNull();
    expect(f.store.peek(binding)).toBeNull();
    expect(f.diagnostics).toEqual(['response', 'revoked']);
    expect(f.store.counts()).toMatchObject({ owners: 1, pending: 0, subjects: 1 });
  });

  it('stop closes all gates immediately and joins actual work without claiming timeout settlement', async () => {
    const f = fixture();
    const login = await f.start();
    const gate = f.blockRedeem();
    const completing = f.complete(login);
    const refused = expect(completing).rejects.toMatchObject({ reason: 'stopped' });
    await flush();
    let stopped = false;
    const first = f.store.stop();
    expect(f.store.stop()).toBe(first);
    void first.then(() => {
      stopped = true;
    });
    await flush();
    expect(stopped).toBe(false);
    await expect(f.start()).rejects.toMatchObject({ reason: 'stopped' });
    expect(f.store.counts()).toMatchObject({ owners: 1, pending: 1, operations: 1 });
    gate.resolve();
    await refused;
    await first;
    expect(Object.values(f.store.counts()).every((count) => count === 0)).toBe(true);
  });
});

describe('refusal and expiry boundaries', () => {
  it('refuses unverified/canonicalized addresses and undeclared return paths before allocation', async () => {
    const f = fixture();
    for (const invalid of [
      { host: 'Institution.example.test:3000' },
      { host: 'evil.example.test/path' },
      { source: 'hostname.example.test' },
      { source: '::ffff:203.0.113.1' },
      { browser: 'malformed' },
      { returnPath: '//foreign.example.test' as '/' },
    ])
      await expect(f.start(invalid)).rejects.toMatchObject({ reason: 'configuration' });
    expect(f.provider.begin).not.toHaveBeenCalled();
    expect(f.store.counts().owners).toBe(0);
  });

  it('a verified expired callback clears only its own transaction and never dispatches a grant', async () => {
    const f = fixture();
    const started = await f.start();
    f.advance(BFF_LIMITS.transactionMs);
    await expect(f.complete(started)).rejects.toMatchObject({
      reason: 'expired',
      clearTransaction: started.state,
    });
    expect(f.provider.redeem).not.toHaveBeenCalled();
    expect(f.store.counts()).toMatchObject({ owners: 0, groups: 0, pending: 0 });
  });

  it('does not change a live session on an invalid signout CSRF proof', async () => {
    const f = fixture();
    const { binding } = await f.login();
    expect(() => f.store.signout(binding, secret())).toThrow(BffSessionError);
    expect(f.store.peek(binding)).not.toBeNull();
    expect(f.provider.logoutUrl).not.toHaveBeenCalled();
  });

  it('checks the merged credential size when refresh omits a previously large replacement token', async () => {
    const f = fixture();
    f.setTokens({ refreshToken: 'r'.repeat(15_000), idToken: 'i'.repeat(15_000) });
    const { binding } = await f.login();
    f.setTokens({ refreshToken: undefined, idToken: 'i'.repeat(18_000) });
    await expect(f.store.admit(binding, source, 'extend')).rejects.toMatchObject({
      reason: 'response',
    });
    expect(f.store.peek(binding)).toBeNull();
  });

  it('a cancelled normal-read waiter cannot hide a later independently refused API outcome', async () => {
    const f = fixture();
    const { binding } = await f.login();
    const gate = f.blockAccount();
    const controller = new AbortController();
    const work = f.store.admit(binding, source, 'read', controller.signal);
    const cancelled = expect(work).rejects.toMatchObject({ reason: 'cancelled' });
    await flush();
    controller.abort();
    await cancelled;
    expect(f.store.peek(binding)).not.toBeNull();
    f.setAccount({ actorId: '019a154b-0000-7000-8000-000000000012' });
    gate.resolve();
    await flush();
    expect(f.store.peek(binding)).toBeNull();
    expect(f.diagnostics).toEqual(['identity']);
  });

  it('the owned thirty-second maintenance timer performs a full expiry pass and is stopped', async () => {
    vi.useFakeTimers();
    try {
      const f = fixture();
      for (let i = 0; i < 16; ++i) await f.start();
      f.advance(BFF_LIMITS.transactionMs);
      await vi.advanceTimersByTimeAsync(BFF_LIMITS.sweepMs);
      expect(f.store.counts()).toMatchObject({ owners: 0, pending: 0, groups: 0 });
      expect(f.diagnostic.mock.calls).toEqual(Array.from({ length: 16 }, () => ['expired']));
      await vi.advanceTimersByTimeAsync(BFF_LIMITS.sweepMs * 2);
      expect(f.diagnostic).toHaveBeenCalledTimes(16);
      await f.store.stop();
      expect(vi.getTimerCount()).toBe(0);
    } finally {
      vi.useRealTimers();
    }
  });

  it('the maintenance tick reports expired once while actual work retains its closing owner', async () => {
    vi.useFakeTimers();
    try {
      const f = fixture();
      f.setTokens({ accessExpiresAt: 86_400_000, refreshExpiresAt: 86_400_000 });
      const { binding } = await f.login();
      f.advance(BFF_LIMITS.idleMs - 1);
      const held = f.blockAccount();
      const work = f.store.admit(binding, source, 'read');
      const refused = expect(work).rejects.toMatchObject({ reason: 'revoked' });
      await flush();
      expect(f.account).toHaveBeenCalledTimes(2);
      await vi.advanceTimersByTimeAsync(BFF_LIMITS.sweepMs);
      expect(f.diagnostic).not.toHaveBeenCalled();
      f.advance(1);
      await vi.advanceTimersByTimeAsync(BFF_LIMITS.sweepMs);
      expect(f.diagnostic.mock.calls).toEqual([['expired']]);
      expect(f.store.counts()).toMatchObject({
        owners: 1,
        operations: 1,
        credentialRecords: 0,
      });
      await vi.advanceTimersByTimeAsync(BFF_LIMITS.sweepMs * 2);
      expect(f.diagnostic.mock.calls).toEqual([['expired']]);
      held.resolve();
      await refused;
      await flush();
      expect(Object.values(f.store.counts()).every((count) => count === 0)).toBe(true);
      // Keep the independent failed-read observation as well as the transition.
      expect(f.diagnostic.mock.calls).toEqual([['expired'], ['revoked']]);
    } finally {
      vi.useRealTimers();
    }
  });
});

describe('current callback visitor provenance', () => {
  it('uses the callback IP for AccountSite while retaining original pending-source credit', async () => {
    const f = fixture();
    const login = await f.start();
    for (let i = 0; i < 15; ++i) await f.start();
    const callbackSource = '203.0.113.2';
    const gate = f.blockRedeem();
    const completing = f.complete(login, host, callbackSource);
    await flush();
    await expect(f.start()).rejects.toMatchObject({ reason: 'source_capacity' });
    await expect(f.start({ source: callbackSource })).resolves.toHaveProperty('state');
    gate.resolve();
    await completing;
    expect(f.account).toHaveBeenCalledTimes(1);
    expect(f.account.mock.calls[0]![1]).toEqual({
      host,
      source: callbackSource,
      tenantId: identity.tenantId,
      organizationId: null,
    });
  });

  it('refuses a malformed callback source before consuming a valid transaction', async () => {
    const f = fixture();
    const login = await f.start();
    for (const invalid of ['::ffff:203.0.113.1', '', undefined, null])
      await expect(
        f.store.complete({
          host,
          source: invalid as string,
          browser: login.browser,
          state: login.state,
          proof: login.proof,
          responseUrl: 'https://' + host + '/api/auth/callback/keycloak',
        }),
      ).rejects.toMatchObject({
        reason: 'configuration',
        clearTransaction: null,
      });
    expect(f.provider.redeem).not.toHaveBeenCalled();
    await expect(f.complete(login)).resolves.toHaveProperty('handle');
    expect(f.provider.redeem).toHaveBeenCalledTimes(1);
  });
});

describe('independent lifetime and logical custody controls', () => {
  const hour = 60 * 60_000;
  function longProvider() {
    const f = fixture({ initialTime: 12_345.5 });
    f.setTokens({ accessExpiresAt: 24 * hour, refreshExpiresAt: 24 * hour });
    return f;
  }
  it('keeps the accepted literal resource and lifetime limits', () => {
    expect(BFF_LIMITS).toEqual({
      owners: 1024,
      pending: 128,
      source: 16,
      subject: 8,
      transactionMs: 300_000,
      idleMs: 1_800_000,
      absoluteMs: 28_800_000,
      sweepMs: 30_000,
      refreshAheadMs: 60_000,
      credentialBytes: 32_768,
    });
  });
  it('expires at thirty idle minutes even when the provider allows twenty-four hours', async () => {
    const f = longProvider();
    const { binding } = await f.login();
    f.advance(1_799_999);
    expect(f.store.peek(binding)).not.toBeNull();
    f.advance(1);
    expect(f.store.peek(binding)).toBeNull();
  });
  it.each(['passive', 'peek', 'transport', 'deadline', 'response', 'provider_refused'] as const)(
    '%s never advances the independent idle deadline',
    async (activity) => {
      const f = longProvider();
      const { binding } = await f.login();
      f.advance(20 * 60_000);
      if (activity === 'peek') f.store.peek(binding);
      else if (activity === 'passive') await f.store.admit(binding, source, 'passive');
      else {
        f.account.mockRejectedValueOnce(new BffProviderError(activity));
        await expect(f.store.admit(binding, source, 'read')).rejects.toMatchObject({
          reason: activity,
        });
      }
      f.advance(10 * 60_000);
      expect(f.store.peek(binding)).toBeNull();
    },
  );
  it('anchors both idle and absolute lifetime at successful publication after delayed provider/API work', async () => {
    const f = fixture();
    f.setTokens({ accessExpiresAt: 24 * hour, refreshExpiresAt: 24 * hour });
    const started = await f.start();
    f.advance(120_000);
    const held = f.blockAccount();
    const pending = f.complete(started);
    await vi.waitFor(() => expect(f.account).toHaveBeenCalledTimes(1));
    f.advance(60_000);
    held.resolve();
    const finished = await pending;
    const binding = { host, browser: finished.browser, handle: finished.handle };
    const publishedAt = 180_000;
    expect(finished.session.expiresAt).toBe(1_800_000_000_000 + publishedAt + 1_800_000);
    for (let i = 0; i < 31; ++i) {
      f.advance(15 * 60_000);
      await f.store.admit(binding, source, 'read');
    }
    expect(f.store.peek(binding)).toMatchObject({
      expiresAt: 1_800_000_000_000 + publishedAt + 28_800_000,
      limitedByAbsolute: true,
    });
    expect(f.provider.refresh).not.toHaveBeenCalled();
    f.advance(15 * 60_000);
    expect(f.store.peek(binding)).toBeNull();
  });

  it('passes the current refreshed ID token, exact root return and independent state to logout', async () => {
    const f = fixture();
    f.setTokens({ idToken: 'initial-id-token' });
    const { binding } = await f.login();
    f.setTokens({ idToken: 'current-id-token', refreshToken: 'current-refresh-token' });
    const projection = await f.store.admit(binding, source, 'extend');
    const logout = f.store.signout(binding, projection.csrfToken);
    expect(f.provider.logoutUrl).toHaveBeenCalledExactlyOnceWith(
      'current-id-token',
      'https://' + host + '/',
      logout.state,
    );
    expect(f.store.counts()).toMatchObject({ credentialRecords: 0, logoutStates: 1 });
  });
  it('successful ordinary authority admission advances idle without refreshing fresh credentials', async () => {
    const f = longProvider();
    const { binding } = await f.login();
    f.advance(10 * 60_000);
    await f.store.admit(binding, source, 'read');
    f.advance(1_799_999);
    expect(f.store.peek(binding)).not.toBeNull();
    f.advance(1);
    expect(f.store.peek(binding)).toBeNull();
    expect(f.provider.refresh).not.toHaveBeenCalled();
  });
  it('refreshes at sixty seconds before access expiry, but not sixty seconds plus one millisecond', async () => {
    const f = fixture({ initialTime: 1234 });
    f.setTokens({ refreshExpiresAt: 24 * hour });
    const { binding } = await f.login();
    f.advance(839_999);
    await f.store.admit(binding, source, 'read');
    expect(f.provider.refresh).not.toHaveBeenCalled();
    f.advance(1);
    await f.store.admit(binding, source, 'read');
    expect(f.provider.refresh).toHaveBeenCalledTimes(1);
  });
  it('a successful shared refresh advances idle even after its only waiter cancels', async () => {
    const f = longProvider();
    const { binding } = await f.login();
    f.advance(10 * 60_000);
    const held = f.blockRefresh();
    const controller = new AbortController();
    const attempted = f.store.admit(binding, source, 'extend', controller.signal);
    await vi.waitFor(() => expect(f.provider.refresh).toHaveBeenCalledTimes(1));
    controller.abort();
    await expect(attempted).rejects.toMatchObject({ reason: 'cancelled' });
    f.advance(60_000);
    held.resolve();
    await vi.waitFor(() => expect(f.store.counts().operations).toBe(0));
    f.advance(1_799_999);
    expect(f.store.peek(binding)).not.toBeNull();
    f.advance(1);
    expect(f.store.peek(binding)).toBeNull();
  });
  it('maintenance uses active idle lifetime rather than the expired login transaction deadline', async () => {
    const f = longProvider();
    const { binding } = await f.login();
    f.advance(10 * 60_000);
    f.store.sweep();
    expect(f.store.peek(binding)).not.toBeNull();
    f.advance(20 * 60_000);
    f.store.sweep();
    expect(f.store.counts()).toMatchObject({ owners: 0, credentialRecords: 0 });
  });
  it('starts a fresh five-minute logout-return window when an older session signs out', async () => {
    const f = longProvider();
    const { binding, finished } = await f.login();
    f.advance(4 * 60_000);
    const logout = f.store.signout(binding, finished.session.csrfToken);
    f.advance(2 * 60_000);
    expect(f.store.consumeLogout({ ...binding, ...logout })).toBe(true);
    expect(f.store.counts()).toMatchObject({ owners: 0, logoutStates: 0 });
  });
  it('rejects logout return at the exact five-minute boundary and reclaims its index', async () => {
    const f = longProvider();
    const { binding, finished } = await f.login();
    f.advance(4 * 60_000);
    const logout = f.store.signout(binding, finished.session.csrfToken);
    f.advance(300_000);
    expect(f.store.consumeLogout({ ...binding, ...logout })).toBe(false);
    f.store.sweep();
    expect(f.store.counts()).toMatchObject({ owners: 0, logoutStates: 0 });
  });
  it('rejects a callback at the exact five-minute transaction boundary', async () => {
    const f = longProvider();
    const started = await f.start();
    f.advance(300_000);
    await expect(f.complete(started)).rejects.toMatchObject({ reason: 'expired' });
    expect(f.provider.redeem).not.toHaveBeenCalled();
    expect(f.store.counts()).toMatchObject({ owners: 0, loginStates: 0 });
  });
  it('cannot restore idle lifetime when post-refresh AccountSite finishes after expiry', async () => {
    const f = longProvider();
    const { binding } = await f.login();
    const held = f.blockAccount();
    const pending = f.store.admit(binding, source, 'extend');
    const refused = expect(pending).rejects.toMatchObject({ reason: 'expired' });
    await vi.waitFor(() => expect(f.account).toHaveBeenCalledTimes(2));
    f.advance(1_800_000);
    held.resolve();
    await refused;
    expect(f.store.peek(binding)).toBeNull();
    expect(f.store.counts()).toMatchObject({ owners: 0, credentialRecords: 0 });
  });
  it('releases every failed begin reservation before another source slot is requested', async () => {
    const f = fixture();
    vi.mocked(f.provider.begin).mockRejectedValue(new BffProviderError('transport'));
    for (let i = 0; i < 17; ++i) {
      await expect(f.start()).rejects.toMatchObject({ reason: 'transport' });
      expect(f.store.counts()).toMatchObject({ owners: 0, pending: 0, groups: 0, loginStates: 0 });
    }
    expect(f.provider.begin).toHaveBeenCalledTimes(17);
    vi.mocked(f.provider.begin).mockImplementation(async (callbackUri) => ({
      transaction: { callbackUri, state: secret(), nonce: secret(), verifier: secret() },
      redirectUrl: 'https://identity.example.test/authorize',
    }));
    await f.login();
    expect(f.store.counts().owners).toBe(1);
  });
  it('idle same-browser replacement releases old owners and keeps one subject credit', async () => {
    const f = fixture();
    let current = await f.login();
    for (let i = 0; i < 12; ++i) {
      const old = current.binding;
      current = await f.login({ browser: old.browser, handle: old.handle });
      expect(f.store.peek(old)).toBeNull();
      expect(f.store.counts()).toMatchObject({ owners: 1, subjects: 1, credentialRecords: 1 });
    }
  });
  it('a concurrent second submission of the same callback never redeems its code twice', async () => {
    const f = fixture();
    const started = await f.start();
    const held = f.blockRedeem();
    const first = f.complete(started);
    await vi.waitFor(() => expect(f.provider.redeem).toHaveBeenCalledTimes(1));
    await expect(f.complete(started)).rejects.toMatchObject({ reason: 'transaction' });
    held.resolve();
    await first;
    expect(f.provider.redeem).toHaveBeenCalledTimes(1);
    expect(f.store.counts()).toMatchObject({ owners: 1, loginStates: 0 });
  });
  it('a logout return is single-use even while revoked AccountSite work remains unsettled', async () => {
    const f = fixture();
    const { binding, finished } = await f.login();
    const held = f.blockAccount();
    const read = f.store.admit(binding, source, 'read');
    const refused = expect(read).rejects.toMatchObject({ reason: 'revoked' });
    await vi.waitFor(() => expect(f.account).toHaveBeenCalledTimes(2));
    const logout = f.store.signout(binding, finished.session.csrfToken);
    expect(f.store.consumeLogout({ ...binding, ...logout })).toBe(true);
    expect(f.store.consumeLogout({ ...binding, ...logout })).toBe(false);
    expect(f.store.counts()).toMatchObject({
      owners: 1,
      operations: 1,
      credentialRecords: 0,
      logoutStates: 0,
    });
    held.resolve();
    await refused;
    expect(Object.values(f.store.counts()).every((count) => count === 0)).toBe(true);
  });
  it('equal external subject strings in different issuers do not share a subject capacity key', async () => {
    const f = fixture();
    for (let i = 0; i < 8; ++i) await f.login();
    await expect(f.login()).rejects.toMatchObject({ reason: 'subject_capacity' });
    f.setIdentity({ issuer: 'https://second-identity.example.test/realms/learnstack' });
    await f.login();
    expect(f.store.counts()).toMatchObject({ owners: 9, subjects: 9 });
  });
  it('one browser proof may name independent host groups without rotating either host binding', async () => {
    const f = fixture();
    const first = await f.login();
    const second = await f.login({ host: secondHost, browser: first.binding.browser });
    expect(second.binding.browser).toBe(first.binding.browser);
    expect(f.store.peek(first.binding)).not.toBeNull();
    expect(f.store.peek(second.binding)).not.toBeNull();
    expect(f.store.counts()).toMatchObject({ owners: 2, groups: 2 });
  });
  it('observes only scalar owner-held custody and indexes through publication, logout and release', async () => {
    const f = fixture();
    const started = await f.start();
    expect(f.store.counts()).toMatchObject({
      credentialRecords: 0,
      transactionRecords: 1,
      proofRecords: 1,
      csrfRecords: 1,
      loginStates: 1,
      logoutStates: 0,
    });
    const finished = await f.complete(started);
    expect(f.store.counts()).toMatchObject({
      credentialRecords: 1,
      transactionRecords: 0,
      proofRecords: 0,
      csrfRecords: 1,
      loginStates: 0,
      logoutStates: 0,
    });
    const binding = { host, browser: finished.browser, handle: finished.handle };
    const logout = f.store.signout(binding, finished.session.csrfToken);
    expect(f.store.counts()).toMatchObject({
      credentialRecords: 0,
      transactionRecords: 0,
      proofRecords: 1,
      csrfRecords: 0,
      loginStates: 0,
      logoutStates: 1,
    });
    expect(f.store.consumeLogout({ ...binding, ...logout })).toBe(true);
    expect(Object.values(f.store.counts()).every((count) => count === 0)).toBe(true);
  });
  it('clears owner-held credentials immediately while cancelled actual work keeps its owner until settlement', async () => {
    const f = fixture();
    const { binding } = await f.login();
    const held = f.blockRefresh();
    const pending = f.store.admit(binding, source, 'extend');
    const refused = expect(pending).rejects.toMatchObject({ reason: 'stopped' });
    await vi.waitFor(() => expect(f.provider.refresh).toHaveBeenCalledTimes(1));
    const stopped = f.store.stop();
    expect(f.store.counts()).toMatchObject({ owners: 1, credentialRecords: 0, operations: 1 });
    held.resolve();
    await refused;
    await stopped;
    expect(Object.values(f.store.counts()).every((count) => count === 0)).toBe(true);
  });
  it('projects an identical integral epoch deadline despite independent UTC sampling jitter', async () => {
    let samples = 0;
    const f = fixture({ initialTime: 1234.125, utcJitter: () => (++samples % 2 ? 0.3 : -0.4) });
    const { binding } = await f.login();
    const first = f.store.peek(binding)!.expiresAt;
    expect(Number.isInteger(first)).toBe(true);
    for (let i = 0; i < 10; ++i) {
      f.advance(0.125);
      expect(f.store.peek(binding)!.expiresAt).toBe(first);
    }
  });
});

describe('native entry, selection and private operation ownership', () => {
  const scope = { tenantId: identity.tenantId!, organizationId: null };
  const entryBinding = (entry: Awaited<ReturnType<ReturnType<typeof fixture>['enter']>>) => ({
    host,
    browser: entry.browser,
    handle: entry.handle,
  });

  it('reserves an independent subject credit before any API work and publishes only an entry projection', async () => {
    const f = fixture();
    f.setIdentity({ actorId: null, tenantId: null, organizationId: null });
    const started = await f.start();
    const result = await f.enter(started);
    expect(result.kind).toBe('entry');
    expect(result.entry).toMatchObject({
      state: 'entry',
      identityState: 'unavailable',
      contexts: null,
    });
    expect(f.store.peek(entryBinding(result))).toBeNull();
    expect(f.native.self).not.toHaveBeenCalled();
    expect(f.native.admit).not.toHaveBeenCalled();
    expect(f.account).not.toHaveBeenCalled();
    expect(f.store.counts()).toMatchObject({
      owners: 1,
      pending: 1,
      subjects: 1,
      credentialRecords: 1,
    });
    expect(JSON.stringify(result)).not.toContain(privateCanary);
    expect(JSON.stringify(result)).not.toContain(identity.subject);
    const page = await f.store.entry(entryBinding(result), source);
    expect(page.identityState).toBe('active');
    expect(page.contexts?.items).toHaveLength(1);
    const selected = await f.store.select(entryBinding(result), source, page.csrfToken, scope);
    expect(selected.kind).toBe('session');
    expect(f.native.admit).toHaveBeenCalledTimes(1);
    expect(f.account).toHaveBeenLastCalledWith(
      expect.any(String),
      { host, source, ...scope },
      expect.any(AbortSignal),
    );
  });

  it('refuses the ninth verified entry before self, creation or admission and does not evict any owner', async () => {
    const f = fixture();
    for (let i = 0; i < 8; ++i) await f.enter(await f.start());
    await expect(f.enter(await f.start())).rejects.toMatchObject({ reason: 'subject_capacity' });
    expect(f.store.counts()).toMatchObject({ owners: 8, pending: 8, subjects: 8 });
    expect(f.native.self).not.toHaveBeenCalled();
    expect(f.native.bootstrap).not.toHaveBeenCalled();
    expect(f.native.admit).not.toHaveBeenCalled();
    expect(f.account).not.toHaveBeenCalled();
  });

  it('keeps the original five-minute entry and source credits without extending them on repeated discovery', async () => {
    const f = fixture();
    const started = await f.start();
    f.advance(120_000);
    const result = await f.enter(started);
    const binding = entryBinding(result);
    const first = await f.store.entry(binding, source);
    f.advance(179_999);
    expect((await f.store.entry(binding, source)).expiresAt).toBe(first.expiresAt);
    f.advance(1);
    await expect(f.store.entry(binding, source)).rejects.toMatchObject({ reason: 'expired' });
    f.store.sweep();
    expect(f.store.counts()).toMatchObject({ owners: 0, pending: 0, subjects: 0 });
  });

  it.each(['Identical name', 'é', 'e\u0301', null])(
    'preserves a live destination for another exact subject regardless of label %s',
    async (label) => {
      const f = fixture();
      const active = await f.login();
      f.setIdentity({ subject: 'different-subject', displayLabel: label });
      const replacement = await f.enter(
        await f.start({ browser: active.binding.browser, handle: active.binding.handle }),
      );
      await expect(
        f.store.select(entryBinding(replacement), source, replacement.entry.csrfToken, scope),
      ).rejects.toMatchObject({ reason: label === null ? 'reauthenticate' : 'account_change' });
      expect(f.store.peek(active.binding)).not.toBeNull();
      expect(f.native.admit).toHaveBeenCalledTimes(1);
      const logout = f.store.discardEntry(entryBinding(replacement), replacement.entry.csrfToken);
      expect(f.store.peek(active.binding)).not.toBeNull();
      expect(f.store.counts().owners).toBe(2);
      expect(f.store.consumeLogout({ ...entryBinding(replacement), ...logout })).toBe(true);
      expect(f.store.peek(active.binding)).not.toBeNull();
      expect(f.store.counts().owners).toBe(1);
    },
  );

  it('retains an unknown creation outcome and resolves a deliberate retry without automatically repeating POST', async () => {
    const f = fixture();
    const entry = await f.enter(await f.start());
    const binding = entryBinding(entry);
    f.native.self.mockResolvedValueOnce({ state: 'missing' } as never);
    f.native.bootstrap.mockRejectedValueOnce(new BffProviderError('transport'));
    await expect(f.store.create(binding, source, entry.entry.csrfToken)).rejects.toMatchObject({
      reason: 'transport',
    });
    expect(f.native.bootstrap).toHaveBeenCalledTimes(1);
    expect(f.store.counts()).toMatchObject({ owners: 1, pending: 1, credentialRecords: 1 });
    const retry = await f.store.create(binding, source, entry.entry.csrfToken);
    expect(retry.identityState).toBe('active');
    expect(f.native.bootstrap).toHaveBeenCalledTimes(1);
  });

  it('uses Off self plus unchanged scoped AccountSite after refresh, never the audited admission endpoint', async () => {
    const f = fixture();
    f.setIdentity({ actorId: null, tenantId: null });
    const active = await f.login();
    f.native.self.mockClear();
    f.native.admit.mockClear();
    await f.store.admit(active.binding, source, 'extend');
    expect(f.native.self).toHaveBeenCalledTimes(1);
    expect(f.native.self).toHaveBeenLastCalledWith(
      expect.any(String),
      { host, source },
      expect.any(AbortSignal),
    );
    expect(f.account).toHaveBeenLastCalledWith(
      expect.any(String),
      { host, source, ...scope },
      expect.any(AbortSignal),
    );
    expect(f.native.admit).not.toHaveBeenCalled();
  });

  it('waits for actual old operation cleanup before publishing a fresh switch handle and withholds late old bytes', async () => {
    const f = fixture();
    const active = await f.login();
    const held = deferred<void>();
    releases.push(() => held.resolve());
    f.administration.mockImplementationOnce(async () => {
      await held.promise;
      return {
        kind: 'success',
        status: 200,
        data: { version: '1' },
        etag: '"1"',
        location: null,
        replayed: false,
      };
    });
    const operation = f.store.operate(active.binding, source, { kind: 'getOwnProfile' });
    const oldRefused = expect(operation).rejects.toMatchObject({ reason: 'cancelled' });
    await vi.waitFor(() => expect(f.administration).toHaveBeenCalledTimes(1));
    let published = false;
    const switching = f.store
      .switchScope(active.binding, source, active.finished.session.csrfToken, scope)
      .then((value) => {
        published = true;
        return value;
      });
    await vi.waitFor(() => expect(f.account).toHaveBeenCalledTimes(2));
    expect(f.store.peek(active.binding)).toBeNull();
    expect(published).toBe(false);
    expect(f.store.counts()).toMatchObject({ owners: 2, subjects: 2 });
    held.resolve();
    await oldRefused;
    const switched = await switching;
    expect(switched.kind).toBe('session');
    if (switched.kind !== 'session') throw new Error('Expected local switch');
    expect(switched.handle).not.toBe(active.binding.handle);
    expect(switched.session.csrfToken).not.toBe(active.finished.session.csrfToken);
    expect(f.store.peek({ ...active.binding, handle: switched.handle })).not.toBeNull();
    expect(f.store.counts()).toMatchObject({ owners: 1, subjects: 1 });
  });

  it('failed target admission preserves the source session and its independent operation capability', async () => {
    const f = fixture();
    const active = await f.login();
    f.native.admit.mockRejectedValueOnce(new BffProviderError('provider_refused'));
    await expect(
      f.store.switchScope(active.binding, source, active.finished.session.csrfToken, scope),
    ).rejects.toMatchObject({ reason: 'provider_refused' });
    expect(f.store.peek(active.binding)).toEqual(active.finished.session);
    expect(f.store.counts()).toMatchObject({ owners: 1, pending: 0, subjects: 1 });
    await expect(
      f.store.operate(active.binding, source, { kind: 'getOwnProfile' }),
    ).resolves.toMatchObject({ kind: 'success' });
  });

  it('reserves an eighth-plus-one switch owner without a same-person capacity bypass', async () => {
    const f = fixture();
    const active = await f.login();
    for (let i = 0; i < 7; ++i) await f.login();
    f.native.admit.mockClear();
    await expect(
      f.store.switchScope(active.binding, source, active.finished.session.csrfToken, scope),
    ).rejects.toMatchObject({ reason: 'subject_capacity' });
    expect(f.native.admit).not.toHaveBeenCalled();
    expect(f.store.peek(active.binding)).not.toBeNull();
    expect(f.store.counts()).toMatchObject({ owners: 8, subjects: 8 });
  });

  it('captures the closed operation before a refresh await so caller mutation cannot turn a read into a write', async () => {
    const f = fixture();
    const active = await f.login();
    f.advance(840_000);
    const held = f.blockRefresh();
    const input = { kind: 'getOwnProfile' as const };
    const pending = f.store.operate(active.binding, source, input);
    await vi.waitFor(() => expect(f.provider.refresh).toHaveBeenCalledTimes(1));
    Object.assign(input, {
      kind: 'setOwnProfile',
      profile: { displayName: 'Changed', preferredLocale: null },
      version: '1',
      idempotencyKey: 'misleading-key',
    });
    held.resolve();
    await pending;
    expect(f.administration).toHaveBeenLastCalledWith(
      expect.any(String),
      { host, source, ...scope },
      { kind: 'getOwnProfile' },
      expect.any(AbortSignal),
    );
  });

  it('requires current session CSRF before any mutation work, including refresh', async () => {
    const f = fixture();
    const active = await f.login();
    f.advance(840_000);
    await expect(
      f.store.operate(
        active.binding,
        source,
        {
          kind: 'setOwnProfile',
          profile: { displayName: null, preferredLocale: null },
          version: '1',
          idempotencyKey: 'fixed-key',
        },
        secret(),
      ),
    ).rejects.toMatchObject({ reason: 'csrf' });
    expect(f.provider.refresh).not.toHaveBeenCalled();
    expect(f.administration).not.toHaveBeenCalled();
  });

  it.each([401, 403, 404, 429, 503])(
    'keeps an otherwise-live session after an administration %s outcome',
    async (status) => {
      const f = fixture();
      const active = await f.login();
      f.administration.mockResolvedValueOnce({
        kind: 'api-error',
        status,
        error: { code: 'not_found' },
        problem: { status, code: 'not_found' },
      } as never);
      await expect(
        f.store.operate(active.binding, source, { kind: 'getOwnProfile' }),
      ).resolves.toMatchObject({ kind: 'api-error', status });
      expect(f.store.peek(active.binding)).not.toBeNull();
    },
  );

  it('retains actual operation custody through logout until the ignored cancellation settles', async () => {
    const f = fixture();
    const active = await f.login();
    const held = deferred<void>();
    releases.push(() => held.resolve());
    let signal: AbortSignal | undefined;
    f.administration.mockImplementationOnce(async (_token, _binding, _operation, current) => {
      signal = current;
      await held.promise;
      return {
        kind: 'success',
        status: 200,
        data: { version: '1' },
        etag: '"1"',
        location: null,
        replayed: false,
      };
    });
    const pending = f.store.operate(active.binding, source, { kind: 'getOwnProfile' });
    const refused = expect(pending).rejects.toMatchObject({ reason: 'cancelled' });
    await vi.waitFor(() => expect(signal).toBeDefined());
    const logout = f.store.signout(active.binding, active.finished.session.csrfToken);
    expect(signal!.aborted).toBe(true);
    f.store.consumeLogout({ ...active.binding, ...logout });
    expect(f.store.counts()).toMatchObject({
      owners: 1,
      subjects: 1,
      operations: 1,
      credentialRecords: 0,
    });
    held.resolve();
    await refused;
    expect(Object.values(f.store.counts()).every((count) => count === 0)).toBe(true);
  });
});

describe('native switch refresh and lifetime boundaries', () => {
  const scope = { tenantId: identity.tenantId!, organizationId: null };
  it('refreshes one near-expiry revision before switch and keeps the original eight-hour absolute ceiling', async () => {
    const f = fixture();
    f.setTokens({ refreshExpiresAt: 24 * 60 * 60_000 });
    const active = await f.login();
    for (let i = 0; i < 23; ++i) {
      f.advance(20 * 60_000);
      await f.store.admit(active.binding, source, 'extend');
    }
    f.advance(14 * 60_000);
    const priorRefreshes = vi.mocked(f.provider.refresh).mock.calls.length;
    const switched = await f.store.switchScope(
      active.binding,
      source,
      f.store.peek(active.binding)!.csrfToken,
      scope,
    );
    expect(f.provider.refresh).toHaveBeenCalledTimes(priorRefreshes + 1);
    expect(f.native.admit).toHaveBeenCalledTimes(2);
    if (switched.kind !== 'session') throw new Error('Expected local scope');
    expect(switched.session.expiresAt).toBe(1_800_000_000_000 + 8 * 60 * 60_000);
    expect(switched.session.limitedByAbsolute).toBe(true);
    f.advance(6 * 60_000);
    expect(f.store.peek({ ...active.binding, handle: switched.handle })).toBeNull();
  });
  it('a failed post-refresh self check closes the source and shadow rather than retaining consumed grant uncertainty', async () => {
    const f = fixture();
    const active = await f.login();
    f.advance(840_000);
    f.native.self.mockRejectedValueOnce(new BffProviderError('provider_refused'));
    await expect(
      f.store.switchScope(active.binding, source, active.finished.session.csrfToken, scope),
    ).rejects.toMatchObject({ reason: 'provider_refused' });
    expect(f.provider.refresh).toHaveBeenCalledTimes(1);
    expect(f.native.admit).toHaveBeenCalledTimes(1);
    expect(f.store.peek(active.binding)).toBeNull();
    expect(f.store.counts()).toMatchObject({ owners: 0, subjects: 0, pending: 0 });
  });
});

describe('scoped switch choices use current tenant discovery', () => {
  const branch = '10000000-0000-4000-8000-000000000002';
  const scope = { tenantId: identity.tenantId!, organizationId: branch };
  const page = (patch = {}) => ({
    kind: 'success' as const,
    status: 200 as const,
    etag: null,
    location: null,
    replayed: false,
    data: {
      items: [
        {
          ...scope,
          tenantName: 'Institution',
          organizationName: 'Branch',
          entryOrigin: 'https://' + host,
          canUseCurrentHost: true,
          ...patch,
        },
      ],
      pageInfo: { nextCursor: null, previousCursor: null, hasNext: false, hasPrevious: false },
    },
  });
  it('uses the old scope, held token and scoped cursor for a derived branch omitted by global discovery', async () => {
    const f = fixture();
    f.setIdentity({ tenantId: null });
    const active = await f.login();
    const heldToken = f.account.mock.calls[0]![0];
    f.native.contexts.mockClear();
    f.setAccount({ organizationId: branch });
    f.administration.mockResolvedValueOnce(page());
    const cursor = Buffer.from('scoped-actor-tenant-coverage-position').toString('base64url');
    const candidate = { ...scope };
    const pending = f.store.switchScope(
      active.binding,
      source,
      active.finished.session.csrfToken,
      candidate,
      cursor,
    );
    candidate.organizationId = '10000000-0000-4000-8000-000000000003';
    const result = await pending;
    expect(result.kind).toBe('session');
    expect(f.administration).toHaveBeenCalledWith(
      heldToken,
      { host, source, tenantId: identity.tenantId!, organizationId: null },
      { kind: 'getOwnContexts', cursor, limit: 20 },
      expect.any(AbortSignal),
    );
    expect(f.native.contexts).not.toHaveBeenCalled();
    expect(f.native.admit).toHaveBeenLastCalledWith(
      heldToken,
      { host, source, ...scope },
      expect.any(AbortSignal),
    );
    expect(f.account).toHaveBeenLastCalledWith(
      heldToken,
      { host, source, ...scope },
      expect.any(AbortSignal),
    );
    expect(f.store.peek(active.binding)).toBeNull();
  });
  it('does not fall back to global discovery after a scoped read refusal', async () => {
    const f = fixture();
    const active = await f.login();
    f.native.contexts.mockClear();
    f.native.admit.mockClear();
    f.administration.mockResolvedValueOnce({ kind: 'transport-error' });
    await expect(
      f.store.switchScope(active.binding, source, active.finished.session.csrfToken, scope),
    ).rejects.toMatchObject({ reason: 'transport' });
    expect(f.native.contexts).not.toHaveBeenCalled();
    expect(f.native.admit).not.toHaveBeenCalled();
    expect(f.store.peek(active.binding)).toEqual(active.finished.session);
    expect(f.store.counts()).toMatchObject({ owners: 1, pending: 0, subjects: 1 });
  });
  it('refuses a foreign-tenant scoped page before target admission and preserves the source', async () => {
    const f = fixture();
    const active = await f.login();
    f.native.admit.mockClear();
    f.administration.mockResolvedValueOnce(
      page({ tenantId: '10000000-0000-4000-8000-000000000099' }),
    );
    await expect(
      f.store.switchScope(active.binding, source, active.finished.session.csrfToken, scope),
    ).rejects.toMatchObject({ reason: 'response' });
    expect(f.native.admit).not.toHaveBeenCalled();
    expect(f.store.peek(active.binding)).toEqual(active.finished.session);
  });
  it('returns only the verified cross-host destination without transferring or replacing the source owner', async () => {
    const f = fixture();
    const active = await f.login();
    f.native.admit.mockClear();
    f.administration.mockResolvedValueOnce(
      page({ entryOrigin: 'https://' + secondHost, canUseCurrentHost: false }),
    );
    await expect(
      f.store.switchScope(active.binding, source, active.finished.session.csrfToken, scope),
    ).resolves.toEqual({
      kind: 'destination',
      origin: 'https://' + secondHost,
      returnPath: '/studio',
      contextHint: branch,
    });
    expect(f.native.admit).not.toHaveBeenCalled();
    expect(f.provider.redeem).toHaveBeenCalledTimes(1);
    expect(f.store.peek(active.binding)).toEqual(active.finished.session);
    expect(f.store.counts()).toMatchObject({ owners: 1, pending: 0, subjects: 1 });
  });
});

describe('entry provider logout is a separate retained owner', () => {
  const binding = (entry: Awaited<ReturnType<ReturnType<typeof fixture>['enter']>>) => ({
    host,
    browser: entry.browser,
    handle: entry.handle,
  });
  it('uses only the entry ID token and independent proofs while retaining a live primary across the return', async () => {
    const f = fixture();
    const active = await f.login();
    f.setTokens({ idToken: 'entry-only-id-token-canary' });
    const entry = await f.enter(
      await f.start({ browser: active.binding.browser, handle: active.binding.handle }),
    );
    const logout = f.store.discardEntry(binding(entry), entry.entry.csrfToken);
    expect(f.provider.logoutUrl).toHaveBeenLastCalledWith(
      'entry-only-id-token-canary',
      'https://' + host + '/',
      logout.state,
    );
    expect(logout.state).not.toBe(logout.proof);
    expect(logout.proof).not.toBe(entry.entry.csrfToken);
    expect(f.store.peek(active.binding)).toEqual(active.finished.session);
    expect(f.store.counts()).toMatchObject({
      owners: 2,
      subjects: 2,
      credentialRecords: 1,
      logoutStates: 1,
    });
    expect(f.store.consumeLogout({ ...binding(entry), ...logout, proof: secret() })).toBe(false);
    expect(f.store.consumeLogout({ ...binding(entry), ...logout, host: secondHost })).toBe(false);
    expect(f.store.consumeLogout({ ...binding(entry), ...logout })).toBe(true);
    expect(f.store.consumeLogout({ ...binding(entry), ...logout })).toBe(false);
    expect(f.store.peek(active.binding)).toEqual(active.finished.session);
    expect(f.store.counts()).toMatchObject({ owners: 1, subjects: 1, credentialRecords: 1 });
  });
  it('cancels an in-flight selection without publishing or retiring an otherwise-live primary, retaining actual work after return', async () => {
    const f = fixture();
    const active = await f.login();
    const entry = await f.enter(
      await f.start({ browser: active.binding.browser, handle: active.binding.handle }),
    );
    const held = deferred<void>();
    releases.push(() => held.resolve());
    const original = f.native.admit.getMockImplementation()!;
    f.native.admit.mockImplementationOnce(async (...args) => {
      await held.promise;
      return original(...args);
    });
    const selection = f.store.select(binding(entry), source, entry.entry.csrfToken, {
      tenantId: identity.tenantId!,
      organizationId: null,
    });
    const refused = expect(selection).rejects.toMatchObject({ reason: 'cancelled' });
    await vi.waitFor(() => expect(f.native.admit).toHaveBeenCalledTimes(2));
    const logout = f.store.discardEntry(binding(entry), entry.entry.csrfToken);
    expect(f.store.peek(active.binding)).not.toBeNull();
    expect(f.store.consumeLogout({ ...binding(entry), ...logout })).toBe(true);
    expect(f.store.counts()).toMatchObject({
      owners: 2,
      subjects: 2,
      operations: 1,
      credentialRecords: 1,
    });
    held.resolve();
    await refused;
    expect(f.store.peek(active.binding)).toEqual(active.finished.session);
    expect(f.store.counts()).toMatchObject({ owners: 1, subjects: 1, operations: 0 });
  });
  it('a provider logout URL failure clears entry credentials but preserves its bounded return and the live primary', async () => {
    const f = fixture();
    const active = await f.login();
    const entry = await f.enter(
      await f.start({ browser: active.binding.browser, handle: active.binding.handle }),
    );
    vi.mocked(f.provider.logoutUrl).mockImplementationOnce(() => {
      throw new BffProviderError('transport');
    });
    const logout = f.store.discardEntry(binding(entry), entry.entry.csrfToken);
    expect(logout.redirectUrl).toBeNull();
    expect(f.store.counts()).toMatchObject({ owners: 2, credentialRecords: 1, logoutStates: 1 });
    expect(f.store.peek(active.binding)).toEqual(active.finished.session);
    expect(f.store.consumeLogout({ ...binding(entry), ...logout })).toBe(true);
    expect(f.store.counts().owners).toBe(1);
  });
});
