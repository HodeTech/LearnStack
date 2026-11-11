// @vitest-environment node
import { createHash, randomBytes } from 'node:crypto';

import { afterEach, describe, expect, it, vi } from 'vitest';

import type { VerifiedLogoutToken } from './backchannel-contract';
import { BackchannelStateError } from './backchannel-state';
import type { NativeAdministrationCaller } from './native-operation-contract';
import type { ProviderCredentials, ProviderIdentity } from './provider';
import { BffSessionError } from './session-contract';
import type { SessionBinding, SessionDependencies } from './session-contract';
import { InMemoryBffSessionStore } from './session-store';

const host = 'institution.example.test:3000';
const source = '203.0.113.1';
const utcBase = 1_800_000_000_000;
const secret = () => randomBytes(32).toString('base64url');
const identity: ProviderIdentity = {
  issuer: 'https://identity.example.test/realms/learnstack',
  sid: 'provider-session',
  subject: 'opaque-subject',
  actorId: '019a154b-0000-7000-8000-000000000011',
  tenantId: '10000000-0000-4000-8000-000000000001',
  organizationId: null,
  displayLabel: 'Confirmed account',
};
function deferred() {
  let resolve!: () => void;
  const promise = new Promise<void>((done) => {
    resolve = done;
  });
  return { promise, resolve };
}
const stores: InMemoryBffSessionStore[] = [];
const gates: ReturnType<typeof deferred>[] = [];
afterEach(async () => {
  const stopped = stores.splice(0).map((store) => store.stop());
  for (const gate of gates.splice(0)) gate.resolve();
  await Promise.all(stopped);
});
function fixture() {
  let now = 0;
  let utcOffset = 0;
  let currentIdentity = identity;
  let serial = 0;
  let redeemGate: ReturnType<typeof deferred> | undefined;
  let refreshGate: ReturnType<typeof deferred> | undefined;
  let admissionGate: ReturnType<typeof deferred> | undefined;
  let invitationGate: ReturnType<typeof deferred> | undefined;
  let operationGate: ReturnType<typeof deferred> | undefined;
  const tokens = new Map<string, ProviderIdentity>();
  const credentials = (value: ProviderIdentity): ProviderCredentials => {
    const accessToken = 'private-access-' + ++serial;
    tokens.set(accessToken, value);
    return {
      identity: value,
      accessToken,
      idToken: 'private-id',
      refreshToken: 'private-refresh',
      accessExpiresAt: now + 900_000,
      refreshExpiresAt: now + 1_800_000,
    };
  };
  const association = (token: string) => ({
    authorityId: identity.tenantId!,
    subjectKey: 'a'.repeat(64),
    actorId: tokens.get(token)!.actorId!,
  });
  const dependencies: SessionDependencies = {
    clock: { monotonic: () => now, utc: () => utcBase + now + utcOffset },
    provider: {
      begin: vi.fn(async (callbackUri) => ({
        transaction: {
          callbackUri,
          state: secret(),
          nonce: secret(),
          verifier: secret(),
        },
        redirectUrl: 'https://identity.example.test/authorize',
      })),
      redeem: vi.fn(async () => {
        const captured = currentIdentity;
        await redeemGate?.promise;
        return credentials(captured);
      }),
      refresh: vi.fn(async () => {
        const captured = currentIdentity;
        await refreshGate?.promise;
        return credentials(captured);
      }),
      logoutUrl: () => 'https://identity.example.test/logout',
    },
    native: {
      self: async (token) => ({ state: 'active', association: association(token) }),
      bootstrap: async (token) => association(token),
      contexts: async (_token, binding) => ({
        items: [
          {
            tenantId: identity.tenantId!,
            organizationId: null,
            tenantName: 'Institution',
            organizationName: null,
            canUseCurrentHost: true,
            entryOrigin: 'https://' + binding.host,
          },
        ],
        endCursor: null,
        hasNextPage: false,
      }),
      admit: vi.fn(async (token, binding) => {
        await admissionGate?.promise;
        return {
          ...association(token),
          tenantId: binding.tenantId,
          organizationId: binding.organizationId,
        };
      }),
      verifyAssociation: () => undefined,
    },
    account: async (token, binding) => ({
      actorId: association(token).actorId,
      tenantId: binding.tenantId,
      organizationId: binding.organizationId,
      displayName: 'Institution',
    }),
    administration: vi.fn<NativeAdministrationCaller>(async (_token, binding, operation) => {
      await operationGate?.promise;
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
                organizationId: null,
                tenantName: 'Institution',
                organizationName: null,
                canUseCurrentHost: true,
                entryOrigin: 'https://' + binding.host,
              },
            ],
            pageInfo: {
              nextCursor: null,
              previousCursor: null,
              hasNext: false,
              hasPrevious: false,
            },
          },
        };
      return {
        kind: 'success',
        status: 200,
        data: { version: '1' },
        etag: null,
        location: null,
        replayed: false,
      };
    }),
    invitations: {
      inspect: vi.fn(async () => {
        await invitationGate?.promise;
        return {
          version: '1',
          invitationId: '019a154b-0000-7000-8000-000000000022',
          tenantLabel: 'Institution',
          organizationLabel: null,
          roleId: '019a154b-0000-7000-8000-000000000033',
          roleKey: 'Learner' as const,
          roleScope: 'Tenant' as const,
          verifiedEmail: 'fixture@example.test',
          expiresAt: '2027-01-01T00:00:00Z',
        };
      }),
      accept: async () => {
        throw new Error('unused fixture branch');
      },
      result: async () => {
        throw new Error('unused fixture branch');
      },
    },
  };
  const store = new InMemoryBffSessionStore(dependencies);
  stores.push(store);
  const hold = (set: (gate: ReturnType<typeof deferred>) => void) => {
    const gate = deferred();
    gates.push(gate);
    set(gate);
    return gate;
  };
  async function start(atHost = host) {
    return store.start({ host: atHost, source, returnPath: '/studio' });
  }
  async function enter(login: Awaited<ReturnType<typeof start>>, atHost = host) {
    return store.complete({
      host: atHost,
      source,
      browser: login.browser,
      proof: login.proof,
      state: login.state,
      responseUrl:
        'https://' + atHost + '/api/auth/callback/keycloak?state=' + login.state + '&code=code',
    });
  }
  async function login(atHost = host) {
    const entry = await enter(await start(atHost), atHost);
    const selected = await store.select(
      { host: atHost, browser: entry.browser, handle: entry.handle },
      source,
      entry.entry.csrfToken,
      { tenantId: identity.tenantId!, organizationId: null },
    );
    if (selected.kind !== 'session') throw new Error('fixture selection');
    const binding = { host: atHost, browser: selected.browser, handle: selected.handle };
    return { binding, selected };
  }
  function token(patch: Partial<VerifiedLogoutToken> = {}): VerifiedLogoutToken {
    const jti = 'notification-' + ++serial;
    return {
      issuer: identity.issuer,
      sid: identity.sid,
      subject: identity.subject,
      jti,
      fingerprint: createHash('sha256').update(jti).digest('hex'),
      issuedAt: utcBase + now + utcOffset,
      expiresAt: utcBase + now + utcOffset + 120_000,
      ...patch,
    };
  }
  return {
    store,
    dependencies,
    start,
    enter,
    login,
    token,
    advance: (ms: number) => {
      now += ms;
    },
    shiftUtc: (ms: number) => {
      utcOffset += ms;
    },
    setIdentity: (patch: Partial<ProviderIdentity>) => {
      currentIdentity = { ...identity, ...patch };
    },
    holdRedeem: () =>
      hold((gate) => {
        redeemGate = gate;
      }),
    holdRefresh: () =>
      hold((gate) => {
        refreshGate = gate;
      }),
    holdAdmission: () =>
      hold((gate) => {
        admissionGate = gate;
      }),
    holdInvitation: () =>
      hold((gate) => {
        invitationGate = gate;
      }),
    holdOperation: () =>
      hold((gate) => {
        operationGate = gate;
      }),
  };
}

