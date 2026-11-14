import { describe, expect, it, vi } from 'vitest';

import type { AdministrationFetch } from './administration-contract';
import { createAdministrationServerSdk } from './administration-server';

const connection = {
  connectionId: '019a154b-0000-7000-8000-0000000000cc',
  protocol: 'oidc',
  alias: 'lsf-' + 'a'.repeat(32),
  displayName: 'Acme SSO',
  issuerOrEntityId: 'https://idp.example/realms/acme',
  status: 'draft',
  source: 'discovery_url',
  clientId: 'learnstack',
  configurationRevision: '1',
  configurationDigest: 'a'.repeat(64),
  desiredDigest: 'b'.repeat(64),
  lastProviderDigest: null,
  pending: null,
  testExpiresAt: null,
  version: '3',
};
const receipt = {
  connectionId: connection.connectionId,
  version: '4',
  outcome: 'applied',
  reason: null,
  testAttemptId: null,
};
const input = {
  protocol: 'oidc',
  displayName: 'Acme SSO',
  source: 'discovery_url',
  documentUrl: 'https://idp.example/realms/acme/.well-known/openid-configuration',
  issuerOrEntityId: null,
  authorizationEndpoint: null,
  tokenEndpoint: null,
  jwksEndpoint: null,
  userInfoEndpoint: null,
  singleSignOnEndpoint: null,
  signingCertificates: [],
  clientId: 'learnstack',
  clientAuthentication: 'client_secret_basic',
  clientSecret: 'write-only-secret',
} satisfies Parameters<ReturnType<typeof createAdministrationServerSdk>['configureFederation']>[0];
const json = (body: unknown, headers: Record<string, string> = {}, status = 200) =>
  new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json', ...headers },
  });

describe('ADR-0099 closed federation administration wrappers', () => {
  it('reads the detail with its ETag exactly when a connection exists', async () => {
    const transport = vi.fn<AdministrationFetch>(async () =>
      json({ connection, provider: 'matches' }, { ETag: '"3"' }),
    );
    const sdk = createAdministrationServerSdk(transport);
    expect((await sdk.getFederationConnection()).kind).toBe('success');
    expect(transport.mock.calls[0]![0]).toBe('/api/v1/identity/federation');
    transport.mockResolvedValueOnce(json({ connection: null, provider: 'not_read' }));
    expect((await sdk.getFederationConnection()).kind).toBe('success');
    for (const bad of [
      json({ connection, provider: 'matches' }),
      json({ connection: null, provider: 'not_read' }, { ETag: '"1"' }),
      json({ connection: { ...connection, secret: 'x' }, provider: 'matches' }, { ETag: '"3"' }),
      json({ connection: { ...connection, alias: 'other' }, provider: 'matches' }, { ETag: '"3"' }),
      json({ connection, provider: 'everything' }, { ETag: '"3"' }),
    ]) {
      transport.mockResolvedValueOnce(bad);
      expect((await sdk.getFederationConnection()).kind).toBe('invalid-response');
    }
  });

  it('sends mutations with If-Match and never an Idempotency-Key', async () => {
    const transport = vi.fn<AdministrationFetch>(async () => json(receipt));
    const sdk = createAdministrationServerSdk(transport);
    expect((await sdk.configureFederation(input, { version: null })).kind).toBe('success');
    expect((await sdk.configureFederation(input, { version: '3' })).kind).toBe('success');
    expect((await sdk.rotateFederationSecret('fresh-secret', { version: '3' })).kind).toBe(
      'success',
    );
    expect((await sdk.renameFederation('Renamed', { version: '3' })).kind).toBe('success');
    for (const call of [
      sdk.activateFederation,
      sdk.disableFederation,
      sdk.enableFederation,
      sdk.deleteFederation,
      sdk.resumeFederationOperation,
    ])
      expect((await call({ version: '3' })).kind).toBe('success');
    const paths = transport.mock.calls.map(([path, init]) => [path, init.method]);
    expect(paths).toEqual([
      ['/api/v1/identity/federation/configuration', 'PUT'],
      ['/api/v1/identity/federation/configuration', 'PUT'],
      ['/api/v1/identity/federation/secret-rotations', 'POST'],
      ['/api/v1/identity/federation/display-name', 'PUT'],
      ['/api/v1/identity/federation/activation', 'POST'],
      ['/api/v1/identity/federation/disablement', 'POST'],
      ['/api/v1/identity/federation/enablement', 'POST'],
      ['/api/v1/identity/federation/deletion', 'POST'],
      ['/api/v1/identity/federation/resumption', 'POST'],
    ]);
    for (const [index, [, init]] of transport.mock.calls.entries()) {
      const headers = new Headers(init.headers);
      expect(headers.has('Idempotency-Key')).toBe(false);
      expect(headers.get('If-Match')).toBe(index === 0 ? null : '"3"');
    }
  });

  it('refuses a malformed request before transport and a receipt carrying an attempt identifier', async () => {
    const transport = vi.fn<AdministrationFetch>(async () => json(receipt));
    const sdk = createAdministrationServerSdk(transport);
    expect(
      (await sdk.configureFederation({ ...input, extra: 1 } as never, { version: null })).kind,
    ).toBe('invalid-request');
    expect(
      (
        await sdk.configureFederation(
          { ...input, documentUrl: 'http://idp.example' },
          { version: null },
        )
      ).kind,
    ).toBe('invalid-request');
    expect((await sdk.rotateFederationSecret('', { version: '3' })).kind).toBe('invalid-request');
    expect((await sdk.activateFederation({ version: '03' })).kind).toBe('invalid-request');
    expect(transport).not.toHaveBeenCalled();
    transport.mockResolvedValueOnce(
      json({ ...receipt, testAttemptId: '019a154b-0000-7000-8000-0000000000aa' }),
    );
    expect((await sdk.activateFederation({ version: '3' })).kind).toBe('invalid-response');
  });
});
