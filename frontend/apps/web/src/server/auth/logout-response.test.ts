// @vitest-environment node
import { createServer } from 'node:http';
import type { ServerResponse } from 'node:http';

import { describe, expect, it, vi } from 'vitest';

import { protectBffLogoutResponse } from './logout-response';

const issuer = 'https://identity.example.test/realms/learnstack';
const host = 'institution.example.test:3000';
const hint = 'private-logout-hint-canary';
const destination = issuer + '/protocol/openid-connect/logout?id_token_hint=' + hint;

async function observe(action: (response: ServerResponse) => void) {
  const server = createServer((_request, response) => {
    protectBffLogoutResponse(response, issuer, host);
    action(response);
  });
  try {
    await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve));
    const address = server.address();
    if (!address || typeof address === 'string') throw new Error('Test listener unavailable');
    // eslint-disable-next-line no-restricted-globals -- Test-owned raw HTTP response, not an API call.
    const response = await fetch('http://127.0.0.1:' + address.port, { redirect: 'manual' });
    return { status: response.status, headers: response.headers, body: await response.text() };
  } finally {
    await new Promise<void>((resolve, reject) => {
      server.close((error) => (error ? reject(error) : resolve()));
      server.closeAllConnections();
    });
  }
}

describe('native logout redirect containment', () => {
  it.each([destination, '/', 'https://' + host + '/'])(
    'retains the allowed Location but removes the Next redirect body: %s',
    async (location) => {
      const result = await observe((response) => {
        response.statusCode = 303;
        response.setHeader('location', location);
        response.setHeader('set-cookie', ['__Host-owned=; Max-Age=0; Path=/; Secure']);
        response.end(location);
      });
      expect(result.status).toBe(303);
      expect(result.headers.get('location')).toBe(location);
      expect(result.headers.get('content-length')).toBe('0');
      expect(result.headers.get('referrer-policy')).toBe('no-referrer');
      expect(result.headers.get('cache-control')).toBe('no-store');
      expect(result.headers.getSetCookie()).toHaveLength(1);
      expect(result.body).toBe('');
    },
  );

  it('suppresses streamed chunks and preserves write/end callbacks after explicit header publication', async () => {
    const written = vi.fn();
    const ended = vi.fn();
    const result = await observe((response) => {
      response.writeHead(303, 'See Other', { location: destination, 'content-length': '999' });
      response.write(hint, written);
      response.end(hint, ended);
    });
    expect(result.status).toBe(303);
    expect(result.body).toBe('');
    expect(result.headers.get('content-length')).toBe('0');
    expect(written).toHaveBeenCalledTimes(1);
    expect(ended).toHaveBeenCalledTimes(1);
  });

  it.each([
    'https://foreign.example.test/logout?id_token_hint=' + hint,
    issuer + '/protocol/openid-connect/auth?id_token_hint=' + hint,
    destination + '#fragment',
    'https://user@identity.example.test/realms/learnstack/protocol/openid-connect/logout',
    '//foreign.example.test/',
    'https://foreign.example.test/',
    'https://' + host + '/?id_token_hint=' + hint,
  ])('refuses a redirect outside the fixed provider logout boundary: %s', async (location) => {
    const result = await observe((response) => {
      response.writeHead(303, { location });
      response.end(location);
    });
    expect(result.status).toBe(503);
    expect(result.headers.has('location')).toBe(false);
    expect(result.headers.get('retry-after')).toBe('30');
    expect(result.body).toBe('');
  });

  it('preserves ordinary nonredirect error bodies', async () => {
    const result = await observe((response) => {
      response.statusCode = 403;
      response.write('invalid');
      response.end(' request');
    });
    expect(result.status).toBe(403);
    expect(result.body).toBe('invalid request');
  });

  it('preserves distinct cookies passed as repeated raw writeHead headers', async () => {
    const result = await observe((response) => {
      response.writeHead(303, [
        'Location',
        destination,
        'Set-Cookie',
        '__Host-one=; Max-Age=0; Path=/; Secure',
        'set-cookie',
        '__Host-two=proof; Path=/; Secure; HttpOnly',
      ]);
      response.end(destination);
    });
    expect(result.status).toBe(303);
    expect(result.headers.getSetCookie()).toHaveLength(2);
    expect(result.body).toBe('');
  });
});
