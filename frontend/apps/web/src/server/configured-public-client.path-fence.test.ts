// @vitest-environment node
import { randomBytes } from 'node:crypto';

import type * as Sdk from '@learnstack/sdk/server';
import { afterEach, expect, it, vi } from 'vitest';

const probe = vi.hoisted(() => ({ path: undefined as string | undefined }));
vi.mock('@learnstack/sdk/server', async (importOriginal) => {
  const sdk = await importOriginal<typeof Sdk>();
  return {
    ...sdk,
    createServerSdk: (transport: Sdk.PublicFetch) =>
      sdk.createServerSdk((path, init) => transport(probe.path ?? path, init)),
  };
});

import { createConfiguredPublicClient } from './configured-public-client';
import { mintProvenance } from './ingress';

afterEach(() => {
  probe.path = undefined;
  vi.unstubAllEnvs();
  vi.unstubAllGlobals();
});

it('checks the normalized destination before the private public hop sends any request', async () => {
  const secret = randomBytes(32).toString('base64url');
  vi.stubEnv('LEARNSTACK_PUBLIC_API_ORIGIN', 'http://127.0.0.1:5080');
  vi.stubEnv('LEARNSTACK_PUBLIC_HOP_SECRET', secret);
  vi.stubEnv('LEARNSTACK_PUBLIC_TLS_CERT', '.data/test/cert.pem');
  vi.stubEnv('LEARNSTACK_PUBLIC_TLS_KEY', '.data/test/key.pem');
  const site = {
    displayName: 'School',
    enabledLocales: ['en'],
    defaultLocale: 'en',
    theme: null,
    showPlatformAttribution: true,
  };
  const fetch = vi
    .fn<typeof globalThis.fetch>()
    .mockImplementation(
      async () =>
        new Response(JSON.stringify(site), { headers: { 'content-type': 'application/json' } }),
    );
  vi.stubGlobal('fetch', fetch);
  const client = createConfiguredPublicClient(
    mintProvenance(
      { host: 'tenant.example', peer: '127.0.0.1', method: 'GET', target: '/en/courses' },
      secret,
    ),
  );
  expect(client).not.toBeNull();
  for (const path of [
    '/api/v1/public/../account/site',
    '/api/v1/public/%2e%2e/account/site',
    '/api/v1/public/site#fragment',
    '/api/v1/public/unknown',
    '/api/v1/public/courses/a%2fb',
    '/api/v1/public/courses/a%5Cb',
    '/api/v1/public/courses/..%2faccount',
    '/api/v1/public/courses/a%252fb',
    '//attacker.example/api/v1/public/site',
  ]) {
    probe.path = path;
    await expect(client!.getSite()).resolves.toEqual({ kind: 'invalid-request' });
  }
  expect(fetch).not.toHaveBeenCalled();
  probe.path = undefined;
  await expect(client!.getCourse({ slug: '..' })).resolves.toEqual({ kind: 'invalid-request' });
  for (const slug of ['a/b', 'a\\b', '..%2faccount', 'a%5cb', 'a%252fb'])
    await expect(client!.getCourse({ slug })).resolves.toEqual({ kind: 'invalid-request' });
  await expect(client!.getLesson({ slug: 'safe', lessonSlug: 'a/b' })).resolves.toEqual({
    kind: 'invalid-request',
  });
  expect(fetch).not.toHaveBeenCalled();
  await expect(client!.getSite()).resolves.toEqual({ kind: 'success', status: 200, data: site });
  expect(fetch).toHaveBeenCalledOnce();
  expect(fetch.mock.calls[0]![0].toString()).toBe('http://127.0.0.1:5080/api/v1/public/site');
});
