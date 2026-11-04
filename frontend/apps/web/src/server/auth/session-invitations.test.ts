// @vitest-environment node
import { afterEach, describe, expect, it, vi } from 'vitest';

import { NativeInvitationError } from './native-invitation-contract';
import type { NativeInvitationApi } from './native-invitation-contract';
import { BFF_LIMITS } from './session-store';
import { bffFixture, bffHost, bffOpaque, bffSource } from '../../test/bff-fixture';

const invitationId = '019a154b-0000-7000-8000-000000000077';
const receipt = {
  invitationId,
  version: '2',
  membershipId: '019a154b-0000-7000-8000-000000000078',
  disposition: 'created' as const,
  tenantId: '10000000-0000-4000-8000-000000000001',
  organizationId: null,
};
const inspection = {
  invitationId,
  version: '1',
  expiresAt: '2026-11-06T12:00:00Z',
  tenantLabel: 'Institution',
  organizationLabel: null,
  roleId: '019a154b-0000-7000-8000-000000000088',
  roleKey: 'Learner' as const,
  roleScope: 'Tenant' as const,
  verifiedEmail: 'recipient@example.test',
};
const fixtures: ReturnType<typeof bffFixture>[] = [];
afterEach(async () => {
  await Promise.all(fixtures.splice(0).map((f) => f.store.stop()));
});
function fixture(unscopedProvider = false) {
  const api = {
    inspect: vi.fn<NativeInvitationApi['inspect']>(async () => inspection),
    accept: vi.fn<NativeInvitationApi['accept']>(async () => receipt),
    result: vi.fn<NativeInvitationApi['result']>(async () => receipt),
  };
  const f = bffFixture({ invitations: api, unscopedProvider });
  fixtures.push(f);
  return { ...f, api };
}
async function captured(f: ReturnType<typeof fixture>, existing = false) {
  const session = existing ? await f.login() : undefined;
  const opened = f.store.openInvitation({
    host: bffHost,
    source: bffSource,
    ...(session ? { browser: session.binding.browser, sessionHandle: session.binding.handle } : {}),
  });
  const binding = { host: bffHost, handle: opened.handle, browser: opened.browser };
  const code = invitationId + '.' + bffOpaque();
  await f.store.invitation(binding, bffSource, { kind: 'capture', csrf: opened.csrfToken, code });
  return { opened, binding, code, session };
}
async function signin(f: ReturnType<typeof fixture>, offer: Awaited<ReturnType<typeof captured>>) {
  return f.enter({
    browser: offer.binding.browser,
    invitation: offer.binding.handle,
    ...(offer.session ? { handle: offer.session.binding.handle } : {}),
  });
}

