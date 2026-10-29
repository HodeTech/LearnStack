// @vitest-environment node
import { afterEach, describe, expect, it, vi } from 'vitest';

import { createAccountSiteCaller } from './account-client';
import type { NativePageSnapshot } from './page-contract';
import type { SessionDependencies } from './session-contract';
import { bffFixture, bffHost, bffRequest, bffSource } from '../../test/bff-fixture';
import { heldWork, snapshotRequest, SnapshotWire } from '../../test/page-response-fixture';

const fixtures: ReturnType<typeof bffFixture>[] = [];
const wires: SnapshotWire[] = [];
const gates: ReturnType<typeof heldWork>[] = [];
function fixture(options: Parameters<typeof bffFixture>[0] = {}) {
  const value = bffFixture(options);
  fixtures.push(value);
  return value;
}
function held() {
  const gate = heldWork();
  gates.push(gate);
  return gate;
}
function document(
  f: ReturnType<typeof bffFixture>,
  binding: Awaited<ReturnType<typeof f.login>>['binding'],
  options: { passive?: boolean; target?: string } = {},
) {
  const wire = new SnapshotWire();
  wires.push(wire);
  const context = {
    host: bffHost,
    peer: bffSource,
    method: 'GET',
    target: options.target ?? '/studio',
  };
  const native = snapshotRequest();
  if (!options.passive) Object.assign(native.headers, { 'sec-fetch-dest': 'document' });
  const work = f.runNative(
    wire,
    context,
    () =>
      f.store.preparePage(binding, bffSource, {
        kind: 'document',
        ticket: f.requestRuntime.preparation.current(context),
      }),
    native,
  );
  // Deliberately held actual work may reject before the assertion awaits it.
  void work.catch(() => {});
  return { wire, work };
}
function scopes(
  f: ReturnType<typeof bffFixture>,
  binding: Awaited<ReturnType<typeof f.login>>['binding'],
  cursor?: string,
) {
  const wire = new SnapshotWire();
  wires.push(wire);
  const context = {
    host: bffHost,
    peer: bffSource,
    method: 'GET',
    target: '/api/auth/entry' + (cursor ? '?cursor=' + cursor : ''),
  };
  const native = snapshotRequest();
  Object.assign(native.headers, { 'sec-fetch-dest': 'document' });
  const work = f.runNative(
    wire,
    context,
    () =>
      f.store.preparePage(binding, bffSource, {
        kind: 'scopes',
        ticket: f.requestRuntime.preparation.current(context),
        ...(cursor ? { cursor } : {}),
      }),
    native,
  );
  void work.catch(() => {});
  return { wire, work };
}
function page(
  value: Awaited<ReturnType<ReturnType<typeof bffFixture>['store']['preparePage']>>,
): NativePageSnapshot {
  if (!('target' in value)) throw new Error('Expected prepared protected document');
  return value;
}
async function settle() {
  for (let i = 0; i < 8; ++i) await Promise.resolve();
}
afterEach(async () => {
  for (const gate of gates.splice(0)) gate.finish();
  for (const wire of wires.splice(0)) {
    wire.destroy();
    wire.drainCallbacks();
  }
  await Promise.all(fixtures.splice(0).flatMap((f) => [f.store.stop(), f.requestRuntime.stop()]));
});

