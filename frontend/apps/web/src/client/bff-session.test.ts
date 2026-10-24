import { afterEach, describe, expect, it, vi } from 'vitest';

import { extendBffSession } from './bff-session';

const token = 'A'.repeat(43);
const value = {
  state: 'authenticated',
  displayName: 'School',
  csrfToken: token,
  expiresAt: 2_000_000_000_000,
  limitedByAbsolute: false,
};
afterEach(() => vi.unstubAllGlobals());
describe('fixed same-origin BFF browser action', () => {
  it('submits only the session CSRF scalar to the no-store fixed action and returns a frozen safe projection', async () => {
    const fetch = vi
      .fn()
      .mockResolvedValue(
        new Response(JSON.stringify(value), { headers: { 'content-type': 'application/json' } }),
      );
    vi.stubGlobal('fetch', fetch);
    const result = await extendBffSession(token);
    expect(result).toEqual(value);
    expect(Object.isFrozen(result)).toBe(true);
    expect(fetch).toHaveBeenCalledExactlyOnceWith('/api/auth/extend', {
      method: 'POST',
      credentials: 'same-origin',
      cache: 'no-store',
      redirect: 'error',
      headers: { 'content-type': 'application/x-www-form-urlencoded;charset=UTF-8' },
      body: 'csrfToken=' + token,
      signal: expect.any(AbortSignal),
    });
  });
  it.each([
    { ...value, accessToken: 'private-canary' },
    { ...value, expiresAt: 'tomorrow' },
    { ...value, limitedByAbsolute: undefined },
    { state: 'anonymous' },
  ])('refuses a widened or invalid browser projection', async (body) => {
    vi.stubGlobal(
      'fetch',
      vi
        .fn()
        .mockResolvedValue(
          new Response(JSON.stringify(body), { headers: { 'content-type': 'application/json' } }),
        ),
    );
    await expect(extendBffSession(token)).rejects.toThrow('Session unavailable');
  });
  it('refuses invalid CSRF before transport and does not expose a failed response body', async () => {
    const fetch = vi
      .fn()
      .mockResolvedValue(new Response('private-provider-failure', { status: 503 }));
    vi.stubGlobal('fetch', fetch);
    await expect(extendBffSession('bad')).rejects.toThrow('Session unavailable');
    expect(fetch).not.toHaveBeenCalled();
    await expect(extendBffSession(token)).rejects.toThrow('Session unavailable');
  });
  it.each([16_384, 16_385])(
    'checks the %i-byte boundary independently of JSON validation',
    async (size) => {
      const json = JSON.stringify(value);
      const body = json + ' '.repeat(size - new TextEncoder().encode(json).byteLength);
      vi.stubGlobal(
        'fetch',
        vi
          .fn()
          .mockResolvedValue(
            new Response(body, { headers: { 'content-type': 'application/json' } }),
          ),
      );
      if (size === 16_384) await expect(extendBffSession(token)).resolves.toEqual(value);
      else await expect(extendBffSession(token)).rejects.toThrow('Session unavailable');
    },
  );
});
