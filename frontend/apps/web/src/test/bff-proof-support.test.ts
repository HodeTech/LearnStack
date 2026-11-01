// @vitest-environment node
import { ChildProcess, execFileSync, spawn } from 'node:child_process';
import { PassThrough } from 'node:stream';

import { describe, expect, it, vi } from 'vitest';

import { waitForBrowserDevTools } from '../../scripts/admission-browser.mjs';
import {
  assertBffCookieAttributes,
  bffDevToolsFailureStage,
  bffListenerFailureDetail,
  bffProofCopy,
  bffPageKind,
  createBffCookieScanner,
  rememberBffCookies,
  observeBffProofShutdown,
  withProofWaiters,
  waitForBffProofListener,
} from '../../scripts/bff-proof-support.mjs';
import { createFixtureOwner } from '../../scripts/fixture-support.mjs';
import { stopTestChild } from '../../scripts/stop-test-child.mjs';
import en from '../i18n/messages/en/auth.json';
import tr from '../i18n/messages/tr/auth.json';

describe('BFF proof observation ownership', () => {
  it('distinguishes an actual zero-exit leader from inherited stdout/stderr still held by its descendant', async () => {
    expect(process.platform).not.toBe('win32');
    const owner = createFixtureOwner();
    const child = owner.ownChild(
      spawn(
        process.execPath,
        [
          '-e',
          `
      const { spawn } = require('node:child_process');
      const descendant = spawn(process.execPath, ['-e', 'setInterval(() => {}, 1000)'],
        { stdio: ['ignore', 1, 2] });
      descendant.unref();
      process.exit(0);
    `,
        ],
        { detached: process.platform !== 'win32', stdio: ['ignore', 'pipe', 'pipe'] },
      ),
    );
    child.once('error', () => {});
    const completed = new Promise<void>((resolve) => child.once('close', () => resolve()));
    const observation = observeBffProofShutdown(child);
    try {
      await new Promise<void>((resolve, reject) => {
        const timer = setTimeout(() => finish(new Error('Owned leader exit deadline')), 5000);
        const failed = () => finish(new Error('Owned leader spawn failed'));
        const exited = (code: number | null) =>
          finish(code === 0 ? undefined : new Error('Owned leader exit failed'));
        function finish(error?: Error) {
          clearTimeout(timer);
          child.removeListener('error', failed);
          child.removeListener('exit', exited);
          if (error) reject(error);
          else resolve();
        }
        child.once('error', failed);
        child.once('exit', exited);
      });
      expect(observation.snapshot()).toBe('absent,0,0,p,p,p,p,zero,0,open,open');
      expect(await stopTestChild(child)).toEqual({ forced: false });
      await completed;
      expect(observation.snapshot()).toBe('absent,0,0,p,p,p,p,zero,1,closed,closed');
    } finally {
      await owner.dispose();
      await completed;
    }
  }, 10_000);
  it('observes only finite monotonic shutdown joins and snapshots actual exit versus close', () => {
    const child = new ChildProcess();
    const observer = observeBffProofShutdown(child);
    const frame = {
      kind: 'proof-shutdown',
      entered: true,
      watchdog: false,
      listener: 'pending',
      next: 'pending',
      sessions: 'pending',
      requests: 'pending',
      milestone: 'entered',
      sample: [1, 0, 3, 2, 1],
    };
    expect(observer.snapshot()).toBe('absent,0,0,p,p,p,p,live,0,absent,absent');
    child.emit('message', { kind: 'snapshot', private: 'PRIVATE-CANARY' });
    expect(observer.snapshot()).toBe('absent,0,0,p,p,p,p,live,0,absent,absent');
    child.emit('message', frame);
    child.emit('message', {
      ...frame,
      next: 'fulfilled',
      milestone: 'next',
      sample: [0, 1, 256, 0, 1],
    });
    expect((observer as unknown as { sample(): string }).sample()).toBe('valid,next,0,1,256,0,1');
    Object.assign(child, { exitCode: 0 });
    expect(observer.snapshot()).toBe('valid,1,0,p,f,p,p,zero,0,absent,absent');
    child.emit('close', 0, null);
    expect(observer.snapshot()).toBe('valid,1,0,p,f,p,p,zero,1,absent,absent');
    expect(child.listenerCount('message')).toBe(0);
    expect(child.listenerCount('close')).toBe(0);
  });
  it.each(['extra', 'value', 'duplicate', 'regression', 'first', 'overflow'] as const)(
    'retains only invalid for a malformed %s shutdown sequence',
    (kind) => {
      const child = new ChildProcess();
      const observer = observeBffProofShutdown(child);
      const frame = {
        kind: 'proof-shutdown',
        entered: true,
        watchdog: false,
        listener: 'pending',
        next: 'pending',
        sessions: 'pending',
        requests: 'pending',
        milestone: 'entered',
        sample: [0, 0, 0, 0, 0],
      };
      if (kind !== 'first') child.emit('message', frame);
      if (kind === 'extra') child.emit('message', { ...frame, private: 'PRIVATE-CANARY' });
      if (kind === 'value') child.emit('message', { ...frame, next: 'PRIVATE-CANARY' });
      if (kind === 'duplicate') child.emit('message', frame);
      if (kind === 'first') child.emit('message', { ...frame, next: 'fulfilled' });
      if (kind === 'regression') {
        child.emit('message', { ...frame, next: 'fulfilled' });
        child.emit('message', frame);
      }
      if (kind === 'overflow') for (let index = 0; index < 7; ++index) child.emit('message', frame);
      expect(observer.snapshot().startsWith('invalid,')).toBe(true);
      expect(observer.snapshot()).not.toContain('PRIVATE-CANARY');
      child.emit('close', 0, null);
    },
  );
  it('captures watchdog and local stream states without retaining stream or process values', () => {
    const child = new ChildProcess();
    const observer = observeBffProofShutdown(child);
    const frame = {
      kind: 'proof-shutdown',
      entered: true,
      watchdog: false,
      listener: 'pending',
      next: 'pending',
      sessions: 'pending',
      requests: 'pending',
      milestone: 'entered',
      sample: [0, 0, 0, 0, 0],
    };
    child.emit('message', frame);
    child.emit('message', { ...frame, watchdog: true, milestone: 'watchdog' });
    Object.assign(child, {
      signalCode: 'SIGTERM',
      stdout: { readableEnded: true, closed: false },
      stderr: { readableEnded: false, closed: true },
    });
    expect(observer.snapshot()).toBe('valid,1,1,p,p,p,p,signal,0,ended,closed');
    child.emit('close', null, 'SIGTERM');
  });
  it.each([
    { sample: [257, 0, 0, 0, 0] },
    { sample: [0, 'PRIVATE-CANARY', 0, 0, 0] },
    { sample: [0, 0, 0, 0] },
  ])('rejects invalid samples without retaining their contents', ({ sample }) => {
    const child = new ChildProcess();
    const observer = observeBffProofShutdown(child);
    const read = () => (observer as unknown as { sample(): string }).sample();
    expect(read()).toBe('absent');
    child.emit('message', {
      kind: 'proof-shutdown',
      entered: true,
      watchdog: false,
      listener: 'pending',
      next: 'pending',
      sessions: 'pending',
      requests: 'pending',
      milestone: 'entered',
      sample,
    });
    expect(read()).toBe('invalid');
    expect(observer.snapshot()).not.toContain('PRIVATE-CANARY');
    child.emit('close', null, 'SIGTERM');
  });
  it('accepts only the owned child readiness frame and retires every observer', async () => {
    vi.useFakeTimers();
    try {
      const child = new ChildProcess();
      const waiting = waitForBffProofListener(child);
      child.emit('message', { kind: 'proof-listener', port: 49152 });
      expect(await waiting).toBe(49152);
      for (const event of ['message', 'close', 'error']) expect(child.listenerCount(event)).toBe(0);
      expect(vi.getTimerCount()).toBe(0);
    } finally {
      vi.useRealTimers();
    }
  });
  it.each([
    { kind: 'snapshot', port: 49152 },
    { kind: 'proof-listener', port: 49152, extra: true },
    { kind: 'proof-listener', port: 0 },
    { kind: 'proof-listener', port: 65536 },
    { kind: 'proof-listener', port: 1.5 },
    { kind: 'proof-listener', port: '49152' },
  ])('refuses malformed physical listener readiness %j without a fallback', async (frame) => {
    vi.useFakeTimers();
    try {
      const child = new ChildProcess();
      const waiting = waitForBffProofListener(child).catch((error: unknown) => error);
      child.emit('message', frame);
      expect(await waiting).toEqual(new Error('owned listener readiness shape'));
      for (const event of ['message', 'close', 'error']) expect(child.listenerCount(event)).toBe(0);
      expect(vi.getTimerCount()).toBe(0);
    } finally {
      vi.useRealTimers();
    }
  });
  it.each(['close', 'error'] as const)(
    'refuses child %s before readiness and retires observers',
    async (event) => {
      vi.useFakeTimers();
      try {
        const child = new ChildProcess();
        const waiting = waitForBffProofListener(child).catch((error: unknown) => error);
        child.emit(event, new Error('PRIVATE-CANARY'));
        const error = await waiting;
        expect(error).toEqual(
          new Error(
            event === 'close'
              ? 'native listener closed before readiness'
              : 'native listener startup failed',
          ),
        );
        for (const name of ['message', 'close', 'error']) expect(child.listenerCount(name)).toBe(0);
        expect(vi.getTimerCount()).toBe(0);
      } finally {
        vi.useRealTimers();
      }
    },
  );
  it('refuses missing readiness at the existing twenty-second startup deadline', async () => {
    vi.useFakeTimers();
    try {
      const child = new ChildProcess();
      const waiting = waitForBffProofListener(child).catch((error: unknown) => error);
      await vi.advanceTimersByTimeAsync(20_000);
      expect(await waiting).toEqual(new Error('listener startup deadline'));
      for (const event of ['message', 'close', 'error']) expect(child.listenerCount(event)).toBe(0);
      expect(vi.getTimerCount()).toBe(0);
    } finally {
      vi.useRealTimers();
    }
  });
  it.each(['exitCode', 'signalCode'] as const)(
    'rejects an already closed child with %s',
    async (field) => {
      const child = new ChildProcess();
      Object.defineProperty(child, field, { value: field === 'exitCode' ? 0 : 'SIGTERM' });
      await expect(waitForBffProofListener(child)).rejects.toThrow(
        'native listener closed before readiness',
      );
      for (const event of ['message', 'close', 'error']) expect(child.listenerCount(event)).toBe(0);
    },
  );
  it('brands only bounded startup facts and retires stderr observation after a closed child', async () => {
    const child = new ChildProcess();
    const stderr = new PassThrough();
    Object.assign(child, { stderr });
    const waiting = waitForBffProofListener(child).catch((error: unknown) => error);
    stderr.write('Framework admission refused: ow');
    stderr.write('ner_busy\n' + 'PRIVATE-CANARY'.repeat(1000));
    Object.assign(child, { exitCode: 1 });
    child.emit('close', 1, null);
    const error = await waiting;
    const detail = bffListenerFailureDetail(error);
    expect(detail).toMatchObject({
      reason: 'closed',
      exitCode: 1,
      signal: 'none',
      frameworkReason: 'owner_busy',
    });
    expect(detail!.elapsedMilliseconds).toBeGreaterThanOrEqual(0);
    expect(detail!.elapsedMilliseconds).toBeLessThanOrEqual(120000);
    expect(Object.isFrozen(detail)).toBe(true);
    expect(JSON.stringify(detail)).not.toContain('PRIVATE-CANARY');
    expect(stderr.listenerCount('data')).toBe(0);
    expect(bffListenerFailureDetail(Object.assign(new Error('PRIVATE-CANARY'), detail))).toBeNull();
    expect(bffListenerFailureDetail(null)).toBeNull();
    stderr.destroy();
  });
  it('keeps unknown child diagnostic values out of the startup evidence', async () => {
    const child = new ChildProcess();
    const stderr = new PassThrough();
    Object.assign(child, { stderr });
    const waiting = waitForBffProofListener(child).catch((error: unknown) => error);
    stderr.write('Framework admission refused: private_unknown_reason\n');
    child.emit('message', { kind: 'PRIVATE-CANARY' });
    const detail = bffListenerFailureDetail(await waiting);
    expect(detail).toMatchObject({
      reason: 'shape',
      exitCode: null,
      signal: 'none',
      frameworkReason: 'framework-other',
    });
    expect(JSON.stringify(detail)).not.toContain('private_unknown_reason');
    expect(JSON.stringify(detail)).not.toContain('PRIVATE-CANARY');
    expect(stderr.listenerCount('data')).toBe(0);
    stderr.destroy();
  });
  it('retires only its own stderr observer on successful listener admission', async () => {
    const child = new ChildProcess();
    const stderr = new PassThrough();
    Object.assign(child, { stderr });
    let chunks = 0;
    const independent = () => {
      chunks++;
    };
    stderr.on('data', independent);
    const waiting = waitForBffProofListener(child);
    child.emit('message', { kind: 'proof-listener', port: 49152 });
    expect(await waiting).toBe(49152);
    stderr.write('ordinary output');
    expect(chunks).toBe(1);
    expect(stderr.listenerCount('data')).toBe(1);
    stderr.removeListener('data', independent);
    stderr.destroy();
  });
  it.each([
    ['/studio', 'studio'],
    ['/portal', 'portal'],
    ['/studio/members', 'members'],
    ['/studio/roles', 'roles'],
    ['/portal/profile', 'profile'],
    ['/studio/members/019a154b-0000-7000-8000-000000000012', 'member'],
    ['/studio/roles/019a154b-0000-7000-8000-000000000013', 'role'],
  ])('selects only the closed page kind for %s', (path, kind) => {
    expect(bffPageKind(path)).toBe(kind);
  });
  it.each([
    '/',
    '/api/account/profile',
    '/api/auth/entry',
    '/en/courses',
    '/studio/settings',
    '/studio/members/',
    '/studio/roles/',
    '/studio/members?cursor=AA',
    '/portal/profile/child',
    '__proto__',
    '/studio/members/019a154b-0000-4000-8000-000000000012',
    '/studio/roles/019A154B-0000-7000-8000-000000000013',
    'https://other.invalid/studio',
    null,
    undefined,
  ])('refuses an unowned page selector %s', (path) => {
    expect(bffPageKind(path)).toBeNull();
  });
  it('retains actual branded readiness observations without exporting forged or private error fields', async () => {
    vi.useFakeTimers();
    try {
      for (const kind of ['missing', 'empty', 'malformed'] as const) {
        const waiting = waitForBrowserDevTools({
          assertActive() {},
          isLive: () => true,
          readPort: () => ({ kind }),
          timeout: 1,
        }).catch((error: unknown) => error);
        await vi.runAllTimersAsync();
        expect(bffDevToolsFailureStage(await waiting)).toBe('browser-devtools-' + kind);
      }
      for (const [options, expected] of [
        [
          {
            assertActive() {
              throw new Error('PRIVATE-CANARY');
            },
          },
          'browser-devtools-inactive',
        ],
        [{ isLive: () => false }, 'browser-not-live'],
        [
          {
            readPort() {
              throw new Error('PRIVATE-CANARY');
            },
          },
          'browser-devtools-unreadable',
        ],
      ] as const) {
        const settings = {
          assertActive() {},
          isLive: () => true,
          readPort: () => ({ kind: 'missing' as const }),
          timeout: 1,
        };
        Object.assign(settings, options);
        const error = await waitForBrowserDevTools(settings).catch((failure: unknown) => failure);
        expect(bffDevToolsFailureStage(error)).toBe(expected);
      }
      expect(
        bffDevToolsFailureStage(
          Object.assign(new Error('PRIVATE-CANARY'), {
            admissionStage: 'browser-devtools',
          }),
        ),
      ).toBe('browser-devtools');
      expect(bffDevToolsFailureStage(null)).toBe('browser-devtools');
    } finally {
      vi.useRealTimers();
    }
  });
  it.each(['stdout', 'stderr'])(
    'the actual owned-child %s observer catches cookies learned after the child settles',
    (stream) => {
      const hostUrl = new URL('../../scripts/bff-proof-host.mjs', import.meta.url).href;
      const fixtureUrl = new URL('../../scripts/fixture-support.mjs', import.meta.url).href;
      const supportUrl = new URL('../../scripts/bff-proof-support.mjs', import.meta.url).href;
      const program = `
        import assert from 'node:assert/strict';
        import { randomBytes } from 'node:crypto';
        import { mkdtempSync } from 'node:fs';
        import { tmpdir } from 'node:os';
        import { join } from 'node:path';
        const { ownBffProofChild } = await import(process.argv[1]);
        const { createFixtureOwner, fixtureEnvironment } = await import(process.argv[2]);
        const { rememberBffCookies, assertBffPrivateOutput } = await import(process.argv[3]);
        const owner = createFixtureOwner();
        const root = owner.ownRoot(mkdtempSync(join(tmpdir(), 'learnstack-bff-observer-')));
        try {
          for (const leaking of [false, true]) {
            // A valid opaque cookie may begin with a Node option prefix.
            const cookie = '-' + randomBytes(32).toString('base64url').slice(1);
            const cookieCanaries = [];
            const scanners = [];
            const child = ownBffProofChild({ owner, app: root, env: fixtureEnvironment({ root }),
              args: ['-e', 'process.${stream}.write(process.argv[1]);', '--', leaking ? cookie : 'clean child output'],
              privateValues: [], cookieCanaries, scanners });
            assert.deepEqual(await child.completed, { code: 0, signal: null });
            assert.doesNotThrow(() => assertBffPrivateOutput(scanners));
            rememberBffCookies(['__Host-ls-session=' + cookie], cookieCanaries);
            if (leaking) assert.throws(() => assertBffPrivateOutput(scanners), /BFF proof private output/);
            else assert.doesNotThrow(() => assertBffPrivateOutput(scanners));
          }
        } finally { await owner.dispose(); }
      `;
      expect(
        execFileSync(
          process.execPath,
          ['--input-type=module', '-e', program, hostUrl, fixtureUrl, supportUrl],
          {
            encoding: 'utf8',
            timeout: 5000,
          },
        ),
      ).toBe('');
    },
  );
  it('checks literal raw cookie attributes independently of browser SameSite defaults', () => {
    const value = 'A'.repeat(43);
    const header =
      '__Host-ls-session=' + value + '; Path=/; Max-Age=28800; HttpOnly; Secure; SameSite=Lax';
    expect(() => assertBffCookieAttributes([header])).not.toThrow();
    expect(() =>
      assertBffCookieAttributes([header.replace(value, '').replace('28800', '0')]),
    ).not.toThrow();
    for (const invalid of [
      header.replace('; SameSite=Lax', ''),
      header.replace('SameSite=Lax', 'SameSite=None'),
      header.replace('; Secure', ''),
      header.replace('; HttpOnly', ''),
      header.replace('Path=/', 'Path=/nested'),
      header + '; Domain=example.test',
    ])
      expect(() => assertBffCookieAttributes([invalid])).toThrow();
    expect(() => assertBffCookieAttributes(['ordinary=public'])).not.toThrow();
  });
  it('uses the chosen committed locale and rejects unknown copy instead of falling back', () => {
    for (const [locale, catalogue] of [
      ['en', en],
      ['tr', tr],
      ['tr-TR', tr],
    ] as const) {
      expect(bffProofCopy(locale, 'signin.continue')).toBe(catalogue.signin.continue);
      expect(bffProofCopy(locale, 'shell.studio')).toBe(catalogue.shell.studio);
      expect(bffProofCopy(locale, 'shell.account')).toBe(catalogue.shell.account);
      expect(bffProofCopy(locale, 'shell.portal')).toBe(catalogue.shell.portal);
      expect(bffProofCopy(locale, 'session.extended')).toBe(catalogue.session.extended);
      expect(bffProofCopy(locale, 'entry.confirm')).toBe(catalogue.entry.confirm);
      expect(bffProofCopy(locale, 'session.javascript_required')).toBe(
        catalogue.session.javascript_required,
      );
    }
    expect(bffProofCopy('en', 'signin.continue')).not.toBe(bffProofCopy('tr', 'signin.continue'));
    expect(bffProofCopy('en', 'session.extended')).not.toBe(bffProofCopy('tr', 'session.extended'));
    expect(() => bffProofCopy('de', 'signin.continue')).toThrow('closed proof locale');
    expect(() => bffProofCopy('en', 'missing.copy')).toThrow('closed proof copy');
  });
  it('does not leave a late rejected waiter unhandled when the browser action fails first', () => {
    const moduleUrl = new URL('../../scripts/bff-proof-support.mjs', import.meta.url).href;
    const program = `
      import assert from 'node:assert/strict';
      const { withProofWaiters } = await import(process.argv[1]);
      const first = new Error('private first failure');
      let fail;
      const pending = new Promise((_, reject) => { fail = reject; });
      assert.equal(await withProofWaiters([pending], () => { throw first; }).catch(e => e), first);
      fail(new Error('private late waiter failure'));
      await new Promise(resolve => setImmediate(resolve));
    `;
    expect(
      execFileSync(
        process.execPath,
        ['--unhandled-rejections=strict', '--input-type=module', '-e', program, moduleUrl],
        {
          encoding: 'utf8',
          timeout: 5000,
        },
      ),
    ).toBe('');
  });

  it('preserves an observation failure and returns actual successful waiter results', async () => {
    const first = new Error('private observation failure');
    await expect(withProofWaiters([Promise.reject(first)], () => undefined)).rejects.toBe(first);
    await expect(
      withProofWaiters([Promise.resolve(3), Promise.resolve('seen')], () => undefined),
    ).resolves.toEqual([3, 'seen']);
  });

  it('learns session, browser, CSRF and transaction secrets for existing output scanners', () => {
    const canaries: string[] = [];
    const scanner = createBffCookieScanner(canaries);
    const values = ['A', 'B', 'C', 'D', 'E', 'F'].map((value) => value.repeat(43));
    rememberBffCookies(
      [
        '__Host-ls-session=' + values[0] + '; HttpOnly; Secure',
        '__Host-ls-browser=' + values[1] + '; HttpOnly; Secure',
        '__Host-ls-csrf=' + values[2] + '; HttpOnly; Secure',
        '__Host-ls-entry=' + values[5] + '; HttpOnly; Secure',
        '__Host-ls-login-' + values[3] + '=' + values[4] + '; HttpOnly; Secure',
        '__Host-ls-session=; Max-Age=0',
        'ordinary=public',
      ],
      canaries,
    );
    expect(new Set(canaries)).toEqual(new Set(values));
    for (const value of values) expect(scanner.contains(value)).toBe(true);
    scanner.push(values[2]!.slice(0, 20));
    scanner.push(values[2]!.slice(20));
    scanner.finish();
    expect(scanner.leaked).toBe(true);
    rememberBffCookies(['__Host-ls-session=' + values[0]], canaries);
    expect(canaries).toHaveLength(6);
  });

  it('rechecks output emitted before the response disclosed its cookie, beyond the old rolling window', () => {
    const canaries: string[] = [];
    const scanner = createBffCookieScanner(canaries);
    const value = 'C'.repeat(43);
    scanner.push(value + '\n');
    scanner.push('x'.repeat(70_000));
    scanner.finish();
    expect(scanner.leaked).toBe(false);
    rememberBffCookies(['__Host-ls-session=' + value], canaries);
    expect(scanner.leaked).toBe(true);
    expect(scanner.tail).toContain(value);
  });

  it('refuses output overflow rather than claiming it scanned discarded bytes', () => {
    const scanner = createBffCookieScanner([]);
    scanner.push(Buffer.alloc(256 * 1024, 'x'));
    expect(scanner.leaked).toBe(false);
    scanner.push('x');
    scanner.finish();
    expect(scanner.leaked).toBe(true);
    expect(Buffer.byteLength(scanner.tail)).toBe(256 * 1024);
  });
});
