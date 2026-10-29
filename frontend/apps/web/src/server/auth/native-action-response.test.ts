// @vitest-environment node
import { afterEach, describe, expect, it, vi } from 'vitest';

import { renderBffDocument } from './html';
import { createBffHttpProtocol } from './http-protocol';
import type { NativeActionPage, NativeEntrySnapshot } from './page-contract';
import { BffSessionError } from './session-contract';
import { bffFixture, bffHost, bffRequest, bffSource } from '../../test/bff-fixture';
import { heldWork, snapshotRequest, SnapshotWire } from '../../test/page-response-fixture';

const fixtures: ReturnType<typeof bffFixture>[] = [];
const wires: SnapshotWire[] = [];
const gates: ReturnType<typeof heldWork>[] = [];
const processing: Promise<unknown>[] = [];
const edited = 'PRIVATE unsaved <profile edit>';
const idempotencyKey = 'same-native-response-key';
function fixture() {
  const value = bffFixture();
  fixtures.push(value);
  return value;
}
function held() {
  const value = heldWork();
  gates.push(value);
  return value;
}
function wire() {
  const value = new SnapshotWire();
  wires.push(value);
  return value;
}
function mutation(csrfToken: string): Extract<NativeActionPage, { kind: 'mutation_error' }> {
  return {
    kind: 'mutation_error',
    action: '/api/account/profile',
    destination: '/portal/profile',
    reason: 'unavailable',
    retryable: true,
    fields: [
      ['csrfToken', csrfToken],
      ['idempotencyKey', idempotencyKey],
      ['version', '7'],
      ['displayName', edited],
      ['preferredLocale', 'tr'],
    ],
  };
}
function post(target: string) {
  return { host: bffHost, peer: bffSource, method: 'POST', target };
}
async function settle() {
  for (let i = 0; i < 10; ++i) await Promise.resolve();
}
afterEach(async () => {
  for (const gate of gates.splice(0)) gate.finish();
  for (const output of wires.splice(0)) {
    output.destroy();
    output.drainCallbacks();
  }
  await Promise.allSettled(processing.splice(0));
  await Promise.all(fixtures.splice(0).flatMap((f) => [f.store.stop(), f.requestRuntime.stop()]));
  vi.restoreAllMocks();
});

