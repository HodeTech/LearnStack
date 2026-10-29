// @vitest-environment node
import { afterEach, describe, expect, it, vi } from 'vitest';

import type { NativeEntrySnapshot, NativePageSnapshot } from './page-contract';
import { createPageSnapshotPool, SnapshotAdmissionError } from './page-snapshot-pool';

const cleanup: (() => void)[] = [];
const pools: ReturnType<typeof createPageSnapshotPool>[] = [];
function deferred() {
  let resolve!: () => void;
  const promise = new Promise<void>((done) => {
    resolve = done;
  });
  cleanup.push(resolve);
  return { promise, resolve };
}
function pool() {
  const value = createPageSnapshotPool();
  pools.push(value);
  return value;
}
function response() {
  const body = deferred();
  const controller = new AbortController();
  return {
    body,
    controller,
    lifetime: {
      signal: controller.signal,
      cleaned: body.promise,
      assertCurrent: vi.fn(),
      invalidate: vi.fn(),
      publishPage: vi.fn(),
    },
  };
}
function snapshot(): NativePageSnapshot {
  return {
    target: '/studio',
    site: {
      displayName: 'Institution',
      defaultLocale: 'en',
      enabledLocales: ['en', 'tr'],
      theme: null,
      showPlatformAttribution: true,
    },
    session: {
      state: 'authenticated',
      displayName: 'Institution',
      expiresAt: 1800000300000,
      limitedByAbsolute: false,
      csrfToken: 'A'.repeat(43),
    },
    scopeKind: 'tenant',
    capabilities: null,
    ownProfile: null,
    profileState: 'missing',
    formKeys: [],
    outcome: { kind: 'ready', view: { kind: 'studio' } },
  };
}
function sized(bytes: number): NativePageSnapshot {
  const value = snapshot();
  const empty = { ...value, site: { ...value.site, displayName: '' } };
  return {
    ...empty,
    site: {
      ...empty.site,
      displayName: 'x'.repeat(bytes - Buffer.byteLength(JSON.stringify(empty))),
    },
  };
}
async function settle() {
  for (let i = 0; i < 8; ++i) await Promise.resolve();
}
afterEach(async () => {
  for (const release of cleanup.splice(0)) release();
  await Promise.all(pools.splice(0).map((value) => value.stop()));
  vi.useRealTimers();
});

