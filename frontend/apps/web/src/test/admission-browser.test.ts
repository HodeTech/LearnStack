// @vitest-environment node
import { afterEach, describe, expect, it, vi } from 'vitest';

import {
  assertFlightFallback,
  atBrowserPhase,
  browserFailureDetail,
  verifyBrowserFallback,
  waitForBrowserDevTools,
  withBrowserCleanup,
} from '../../scripts/admission-browser.mjs';
import { createFixtureOwner } from '../../scripts/fixture-support.mjs';

const origin = 'https://fixture.localhost:3000';
const target = '/en/courses/fixture-course';
const flight = {
  url: origin + target + '?_rsc=fixture',
  type: 'Fetch',
  rsc: true,
  status: 503,
};
const document = { url: origin + target, type: 'Document', rsc: false, status: 200 };

afterEach(() => vi.useRealTimers());

describe('stock browser Flight fallback observation', () => {
  it('exposes only a closed phase when a private browser operation fails', async () => {
    const privateValue = 'private URL, envelope and browser output';
    const failure = await atBrowserPhase('browser-hydration', () => {
      throw new Error(privateValue);
    }).catch((error: Error) => error);
    expect(browserFailureDetail(failure)).toBe(' browser-hydration');
    expect(String(failure)).not.toContain(privateValue);
    expect(failure).not.toHaveProperty('cause');
    expect(browserFailureDetail({ admissionStage: privateValue })).toBe('');
    expect(browserFailureDetail(new Error(privateValue))).toBe('');
    expect(browserFailureDetail(null)).toBe('');
  });

  it('preserves successful browser operations and rejects unregistered phases', async () => {
    await expect(atBrowserPhase('browser-wire', () => 42)).resolves.toBe(42);
    // @ts-expect-error Exercise the runtime guard for an untyped caller.
    await expect(atBrowserPhase('unregistered', () => 42)).rejects.toThrow(
      'Browser diagnostic phase is registered',
    );
  });

  it('reports browser death before reading a debugging port as a launch failure', async () => {
    const readPort = vi.fn(() => 12345);
    const failure = await waitForBrowserDevTools({
      assertActive: () => undefined,
      isLive: () => false,
      readPort,
    }).catch((error: Error) => error);
    expect(browserFailureDetail(failure)).toBe(' browser-launch');
    expect(readPort).not.toHaveBeenCalled();
  });

  it('reports a live browser without a debugging port at the unchanged readiness deadline', async () => {
    vi.useFakeTimers();
    const waiting = waitForBrowserDevTools({
      assertActive: () => undefined,
      isLive: () => true,
      readPort: () => null,
    }).catch((error: Error) => error);
    await vi.advanceTimersByTimeAsync(20_000);
    expect(browserFailureDetail(await waiting)).toBe(' browser-devtools');
  });

  it('accepts the debugging port of a live owned browser', async () => {
    const assertActive = vi.fn();
    await expect(
      waitForBrowserDevTools({ assertActive, isLive: () => true, readPort: () => 12345 }),
    ).resolves.toBe(12345);
    expect(assertActive).toHaveBeenCalledOnce();
  });

  it('preserves a sanitized inner phase without trusting a raw error with the same token', async () => {
    const failure = await atBrowserPhase('browser-private-carrier', () => {
      throw new Error('Private browser output');
    }).catch((error: Error) => error);
    await expect(
      atBrowserPhase('browser-shutdown', () => {
        throw failure;
      }),
    ).rejects.toBe(failure);
    const forged = Object.assign(new Error('Private browser output'), {
      admissionStage: 'browser-private-carrier',
    });
    const sanitized = await atBrowserPhase('browser-shutdown', () => {
      throw forged;
    }).catch((error: Error) => error);
    expect(browserFailureDetail(sanitized)).toBe(' browser-shutdown');
    expect(String(sanitized)).not.toContain('Private browser output');
  });

  it('keeps a private-carrier failure distinct when cleanup is the sole failure', async () => {
    const failure = await withBrowserCleanup(
      () => 42,
      () =>
        atBrowserPhase('browser-private-carrier', () => {
          throw new Error('Private browser shutdown output');
        }),
    ).catch((error: Error) => error);
    expect(browserFailureDetail(failure)).toBe(' browser-private-carrier');
    expect(String(failure)).not.toContain('Private browser shutdown output');
    expect(failure).not.toHaveProperty('cause');
  });

  it.each([false, true])(
    'trusts only branded failures at the outer boundary (branded=%s)',
    async (branded) => {
      const raw = Object.assign(new Error('Private browser setup value'), {
        admissionStage: 'browser-wire',
      });
      const inner = branded
        ? await atBrowserPhase('browser-wire', () => {
            throw raw;
          }).catch((error: Error) => error)
        : raw;
      const owner = createFixtureOwner();
      try {
        const result = await verifyBrowserFallback({
          owner,
          app: '',
          root: '',
          certificate: '',
          get origin(): string {
            throw inner;
          },
          initialPath: '',
          target,
          status: 503,
          beforeNavigation: async () => undefined,
          afterNavigation: async () => undefined,
          secrets: [],
        }).catch((error: Error) => error);
        expect(browserFailureDetail(result)).toBe(branded ? ' browser-wire' : ' browser-launch');
        expect(String(result)).not.toContain('Private browser setup value');
        expect(result).not.toHaveProperty('cause');
        if (branded) expect(result).toBe(inner);
        else expect(result).not.toBe(inner);
      } finally {
        await owner.dispose();
      }
    },
  );

  it('keeps the first proof failure when cleanup also detects a private carrier', async () => {
    const first = await atBrowserPhase('browser-wire', () => {
      throw new Error('Private wire failure');
    }).catch((error: Error) => error);
    await expect(
      withBrowserCleanup(
        () => {
          throw first;
        },
        () =>
          atBrowserPhase('browser-private-carrier', () => {
            throw new Error('Private browser shutdown output');
          }),
      ),
    ).rejects.toBe(first);
  });

  it('runs cleanup and preserves the first proof failure if both operations fail', async () => {
    const first = new Error('Private primary failure');
    let cleaned = false;
    const result = withBrowserCleanup(
      () => {
        throw first;
      },
      () => {
        cleaned = true;
        throw new Error('Private cleanup failure');
      },
    );
    await expect(result).rejects.toBe(first);
    expect(cleaned).toBe(true);
  });

  it('reports only the shutdown phase when cleanup is the sole failure', async () => {
    const failure = await withBrowserCleanup(
      () => 42,
      () => {
        throw new Error('Private cleanup failure');
      },
    ).catch((error: Error) => error);
    expect(browserFailureDetail(failure)).toBe(' browser-shutdown');
    expect(String(failure)).not.toContain('Private cleanup failure');
    await expect(
      withBrowserCleanup(
        () => 42,
        () => undefined,
      ),
    ).resolves.toBe(42);
  });

  it.each([429, 503])('accepts one refused Flight then recovered document: %s', (status) => {
    expect(() =>
      assertFlightFallback(
        [
          { url: origin + '/_next/static/fixture.js', type: 'Script', rsc: false, status: 200 },
          { ...flight, status },
          document,
        ],
        origin,
        target,
        status,
      ),
    ).not.toThrow();
  });

  it.each([
    ['direct document only', [document]],
    ['failed Flight only', [flight]],
    ['reversed order', [document, flight]],
    ['extra Flight', [flight, flight, document]],
    ['Flight without RSC', [{ ...flight, rsc: false }, document]],
    ['Flight misclassified as document', [{ ...flight, type: 'Document' }, document]],
    ['wrong refusal status', [{ ...flight, status: 200 }, document]],
    ['failed recovered document', [flight, { ...document, status: 503 }]],
    ['document still marked RSC', [flight, { ...document, rsc: true }]],
    ['different target', [{ ...flight, url: origin + '/en/courses/other' }, document]],
    ['opposite origin', [{ ...flight, url: 'https://other.localhost:3000' + target }, document]],
    ['response not observed', [{ ...flight, status: null }, document]],
  ])('rejects a broken navigation proof: %s', (_name, events) => {
    expect(() => assertFlightFallback(events, origin, target, 503)).toThrow();
  });
});