describe('native action recovery response lifetime', () => {
  it.each([
    ['mutation_error', 'logout'],
    ['mutation_error', 'revision'],
    ['account_change', 'logout'],
    ['account_change', 'revision'],
  ] as const)(
    'withholds late %s bytes after %s and joins the actual handler/write callback',
    async (kind, invalidation) => {
      const f = fixture();
      const active = await f.login();
      const output = wire();
      const ready = held();
      const handler = held();
      const context = post(
        kind === 'mutation_error' ? '/api/account/profile' : '/api/auth/entry/select',
      );
      const page: NativeActionPage =
        kind === 'mutation_error'
          ? mutation(active.finished.session.csrfToken)
          : { kind: 'account_change', csrfToken: active.finished.session.csrfToken };
      let prepared: NativeEntrySnapshot | undefined;
      const running = f.runNative(
        output,
        context,
        async () => {
          if (kind === 'mutation_error') {
            f.administration.mockResolvedValueOnce({ kind: 'transport-error' });
            const result = await f.store.operate(
              active.binding,
              bffSource,
              {
                kind: 'setOwnProfile',
                version: '7',
                idempotencyKey,
                profile: { displayName: edited, preferredLocale: 'tr' },
              },
              active.finished.session.csrfToken,
            );
            expect(result.kind).toBe('transport-error');
            expect(f.store.counts().operations).toBe(0);
          }
          const snapshot = await f.store.preparePage(active.binding, bffSource, {
            kind: 'response',
            ticket: f.requestRuntime.preparation.current(context),
            page,
          });
          if ('target' in snapshot) throw new Error('Expected native action snapshot');
          const frozenPage = snapshot.view;
          if (frozenPage.kind !== 'mutation_error' && frozenPage.kind !== 'account_change')
            throw new Error('Expected frozen action view');
          prepared = snapshot;
          output.native.write('<!doctype html>');
          ready.finish();
          await handler.promise;
          output.native.end(renderBffDocument(snapshot.site, frozenPage));
        },
        snapshotRequest(),
      );
      processing.push(running);
      void running.catch(() => ready.finish());
      await ready.promise;
      if (!prepared) await running;
      expect(prepared?.view.kind).toBe(kind);
      expect(f.requestRuntime.snapshotCounts().active).toBe(1);
      expect(output.callbacks).toHaveLength(1);
      if (invalidation === 'logout')
        f.store.signout(active.binding, active.finished.session.csrfToken);
      else await f.store.admit(active.binding, bffSource, 'extend');
      expect(output.destroyed).toBe(true);
      expect(f.requestRuntime.snapshotCounts().active).toBe(1);
      handler.finish();
      await running;
      await settle();
      expect(f.requestRuntime.snapshotCounts().active).toBe(1);
      expect(f.requestRuntime.counts()).toBe(1);
      expect(output.chunks).toEqual(['<!doctype html>']);
      const body = output.chunks.join('');
      expect(body).not.toContain(active.finished.session.csrfToken);
      expect(body).not.toContain(edited);
      output.drainCallbacks();
      await settle();
      expect(f.requestRuntime.snapshotCounts()).toEqual({ active: 0, owners: 0, bytes: 0 });
      expect(f.requestRuntime.counts()).toBe(0);
      if (invalidation === 'revision')
        expect(f.store.peek(active.binding)?.state).toBe('authenticated');
      else expect(f.store.peek(active.binding)).toBeNull();
    },
  );

  it('prepares live unknown-outcome HTTP recovery with the exact same body and retry identity', async () => {
    const f = fixture();
    const active = await f.login();
    f.administration.mockResolvedValueOnce({ kind: 'transport-error' });
    const output = wire();
    const context = post('/api/account/profile');
    const protocol = createBffHttpProtocol(f.store, f.requestRuntime.preparation);
    const fields = mutation(active.finished.session.csrfToken).fields;
    const response = await f.runNative(output, context, () =>
      protocol.handle(
        bffRequest(context.target, {
          method: 'POST',
          cookie: active.cookie,
          form: new URLSearchParams(fields.map(([name, value]) => [name, value])).toString(),
        }),
        (page) =>
          renderBffDocument(
            {
              displayName: 'Institution',
              defaultLocale: 'en',
              enabledLocales: ['en'],
              theme: null,
              showPlatformAttribution: true,
            },
            page,
          ),
      ),
    );
    expect(response?.status).toBe(503);
    expect(f.store.counts().operations).toBe(1);
    expect(f.requestRuntime.snapshotCounts().active).toBe(1);
    const body = await response!.text();
    expect(body).toContain('form action="/api/account/profile" method="post"');
    expect(body).toContain('name="csrfToken" value="' + active.finished.session.csrfToken + '"');
    expect(body).toContain('name="idempotencyKey" value="' + idempotencyKey + '"');
    expect(body).toContain('name="version" value="7"');
    expect(body).toContain('PRIVATE unsaved &lt;profile edit&gt;');
    output.native.end(body);
    await settle();
    expect(f.requestRuntime.snapshotCounts().active).toBe(1);
    expect(f.store.counts().operations).toBe(1);
    output.drainCallbacks();
    await settle();
    expect(f.requestRuntime.snapshotCounts().active).toBe(0);
    expect(f.store.counts().operations).toBe(0);
    expect(f.store.peek(active.binding)?.state).toBe('authenticated');
  });

  it('pins the existing session account-change response on the actual entry/select POST route', async () => {
    const f = fixture();
    const active = await f.login();
    const entry = await f.enter({ browser: active.binding.browser });
    vi.spyOn(f.store, 'select').mockRejectedValueOnce(new BffSessionError('account_change'));
    const output = wire();
    const context = post('/api/auth/entry/select');
    const protocol = createBffHttpProtocol(f.store, f.requestRuntime.preparation);
    const response = await f.runNative(output, context, () =>
      protocol.handle(
        bffRequest(context.target, {
          method: 'POST',
          cookie: active.cookie + '; __Host-ls-entry=' + entry.finished.handle,
          form: new URLSearchParams({
            csrfToken: entry.finished.entry.csrfToken,
            tenantId: f.scope.tenantId,
          }).toString(),
        }),
        (page) =>
          renderBffDocument(
            {
              displayName: 'Institution',
              defaultLocale: 'en',
              enabledLocales: ['en'],
              theme: null,
              showPlatformAttribution: true,
            },
            page,
          ),
      ),
    );
    expect(response?.status).toBe(409);
    expect(f.requestRuntime.snapshotCounts().active).toBe(1);
    const body = await response!.text();
    expect(body).toContain('name="csrfToken" value="' + active.finished.session.csrfToken + '"');
    expect(body).not.toContain(entry.finished.entry.csrfToken);
    f.store.signout(active.binding, active.finished.session.csrfToken);
    output.native.end(body);
    expect(output.chunks).toEqual([]);
    output.drainCallbacks();
    await settle();
    expect(f.requestRuntime.snapshotCounts().active).toBe(0);
  });
});