describe('Store page preparation through the actual private native port', () => {
  it('returns a closed 400 for canonical but unauthentic member and current-scope cursors before any API', async () => {
    const f = fixture();
    const active = await f.login();
    const calls = {
      account: f.account.mock.calls.length,
      refresh: f.provider.refresh.mock.calls.length,
      administration: f.administration.mock.calls.length,
    };
    const projected = await f.http.project(
      bffRequest('/studio/members?cursor=AA', {
        cookie: active.cookie,
        headers: { 'sec-fetch-dest': 'document' },
      }),
    );
    expect(projected).toMatchObject({ session: null, refusalStatus: 400 });
    const entry = await f.http.handle(
      bffRequest('/api/auth/entry?cursor=AA', { cookie: active.cookie }),
    );
    expect(entry?.status).toBe(400);
    expect(f.account).toHaveBeenCalledTimes(calls.account);
    expect(f.provider.refresh).toHaveBeenCalledTimes(calls.refresh);
    expect(f.administration).toHaveBeenCalledTimes(calls.administration);
    expect(f.requestRuntime.snapshotCounts()).toEqual({ active: 0, owners: 0, bytes: 0 });
    expect(f.store.peek(active.binding)?.state).toBe('authenticated');
  });

  it('reserves actual owner capacity before AccountSite, refresh or administration calls', async () => {
    const f = fixture();
    const active = await f.login();
    const retained = [];
    for (let i = 0; i < 8; ++i) {
      const request = document(f, active.binding);
      const prepared = page(await request.work);
      expect(prepared.outcome).toMatchObject({ kind: 'ready', view: { kind: 'studio' } });
      retained.push(request);
    }
    expect(f.requestRuntime.snapshotCounts().active).toBe(8);
    const before = {
      account: f.account.mock.calls.length,
      refresh: f.provider.refresh.mock.calls.length,
      administration: f.administration.mock.calls.length,
    };
    const ninth = document(f, active.binding);
    await expect(ninth.work).rejects.toMatchObject({ status: 429 });
    expect(f.account).toHaveBeenCalledTimes(before.account);
    expect(f.provider.refresh).toHaveBeenCalledTimes(before.refresh);
    expect(f.administration).toHaveBeenCalledTimes(before.administration);
    expect(f.store.peek(active.binding)?.state).toBe('authenticated');
    retained[0]!.wire.destroy();
    retained[0]!.wire.drainCallbacks();
    await settle();
    expect(f.requestRuntime.snapshotCounts().active).toBe(7);
    expect(page(await document(f, active.binding).work).outcome.kind).toBe('ready');
    expect(f.requestRuntime.snapshotCounts().active).toBe(8);
  });

  it('withholds an already prepared render after a successful credential revision while the owner remains live', async () => {
    const f = fixture();
    const active = await f.login();
    const old = document(f, active.binding);
    const prepared = page(await old.work);
    expect(prepared.ownProfile).toMatchObject({ displayName: 'Current actor' });
    expect(f.requestRuntime.snapshotCounts().active).toBe(1);
    await f.store.admit(active.binding, bffSource, 'extend');
    expect(f.provider.refresh).toHaveBeenCalledOnce();
    expect(f.store.peek(active.binding)?.state).toBe('authenticated');
    old.wire.native.write('late-private-old-render');
    expect(old.wire.chunks).not.toContain('late-private-old-render');
    expect(old.wire.statusCode).toBe(503);
    old.wire.drainCallbacks();
    await settle();
    expect(page(await document(f, active.binding).work).outcome.kind).toBe('ready');
  });

  it('retains snapshot credit through an API that ignores abort after native response closure', async () => {
    const f = fixture();
    const active = await f.login();
    const api = held();
    const entered = held();
    const original = f.administration.getMockImplementation()!;
    f.administration.mockImplementation(async (...args) => {
      if (args[2].kind === 'getOwnCapabilities') {
        entered.finish();
        await api.promise;
      }
      return original(...args);
    });
    const request = document(f, active.binding);
    await entered.promise;
    request.wire.destroy();
    request.wire.drainCallbacks();
    await settle();
    expect(f.requestRuntime.snapshotCounts().active).toBe(1);
    expect(f.store.counts().owners).toBeGreaterThan(0);
    api.finish();
    await expect(request.work).rejects.toThrow();
    await settle();
    expect(f.requestRuntime.snapshotCounts().active).toBe(0);
    expect(request.wire.chunks).toEqual([]);
  });

  it('joins shared refresh cleanup and the old page response independently during stop', async () => {
    const f = fixture();
    const active = await f.login();
    const refresh = held();
    const entered = held();
    const original = f.provider.refresh.getMockImplementation()!;
    f.provider.refresh.mockImplementation(async () => {
      entered.finish();
      await refresh.promise;
      return original();
    });
    f.advance(900_001);
    const request = document(f, active.binding);
    await entered.promise;
    let stopped = false;
    const stopping = Promise.all([f.store.stop(), f.requestRuntime.stop()]).then(() => {
      stopped = true;
    });
    request.wire.drainCallbacks();
    await settle();
    expect(stopped).toBe(false);
    expect(f.requestRuntime.snapshotCounts().active).toBe(1);
    refresh.finish();
    await expect(request.work).rejects.toThrow();
    request.wire.drainCallbacks();
    await stopping;
    expect(f.requestRuntime.snapshotCounts()).toEqual({ active: 0, owners: 0, bytes: 0 });
    expect(f.store.counts().owners).toBe(0);
  });

  it('rejects a mismatched native source before any private API call', async () => {
    const f = fixture();
    const active = await f.login();
    const wire = new SnapshotWire();
    wires.push(wire);
    const context = { host: bffHost, peer: bffSource, method: 'GET', target: '/studio' };
    const before = f.administration.mock.calls.length;
    const result = f.runNative(wire, context, () =>
      f.store.preparePage(active.binding, '203.0.113.99', {
        kind: 'document',
        ticket: f.requestRuntime.preparation.current(context),
      }),
    );
    await expect(result).rejects.toThrow();
    expect(f.administration).toHaveBeenCalledTimes(before);
    expect(f.requestRuntime.snapshotCounts().active).toBe(0);
  });

  it.each([401, 403])(
    'AccountSite %i produces public-only recovery before any administration read',
    async (status) => {
      const cachedName = 'PRIVATE cached AccountSite display name';
      const actorId = '019a154b-0000-7000-8000-000000000011';
      const account = vi.fn<SessionDependencies['account']>(async () => ({
        actorId,
        tenantId: '10000000-0000-4000-8000-000000000001',
        organizationId: null,
        displayName: cachedName,
      }));
      const f = fixture({ account });
      const active = await f.login();
      expect(active.finished.session.displayName).toBe(cachedName);
      const transport = vi.fn(
        async () =>
          new Response(
            JSON.stringify({
              status,
              code: status === 401 ? 'unauthorized' : 'forbidden',
              messageKey: 'lockey_private_account_refusal',
              correlationId: actorId,
              type: 'about:blank',
              title: 'Private upstream title',
              instance: '/private-upstream',
            }),
            { status, headers: { 'content-type': 'application/problem+json' } },
          ),
      );
      account.mockImplementationOnce(
        createAccountSiteCaller(
          {
            apiOrigin: 'http://127.0.0.1:5100',
            secret: 'private-hop-'.padEnd(48, 'x'),
            nativeSourceSecret: Buffer.from(
              Array.from({ length: 32 }, (_, index) => index + 1),
            ).toString('base64url'),
          },
          transport,
        ),
      );
      const request = document(f, active.binding);
      const result = await request.work;
      expect(transport).toHaveBeenCalledOnce();
      expect(result).toMatchObject({
        view: {
          kind: 'failure',
          reason: status === 401 ? 'unavailable' : 'reauthenticate',
          code: status === 401 ? 'provider_refused' : 'identity',
        },
      });
      expect(Object.keys(result).sort()).toEqual(['locale', 'site', 'view']);
      const serialized = JSON.stringify(result);
      for (const secret of [
        cachedName,
        active.finished.session.csrfToken,
        actorId,
        'Private upstream title',
        'lockey_private_account_refusal',
        'private-access-canary',
      ])
        expect(serialized).not.toContain(secret);
      expect(result).not.toHaveProperty('session');
      expect(result).not.toHaveProperty('ownProfile');
      expect(result).not.toHaveProperty('formKeys');
      expect(f.administration).not.toHaveBeenCalled();
      if (status === 401) expect(f.store.peek(active.binding)?.state).toBe('authenticated');
      else expect(f.store.peek(active.binding)).toBeNull();
      // Recovery remains publishable after an identity refusal closes the private owner.
      request.wire.native.end(serialized);
      expect(request.wire.chunks).toContain(serialized);
      request.wire.drainCallbacks();
    },
  );

  it('preloads an ordinary passive document without refreshing or extending idle expiry', async () => {
    const f = fixture();
    const active = await f.login();
    const expiry = f.store.peek(active.binding)!.expiresAt;
    f.advance(60_000);
    const result = page(await document(f, active.binding, { passive: true }).work);
    expect(result.outcome).toMatchObject({ kind: 'ready', view: { kind: 'studio' } });
    expect(f.administration.mock.calls.map(([, , operation]) => operation.kind)).toEqual([
      'getOwnCapabilities',
      'getOwnProfile',
    ]);
    expect(f.provider.refresh).not.toHaveBeenCalled();
    expect(result.session.expiresAt).toBe(expiry);
    expect(f.store.peek(active.binding)!.expiresAt).toBe(expiry);
  });

  it('round-trips encrypted member/scope cursors and refuses cross-purpose use before any API', async () => {
    const f = fixture();
    const active = await f.login();
    const subject = 'a'.repeat(64);
    const rawMembers = Buffer.from('members:' + subject).toString('base64url');
    const rawScopes = Buffer.from('contexts:' + subject).toString('base64url');
    const original = f.administration.getMockImplementation()!;
    f.administration.mockImplementation(async (...args) => {
      const kind = args[2].kind;
      if (kind === 'listMemberships')
        return {
          kind: 'success',
          status: 200,
          etag: null,
          location: null,
          replayed: false,
          data: {
            items: [],
            pageInfo: {
              nextCursor: rawMembers,
              previousCursor: null,
              hasNext: true,
              hasPrevious: false,
            },
          },
        };
      if (kind === 'getOwnContexts')
        return {
          kind: 'success',
          status: 200,
          etag: null,
          location: null,
          replayed: false,
          data: {
            items: [
              {
                ...f.scope,
                tenantName: 'Institution',
                organizationName: null,
                entryOrigin: 'https://' + bffHost,
                canUseCurrentHost: true,
              },
            ],
            pageInfo: {
              nextCursor: rawScopes,
              previousCursor: null,
              hasNext: true,
              hasPrevious: false,
            },
          },
        };
      return original(...args);
    });
    const members = page(await document(f, active.binding, { target: '/studio/members' }).work);
    if (members.outcome.kind !== 'ready' || members.outcome.view.kind !== 'members')
      throw new Error('Expected member page');
    const memberCursor = members.outcome.view.endCursor!;
    const choices = await scopes(f, active.binding).work;
    if (!('view' in choices) || choices.view.kind !== 'scopes')
      throw new Error('Expected scope page');
    const scopeCursor = choices.view.selection.contexts.endCursor!;
    for (const cursor of [memberCursor, scopeCursor]) {
      expect(cursor).toMatch(/^[A-Za-z0-9_-]+$/);
      expect(cursor.length).toBeLessThanOrEqual(1024);
      expect(Buffer.from(cursor, 'base64url').toString('utf8')).not.toContain(subject);
    }
    expect(JSON.stringify(members)).not.toContain(rawMembers);
    expect(JSON.stringify(choices)).not.toContain(rawScopes);
    await document(f, active.binding, { target: '/studio/members?cursor=' + memberCursor }).work;
    expect(f.administration).toHaveBeenLastCalledWith(
      expect.any(String),
      expect.any(Object),
      { kind: 'listMemberships', limit: 20, cursor: rawMembers },
      expect.any(AbortSignal),
    );
    await scopes(f, active.binding, scopeCursor).work;
    expect(f.administration).toHaveBeenLastCalledWith(
      expect.any(String),
      expect.any(Object),
      { kind: 'getOwnContexts', limit: 20, cursor: rawScopes },
      expect.any(AbortSignal),
    );
    const calls = {
      account: f.account.mock.calls.length,
      administration: f.administration.mock.calls.length,
    };
    await expect(
      document(f, active.binding, { target: '/studio/members?cursor=' + scopeCursor }).work,
    ).rejects.toThrow();
    await expect(scopes(f, active.binding, memberCursor).work).rejects.toThrow();
    expect(f.account).toHaveBeenCalledTimes(calls.account);
    expect(f.administration).toHaveBeenCalledTimes(calls.administration);
    expect(f.requestRuntime.snapshotCounts().active).toBe(4);
    expect(f.store.peek(active.binding)?.state).toBe('authenticated');
  });
});