describe('owned invitation continuation', () => {
  it('refuses a different callback subject and preserves the original live account', async () => {
    const f = fixture();
    const offer = await captured(f, true);
    const credentials = await f.provider.redeem();
    f.provider.redeem.mockResolvedValueOnce({
      ...credentials,
      identity: { ...credentials.identity, subject: 'other-account' },
    });
    await expect(signin(f, offer)).rejects.toMatchObject({ reason: 'account_change' });
    expect(f.store.peek(offer.session!.binding)).not.toBeNull();
    expect((await f.store.invitation(offer.binding, bffSource)).state).toBe('account_change');
    expect(f.api.inspect).not.toHaveBeenCalled();
    expect(f.api.accept).not.toHaveBeenCalled();
  });

  it('keeps capture passive, never projects the code and does not replace the live session', async () => {
    const f = fixture();
    const offer = await captured(f, true);
    const view = await f.store.invitation(offer.binding, bffSource);
    expect(JSON.stringify(view)).not.toContain(offer.code);
    expect(view.state).toBe('inspect');
    expect(f.api.inspect).not.toHaveBeenCalled();
    expect(f.api.accept).not.toHaveBeenCalled();
    expect(f.store.peek(offer.session!.binding)).not.toBeNull();
    await expect(
      f.store.invitation({ ...offer.binding, host: 'other.example.test:3000' }, bffSource),
    ).rejects.toMatchObject({ reason: 'revoked' });
    await expect(
      f.store.invitation(offer.binding, bffSource, { kind: 'inspect', csrf: bffOpaque() }),
    ).rejects.toMatchObject({ reason: 'csrf' });
  });
  it.each([false, true])(
    'keeps the original absolute lifetime when accepting another scope (verification: %s)',
    async (verify) => {
      const f = fixture(true);
      const session = await f.login();
      for (let i = 0; i < 23; i++) {
        f.advance(20 * 60_000);
        await f.store.admit(session.binding, bffSource, 'extend');
      }
      f.advance(14 * 60_000);
      const opened = f.store.openInvitation({
        host: bffHost,
        source: bffSource,
        browser: session.binding.browser,
        sessionHandle: session.binding.handle,
      });
      const binding = { host: bffHost, handle: opened.handle, browser: opened.browser };
      await f.store.invitation(binding, bffSource, {
        kind: 'capture',
        csrf: opened.csrfToken,
        code: invitationId + '.' + bffOpaque(),
      });
      if (verify) {
        f.api.inspect.mockRejectedValueOnce(new NativeInvitationError('forbidden', 403));
        await f.store.invitation(binding, bffSource, { kind: 'inspect', csrf: opened.csrfToken });
        const verification = await f.store.invitation(binding, bffSource, {
          kind: 'verify',
          csrf: opened.csrfToken,
        });
        const login = verification.login!;
        await f.store.complete({
          host: bffHost,
          source: bffSource,
          browser: login.browser,
          state: login.state,
          proof: login.proof,
          responseUrl:
            'https://' +
            bffHost +
            '/api/auth/callback/keycloak?state=' +
            login.state +
            '&code=owned',
        });
      }
      await f.store.invitation(binding, bffSource, { kind: 'inspect', csrf: opened.csrfToken });
      const organizationId = '10000000-0000-4000-8000-000000000002';
      f.api.accept.mockResolvedValueOnce({ ...receipt, organizationId });
      f.account.mockResolvedValueOnce({
        actorId: '019a154b-0000-7000-8000-000000000011',
        tenantId: receipt.tenantId,
        organizationId,
        displayName: 'Institution',
      });
      const accepted = await f.store.invitation(binding, bffSource, {
        kind: 'accept',
        csrf: opened.csrfToken,
        version: '1',
      });
      expect(accepted).toMatchObject({ state: 'accepted', sessionAvailable: true });
      const completion = accepted.completion!;
      expect(completion.session.expiresAt).toBe(1_800_000_000_000 + BFF_LIMITS.absoluteMs);
      expect(completion.session.limitedByAbsolute).toBe(true);
      expect(f.store.peek(session.binding)).toBeNull();
      f.advance(6 * 60_000);
      expect(f.store.peek({ ...session.binding, handle: completion.handle })).toBeNull();
    },
  );
  it('requires deliberate inspection, explicit global bootstrap and separate session admission', async () => {
    const f = fixture();
    const offer = await captured(f);
    const entry = await signin(f, offer);
    f.native.self.mockResolvedValueOnce({ state: 'missing' });
    const missing = await f.store.invitation(offer.binding, bffSource, {
      kind: 'inspect',
      csrf: offer.opened.csrfToken,
    });
    expect(missing.state).toBe('bootstrap');
    expect(missing.entryCsrfToken).toBe(entry.finished.entry.csrfToken);
    expect(f.api.inspect).not.toHaveBeenCalled();
    expect(f.native.bootstrap).not.toHaveBeenCalled();
    f.native.self.mockResolvedValueOnce({ state: 'missing' });
    await f.store.create(entry.binding, bffSource, missing.entryCsrfToken!);
    expect(f.native.bootstrap).toHaveBeenCalledTimes(1);
    const ready = await f.store.invitation(offer.binding, bffSource, {
      kind: 'inspect',
      csrf: offer.opened.csrfToken,
    });
    expect(ready.inspection?.verifiedEmail).toBe(inspection.verifiedEmail);
    expect(f.api.accept).not.toHaveBeenCalled();
    const accepted = await f.store.invitation(offer.binding, bffSource, {
      kind: 'accept',
      csrf: offer.opened.csrfToken,
      version: '1',
    });
    expect(accepted).toMatchObject({
      state: 'accepted',
      disposition: 'created',
      sessionAvailable: true,
    });
    expect(accepted.completion?.kind).toBe('session');
    expect(f.native.admit).toHaveBeenCalledTimes(1);
    expect(f.account).toHaveBeenCalledTimes(1);
    expect(JSON.stringify(accepted)).not.toContain(offer.code);
  });
  it('uses result reconciliation after an unknown acceptance and retains a truthful committed receipt if admission fails', async () => {
    const f = fixture();
    const offer = await captured(f);
    await signin(f, offer);
    await f.store.invitation(offer.binding, bffSource, {
      kind: 'inspect',
      csrf: offer.opened.csrfToken,
    });
    f.api.accept.mockRejectedValueOnce(new Error('private upstream canary'));
    expect(
      (
        await f.store.invitation(offer.binding, bffSource, {
          kind: 'accept',
          csrf: offer.opened.csrfToken,
          version: '1',
        })
      ).state,
    ).toBe('unknown');
    expect(f.api.accept).toHaveBeenCalledTimes(1);
    f.account.mockRejectedValueOnce(new Error('account unavailable'));
    const result = await f.store.invitation(offer.binding, bffSource, {
      kind: 'result',
      csrf: offer.opened.csrfToken,
    });
    expect(result).toMatchObject({
      state: 'accepted',
      disposition: 'created',
      sessionAvailable: false,
    });
    expect(result.completion).toBeUndefined();
    expect(f.api.accept).toHaveBeenCalledTimes(1);
  });
  it.each(['success', 'cancelled', 'error'])(
    'binds VERIFY_EMAIL status %s to the owned subject without treating it as verified',
    async (status) => {
      const f = fixture();
      const offer = await captured(f);
      const entry = await signin(f, offer);
      expect((await f.store.invitation(offer.binding, bffSource)).entryCsrfToken).toBe(
        entry.finished.entry.csrfToken,
      );
      f.api.inspect.mockRejectedValueOnce(new NativeInvitationError('forbidden', 403));
      const forbidden = await f.store.invitation(offer.binding, bffSource, {
        kind: 'inspect',
        csrf: offer.opened.csrfToken,
      });
      expect(forbidden.state).toBe('forbidden');
      expect(forbidden.entryCsrfToken).toBe(entry.finished.entry.csrfToken);
      const verification = await f.store.invitation(offer.binding, bffSource, {
        kind: 'verify',
        csrf: offer.opened.csrfToken,
      });
      expect(f.provider.begin).toHaveBeenLastCalledWith(
        'https://' + bffHost + '/api/auth/callback/keycloak',
        expect.any(AbortSignal),
        'verify_email',
      );
      const login = verification.login!;
      for (const query of [
        'kc_action_status=success',
        'kc_action=VERIFY_EMAIL',
        'kc_action=UPDATE_PASSWORD&kc_action_status=success',
        'kc_action=VERIFY_EMAIL&kc_action_status=unknown',
        'kc_action=VERIFY_EMAIL&kc_action=VERIFY_EMAIL&kc_action_status=success',
      ]) {
        await expect(
          f.store.complete({
            host: bffHost,
            source: bffSource,
            browser: login.browser,
            state: login.state,
            proof: login.proof,
            responseUrl:
              'https://' +
              bffHost +
              '/api/auth/callback/keycloak?state=' +
              login.state +
              '&code=owned&' +
              query,
          }),
        ).rejects.toMatchObject({ reason: 'transaction' });
      }
      expect(f.provider.redeem).toHaveBeenCalledTimes(1);
      const done = await f.store.complete({
        host: bffHost,
        source: bffSource,
        browser: login.browser,
        state: login.state,
        proof: login.proof,
        responseUrl:
          'https://' +
          bffHost +
          '/api/auth/callback/keycloak?state=' +
          login.state +
          '&code=opaque&kc_action=VERIFY_EMAIL&kc_action_status=' +
          status,
      });
      expect(done.invitation).toBe(offer.binding.handle);
      expect((await f.store.invitation(offer.binding, bffSource)).state).toBe('inspect');
      expect(f.api.inspect).toHaveBeenCalledTimes(1);
      expect(f.api.accept).not.toHaveBeenCalled();
    },
  );
  it('refuses action status on ordinary login without consuming its valid pending transaction', async () => {
    const f = fixture();
    const login = await f.store.start({ host: bffHost, source: bffSource, returnPath: '/' });
    const input = {
      host: bffHost,
      source: bffSource,
      browser: login.browser,
      state: login.state,
      proof: login.proof,
      responseUrl:
        'https://' + bffHost + '/api/auth/callback/keycloak?state=' + login.state + '&code=owned',
    };
    await expect(
      f.store.complete({
        ...input,
        responseUrl: input.responseUrl + '&kc_action=VERIFY_EMAIL&kc_action_status=success',
      }),
    ).rejects.toMatchObject({ reason: 'transaction' });
    expect(f.provider.redeem).not.toHaveBeenCalled();
    expect((await f.store.complete(input)).kind).toBe('entry');
  });
  it('keeps cancellation quota until actual response cleanup and prevents late publication', async () => {
    const f = fixture();
    const offer = await captured(f);
    let release!: () => void;
    let assert!: () => void;
    const cleaned = new Promise<void>((resolve) => {
      release = resolve;
    });
    f.store.retainInvitation(offer.binding, {
      signal: new AbortController().signal,
      cleaned,
      pin: (value) => {
        assert = value;
      },
    });
    const before = f.store.counts().owners;
    await f.store.invitation(offer.binding, bffSource, {
      kind: 'cancel',
      csrf: offer.opened.csrfToken,
    });
    expect(f.store.counts().owners).toBe(before);
    expect(() => assert()).toThrow();
    release();
    for (let i = 0; i < 8; i++) await Promise.resolve();
    expect(f.store.counts().owners).toBe(0);
  });
  it('replacing custody invalidates old work and retains the original five-minute deadline', async () => {
    const f = fixture();
    const offer = await captured(f, true);
    let settle!: (value: typeof inspection) => void;
    f.api.inspect.mockImplementationOnce(
      () =>
        new Promise((resolve) => {
          settle = resolve;
        }),
    );
    const pending = f.store.invitation(offer.binding, bffSource, {
      kind: 'inspect',
      csrf: offer.opened.csrfToken,
    });
    for (let i = 0; i < 20 && !settle; i++) await Promise.resolve();
    expect(settle).toBeTypeOf('function');
    f.advance(BFF_LIMITS.transactionMs - 1000);
    const replacement = await f.store.invitation(offer.binding, bffSource, {
      kind: 'capture',
      csrf: offer.opened.csrfToken,
      code: invitationId + '.' + bffOpaque(),
    });
    expect(replacement.opening).toBeDefined();
    expect(f.store.counts().owners).toBeGreaterThan(2);
    settle(inspection);
    await expect(pending).rejects.toMatchObject({ reason: 'cancelled' });
    const next = { ...offer.binding, handle: replacement.opening!.handle };
    f.advance(1001);
    await expect(f.store.invitation(next, bffSource)).rejects.toMatchObject({ reason: 'expired' });
    expect(f.store.peek(offer.session!.binding)).not.toBeNull();
  });
  it('includes invitation owners in the existing eight verified-subject ceiling', async () => {
    const f = fixture();
    const sessions = [];
    for (let i = 0; i < 4; i++) sessions.push(await captured(f, true));
    expect(f.store.counts().owners).toBe(8);
    await expect(f.enter()).rejects.toMatchObject({ reason: 'subject_capacity' });
    expect(f.store.counts().owners).toBe(8);
    await f.store.invitation(sessions[0]!.binding, bffSource, {
      kind: 'cancel',
      csrf: sessions[0]!.opened.csrfToken,
    });
    await expect(f.enter()).resolves.toBeDefined();
  });
});
