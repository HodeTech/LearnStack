// @vitest-environment node
import { randomBytes } from 'node:crypto';

import { afterEach, expect, it, vi } from 'vitest';

const probe = vi.hoisted(() => ({ path: '/api/v1/public/site' }));
vi.mock('@learnstack/sdk/server', () => ({
  createServerSdk: (transport: (path: string) => unknown) => ({
    getSite: () => transport(probe.path),
  }),
}));

import { createConfiguredPublicClient } from './configured-public-client';
import { mintProvenance } from './ingress';

afterEach(() => {
  vi.unstubAllEnvs();
  vi.unstubAllGlobals();
});

it('checks the normalized destination before the private public hop sends any request', async () => {
  const secret = randomBytes(32).toString('base64url');
  vi.stubEnv('LEARNSTACK_PUBLIC_API_ORIGIN', 'http://127.0.0.1:5080');
  vi.stubEnv('LEARNSTACK_PUBLIC_HOP_SECRET', secret);
  vi.stubEnv('LEARNSTACK_PUBLIC_TLS_CERT', '.data/test/cert.pem');
  vi.stubEnv('LEARNSTACK_PUBLIC_TLS_KEY', '.data/test/key.pem');
  const fetch = vi
    .fn<typeof globalThis.fetch>()
    .mockResolvedValue(new Response('{}', { headers: { 'content-type': 'application/json' } }));
  vi.stubGlobal('fetch', fetch);
  const client = createConfiguredPublicClient(
    mintProvenance(
      { host: 'tenant.example', peer: '127.0.0.1', method: 'GET', target: '/en' },
      secret,
    ),
  );
  expect(client).not.toBeNull();
  for (const path of [
    '/api/v1/public/../account/site',
    '/api/v1/public/%2e%2e/account/site',
    '/api/v1/public/site#fragment',
    '//attacker.example/api/v1/public/site',
  ]) {
    probe.path = path;
    expect(() => client!.getSite()).toThrow('Invalid public API operation');
  }
  expect(fetch).not.toHaveBeenCalled();
  probe.path = '/api/v1/public/site';
  await client!.getSite();
  expect(fetch).toHaveBeenCalledOnce();
  expect(fetch.mock.calls[0]![0].toString()).toBe('http://127.0.0.1:5080/api/v1/public/site');
});