async function waitFor(call: () => number) {
  await vi.waitFor(() => expect(call()).toBeGreaterThan(0));
}

describe('native session-targeted notification ownership', () => {
  it('closes the same SSO across hosts and preserves other issuer, sid and subject owners', async () => {
    const f = fixture();
    const first = await f.login();
    const second = await f.login('second.example.test:3000');
    const independent: SessionBinding[] = [];
    for (const patch of [
      { sid: 'other-session' },
      { subject: 'other-subject' },
      { issuer: 'https://other.example.test/realms/learnstack' },
    ]) {
      f.setIdentity(patch);
      independent.push((await f.login()).binding);
    }
    expect(f.store.applyProviderLogout(f.token())).toBe('accepted');
    expect(f.store.peek(first.binding)).toBeNull();
    expect(f.store.peek(second.binding)).toBeNull();
    for (const binding of independent) expect(f.store.peek(binding)).not.toBeNull();
    f.setIdentity({ sid: 'later-independent-sso' });
    expect(f.store.peek((await f.login()).binding)).not.toBeNull();
  });

  it('permits absent sub to target every owner of exactly the provider session', async () => {
    const f = fixture();
    const first = await f.login();
    f.setIdentity({ subject: 'other-subject' });
    const second = await f.login();
    f.store.applyProviderLogout(f.token({ subject: null }));
    expect(f.store.peek(first.binding)).toBeNull();
    expect(f.store.peek(second.binding)).toBeNull();
  });

  it('acknowledges exact duplicates once and refuses a changed fingerprint', async () => {
    const f = fixture();
    const active = await f.login();
    const token = f.token();
    expect(f.store.applyProviderLogout(token)).toBe('accepted');
    expect(f.store.applyProviderLogout({ ...token })).toBe('duplicate');
    expect(() => f.store.applyProviderLogout({ ...token, fingerprint: 'f'.repeat(64) })).toThrow(
      BackchannelStateError,
    );
    expect(f.store.peek(active.binding)).toBeNull();
    expect(f.store.backchannelCounts()).toMatchObject({ replay: 1, fences: 1 });
  });

  it('fences unknown callbacks before credential custody or entry publication', async () => {
    const f = fixture();
    const login = await f.start();
    const gate = f.holdRedeem();
    const completing = f.enter(login);
    const refused = expect(completing).rejects.toBeInstanceOf(BffSessionError);
    await waitFor(() => vi.mocked(f.dependencies.provider.redeem).mock.calls.length);
    f.store.applyProviderLogout(f.token());
    gate.resolve();
    await refused;
    expect(f.store.counts()).toMatchObject({ owners: 0, credentialRecords: 0, subjects: 0 });
  });

  it('retains a fence through unknown callbacks admitted during its horizon and ignored abort', async () => {
    const f = fixture();
    f.store.applyProviderLogout(f.token());
    f.advance(290_000);
    const started = await f.start();
    const gate = f.holdRedeem();
    const completing = f.enter(started);
    const refused = expect(completing).rejects.toBeInstanceOf(BffSessionError);
    await waitFor(() => vi.mocked(f.dependencies.provider.redeem).mock.calls.length);
    f.advance(20_000);
    f.store.sweep();
    expect(f.store.backchannelCounts()).toMatchObject({ replay: 0, fences: 1 });
    gate.resolve();
    await refused;
    f.store.sweep();
    expect(f.store.backchannelCounts()).toMatchObject({ replay: 0, fences: 0 });
  });

  it('preserves different-session callbacks held while a matching owner closes', async () => {
    const f = fixture();
    const active = await f.login();
    const started = await f.store.start({
      host,
      source,
      returnPath: '/studio',
      browser: active.binding.browser,
      handle: active.binding.handle,
    });
    f.setIdentity({ sid: 'independent-new-session' });
    const gate = f.holdRedeem();
    const completing = f.enter(started);
    await waitFor(() => vi.mocked(f.dependencies.provider.redeem).mock.calls.length - 1);
    f.store.applyProviderLogout(f.token());
    gate.resolve();
    const entry = await completing;
    const selected = await f.store.select(
      { host, browser: entry.browser, handle: entry.handle },
      source,
      entry.entry.csrfToken,
      { tenantId: identity.tenantId!, organizationId: null },
    );
    expect(selected.kind).toBe('session');
  });

  it('closes entry and selecting owners while held admission remains physically retained', async () => {
    const f = fixture();
    const entry = await f.enter(await f.start());
    const gate = f.holdAdmission();
    const selecting = f.store.select(
      { host, browser: entry.browser, handle: entry.handle },
      source,
      entry.entry.csrfToken,
      { tenantId: identity.tenantId!, organizationId: null },
    );
    const refused = expect(selecting).rejects.toBeInstanceOf(BffSessionError);
    await waitFor(() => vi.mocked(f.dependencies.native.admit).mock.calls.length);
    f.store.applyProviderLogout(f.token());
    expect(f.store.counts()).toMatchObject({ owners: 1, credentialRecords: 0 });
    f.advance(310_000);
    f.store.sweep();
    expect(f.store.backchannelCounts().fences).toBe(1);
    gate.resolve();
    await refused;
    f.store.sweep();
    expect(f.store.backchannelCounts().fences).toBe(0);
  });

  it('prevents held refresh publication and retains target evidence after credential erasure', async () => {
    const f = fixture();
    const active = await f.login();
    const gate = f.holdRefresh();
    const refreshing = f.store.admit(active.binding, source, 'extend');
    const refused = expect(refreshing).rejects.toBeInstanceOf(BffSessionError);
    await waitFor(() => vi.mocked(f.dependencies.provider.refresh).mock.calls.length);
    f.store.applyProviderLogout(f.token());
    expect(f.store.counts()).toMatchObject({ owners: 1, credentialRecords: 0, identityRecords: 0 });
    f.advance(310_000);
    f.store.sweep();
    expect(f.store.backchannelCounts().fences).toBe(1);
    gate.resolve();
    await refused;
    f.store.sweep();
    expect(f.store.counts().owners).toBe(0);
    expect(f.store.backchannelCounts().fences).toBe(0);
  });

  it('retains invitation custody associations and suppresses an ignored-abort inspection', async () => {
    const f = fixture();
    const active = await f.login();
    const opening = f.store.openInvitation({
      host,
      source,
      browser: active.binding.browser,
      sessionHandle: active.binding.handle,
    });
    const binding = { host, browser: opening.browser, handle: opening.handle };
    await f.store.invitation(binding, source, {
      kind: 'capture',
      csrf: opening.csrfToken,
      code: '019a154b-0000-7000-8000-000000000022.' + secret(),
    });
    const gate = f.holdInvitation();
    const inspecting = f.store.invitation(binding, source, {
      kind: 'inspect',
      csrf: opening.csrfToken,
    });
    const refused = expect(inspecting).rejects.toBeInstanceOf(BffSessionError);
    await waitFor(() => vi.mocked(f.dependencies.invitations!.inspect).mock.calls.length);
    f.store.applyProviderLogout(f.token());
    expect(f.store.peek(active.binding)).toBeNull();
    f.advance(310_000);
    f.store.sweep();
    expect(f.store.backchannelCounts().fences).toBe(1);
    gate.resolve();
    await refused;
    f.store.sweep();
    expect(f.store.backchannelCounts().fences).toBe(0);
  });

  it('retains logout-return targets after credentials have already been erased', async () => {
    const f = fixture();
    const active = await f.login();
    const logout = f.store.signout(active.binding, active.selected.session.csrfToken);
    expect(f.store.counts()).toMatchObject({ owners: 1, credentialRecords: 0, logoutStates: 1 });
    f.store.applyProviderLogout(f.token());
    expect(f.store.counts()).toMatchObject({ owners: 0, logoutStates: 0 });
    expect(
      f.store.consumeLogout({
        host,
        browser: active.binding.browser,
        state: logout.state,
        proof: logout.proof,
      }),
    ).toBe(false);
  });

  it('withholds held API result bytes after the owner gate closes', async () => {
    const f = fixture();
    const active = await f.login();
    const gate = f.holdOperation();
    const operating = f.store.operate(active.binding, source, { kind: 'getOwnProfile' });
    const refused = expect(operating).rejects.toBeInstanceOf(BffSessionError);
    await vi.waitFor(() => expect(f.store.counts().operations).toBeGreaterThan(0));
    f.store.applyProviderLogout(f.token());
    gate.resolve();
    await refused;
    expect(f.store.peek(active.binding)).toBeNull();
  });

  it('closes both owners of a held scope switch before any replacement can publish', async () => {
    const f = fixture();
    const active = await f.login();
    const gate = f.holdOperation();
    const switching = f.store.switchScope(
      active.binding,
      source,
      active.selected.session.csrfToken,
      { tenantId: identity.tenantId!, organizationId: null },
    );
    const refused = expect(switching).rejects.toBeInstanceOf(BffSessionError);
    await waitFor(() => vi.mocked(f.dependencies.administration).mock.calls.length);
    expect(f.store.counts().owners).toBe(2);
    f.store.applyProviderLogout(f.token());
    expect(f.store.peek(active.binding)).toBeNull();
    expect(f.store.counts().credentialRecords).toBe(0);
    gate.resolve();
    await refused;
    expect(f.store.counts().owners).toBe(0);
  });

  it('leaves a live owner usable when replay capacity refuses its notification', async () => {
    const f = fixture();
    const active = await f.login();
    for (let i = 0; i < 4096; i++)
      f.store.applyProviderLogout(f.token({ sid: 'unrelated-session' }));
    expect(() => f.store.applyProviderLogout(f.token())).toThrow(BackchannelStateError);
    expect(f.store.peek(active.binding)).not.toBeNull();
    expect(f.store.backchannelCounts()).toMatchObject({ replay: 4096, fences: 1, saturated: true });
  });

  it('retains a full fence census through physical retirement at the full owner census', async () => {
    const f = fixture();
    for (let i = 0; i < 1023; i++) {
      f.setIdentity({ sid: 'independent-session-' + i, subject: 'independent-subject-' + i });
      await f.login();
    }
    const started = await f.start();
    const gate = f.holdRedeem();
    const completing = f.enter(started);
    const refused = expect(completing).rejects.toBeInstanceOf(BffSessionError);
    await waitFor(() => vi.mocked(f.dependencies.provider.redeem).mock.calls.length - 1023);
    for (let i = 0; i < 4096; i++)
      f.store.applyProviderLogout(f.token({ sid: 'unknown-target-' + i }));
    f.advance(310_000);
    f.store.sweep();
    expect(f.store.counts()).toMatchObject({ owners: 1024, credentialRecords: 1023 });
    expect(f.store.backchannelCounts()).toMatchObject({ replay: 0, fences: 4096, saturated: true });
    gate.resolve();
    await refused;
    f.store.sweep();
    expect(f.store.counts()).toMatchObject({ owners: 1023, credentialRecords: 1023 });
    expect(f.store.backchannelCounts()).toMatchObject({ replay: 0, fences: 0, saturated: false });
  });

  it('refuses changed sid during refresh', async () => {
    const f = fixture();
    const active = await f.login();
    f.setIdentity({ sid: 'changed-session' });
    await expect(f.store.admit(active.binding, source, 'extend')).rejects.toMatchObject({
      reason: 'identity',
    });
    expect(f.store.peek(active.binding)).toBeNull();
  });

  it('seals notification admission on stop and retains only actually unsettled fence work', async () => {
    const f = fixture();
    const started = await f.start();
    const gate = f.holdRedeem();
    const completing = f.enter(started);
    const refused = expect(completing).rejects.toBeInstanceOf(BffSessionError);
    await waitFor(() => vi.mocked(f.dependencies.provider.redeem).mock.calls.length);
    const token = f.token();
    f.store.applyProviderLogout(token);
    const stopping = f.store.stop();
    expect(() => f.store.applyProviderLogout(token)).toThrow(BackchannelStateError);
    expect(f.store.backchannelCounts().fences).toBe(1);
    gate.resolve();
    await refused;
    await stopping;
    expect(f.store.backchannelCounts()).toMatchObject({ replay: 0, fences: 0, unavailable: true });
  });
});
