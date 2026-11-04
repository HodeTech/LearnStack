// @vitest-environment node
import { createServer } from 'node:http';
import type { ServerResponse } from 'node:http';
import { createConnection } from 'node:net';

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

async function observeWire(action: (response: ServerResponse) => void, protect = true) {
  const server = createServer((_request, response) => {
    if (protect) protectBffLogoutResponse(response, issuer, host);
    action(response);
  });
  try {
    await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve));
    const address = server.address();
    if (!address || typeof address === 'string') throw new Error('Test listener unavailable');
    const wire = await new Promise<Buffer>((resolve, reject) => {
      const socket = createConnection({ host: '127.0.0.1', port: address.port });
      const chunks: Buffer[] = [];
      socket.setTimeout(2000, () => socket.destroy(new Error('Owned socket did not settle')));
      socket.once('connect', () =>
        socket.write('GET / HTTP/1.1\r\nHost: owned.test\r\nConnection: close\r\n\r\n'),
      );
      socket.on('data', (chunk) => chunks.push(chunk));
      socket.once('error', reject);
      socket.once('end', () => resolve(Buffer.concat(chunks)));
    });
    const end = wire.indexOf('\r\n\r\n');
    expect(end).toBeGreaterThan(0);
    return { headers: wire.subarray(0, end).toString(), body: wire.subarray(end + 4).toString() };
  } finally {
    await new Promise<void>((resolve, reject) => {
      server.close((error) => (error ? reject(error) : resolve()));
      server.closeAllConnections();
    });
  }
}

describe('native logout redirect containment', () => {
  it.each(
    (['write', 'end'] as const).flatMap((method) =>
      (['explicit', 'implicit'] as const).map((publication) => ({ method, publication })),
    ),
  )(
    'suppresses actual wire bytes from $method with $publication header publication',
    async ({ method, publication }) => {
      const callback = vi.fn();
      const action = (response: ServerResponse) => {
        const headers = {
          location: destination,
          'content-length': '0',
          'set-cookie': [
            '__Host-one=; Max-Age=0; Path=/; Secure',
            '__Host-two=owned; Path=/; Secure',
          ],
        };
        if (publication === 'explicit') response.writeHead(303, headers);
        else {
          response.statusCode = 303;
          for (const [name, value] of Object.entries(headers)) response.setHeader(name, value);
        }
        if (method === 'write') {
          response.write(hint, callback);
          response.end();
        } else response.end(hint, callback);
      };
      const clean = await observeWire(action);
      expect(clean.headers).toMatch(/^HTTP\/1\.1 303 /);
      expect(clean.headers.toLowerCase()).toContain('content-length: 0');
      expect(clean.headers.toLowerCase().match(/\r\nset-cookie:/g)).toHaveLength(2);
      expect(clean.body).toBe('');
      expect(callback).toHaveBeenCalledOnce();
      // Same observer and same declared zero length must expose an unprotected body.
      const control = await observeWire(action, false);
      expect(control.headers.toLowerCase()).toContain('content-length: 0');
      expect(control.body).toBe(hint);
      expect(callback).toHaveBeenCalledTimes(2);
    },
  );
  it.each([
    destination,
    '/',
    'https://' + host + '/',
    '/api/auth/signin',
    'https://' + host + '/api/auth/signin',
  ])('retains the allowed Location but removes the Next redirect body: %s', async (location) => {
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
  });

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
    'https://foreign.example.test/realms/learnstack/protocol/openid-connect/logout?id_token_hint=' +
      hint,
    'http://identity.example.test/realms/learnstack/protocol/openid-connect/logout?id_token_hint=' +
      hint,
    'https://identity.example.test:444/realms/learnstack/protocol/openid-connect/logout?id_token_hint=' +
      hint,
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
