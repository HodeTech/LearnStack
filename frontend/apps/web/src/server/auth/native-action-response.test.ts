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
  it('withholds correction values when logout wins a held preference read and retains the owner until that read ends', async () => {
    const f = fixture();
    const active = await f.login();
    const entered = held();
    const release = held();
    const original = f.administration.getMockImplementation()!;
    f.administration.mockImplementation(async (...args) => {
      if (args[2].kind === 'getOwnProfile') {
        entered.finish();
        await release.promise;
      }
      return original(...args);
    });
    const output = wire();
    const protocol = createBffHttpProtocol(f.store, f.requestRuntime.preparation);
    const running = f.runNative(output, post('/api/account/profile'), () =>
      protocol.handle(
        bffRequest('/api/account/profile', {
          method: 'POST',
          cookie: active.cookie,
          form: new URLSearchParams({
            csrfToken: active.finished.session.csrfToken,
            version: '7',
            idempotencyKey,
            displayName: '   ',
            preferredLocale: 'tr',
          }).toString(),
        }),
        (page) =>
          renderBffDocument(
            {
              displayName: 'Institution',
              defaultLocale: 'en',
              enabledLocales: ['en', 'tr'],
              theme: null,
              showPlatformAttribution: true,
            },
            page,
          ),
      ),
    );
    processing.push(running);
    await entered.promise;
    f.store.signout(active.binding, active.finished.session.csrfToken);
    // The locale read precedes publication: no private document has been
    // admitted or written yet. Logout must prevent that later publication.
    expect(output.chunks).toEqual([]);
    expect(f.requestRuntime.snapshotCounts().active).toBe(1);
    release.finish();
    const response = await running;
    const body = await response?.text();
    expect(body).not.toContain('name="displayName"');
    expect(body).not.toContain(active.finished.session.csrfToken);
    if (!output.writableEnded && !output.destroyed) output.native.end(body);
    output.drainCallbacks();
    await settle();
    expect(f.requestRuntime.snapshotCounts().active).toBe(0);
  });

  it.each(['/api/account/profile', '/api/studio/membership-profile'])(
    'recovers whitespace validation on the native, JavaScript-free %s form and accepts a corrected POST',
    async (path) => {
      const f = fixture();
      const active = await f.login();
      const fields = new URLSearchParams({
        csrfToken: active.finished.session.csrfToken,
        idempotencyKey,
        version: '7',
        displayName: '   ',
        preferredLocale: 'tr',
        ...(path.includes('membership-')
          ? { membershipId: '019a154b-0000-7000-8000-000000000022' }
          : {}),
      });
      const protocol = createBffHttpProtocol(f.store, f.requestRuntime.preparation);
      const output = wire();
      const response = await f.runNative(output, post(path), () =>
        protocol.handle(
          bffRequest(path, {
            method: 'POST',
            cookie: active.cookie,
            form: fields.toString(),
            headers: { accept: 'text/html' },
          }),
          (page) =>
            renderBffDocument(
              {
                displayName: 'Institution',
                defaultLocale: 'en',
                enabledLocales: ['en', 'tr'],
                theme: null,
                showPlatformAttribution: true,
              },
              page,
            ),
        ),
      );
      expect(response?.status).toBe(400);
      expect(response?.headers.get('content-type')).toContain('text/html');
      expect(response?.headers.get('cache-control')).toBe('no-store');
      expect(response?.headers.get('location')).toBeNull();
      const body = await response!.text();
      expect(body).toContain(`form action="${path}" method="post"`);
      expect(body).toContain('name="displayName" value="   "');
      expect(body).toContain('aria-describedby="profile-name-hint profile-name-error"');
      const correctionKey = /name="idempotencyKey" value="([A-Za-z0-9_-]{43})"/.exec(body)?.[1];
      expect(correctionKey).toMatch(/^[A-Za-z0-9_-]{43}$/);
      expect(correctionKey).not.toBe(idempotencyKey);
      expect(body).not.toContain('script src=');
      expect(
        f.administration.mock.calls.some(([, , operation]) => operation.kind.startsWith('set')),
      ).toBe(false);
      output.native.end(body);
      output.drainCallbacks();
      await settle();
      fields.set('displayName', 'Corrected name');
      fields.set('idempotencyKey', correctionKey!);
      f.administration.mockResolvedValueOnce({
        kind: 'success',
        status: 200,
        data: { version: '8' },
        etag: '"8"',
        location: null,
        replayed: false,
      });
      const corrected = await f.runNative(wire(), post(path), () =>
        protocol.handle(
          bffRequest(path, { method: 'POST', cookie: active.cookie, form: fields.toString() }),
        ),
      );
      expect(corrected?.status).toBe(303);
      expect(corrected?.headers.get('location')).toBe(
        path.includes('membership-')
          ? '/studio/members/019a154b-0000-7000-8000-000000000022'
          : '/portal/profile',
      );
      const operation = f.administration.mock.calls.at(-1)![2];
      expect(operation).toMatchObject({
        profile: { displayName: 'Corrected name', preferredLocale: 'tr' },
        version: '7',
        idempotencyKey: correctionKey,
      });
    },
  );

  it.each(['stale', 'unavailable'] as const)(
    'uses the live actor preference, not submitted values, for %s recovery',
    async (reason) => {
      const f = fixture();
      const active = await f.login();
      f.administration.mockResolvedValueOnce(
        reason === 'stale'
          ? {
              kind: 'api-error',
              status: 409,
              error: { code: 'concurrency_conflict' },
              problem: {
                type: 'about:blank',
                title: 'Conflict',
                status: 409,
                instance: '/api/account/profile',
                code: 'concurrency_conflict',
                messageKey: 'lockey_concurrency_conflict',
                correlationId: 'test-conflict',
              },
            }
          : { kind: 'transport-error' },
      );
      f.administration.mockResolvedValueOnce({
        kind: 'success',
        status: 200,
        data: { displayName: 'Current actor', preferredLocale: 'tr', version: '8' },
        etag: '"8"',
        location: null,
        replayed: false,
      });
      const output = wire();
      const fields = new URLSearchParams(
        mutation(active.finished.session.csrfToken).fields.map(([k, v]) => [k, v]),
      );
      fields.set('preferredLocale', 'en');
      const protocol = createBffHttpProtocol(f.store, f.requestRuntime.preparation);
      const response = await f.runNative(output, post('/api/account/profile'), () =>
        protocol.handle(
          bffRequest('/api/account/profile', {
            method: 'POST',
            cookie: active.cookie,
            form: fields.toString(),
          }),
          (page) =>
            renderBffDocument(
              {
                displayName: 'Institution',
                defaultLocale: 'en',
                enabledLocales: ['en', 'tr'],
                theme: null,
                showPlatformAttribution: true,
              },
              page,
            ),
        ),
      );
      expect(response?.status).toBe(reason === 'stale' ? 409 : 503);
      const body = await response!.text();
      expect(body).toContain('<html lang="tr"');
      expect(f.administration.mock.calls.at(-1)![2]).toEqual({ kind: 'getOwnProfile' });
      output.native.end(body);
      output.drainCallbacks();
    },
  );

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
