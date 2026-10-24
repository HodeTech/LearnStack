// @vitest-environment node
import { execFileSync } from 'node:child_process';

import { describe, expect, it } from 'vitest';

import {
  createBffCookieScanner,
  rememberBffCookies,
  withProofWaiters,
} from '../../scripts/bff-proof-support.mjs';

describe('BFF proof observation ownership', () => {
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
