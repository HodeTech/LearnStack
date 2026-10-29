import { describe, expect, it } from 'vitest';

import { renderBffDocument } from './html';
import { BFF_HISTORY_SOURCE } from '../../components/auth/history';
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
        expect(document.querySelector('script:not(#bff-history), form, input')).toBeNull();
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
      expect(document.querySelector('input[type="password"], script:not(#bff-history)')).toBeNull();
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
    expect(
      document.querySelector('img, script:not(#bff-history), [autofocus], [onfocus]'),
    ).toBeNull();
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
    expect(document.querySelector('img, script:not(#bff-history), [onerror]')).toBeNull();
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
  expect(document.querySelector('script:not(#bff-history)')).toBeNull();
  expect(document.querySelector('form')?.getAttribute('action')).toBe('/api/account/profile');
  expect([...document.querySelectorAll('input')].map((input) => [input.name, input.value])).toEqual(
    fields,
  );
  expect(document.body.textContent).toContain('could not be confirmed');
  expect(document.querySelector('a[href="/portal/profile"]')).not.toBeNull();
});

it.each([
  ['en', 'stale'],
  ['en', 'key_reuse'],
  ['tr', 'stale'],
  ['tr', 'key_reuse'],
] as const)(
  '%s %s recovery visibly preserves bounded edits for copying without a stale resubmission',
  (locale, reason) => {
    const edits = [
      ['displayName', '"><script>edited-name</script>'],
      ['preferredLocale', ''],
      ['status', 'Suspended'],
      ['roleIds', '["019a154b-0000-7000-8000-000000000022"]'],
      ['grants', '[{"scope":"Tenant","key":"identity.role.admin"}]'],
      ['manifestVersion', '2'],
    ] as const;
    const internal = [
      ['csrfToken', 'A'.repeat(43)],
      ['version', '9223372036854775807'],
      ['idempotencyKey', 'private-nonce-canary'],
      ['membershipId', '019a154b-0000-7000-8000-000000000011'],
      ['roleId', '019a154b-0000-7000-8000-000000000033'],
      ['manifestFingerprint', 'c'.repeat(64)],
      ['unknownProof', 'private-proof-canary'],
    ] as const;
    const document = new DOMParser().parseFromString(
      renderBffDocument(
        { ...site(), defaultLocale: locale },
        {
          kind: 'mutation_error',
          action: '/api/account/profile',
          destination: '/portal/profile',
          fields: [...internal, ...edits],
          reason,
          retryable: false,
        },
      ),
      'text/html',
    );
    const copies = [...document.querySelectorAll('textarea')];
    expect(copies.map((field) => field.value)).toEqual(
      locale === 'en'
        ? [
            edits[0][1],
            "Use the institution's default language",
            'Suspended',
            'The names of the submitted roles are unavailable. Check the current item before preparing a new selection.',
            'Manage institution roles',
            '2',
          ]
        : [
            edits[0][1],
            'Kurumun varsayılan dilini kullan',
            'Askıda',
            'Gönderilen rollerin adları kullanılamıyor. Yeni bir seçim hazırlamadan önce mevcut öğeyi kontrol edin.',
            'Kurum rollerini yönetme',
            '2',
          ],
    );
    expect(
      [...document.querySelectorAll<HTMLInputElement>('input[type="hidden"]')].map((field) => [
        field.name,
        field.value,
      ]),
    ).toEqual(edits);
    for (const field of document.querySelectorAll('input'))
      expect(field.closest('form')).toBeNull();
    expect(document.body.textContent).not.toContain('019a154b-0000-7000-8000-000000000022');
    expect(copies.map((field) => field.labels?.[0]?.textContent)).toEqual(
      locale === 'en'
        ? [
            'Display name',
            'Preferred language',
            'Membership status',
            'Role assignment data',
            'Permission grant data',
            'Release version',
          ]
        : [
            'Ad',
            'Tercih edilen dil',
            'Üyelik durumu',
            'Rol atama verileri',
            'Yetki verileri',
            'Yayın sürümü',
          ],
    );
    for (const field of copies) {
      expect(field.readOnly).toBe(true);
      expect(field.disabled).toBe(false);
      expect(field.closest('form, [hidden], [aria-hidden="true"]')).toBeNull();
    }
    expect(document.querySelector('h2')?.textContent).toBe(
      locale === 'en' ? 'Submitted edits' : 'Gönderilen değişiklikler',
    );
    expect(document.body.textContent).toContain(
      locale === 'en'
        ? 'Copy these values before checking the item.'
        : 'Öğeyi kontrol etmeden önce bu değerleri kopyalayın.',
    );
    for (const [, value] of internal) expect(document.body.textContent).not.toContain(value);
    expect(
      document.querySelector('form, script:not(#bff-history), [onerror], [autofocus]'),
    ).toBeNull();
    expect(document.querySelector('a[href="/portal/profile"]')).not.toBeNull();
  },
);

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
    expect(reauthentication.querySelector('form, input, script:not(#bff-history)')).toBeNull();
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
    expect(busy.querySelector('form, script:not(#bff-history)')).toBeNull();
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
  ['creation_disabled', 'Creating a LearnStack account is not available right now.'],
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

