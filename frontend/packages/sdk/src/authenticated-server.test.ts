import { describe, expect, it, vi } from 'vitest';

import { createAuthenticatedServerSdk } from './authenticated-server';
import { PUBLIC_GET_WRAPPERS } from './server';

const site = {
  displayName: 'School',
  enabledLocales: ['en'],
  defaultLocale: 'en',
  theme: null,
  showPlatformAttribution: true,
};
const account = {
  actorId: '019a154b-0000-7000-8000-000000000011',
  tenantId: '10000000-0000-4000-8000-000000000001',
  organizationId: null,
  site,
};
const response = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status });
describe('exact authenticated AccountSite SDK', () => {
  it('has one typed GET/no-store capability; public wrappers remain anonymous', async () => {
    const transport = vi.fn(async () => response(account));
    const sdk = createAuthenticatedServerSdk(transport);
    expect(Object.keys(sdk)).toEqual(['getAccountSite']);
    expect((await sdk.getAccountSite()).kind).toBe('success');
    expect(transport).toHaveBeenCalledWith('/api/v1/account/site', {
      method: 'GET',
      cache: 'no-store',
      headers: { Accept: 'application/json' },
      signal: undefined,
    });
    expect(Object.values(PUBLIC_GET_WRAPPERS)).not.toContain('GetAccountSite');
  });
  it.each([
    { ...account, actorId: account.tenantId },
    { ...account, tenantId: '00000000-0000-0000-0000-000000000000' },
    { ...account, organizationId: 'bad' },
    { ...account, site: {} },
    {},
  ])('refuses malformed identity/projection %j', async (body) => {
    expect(await createAuthenticatedServerSdk(async () => response(body)).getAccountSite()).toEqual(
      { kind: 'invalid-response', reason: 'invalid-success' },
    );
  });
  it('does not turn a failure into a public success or expose thrown provider details', async () => {
    const sdk = createAuthenticatedServerSdk(async () => {
      throw new Error('private-canary');
    });
    expect(await sdk.getAccountSite()).toEqual({ kind: 'transport-error' });
    expect(
      await createAuthenticatedServerSdk(async () => response(account, 201)).getAccountSite(),
    ).toEqual({ kind: 'invalid-response', reason: 'unexpected-status' });
  });
  it('honors cancellation before any transport call', async () => {
    const transport = vi.fn();
    const controller = new AbortController();
    controller.abort();
    expect(
      await createAuthenticatedServerSdk(transport).getAccountSite({ signal: controller.signal }),
    ).toEqual({ kind: 'cancelled' });
    expect(transport).not.toHaveBeenCalled();
  });
});
