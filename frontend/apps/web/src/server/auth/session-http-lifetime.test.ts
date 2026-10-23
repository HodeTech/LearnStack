// @vitest-environment node
import { afterEach, describe, expect, it, vi } from 'vitest';

import { bffFixture, bffHost, bffSource } from '../../test/bff-fixture';

const fixtures: ReturnType<typeof bffFixture>[] = [];
const releases: (() => void)[] = [];
function fixture() {
  const f = bffFixture();
  fixtures.push(f);
  return f;
}
function gate() {
  let release!: () => void;
  const promise = new Promise<void>((yes) => {
    release = yes;
  });
  releases.push(release);
  return { promise, release };
}
afterEach(async () => {
  for (const release of releases.splice(0)) release();
  await Promise.all(fixtures.splice(0).map((f) => f.store.stop()));
});
describe('exclusive login work follows the response lifetime', () => {
  it('aborted discovery retains pending ownership until actual work settles and never publishes a transaction', async () => {
    const f = fixture();
    const held = gate();
    const original = f.provider.begin.getMockImplementation()!;
    f.provider.begin.mockImplementationOnce(async (uri) => {
      await held.promise;
      return original(uri);
    });
    const controller = new AbortController();
    const pending = f.store.start({
      host: bffHost,
      source: bffSource,
      returnPath: '/',
      signal: controller.signal,
    });
    const refused = expect(pending).rejects.toMatchObject({ reason: 'cancelled' });
    await vi.waitFor(() => expect(f.provider.begin).toHaveBeenCalledTimes(1));
    controller.abort();
    expect(f.store.counts()).toMatchObject({ owners: 1, pending: 1 });
    held.release();
    await refused;
    expect(f.store.counts().owners).toBe(0);
  });
  it.each(['redeem', 'account'] as const)(
    'aborted %s retains actual work and cannot publish late credentials',
    async (phase) => {
      const f = fixture();
      const held = gate();
      const controller = new AbortController();
      const started = await f.store.start({ host: bffHost, source: bffSource, returnPath: '/' });
      if (phase === 'redeem') {
        const original = f.provider.redeem.getMockImplementation()!;
        f.provider.redeem.mockImplementationOnce(async () => {
          await held.promise;
          return original();
        });
      } else {
        const original = f.account.getMockImplementation()!;
        f.account.mockImplementationOnce(async () => {
          await held.promise;
          return original();
        });
      }
      const pending = f.store.complete({
        host: bffHost,
        source: bffSource,
        browser: started.browser,
        state: started.state,
        proof: started.proof,
        responseUrl:
          'https://' +
          bffHost +
          '/api/auth/callback/keycloak?state=' +
          started.state +
          '&code=owned',
        signal: controller.signal,
      });
      const refused = expect(pending).rejects.toMatchObject({
        reason: 'cancelled',
        clearTransaction: started.state,
      });
      await vi.waitFor(() =>
        expect(phase === 'redeem' ? f.provider.redeem : f.account).toHaveBeenCalledTimes(1),
      );
      controller.abort();
      expect(f.store.counts().owners).toBe(1);
      held.release();
      await refused;
      expect(f.store.counts().owners).toBe(0);
    },
  );
  it('carries only the current callback/read trace to AccountSite, never a stored login request trace', async () => {
    const f = fixture();
    const started = await f.store.start({ host: bffHost, source: bffSource, returnPath: '/' });
    const finished = await f.store.complete({
      host: bffHost,
      source: '203.0.113.2',
      traceparent: 'callback-trace',
      browser: started.browser,
      state: started.state,
      proof: started.proof,
      responseUrl:
        'https://' + bffHost + '/api/auth/callback/keycloak?state=' + started.state + '&code=owned',
    });
    expect(f.account).toHaveBeenLastCalledWith(
      'private-access-canary',
      { host: bffHost, source: '203.0.113.2', traceparent: 'callback-trace' },
      expect.any(AbortSignal),
    );
    await f.store.admit(
      {
        host: bffHost,
        browser: finished.browser,
        handle: finished.handle,
        traceparent: 'read-trace',
      },
      '203.0.113.3',
      'read',
    );
    expect(f.account).toHaveBeenLastCalledWith(
      'private-access-canary',
      { host: bffHost, source: '203.0.113.3', traceparent: 'read-trace' },
      expect.any(AbortSignal),
    );
  });
});