it.each(['en', 'tr'])(
  'active scope selection in %s preserves current access and posts only another selection',
  (locale) => {
    const tenantId = '10000000-0000-4000-8000-000000000001';
    const organizationId = '019a154b-0000-7000-8000-000000000011';
    const document = new DOMParser().parseFromString(
      renderBffDocument(
        { ...site(), defaultLocale: locale },
        {
          kind: 'scope_choices',
          cursor: 'YQ',
          selection: {
            session: {
              state: 'authenticated',
              displayName: 'Institution',
              csrfToken: 'A'.repeat(43),
              expiresAt: 123,
              limitedByAbsolute: false,
            },
            expiresAt: 123,
            contexts: {
              items: [
                {
                  tenantId,
                  organizationId: null,
                  tenantName: '<Institution>',
                  organizationName: null,
                  entryOrigin: 'https://private.example',
                  canUseCurrentHost: true,
                  current: true,
                },
                {
                  tenantId,
                  organizationId,
                  tenantName: '<Institution>',
                  organizationName: '<Branch>',
                  entryOrigin: 'https://private.example',
                  canUseCurrentHost: true,
                  current: false,
                },
                {
                  tenantId: '20000000-0000-4000-8000-000000000002',
                  organizationId: null,
                  tenantName: 'Other institution',
                  organizationName: null,
                  entryOrigin: 'https://other.example',
                  canUseCurrentHost: false,
                  current: false,
                },
              ],
              endCursor: 'Yg',
              hasNextPage: true,
            },
          },
        },
      ),
      'text/html',
    );
    expect(document.documentElement.lang).toBe(locale);
    expect(
      document.querySelector('[aria-current]')?.closest('li')?.querySelector('form'),
    ).toBeNull();
    const forms = [
      ...document.querySelectorAll<HTMLFormElement>('form[action="/api/auth/switch"]'),
    ];
    expect(forms).toHaveLength(2);
    expect(forms.map((form) => form.method)).toEqual(['post', 'post']);
    expect([...new FormData(forms[0]!).entries()]).toEqual([
      ['csrfToken', 'A'.repeat(43)],
      ['tenantId', tenantId],
      ['organizationId', organizationId],
      ['cursor', 'YQ'],
    ]);
    expect(forms[1]!.querySelector('button')?.textContent).toBe(
      locale === 'en' ? 'Sign in at this institution' : 'Bu kurumda giriş yap',
    );
    expect(document.body.textContent).toContain('<Branch>');
    for (const hidden of [tenantId, organizationId, 'private.example', 'other.example'])
      expect(document.body.textContent).not.toContain(hidden);
    expect(document.querySelector('a[href="/api/auth/entry?cursor=Yg"]')).not.toBeNull();
    expect(document.querySelectorAll('script')).toHaveLength(1);
    expect(document.querySelector('#bff-history')?.textContent).toBe(BFF_HISTORY_SOURCE);
    expect(document.documentElement.getAttribute('data-bff-history-recovery')).toBe(
      '/api/auth/entry',
    );
    expect(document.querySelector('noscript')?.textContent).toContain('JavaScript');
  },
);

it.each(['en', 'tr'])(
  'uses authorized role names in %s stale recovery without visible IDs or a rebase form',
  (locale) => {
    const roleId = '019a154b-0000-7000-8000-000000000022';
    const fields = [['roleIds', JSON.stringify([roleId])]] as const;
    const document = new DOMParser().parseFromString(
      renderBffDocument(
        { ...site(), defaultLocale: locale },
        {
          kind: 'mutation_error',
          action: '/api/studio/membership-roles',
          destination: '/studio/members/019a154b-0000-7000-8000-000000000011',
          reason: 'stale',
          retryable: false,
          fields,
          roleLabels: [{ roleId, builtInKey: 'Instructor', capabilityScope: 'Tenant' }],
        },
      ),
      'text/html',
    );
    expect(document.querySelector('textarea')?.value).toBe(
      locale === 'en' ? 'Institution instructor' : 'Kurum eğitmeni',
    );
    expect(document.body.textContent).not.toContain(roleId);
    expect(document.querySelector<HTMLInputElement>('input[name="roleIds"]')?.value).toBe(
      fields[0][1],
    );
    expect(document.querySelector('form')).toBeNull();
    expect(document.documentElement.getAttribute('data-bff-history-recovery')).toBe(
      '/studio/members/019a154b-0000-7000-8000-000000000011',
    );
    expect(document.querySelector('#bff-history')?.textContent).toBe(BFF_HISTORY_SOURCE);
  },
);

it('all native POST error documents expose only a fixed GET history recovery, never their POST action', () => {
  const pages = [
    {
      kind: 'mutation_error' as const,
      action: '/api/account/profile',
      destination: '/portal/profile',
      reason: 'unavailable' as const,
      retryable: true,
      fields: [['csrfToken', 'A'.repeat(43)]],
    },
    { kind: 'entry_error' as const, reason: 'unavailable' as const },
    { kind: 'account_change' as const, csrfToken: 'A'.repeat(43) },
    { kind: 'error' as const },
  ];
  for (const [index, page] of pages.entries()) {
    const document = new DOMParser().parseFromString(
      renderBffDocument(site(), page as Parameters<typeof renderBffDocument>[1]),
      'text/html',
    );
    expect(document.documentElement.getAttribute('data-bff-history-recovery')).toBe(
      ['/portal/profile', '/api/auth/entry', '/api/auth/entry', '/api/auth/signin'][index],
    );
    expect(document.querySelectorAll('script')).toHaveLength(1);
    expect(document.querySelector('#bff-history')?.textContent).toBe(BFF_HISTORY_SOURCE);
    expect(document.querySelector('#bff-history')?.textContent).not.toContain('A'.repeat(43));
  }
});

it.each([
  ['denied', 'This change is not available in your current account access.'],
  ['stale', 'This item changed.'],
  ['in_progress', 'This request is still in progress.'],
  ['key_reuse', 'This form was already used for a different change.'],
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
