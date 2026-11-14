import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it, vi } from 'vitest';

import { PreparedAccountPage } from './shell';
import { federationForm } from '../../server/auth/federation-forms';
import type { PageView } from '../../server/auth/page-contract';
import { administrationSnapshot } from '../../test/administration-ui-fixture';

vi.mock('@/server/auth/page', () => ({ optionalPreparedPage: vi.fn() }));
vi.mock('@/client/bff-session', () => ({ extendBffSession: vi.fn() }));

const alias = 'lsf-' + 'a'.repeat(32);
const connection = {
  connectionId: '019a154b-0000-7000-8000-0000000000cc',
  protocol: 'oidc' as const,
  alias,
  displayName: 'Acme <SSO>',
  issuerOrEntityId: 'https://idp.example/realms/acme',
  status: 'draft' as const,
  source: 'discovery_url' as const,
  clientId: 'learnstack',
  configurationRevision: '1',
  configurationDigest: 'a'.repeat(64),
  desiredDigest: 'a'.repeat(64),
  lastProviderDigest: null,
  pending: null,
  testExpiresAt: null,
  version: '9223372036854775807',
};
const values = {
  redirectUri: 'https://keycloak.example/realms/learnstack/broker/' + alias + '/endpoint',
  entityId: 'https://keycloak.example/realms/learnstack',
  descriptorUrl:
    'https://keycloak.example/realms/learnstack/broker/' + alias + '/endpoint/descriptor',
};
function render(
  federation: Extract<PageView, { kind: 'federation' }>['federation'],
  locale = 'en',
) {
  const view: PageView = { kind: 'federation', federation };
  const base = administrationSnapshot(view);
  const snapshot = administrationSnapshot(view, {
    ownProfile: { ...base.ownProfile!, preferredLocale: locale },
  });
  return new DOMParser().parseFromString(
    renderToStaticMarkup(<PreparedAccountPage snapshot={snapshot} kind="federation" />),
    'text/html',
  );
}
const actions = (document: Document) =>
  [...document.querySelectorAll('form')]
    .map((form) => form.getAttribute('action'))
    .filter((action) => action?.startsWith('/api/studio/'));

describe('ADR-0099 Studio federation page', () => {
  it('offers both protocols before any connection, with the explanations and no IdP values yet', () => {
    const document = render({ connection: null, provider: 'not_read', values: null });
    expect(actions(document)).toEqual([
      '/api/studio/federation-configure',
      '/api/studio/federation-configure',
    ]);
    const text = document.body.textContent!;
    expect(text).toContain('links an existing account');
    expect(text).toContain('signs you out');
    expect(text).toContain('These values appear after the first configuration is saved.');
    // The first OIDC configuration requires a write-only secret control that never carries a value.
    const secret = document.querySelector<HTMLInputElement>('#federation-oidc-secret')!;
    expect(secret.type).toBe('password');
    expect(secret.required).toBe(true);
    expect(secret.getAttribute('value')).toBeNull();
    expect(secret.autocomplete).toBe('new-password');
  });

  it('shows the values, the test and the closed actions for a Draft connection; no secret or attempt appears', () => {
    const document = render({ connection, provider: 'matches', values });
    expect(actions(document)).toEqual([
      '/api/studio/federation-configure',
      '/api/studio/federation-rename',
      '/api/studio/federation-rotate-secret',
      '/api/studio/federation-test',
      '/api/studio/federation-delete',
    ]);
    const html = document.documentElement.outerHTML;
    expect(html).toContain(values.redirectUri);
    expect(html).not.toContain('testAttemptId');
    expect(html).toContain('Acme &lt;SSO&gt;');
    for (const input of document.querySelectorAll<HTMLInputElement>('input[name="clientSecret"]'))
      expect(input.getAttribute('value')).toBeNull();
    // Every form submits only fields its closed parser admits.
    for (const form of document.querySelectorAll('form')) {
      const action = form.getAttribute('action')!;
      if (!action.startsWith('/api/studio/federation-')) continue;
      const names = [...form.querySelectorAll('[name]')].map(
        (element) => element.getAttribute('name')!,
      );
      expect(names).toContain('csrfToken');
      expect(names).not.toContain('idempotencyKey');
    }
    const confirm = document.querySelector<HTMLInputElement>('#federation-confirm-delete')!;
    expect(confirm.required).toBe(true);
    expect(document.querySelector(`label[for="federation-confirm-delete"]`)).not.toBeNull();
  });

  it('activates only after a passed test and disables only with a confirmation', () => {
    expect(
      actions(
        render({
          connection: { ...connection, status: 'test_passed' },
          provider: 'matches',
          values,
        }),
      ),
    ).toContain('/api/studio/federation-activate');
    const active = render({
      connection: { ...connection, status: 'active' },
      provider: 'matches',
      values,
    });
    expect(actions(active)).toContain('/api/studio/federation-disable');
    expect(actions(active)).not.toContain('/api/studio/federation-test');
    const form = active.querySelector<HTMLFormElement>(
      'form[action="/api/studio/federation-disable"]',
    )!;
    const fields = new URLSearchParams(
      [...form.querySelectorAll<HTMLInputElement>('input')].map((input) => [
        input.name,
        input.value,
      ]),
    );
    expect(federationForm('/api/studio/federation-disable', fields).operation).toEqual({
      kind: 'disableFederation',
      version: connection.version,
    });
    expect(
      actions(
        render({ connection: { ...connection, status: 'disabled' }, provider: 'matches', values }),
      ),
    ).toContain('/api/studio/federation-enable');
  });

  it('shows pending, drift and unavailable banners with Resume', () => {
    const document = render({
      connection: {
        ...connection,
        pending: {
          kind: 'configure',
          startedAt: '2026-11-14T10:00:00Z',
          quietUntil: '2026-11-14T10:01:30Z',
        },
      },
      provider: 'drift',
      values,
    });
    expect(actions(document)).toContain('/api/studio/federation-resume');
    expect(document.querySelectorAll('[role="status"]')).toHaveLength(2);
    expect(render({ connection, provider: 'unavailable', values }).body.textContent).toContain(
      'could not be checked',
    );
  });

  it('renders Turkish copy from the same closed catalogue', () => {
    const document = render({ connection, provider: 'matches', values }, 'tr');
    expect(document.body.textContent).toContain('Çoklu oturum açma');
  });
});
