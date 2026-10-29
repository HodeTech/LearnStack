import { describe, expect, it } from 'vitest';

import { renderBffDocument } from './html';
import type { PublicSite } from '../public-entry';

const site = (): PublicSite => ({
  displayName: 'Institution',
  enabledLocales: ['en', 'tr'],
  defaultLocale: 'en',
  theme: null,
  showPlatformAttribution: true,
});

describe('bounded localized BFF documents', () => {
  it.each([
    ['en', 'Account access unavailable'],
    ['tr', 'Hesap erişimi kullanılamıyor'],
  ] as const)(
    'offers a closed checked retry and reauthentication in %s without exposing a retained session',
    (locale, title) => {
      for (const returnPath of ['/studio', '/portal'] as const) {
        const document = new DOMParser().parseFromString(
          renderBffDocument(
            { ...site(), defaultLocale: locale },
            { kind: 'account_unavailable', returnPath },
          ),
          'text/html',
        );
        expect(document.querySelector('h1')?.textContent).toBe(title);
        expect([...document.querySelectorAll('a')].map((a) => a.getAttribute('href'))).toEqual([
          '#main-content',
          returnPath,
          '/api/auth/signin?returnTo=' + encodeURIComponent(returnPath),
          '/' + locale + '/courses',
        ]);
        expect(document.querySelector('script, form, input')).toBeNull();
        expect(document.body.textContent).not.toContain('Institution');
      }
    },
  );
  it.each(['en', 'tr'])(
    'renders a native form and complete %s UI without a client catalogue or local password',
    (locale) => {
      const document = new DOMParser().parseFromString(
        renderBffDocument(
          { ...site(), defaultLocale: locale },
          { kind: 'signin', csrfToken: 'A'.repeat(43), returnPath: '/studio' },
        ),
        'text/html',
      );
      expect(document.documentElement.lang).toBe(locale);
      expect(document.body.firstElementChild?.getAttribute('href')).toBe('#main-content');
      expect(document.querySelector('#main-content')?.getAttribute('tabindex')).toBe('-1');
      expect(document.body.firstElementChild?.textContent).toMatch(
        locale === 'en' ? /Skip/ : /İçeriğe/,
      );
      expect(document.querySelector('form')?.getAttribute('action')).toBe(
        '/api/auth/signin/keycloak',
      );
      expect(document.querySelector('form')?.getAttribute('method')).toBe('post');
      expect(document.querySelector('[name="csrfToken"]')?.getAttribute('value')).toBe(
        'A'.repeat(43),
      );
      expect(document.querySelector('[name="returnTo"]')?.getAttribute('value')).toBe('/studio');
      expect(document.querySelector('input[type="password"], script')).toBeNull();
      expect(document.querySelector('button')?.textContent).toMatch(
        locale === 'en' ? /Continue/ : /devam/,
      );
    },
  );
  it('escapes institution text and all form attributes, and rejects arbitrary theme CSS', () => {
    const document = new DOMParser().parseFromString(
      renderBffDocument(
        {
          ...site(),
          displayName: '<img src=x onerror=alert(1)>',
          theme: { background: '</style><script>private</script>' },
        } as never,
        { kind: 'signin', csrfToken: '" autofocus onfocus=alert(1) x="', returnPath: '/studio' },
      ),
      'text/html',
    );
    expect(document.querySelector('img, script, [autofocus], [onfocus]')).toBeNull();
    expect(document.body.textContent).toContain('<img src=x');
    expect(document.querySelector('style')?.textContent).not.toContain('private');
  });
});

it.each(['en', 'tr'])(
  'entry confirmation in %s escapes self labels and leaves scope identifiers only in form values',
  (locale) => {
    const tenantId = '10000000-0000-4000-8000-000000000001';
    const organizationId = '019a154b-0000-7000-8000-000000000011';
    const document = new DOMParser().parseFromString(
      renderBffDocument(
        { ...site(), defaultLocale: locale },
        {
          kind: 'entry',
          cursor: 'page_cursor',
          entry: {
            state: 'entry',
            displayLabel: '<img src=x onerror=alert(1)>',
            csrfToken: 'A'.repeat(43),
            expiresAt: 123,
            identityState: 'active',
            contextHint: null,
            contexts: {
              items: [
                {
                  tenantId,
                  organizationId,
                  tenantName: '<School>',
                  organizationName: '<Branch>',
                  entryOrigin: 'https://verified.example:3000',
                  canUseCurrentHost: true,
                },
              ],
              endCursor: 'next_cursor',
              hasNextPage: true,
            },
          },
        },
      ),
      'text/html',
    );
    expect(document.documentElement.lang).toBe(locale);
    expect(document.querySelector('img, script, [onerror]')).toBeNull();
    expect(document.body.textContent).toContain('<img src=x');
    expect(document.body.textContent).toContain('<School>');
    expect(document.body.textContent).not.toContain(tenantId);
    expect(document.body.textContent).not.toContain(organizationId);
    const form = document.querySelector('form[action="/api/auth/entry/select"]')!;
    expect([...form.querySelectorAll('input')].map((input) => [input.name, input.value])).toEqual([
      ['csrfToken', 'A'.repeat(43)],
      ['tenantId', tenantId],
      ['organizationId', organizationId],
      ['cursor', 'page_cursor'],
    ]);
    expect(document.querySelector('a[href="/api/auth/entry?cursor=next_cursor"]')).not.toBeNull();
    expect(document.body.textContent).not.toContain('verified.example');
  },
);
it('entry recovery does not manufacture a selection when a label or membership is missing', () => {
  const entry = {
    state: 'entry' as const,
    displayLabel: null,
    csrfToken: 'A'.repeat(43),
    expiresAt: 123,
    identityState: 'active' as const,
    contextHint: null,
    contexts: { items: [], endCursor: null, hasNextPage: false },
  };
  const unlabeled = new DOMParser().parseFromString(
    renderBffDocument(site(), { kind: 'entry', entry }),
    'text/html',
  );
  expect(
    [...unlabeled.querySelectorAll('form')].map((form) => form.getAttribute('action')),
  ).toEqual(['/api/auth/signout']);
  expect(unlabeled.body.textContent).toContain('Sign out and sign in again');
  const empty = new DOMParser().parseFromString(
    renderBffDocument(site(), { kind: 'entry', entry: { ...entry, displayLabel: 'My account' } }),
    'text/html',
  );
  expect(empty.body.textContent).toContain('No institution access');
  expect(empty.querySelector('form[action="/api/auth/entry/select"]')).toBeNull();
  expect(empty.querySelector('a[href="/api/auth/entry"]')).not.toBeNull();
});
it('uncertain mutation recovery keeps the original key and edits escaped in a deliberate native form', () => {
  const fields = [
    ['csrfToken', 'A'.repeat(43)],
    ['version', '9223372036854775807'],
    ['idempotencyKey', 'same-key-123'],
    ['displayName', '"><script>private</script>'],
    ['preferredLocale', ''],
  ] as const;
  const document = new DOMParser().parseFromString(
    renderBffDocument(site(), {
      kind: 'mutation_error',
      action: '/api/account/profile',
      destination: '/portal/profile',
      fields,
      reason: 'unavailable',
      retryable: true,
    }),
    'text/html',
  );
  expect(document.querySelector('script')).toBeNull();
  expect(document.querySelector('form')?.getAttribute('action')).toBe('/api/account/profile');
  expect([...document.querySelectorAll('input')].map((input) => [input.name, input.value])).toEqual(
    fields,
  );
  expect(document.body.textContent).toContain('could not be confirmed');
  expect(document.querySelector('a[href="/portal/profile"]')).not.toBeNull();
});

