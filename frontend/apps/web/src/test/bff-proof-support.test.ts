// @vitest-environment node
import { execFileSync } from 'node:child_process';

import { describe, expect, it, vi } from 'vitest';

import { waitForBrowserDevTools } from '../../scripts/admission-browser.mjs';
import {
  assertBffCookieAttributes,
  bffDevToolsFailureStage,
  bffProofCopy,
  createBffCookieScanner,
  rememberBffCookies,
  withProofWaiters,
} from '../../scripts/bff-proof-support.mjs';
import en from '../i18n/messages/en/auth.json';
import tr from '../i18n/messages/tr/auth.json';

describe('BFF proof observation ownership', () => {
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
        [{ isLive: () => false }, 'browser-exited'],
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
            const cookie = randomBytes(32).toString('base64url');
            const cookieCanaries = [];
            const scanners = [];
            const child = ownBffProofChild({ owner, app: root, env: fixtureEnvironment({ root }),
              args: ['-e', 'process.${stream}.write(process.argv[1]);', leaking ? cookie : 'clean child output'],
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
    const values = ['A', 'B', 'C', 'D', 'E'].map((value) => value.repeat(43));
    rememberBffCookies(
      [
        '__Host-ls-session=' + values[0] + '; HttpOnly; Secure',
        '__Host-ls-browser=' + values[1] + '; HttpOnly; Secure',
        '__Host-ls-csrf=' + values[2] + '; HttpOnly; Secure',
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
    expect(canaries).toHaveLength(5);
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
