// @vitest-environment node
import { mkdtempSync, mkdirSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { afterEach, describe, expect, it, vi } from 'vitest';

import {
  assertFlightFallback,
  atBrowserPhase,
  browserFailureDetail,
  readBrowserDevToolsPort,
  verifyBrowserFallback,
  waitForBrowserDevTools,
  waitForBrowserPage,
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
const browserEndpoint = 'ws://127.0.0.1:12345/devtools/page/owned';
const browserPages = [{ type: 'page', webSocketDebuggerUrl: browserEndpoint }];

const roots: string[] = [];
afterEach(() => {
  vi.useRealTimers();
  for (const root of roots.splice(0)) rmSync(root, { recursive: true, force: true });
});

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
    expect(
      browserFailureDetail({ admissionStage: 'browser-devtools', kind: 'deadline-missing' }),
    ).toBe('');
  });

  it('preserves successful browser operations and rejects unregistered phases', async () => {
    await expect(atBrowserPhase('browser-wire', () => 42)).resolves.toBe(42);
    // @ts-expect-error Exercise the runtime guard for an untyped caller.
    await expect(atBrowserPhase('unregistered', () => 42)).rejects.toThrow(
      'Browser diagnostic phase is registered',
    );
  });

  it('reports browser death before reading a debugging port as a launch failure', async () => {
    const readPort = vi.fn(() => ({ kind: 'ready' as const, port: 12345 }));
    const failure = await waitForBrowserDevTools({
      assertActive: () => undefined,
      isLive: () => false,
      readPort,
    }).catch((error: Error) => error);
    expect(browserFailureDetail(failure)).toBe(' browser-launch');
    expect(readPort).not.toHaveBeenCalled();
  });

  it.each(['missing', 'empty', 'malformed'] as const)(
    'reports a live browser with %s readiness at the unchanged deadline',
    async (kind) => {
      vi.useFakeTimers();
      const waiting = waitForBrowserDevTools({
        assertActive: () => undefined,
        isLive: () => true,
        readPort: () => ({ kind }),
      }).catch((error: Error) => error);
      await vi.advanceTimersByTimeAsync(20_000);
      expect(browserFailureDetail(await waiting)).toBe(' browser-devtools deadline-' + kind);
    },
  );

  it('accepts the debugging port of a live owned browser', async () => {
    const assertActive = vi.fn();
    await expect(
      waitForBrowserDevTools({
        assertActive,
        isLive: () => true,
        readPort: () => ({ kind: 'ready', port: 12345 }),
      }),
    ).resolves.toBe(12345);
    expect(assertActive).toHaveBeenCalledOnce();
  });

  it('reads actual readiness files with closed observations and no check/read race', () => {
    const root = mkdtempSync(join(tmpdir(), 'learnstack-admission-port-'));
    roots.push(root);
    const file = join(root, 'DevToolsActivePort');
    expect(readBrowserDevToolsPort(file)).toEqual({ kind: 'missing' });
    for (const value of ['', '\n/devtools/private-canary']) {
      writeFileSync(file, value);
      expect(readBrowserDevToolsPort(file)).toEqual({ kind: 'empty' });
    }
    for (const value of [
      'PRIVATE-CANARY',
      '65536\n/devtools/private-canary',
      '0x1234',
      '12345junk',
    ]) {
      writeFileSync(file, value);
      expect(readBrowserDevToolsPort(file)).toEqual({ kind: 'malformed' });
    }
    writeFileSync(file, '12345\n/devtools/private-canary');
    expect(readBrowserDevToolsPort(file)).toEqual({ kind: 'ready', port: 12345 });
    rmSync(file);
    expect(readBrowserDevToolsPort(file)).toEqual({ kind: 'missing' });
    mkdirSync(file);
    expect(readBrowserDevToolsPort(file)).toEqual({ kind: 'unreadable' });
  });

  it('waits through partial file publication until the live browser is ready', async () => {
    vi.useFakeTimers();
    const readPort = vi
      .fn()
      .mockReturnValueOnce({ kind: 'missing' })
      .mockReturnValueOnce({ kind: 'empty' })
      .mockReturnValueOnce({ kind: 'malformed' })
      .mockReturnValue({ kind: 'ready', port: 12345 });
    const waiting = waitForBrowserDevTools({
      assertActive: () => undefined,
      isLive: () => true,
      readPort,
    });
    await vi.advanceTimersByTimeAsync(150);
    await expect(waiting).resolves.toBe(12345);
    expect(readPort).toHaveBeenCalledTimes(4);
  });

  it.each(['owner-inactive', 'port-unreadable'] as const)(
    'keeps %s failures closed without private error text',
    async (kind) => {
      const refuse = () => {
        throw new Error('PRIVATE-CANARY');
      };
      const failure = await waitForBrowserDevTools({
        assertActive: kind === 'owner-inactive' ? refuse : () => undefined,
        isLive: () => true,
        readPort: kind === 'port-unreadable' ? refuse : () => ({ kind: 'missing' }),
      }).catch((error: Error) => error);
      expect(browserFailureDetail(failure)).toBe(' browser-devtools ' + kind);
      expect(String(failure)).not.toContain('PRIVATE-CANARY');
      expect(failure).not.toHaveProperty('cause');
    },
  );

  it('observes browser death at the deadline boundary without reporting a live timeout', async () => {
    vi.useFakeTimers();
    vi.setSystemTime(0);
    const waiting = waitForBrowserDevTools({
      assertActive: () => undefined,
      isLive: () => Date.now() < 20_000,
      readPort: () => ({ kind: 'missing' }),
    }).catch((error: Error) => error);
    await vi.advanceTimersByTimeAsync(20_000);
    expect(browserFailureDetail(await waiting)).toBe(' browser-launch');
  });

  it('waits for an initially empty target list to expose the owned page', async () => {
    vi.useFakeTimers();
    const fetch = vi
      .spyOn(globalThis, 'fetch')
      .mockResolvedValueOnce(new Response('[]'))
      .mockResolvedValueOnce(new Response(JSON.stringify(browserPages)));
    const waiting = waitForBrowserPage({
      assertActive: () => undefined,
      isLive: () => true,
      port: 12345,
    }).catch((error: Error) => error);
    await vi.advanceTimersByTimeAsync(50);
    expect(await waiting).toEqual(new URL(browserEndpoint));
    expect(fetch).toHaveBeenCalledTimes(2);
    expect(fetch.mock.calls[0]?.[1]?.signal).toBe(fetch.mock.calls[1]?.[1]?.signal);
    expect(vi.getTimerCount()).toBe(0);
  });

  it('refuses a perpetually empty target list at the original five-second deadline', async () => {
    vi.useFakeTimers();
    const fetch = vi.spyOn(globalThis, 'fetch').mockImplementation(async () => new Response('[]'));
    const complete = vi.fn();
    const waiting = waitForBrowserPage({
      assertActive: () => undefined,
      isLive: () => true,
      port: 12345,
    })
      .catch((error: Error) => error)
      .then((result) => {
        complete();
        return result;
      });
    await vi.advanceTimersByTimeAsync(4999);
    expect(complete).not.toHaveBeenCalled();
    await vi.advanceTimersByTimeAsync(1);
    expect(browserFailureDetail(await waiting)).toBe(' browser-discovery');
    expect(fetch.mock.calls.length).toBeGreaterThan(1);
    expect(fetch.mock.calls.every((call) => call[1]?.signal?.aborted)).toBe(true);
    expect(vi.getTimerCount()).toBe(0);
  });

  it.each(['fetch', 'body'])('does not reset discovery time for a late %s', async (stage) => {
    vi.useFakeTimers();
    const response = new Response(JSON.stringify(browserPages));
    if (stage === 'body') {
      vi.spyOn(response, 'json').mockImplementation(
        () => new Promise((resolve) => setTimeout(() => resolve(browserPages), 4975)),
      );
    }
    const fetch = vi
      .spyOn(globalThis, 'fetch')
      .mockResolvedValueOnce(new Response('[]'))
      .mockImplementationOnce(() =>
        stage === 'fetch'
          ? new Promise((resolve) => setTimeout(() => resolve(response), 4975))
          : Promise.resolve(response),
      );
    const complete = vi.fn();
    const waiting = waitForBrowserPage({
      assertActive: () => undefined,
      isLive: () => true,
      port: 12345,
    })
      .catch((error: Error) => error)
      .then((result) => {
        complete();
        return result;
      });
    await vi.advanceTimersByTimeAsync(4999);
    expect(complete).not.toHaveBeenCalled();
    await vi.advanceTimersByTimeAsync(1);
    expect(browserFailureDetail(await waiting)).toBe(' browser-discovery');
    expect(fetch.mock.calls[0]?.[1]?.signal).toBe(fetch.mock.calls[1]?.[1]?.signal);
    expect(fetch.mock.calls[1]?.[1]?.signal?.aborted).toBe(true);
    await vi.advanceTimersByTimeAsync(25);
    expect(fetch).toHaveBeenCalledTimes(2);
    expect(vi.getTimerCount()).toBe(0);
  });

  it.each([
    ['invalid shape', '{}', 200],
    ['malformed JSON', '{', 200],
    ['failed HTTP', '[]', 503],
    ['non-page targets', JSON.stringify([{ type: 'worker' }]), 200],
    ['invalid page endpoint', JSON.stringify([{ type: 'page' }]), 200],
    [
      'foreign host',
      JSON.stringify([{ type: 'page', webSocketDebuggerUrl: 'ws://foreign.invalid:12345/page' }]),
      200,
    ],
    [
      'foreign port',
      JSON.stringify([{ type: 'page', webSocketDebuggerUrl: 'ws://127.0.0.1:12346/page' }]),
      200,
    ],
    [
      'invalid protocol',
      JSON.stringify([{ type: 'page', webSocketDebuggerUrl: 'https://127.0.0.1:12345/page' }]),
      200,
    ],
  ] as const)('refuses %s without retrying discovery', async (_name, body, status) => {
    const fetch = vi.spyOn(globalThis, 'fetch').mockResolvedValue(new Response(body, { status }));
    const failure = await waitForBrowserPage({
      assertActive: () => undefined,
      isLive: () => true,
      port: 12345,
    }).catch((error: Error) => error);
    expect(browserFailureDetail(failure)).toBe(' browser-discovery');
    expect(fetch).toHaveBeenCalledOnce();
    expect(failure).not.toHaveProperty('cause');
    expect(String(failure)).not.toContain(body);
  });

  it('sanitizes a raw network error immediately without trusting its phase token', async () => {
    const fetch = vi.spyOn(globalThis, 'fetch').mockRejectedValue(
      Object.assign(new Error('Private browser connection value'), {
        admissionStage: 'browser-launch',
      }),
    );
    const failure = await waitForBrowserPage({
      assertActive: () => undefined,
      isLive: () => true,
      port: 12345,
    }).catch((error: Error) => error);
    expect(browserFailureDetail(failure)).toBe(' browser-discovery');
    expect(fetch).toHaveBeenCalledOnce();
    expect(String(failure)).not.toContain('Private browser connection value');
    expect(failure).not.toHaveProperty('cause');
  });

  it.each(['before request', 'after fetch', 'after body'])(
    'keeps browser death %s distinct from discovery readiness',
    async (stage) => {
      let live = stage !== 'before request';
      const response = new Response(JSON.stringify(browserPages));
      const body = vi.spyOn(response, 'json').mockImplementation(async () => {
        if (stage === 'after body') live = false;
        return browserPages;
      });
      const fetch = vi.spyOn(globalThis, 'fetch').mockImplementation(async () => {
        if (stage === 'after fetch') live = false;
        return response;
      });
      const failure = await waitForBrowserPage({
        assertActive: () => undefined,
        isLive: () => live,
        port: 12345,
      }).catch((error: Error) => error);
      expect(browserFailureDetail(failure)).toBe(' browser-launch');
      expect(fetch).toHaveBeenCalledTimes(stage === 'before request' ? 0 : 1);
      expect(body).toHaveBeenCalledTimes(stage === 'after body' ? 1 : 0);
    },
  );

  it.each(['before request', 'after body'])('refuses a closed owner %s', async (stage) => {
    const owner = createFixtureOwner();
    try {
      if (stage === 'before request') await owner.dispose();
      const response = new Response(JSON.stringify(browserPages));
      vi.spyOn(response, 'json').mockImplementation(async () => {
        await owner.dispose();
        return browserPages;
      });
      const fetch = vi.spyOn(globalThis, 'fetch').mockResolvedValue(response);
      const failure = await waitForBrowserPage({
        assertActive: () => owner.assertActive(),
        isLive: () => true,
        port: 12345,
      }).catch((error: Error) => error);
      expect(browserFailureDetail(failure)).toBe(' browser-discovery');
      expect(fetch).toHaveBeenCalledTimes(stage === 'before request' ? 0 : 1);
      expect(String(failure)).not.toContain('Fixture owner is closed');
    } finally {
      await owner.dispose();
    }
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