describe('native complete snapshot capacity and actual cleanup', () => {
  it('reserves eight slots for the actual owner before work; abort cannot make a ninth slot', async () => {
    const value = pool();
    const owner = {};
    const held = Array.from({ length: 8 }, () => {
      const native = response();
      return { native, ...value.reserve(owner, native.lifetime) };
    });
    expect(value.counts()).toEqual({ active: 8, owners: 1, bytes: 0 });
    const denied = response();
    expect(() => value.reserve(owner, denied.lifetime)).toThrow(
      expect.objectContaining({ reason: 'owner_capacity', status: 429 }),
    );
    held[0]!.lease.abort();
    await settle();
    expect(value.counts().active).toBe(8);
    expect(() => value.reserve(owner, denied.lifetime)).toThrow(SnapshotAdmissionError);
    held[0]!.native.body.resolve();
    await held[0]!.lease.cleaned;
    value.reserve(owner, denied.lifetime);
    expect(value.counts()).toEqual({ active: 8, owners: 1, bytes: 0 });
    // Equal-looking objects are distinct actual owners, never a browser-supplied owner key.
    value.reserve({}, response().lifetime);
    expect(value.counts().owners).toBe(2);
  });

  it('retains 128 complete maximum-size snapshots (32 MiB) and refuses the 129th', async () => {
    const value = pool();
    const owners = Array.from({ length: 16 }, () => ({}));
    const maximum = sized(256 * 1024);
    const held = Array.from({ length: 128 }, (_, i) => {
      const native = response();
      const result = value.reserve(owners[Math.floor(i / 8)]!, native.lifetime);
      result.lease.pin(() => true, 30_000);
      result.lease.publish(maximum);
      return { native, ...result };
    });
    expect(value.counts()).toEqual({ active: 128, owners: 16, bytes: 32 * 1024 * 1024 });
    expect(() => value.reserve({}, response().lifetime)).toThrow(
      expect.objectContaining({ reason: 'global_capacity', status: 503 }),
    );
    held[0]!.lease.abort();
    await settle();
    expect(value.counts().bytes).toBe(32 * 1024 * 1024);
    held[0]!.native.body.resolve();
    await held[0]!.lease.cleaned;
    expect(value.counts()).toEqual({ active: 127, owners: 16, bytes: 127 * 256 * 1024 });
    value.reserve({}, response().lifetime);
    expect(value.counts().active).toBe(128);
  });

  it.each([0, 1])(
    'measures the complete serialized page at 256 KiB + %i, including Site and common fields',
    (extra) => {
      const value = pool();
      const native = response();
      const { lease } = value.reserve({}, native.lifetime);
      lease.pin(() => true, 30_000);
      const input = sized(256 * 1024 + extra);
      expect(Buffer.byteLength(JSON.stringify(input), 'utf8')).toBe(256 * 1024 + extra);
      if (extra) {
        expect(() => lease.publish(input)).toThrow(
          expect.objectContaining({ reason: 'snapshot_size' }),
        );
        expect(lease.signal.aborted).toBe(true);
        expect(native.lifetime.publishPage).not.toHaveBeenCalled();
        expect(value.counts()).toEqual({ active: 1, owners: 1, bytes: 0 });
      } else {
        expect(lease.publish(input)).toEqual(input);
        expect(value.counts().bytes).toBe(256 * 1024);
      }
    },
  );

  it('counts UTF-8 and JSON escaping, rather than character length or only the page-specific data', () => {
    const value = pool();
    const { lease } = value.reserve({}, response().lifetime);
    lease.pin(() => true, 30_000);
    const input = snapshot();
    const large = { ...input, site: { ...input.site, displayName: 'İ\n"'.repeat(50_000) } };
    expect(JSON.stringify(large).length).toBeLessThan(256 * 1024);
    expect(Buffer.byteLength(JSON.stringify(large))).toBeGreaterThan(256 * 1024);
    expect(() => lease.publish(large)).toThrow(
      expect.objectContaining({ reason: 'snapshot_size' }),
    );
  });

  it('applies the same complete-payload bound to native entry rendering', () => {
    const value = pool();
    const input: NativeEntrySnapshot = {
      site: snapshot().site,
      locale: 'en',
      view: { kind: 'entry', entry: null, cursor: null },
    };
    const overhead = Buffer.byteLength(JSON.stringify(input));
    for (const extra of [0, 1]) {
      const { lease } = value.reserve({}, response().lifetime);
      lease.pin(() => true, 30_000);
      const candidate = { ...input, locale: 'en' + 'x'.repeat(256 * 1024 - overhead + extra) };
      if (extra) expect(() => lease.publishEntry(candidate)).toThrow(SnapshotAdmissionError);
      else
        expect(Buffer.byteLength(JSON.stringify(lease.publishEntry(candidate)))).toBe(256 * 1024);
    }
  });

  it('publishes a deep frozen copy without retaining mutable nested source aliases', () => {
    const value = pool();
    const native = response();
    const { lease } = value.reserve({}, native.lifetime);
    lease.pin(() => true, 30_000);
    const input = snapshot();
    const source = {
      ...input,
      formKeys: ['first'],
      site: { ...input.site, enabledLocales: ['en'] },
    };
    const published = lease.publish(source);
    source.formKeys.push('second');
    source.site.enabledLocales[0] = 'tr';
    expect(published.formKeys).toEqual(['first']);
    expect(published.site.enabledLocales).toEqual(['en']);
    for (const part of [
      published,
      published.site,
      published.site.enabledLocales,
      published.formKeys,
    ])
      expect(Object.isFrozen(part)).toBe(true);
    expect(native.lifetime.publishPage).toHaveBeenCalledWith(published);
    expect(() => lease.publish(snapshot())).toThrow(SnapshotAdmissionError);
  });

  it.each(['getter', 'toJSON', 'cycle', 'function', 'nonfinite'] as const)(
    'refuses a non-JSON %s without invoking source code during publication',
    (kind) => {
      const value = pool();
      const { lease } = value.reserve({}, response().lifetime);
      lease.pin(() => true, 30_000);
      const called = vi.fn(() => 'private');
      const input: Record<string, unknown> = { ...snapshot() };
      if (kind === 'getter')
        Object.defineProperty(input, 'extra', { enumerable: true, get: called });
      if (kind === 'toJSON') input.toJSON = called;
      if (kind === 'cycle') input.extra = input;
      if (kind === 'function') input.extra = called;
      if (kind === 'nonfinite') input.extra = Number.NaN;
      expect(() => lease.publish(input as unknown as NativePageSnapshot)).toThrow(
        SnapshotAdmissionError,
      );
      expect(called).not.toHaveBeenCalled();
    },
  );

  it.each(['work-first', 'response-first'] as const)(
    'joins actual API/shared-refresh work and native cleanup in %s order',
    async (order) => {
      const value = pool();
      const native = response();
      const api = deferred();
      const refresh = deferred();
      const { lease, assertOutput } = value.reserve({}, native.lifetime);
      lease.track(api.promise);
      lease.track(refresh.promise);
      lease.pin(() => true, 30_000);
      lease.publish(snapshot());
      const bytes = value.counts().bytes;
      let cleaned = false;
      void lease.cleaned.then(() => {
        cleaned = true;
      });
      native.controller.abort();
      expect(() => assertOutput()).toThrow(SnapshotAdmissionError);
      expect(lease.signal.aborted).toBe(true);
      if (order === 'work-first') {
        api.resolve();
        refresh.resolve();
      } else native.body.resolve();
      await settle();
      expect(cleaned).toBe(false);
      expect(value.counts()).toEqual({ active: 1, owners: 1, bytes });
      if (order === 'work-first') native.body.resolve();
      else {
        api.resolve();
        await settle();
        expect(cleaned).toBe(false);
        refresh.resolve();
      }
      await lease.cleaned;
      expect(value.counts()).toEqual({ active: 0, owners: 0, bytes: 0 });
    },
  );

  it.each(['epoch', 'revision'] as const)(
    'withholds old output after the pinned %s changes',
    (dimension) => {
      const value = pool();
      const native = response();
      let epoch = 1;
      let revision = 1;
      const { lease, assertOutput } = value.reserve({}, native.lifetime);
      lease.pin(() => epoch === 1 && revision === 1, 30_000);
      lease.publish(snapshot());
      assertOutput();
      if (dimension === 'epoch') ++epoch;
      else ++revision;
      expect(() => assertOutput()).toThrow(SnapshotAdmissionError);
      expect(native.lifetime.invalidate).toHaveBeenCalledOnce();
      expect(value.counts().active).toBe(1);
    },
  );

  it('expires the original pin without renewal and retains the slot until cleanup', async () => {
    vi.useFakeTimers();
    const value = pool();
    const { lease, assertOutput } = value.reserve({}, response().lifetime);
    lease.pin(() => true, 1000);
    lease.publish(snapshot());
    vi.advanceTimersByTime(999);
    assertOutput();
    expect(() => lease.pin(() => true, 1000)).toThrow(SnapshotAdmissionError);
    vi.advanceTimersByTime(1);
    expect(() => assertOutput()).toThrow(SnapshotAdmissionError);
    expect(value.counts().active).toBe(1);
    vi.useRealTimers();
    await settle();
  });

  it('stop rejects new reservations and joins nested completion work before releasing credits', async () => {
    const value = pool();
    const native = response();
    const outer = deferred();
    const nested = deferred();
    const { lease } = value.reserve({}, native.lifetime);
    lease.track(outer.promise);
    void outer.promise.then(() => lease.track(nested.promise));
    let stopped = false;
    const stopping = value.stop().then(() => {
      stopped = true;
    });
    expect(() => value.reserve({}, response().lifetime)).toThrow(SnapshotAdmissionError);
    native.body.resolve();
    outer.resolve();
    await settle();
    expect(stopped).toBe(false);
    expect(value.counts().active).toBe(1);
    nested.resolve();
    await stopping;
    expect(value.counts()).toEqual({ active: 0, owners: 0, bytes: 0 });
  });
});
