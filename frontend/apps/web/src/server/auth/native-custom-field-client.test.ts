// @vitest-environment node
import { describe, expect, it, vi } from 'vitest';

import {
  createNativeAdministrationCaller,
  createNativeRetainedConsentCaller,
} from './native-administration-client';
const id = '019a154b-0000-7000-8000-000000000011';
const configuration = {
  apiOrigin: 'https://api.example.test',
  secret: 'private-hop-'.padEnd(48, 'x'),
  nativeSourceSecret: Buffer.from(Array.from({ length: 32 }, (_, index) => index + 1)).toString(
    'base64url',
  ),
};
const binding = { host: 'institution.example.test:3000', source: '203.0.113.19' };
const page = {
  items: [],
  pageInfo: { nextCursor: null, previousCursor: null, hasNext: false, hasPrevious: false },
};
const json = (value: unknown, headers: Record<string, string> = {}) =>
  new Response(JSON.stringify(value), {
    headers: { 'content-type': 'application/json', ...headers },
  });
describe('native custom-field operation source', () => {
  it('uses retained secret source without tenant or organization selectors', async () => {
    const transport = vi.fn<typeof fetch>(async () => json(page));
    const caller = createNativeRetainedConsentCaller(configuration, transport);
    expect(
      (
        await caller(
          'owned-token',
          binding,
          { kind: 'listOwnConsentHistory', membershipId: id, limit: 20 },
          new AbortController().signal,
        )
      ).kind,
    ).toBe('success');
    expect(transport).toHaveBeenCalledWith(
      'https://api.example.test/api/v1/tenancy/membership-field-consents/own/history?membershipId=' +
        id +
        '&limit=20',
      expect.anything(),
    );
    const headers = new Headers(transport.mock.calls[0]![1]!.headers);
    expect(headers.get('X-LearnStack-Native-Session-Secret')).toBe(
      configuration.nativeSourceSecret,
    );
    expect(headers.has('X-LearnStack-Native-Tenant-Id')).toBe(false);
    expect(headers.has('X-LearnStack-Native-Organization-Id')).toBe(false);
  });
  it('keeps member PATCH inside the captured current scope', async () => {
    const transport = vi.fn<typeof fetch>(async () => json({ id, version: '2' }, { ETag: '"2"' }));
    const caller = createNativeAdministrationCaller(configuration, transport);
    expect(
      (
        await caller(
          'owned-token',
          { ...binding, tenantId: id, organizationId: null },
          {
            kind: 'setOwnCustomFields',
            patch: {
              set: [],
              remove: [{ conceptKey: 'answer', definitionId: id, schemaVersion: 1 }],
            },
            version: '1',
            idempotencyKey: 'request-key',
          },
          new AbortController().signal,
        )
      ).kind,
    ).toBe('success');
    const init = transport.mock.calls[0]![1]!;
    expect(init.method).toBe('PATCH');
    expect(new Headers(init.headers).get('X-LearnStack-Native-Tenant-Id')).toBe(id);
  });
});
