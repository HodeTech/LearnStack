// @vitest-environment node
import { randomBytes } from 'node:crypto';

import { afterEach, describe, expect, it, vi } from 'vitest';

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

function fixture() {
  let now = 0;
  let currentIdentity = identity;
  let sequence = 0;
  let tokenPatch: Partial<ProviderCredentials> = {};
  let accountPatch: Partial<AccountWitness> = {};
  let refreshing: ReturnType<typeof deferred<void>> | undefined;
  let redeeming: ReturnType<typeof deferred<void>> | undefined;
  let accounting: ReturnType<typeof deferred<void>> | undefined;
  const tokens = new Map<string, ProviderIdentity>();
  const diagnostics: SessionFailure[] = [];
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
      actorId: validated.actorId,
      tenantId: validated.tenantId,
      organizationId: validated.organizationId,
      displayName: 'Institution',
      ...accountPatch,
    };
  });
  const store = new InMemoryBffSessionStore({
    provider,
    account,
    clock: { monotonic: () => now, utc: () => 1_800_000_000_000 + now },
    diagnostic: (reason) => diagnostics.push(reason),
  });
  stores.push(store);
  async function start(extra: Partial<Parameters<typeof store.start>[0]> = {}) {
    return store.start({ host, source, returnPath: '/studio', ...extra });
  }
  async function complete(
    login: Awaited<ReturnType<typeof start>>,
    atHost = host,
    callbackSource = source,
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
    });
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
    diagnostics,
    start,
    complete,
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
    expect(JSON.stringify(f.store)).toBe('{}');
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
          clearTransaction: login.state,
        });
        await flush();
        expect(f.store.counts()).toMatchObject({ owners: 0, groups: 0 });
      },
    );
  }

  it('permits API host-resolved organization only when the protected ID claim is absent', async () => {
    const f = fixture();
    const organization = '30000000-0000-4000-8000-000000000003';
    f.setAccount({ organizationId: organization });
    const first = await f.login();
    expect(f.store.peek(first.binding)).not.toBeNull();
    f.setIdentity({ organizationId: '40000000-0000-4000-8000-000000000004' });
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
      reason: 'revoked',
      clearTransaction: second.state,
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
    f.setAccount({ tenantId: '20000000-0000-4000-8000-000000000002' });
    await expect(
      f.login({ browser: old.binding.browser, handle: old.binding.handle }),
    ).rejects.toMatchObject({ reason: 'identity' });
    expect(f.store.peek(old.binding)).not.toBeNull();
  });

  it('replaces only its verified same-browser primary, holding subject credit until old I/O settles', async () => {
    const f = fixture();
    const old = await f.login();
    for (let i = 0; i < 7; ++i) await f.login();
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
    await flush();
    expect(published).toBe(false);
    expect(f.store.peek(old.binding)).toBeNull();
    expect(f.store.counts()).toMatchObject({ owners: 9, pending: 1, subjects: 8 });
    await expect(f.login()).rejects.toMatchObject({ reason: 'subject_capacity' });
    gate.resolve();
    await ended;
    const next = await replacement;
    expect(f.store.peek({ host, browser: next.browser, handle: next.handle })).not.toBeNull();
    expect(f.store.peek(old.binding)).toBeNull();
    expect(f.store.counts()).toMatchObject({ owners: 8, subjects: 8 });
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
    expect(f.diagnostics).toEqual(['response']);
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
    expect(f.store.counts()).toEqual({
      owners: 0,
      pending: 0,
      groups: 0,
      subjects: 0,
      operations: 0,
    });
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
      await f.store.stop();
      expect(vi.getTimerCount()).toBe(0);
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
    expect(f.account.mock.calls[0]![1]).toEqual({ host, source: callbackSource });
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