it.each(['en', 'tr'])(
  'entry failure recovery in %s contains no creation or selection mutation and only fixed navigation',
  (locale) => {
    const reauthentication = new DOMParser().parseFromString(
      renderBffDocument(
        { ...site(), defaultLocale: locale },
        { kind: 'entry_error', reason: 'reauthenticate' },
      ),
      'text/html',
    );
    expect(reauthentication.documentElement.lang).toBe(locale);
    expect(reauthentication.querySelector('form, input, script')).toBeNull();
    expect(reauthentication.querySelector('a[href="/api/auth/signin"]')).not.toBeNull();
    expect(reauthentication.querySelector('a[href="/api/auth/entry"]')).toBeNull();
    const busy = new DOMParser().parseFromString(
      renderBffDocument(
        { ...site(), defaultLocale: locale },
        { kind: 'entry_error', reason: 'busy', retryAfterSeconds: 37 },
      ),
      'text/html',
    );
    expect(busy.body.textContent).toContain('37');
    expect(busy.querySelector('form, script')).toBeNull();
    expect(busy.querySelector('a[href="/api/auth/entry"]')).not.toBeNull();
    expect(busy.body.textContent).not.toContain('provider_refused');
  },
);

it.each(['en', 'tr'])(
  'entry and existing destination signout disclose their provider scope in %s',
  (locale) => {
    const pages = [
      {
        kind: 'entry' as const,
        entry: {
          state: 'entry' as const,
          displayLabel: null,
          csrfToken: 'A'.repeat(43),
          expiresAt: 123,
          identityState: 'unavailable' as const,
          contextHint: null,
          contexts: null,
        },
      },
      { kind: 'account_change' as const, csrfToken: 'B'.repeat(43) },
    ];
    for (const page of pages) {
      const document = new DOMParser().parseFromString(
        renderBffDocument({ ...site(), defaultLocale: locale }, page),
        'text/html',
      );
      expect(document.body.textContent).toContain(
        locale === 'en'
          ? 'Other signed-in institution tabs may also require sign-in.'
          : 'Giriş yaptığınız diğer kurum sekmelerinde de tekrar giriş yapmanız gerekebilir.',
      );
      expect(document.querySelector('form')?.getAttribute('action')).toBe('/api/auth/signout');
      expect(document.querySelector<HTMLInputElement>('[name="csrfToken"]')?.value).toBe(
        page.kind === 'entry' ? 'A'.repeat(43) : 'B'.repeat(43),
      );
    }
  },
);

it.each([
  ['reauthenticate', 'This sign-in can no longer be used.'],
  ['creation_disabled', 'Creating a LearnStack identity is not available right now.'],
  ['denied', 'This account access is not available.'],
  ['busy', 'Too many account access requests.'],
  ['unavailable', 'The result could not be confirmed.'],
] as const)('renders the specific entry recovery for %s', (reason, expected) => {
  const document = new DOMParser().parseFromString(
    renderBffDocument(site(), { kind: 'entry_error', reason }),
    'text/html',
  );
  expect(document.querySelector('main > p')?.textContent).toContain(expected);
});

it.each([
  ['denied', 'This change is not available in your current account access.'],
  ['stale', 'This item changed.'],
  ['in_progress', 'This request is still in progress.'],
  ['key_reuse', 'This request key was already used for a different change.'],
  ['unavailable', 'The result could not be confirmed.'],
] as const)('renders the specific mutation recovery for %s', (reason, expected) => {
  const document = new DOMParser().parseFromString(
    renderBffDocument(site(), {
      kind: 'mutation_error',
      reason,
      action: '/api/account/profile',
      destination: '/portal/profile',
      fields: [],
      retryable: false,
    }),
    'text/html',
  );
  expect(document.querySelector('main > p')?.textContent).toContain(expected);
});
