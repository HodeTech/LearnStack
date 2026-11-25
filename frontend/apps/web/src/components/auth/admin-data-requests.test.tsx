import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it, vi } from 'vitest';

import {
  administratorMayCancel,
  administratorMayRevoke,
  dataRequestDisplayState,
  dataRequestResultCurrent,
  dataRequestUnavailableSince as dataRequestAdministratorUnavailableSince,
} from './admin-data-request-labels';
import { PreparedAccountPage, accountPageMetadata } from './shell';
import {
  dataRequestCancellable,
  dataRequestState,
  dataRequestUnavailableSince,
} from '../../server/auth/data-request-html';
import { bffAdministrationForm } from '../../server/auth/http-forms';
import { optionalPreparedPage } from '../../server/auth/page';
import type {
  NativePageSnapshot,
  PageDataRequest,
  PageView,
} from '../../server/auth/page-contract';
import { administrationSnapshot, uiMember } from '../../test/administration-ui-fixture';

vi.mock('@/server/auth/page', () => ({ optionalPreparedPage: vi.fn() }));
vi.mock('@/client/bff-session', () => ({ extendBffSession: vi.fn() }));

const requestId = '019a154b-0000-7000-8000-000000000041';
const preparedAt = '2026-11-23T12:00:00.000Z';
const named = {
  membershipId: uiMember.membershipId,
  displayName: '<Subject>',
  status: 'Active',
  organizationScoped: false,
} as const;
const request = (value: Partial<PageDataRequest> = {}): PageDataRequest => ({
  id: requestId,
  kind: 'erasure',
  origin: 'administrator',
  status: 'scheduled',
  createdAt: '2026-11-23T10:00:00.0000000+00:00',
  updatedAt: null,
  completedAt: null,
  lastRefusal: null,
  failureReason: null,
  retained: [],
  artifact: null,
  version: '9223372036854775807',
  executeAt: '2026-11-24T10:00:00.0000000+00:00',
  requestedByYou: true,
  membershipIds: [uiMember.membershipId],
  membershipCount: 1,
  // An administrator's erasure is filed only for a Revoked Membership, which is never read (ADR-0106 § 3).
  subject: 'revoked',
  ...value,
});
const exported = (value: Partial<PageDataRequest> = {}) =>
  request({
    kind: 'export',
    status: 'completed',
    executeAt: null,
    completedAt: '2026-11-23T10:05:00.0000000+00:00',
    updatedAt: '2026-11-23T10:05:00.0000000+00:00',
    subject: named,
    artifact: {
      id: '019a154b-0000-7000-8000-000000000042',
      status: 'available',
      expiresAt: '2026-11-30T10:05:00.0000000+00:00',
      purgedAt: null,
      byteCount: 2 * 1024 * 1024,
      chunkCount: 16,
      truncations: [{ collection: 'audit.entries', included: 10, total: 12 }],
      version: '9007199254740993',
      readable: true,
    },
    ...value,
  });
function documentFor(view: PageView, overrides: Partial<NativePageSnapshot> = {}) {
  const snapshot = administrationSnapshot(view, overrides);
  const html = renderToStaticMarkup(<PreparedAccountPage snapshot={snapshot} kind={view.kind} />);
  return { snapshot, document: new DOMParser().parseFromString(html, 'text/html') };
}
const inLocale = (locale: string): Partial<NativePageSnapshot> => ({
  ownProfile: { displayName: null, preferredLocale: locale, version: '1' },
});
function forms(document: Document) {
  return [...document.querySelectorAll<HTMLFormElement>('form')].filter((form) =>
    form.getAttribute('action')!.startsWith('/api/studio/data-request-'),
  );
}
function fields(form: HTMLFormElement) {
  const values = new URLSearchParams();
  for (const [name, value] of new FormData(form)) values.append(name, String(value));
  return values;
}
const list = (items: PageDataRequest[], next: string | null = null): PageView => ({
  kind: 'data_requests',
  items,
  endCursor: next,
  hasNextPage: next !== null,
  preparedAt,
});
const detail = (
  value: PageDataRequest,
  result: Extract<PageView, { kind: 'data_request' }>['result'] = null,
): PageView => ({ kind: 'data_request', request: value, result, preparedAt });
// The member page exists only for an Active or Suspended Membership: the API answers 404 for a Revoked one.
const memberView = (
  status: 'Active' | 'Suspended',
  dataRequests: Extract<PageView, { kind: 'member' }>['dataRequests'],
  inCurrentScope = true,
): PageView => ({
  kind: 'member',
  member: {
    ...uiMember,
    status,
    inCurrentScope,
    canChangeStatus: true,
    canReplaceRoles: false,
  },
  roles: null,
  ...(dataRequests ? { dataRequests } : {}),
});
const erasurePage = (membershipId: string | null = uiMember.membershipId): PageView => ({
  kind: 'data_request_erasure',
  membershipId,
});

describe('Studio data requests: the list (ADR-0106 §§ 6, 13)', () => {
  it.each(['en', 'tr'])(
    'names the subject, the status and the filer without a form (%s)',
    (locale) => {
      const items = [
        request(),
        exported({ id: '019a154b-0000-7000-8000-000000000043', requestedByYou: false }),
        request({
          id: '019a154b-0000-7000-8000-000000000044',
          origin: 'self',
          requestedByYou: false,
          subject: null,
        }),
      ];
      const { document } = documentFor(list(items, 'c2VhbGVk'), inLocale(locale));
      expect(document.querySelectorAll('h1')).toHaveLength(1);
      expect(document.querySelector('h1')?.textContent).toBe(
        locale === 'en' ? 'Data requests' : 'Veri talepleri',
      );
      const headings = [...document.querySelectorAll('.auth-resource-list article h3 a')];
      expect(headings.map((link) => link.getAttribute('href'))).toEqual(
        items.map((item) => '/studio/data-requests/' + item.id),
      );
      expect(headings[0]!.querySelector('bdi')).toBeNull();
      expect(headings[1]!.querySelector('bdi')?.textContent).toBe('<Subject>');
      expect(document.querySelector('subject')).toBeNull();
      // Each flex link holds one element, so the space before the name is never a flex item's trailing space.
      for (const link of headings) {
        expect(link.childNodes).toHaveLength(1);
        expect((link.firstChild as Element).tagName).toBe('SPAN');
      }
      if (locale === 'en') {
        expect(headings.map((link) => link.textContent)).toEqual([
          'Erasure: Revoked member',
          'Data export: <Subject>',
          'Erasure: Member name unavailable',
        ]);
        const filed = [...document.querySelectorAll('article dl')].map(
          (facts) => facts.querySelectorAll('dd')[0]?.textContent,
        );
        expect(filed).toEqual(['You', 'Another administrator', 'The person themselves']);
        expect(document.body.textContent).toContain('Status: Scheduled');
        expect(document.body.textContent).toContain('Administrators cannot download it.');
      }
      for (const status of document.querySelectorAll('.auth-data-request-status')) {
        const icon = status.querySelector('svg')!;
        expect(icon.getAttribute('aria-hidden')).toBe('true');
        expect(icon.getAttribute('focusable')).toBe('false');
        expect(status.querySelector('span')?.textContent?.length).toBeGreaterThan(5);
      }
      expect(document.querySelectorAll('time').length).toBeGreaterThanOrEqual(4);
      expect(forms(document)).toHaveLength(0);
      expect(
        document.querySelector('a[href="/studio/data-requests?cursor=c2VhbGVk"]'),
      ).not.toBeNull();
      expect(document.querySelector('a[href="/studio/members"]')).not.toBeNull();
      expect(document.body.textContent).not.toContain(uiMember.membershipId);
      expect(document.querySelector('script')).toBeNull();
    },
  );

  it('shows the navigation entry only with the membership capability, and an empty state', () => {
    const { document, snapshot } = documentFor(list([]));
    expect(document.querySelector('nav a[href="/studio/data-requests"]')?.textContent).toBe(
      'Data requests',
    );
    expect(document.body.textContent).toContain('No data requests have been made in this scope.');
    const without = documentFor(list([]), {
      capabilities: { ...snapshot.capabilities!, canManageMemberships: false },
    }).document;
    expect(
      without.querySelector('nav[aria-label="Account navigation"] a[href="/studio/data-requests"]'),
    ).toBeNull();
    expect(
      document.querySelector(
        'nav[aria-label="Account navigation"] a[href="/studio/data-requests"]',
      ),
    ).not.toBeNull();
  });

  it('offers the first page after an invalid cursor', () => {
    const { document } = documentFor(list([]), {
      outcome: { kind: 'failure', reason: 'invalid_cursor', status: 400, retryAfterSeconds: null },
    });
    expect(document.querySelector('section a[href="/studio/data-requests"]')).not.toBeNull();
  });
});

describe('Studio data requests: one request', () => {
  it('cancels a scheduled erasure the caller filed at its version, with a fresh key', () => {
    const { document, snapshot } = documentFor(detail(request()));
    const [form] = forms(document);
    expect(form?.getAttribute('action')).toBe('/api/studio/data-request-cancel');
    const values = fields(form!);
    expect([...values.keys()]).toEqual(['csrfToken', 'version', 'idempotencyKey', 'requestId']);
    expect(values.get('idempotencyKey')).toBe(snapshot.formKeys[0]);
    expect(bffAdministrationForm('/api/studio/data-request-cancel', values)).toEqual({
      operation: {
        kind: 'cancelDataRequest',
        id: requestId,
        version: '9223372036854775807',
        idempotencyKey: snapshot.formKeys[0],
      },
      destination: '/studio/data-requests/' + requestId,
    });
    // A Revoked subject has no member page: a truthful label, and no link to a page that answers 404.
    expect(document.querySelector('.auth-resource-name a, dd a')).toBeNull();
    expect(document.querySelector('.auth-resource-name')?.textContent).toBe(
      'Erasure: Revoked member',
    );
    expect(document.body.textContent).toContain('Starts after');
    expect(document.body.textContent).toContain(
      'Until then the person can cancel it, and so can the administrator who asked for it.',
    );
  });

  it('links a named subject to the Membership whose read named it, and an unnamed one nowhere', () => {
    const other = '019a154b-0000-7000-8000-000000000077';
    const { document } = documentFor(
      detail(exported({ subject: { ...named, membershipId: other } })),
    );
    const link = document.querySelector('dd a')!;
    expect(link.getAttribute('href')).toBe('/studio/members/' + other);
    expect(link.childNodes).toHaveLength(1);
    expect(link.querySelector('span > bdi')?.textContent).toBe('<Subject>');
    const unnamed = documentFor(detail(exported({ subject: null }))).document;
    expect(unnamed.querySelector('dd a')).toBeNull();
    expect(unnamed.body.textContent).toContain('Member name unavailable');
  });

  it("does not credit an administrator with a self-filed erasure's cancellation", () => {
    const self = documentFor(detail(request({ origin: 'self', requestedByYou: false })));
    const text = self.document.body.textContent!;
    expect(text).toContain(
      'Erasure starts after the time shown. Until then the person can cancel it.',
    );
    expect(text).not.toContain('the administrator who asked for it');
  });

  it('offers no cancellation once the window ended, nor to anyone but the administrator who filed it', () => {
    const due = documentFor(detail(request({ executeAt: '2026-11-23T11:00:00.0000000+00:00' })));
    expect(forms(due.document)).toHaveLength(0);
    expect(due.document.body.textContent).toContain('Status: Starting soon');
    expect(due.document.body.textContent).toContain('It can no longer be cancelled.');
    const other = documentFor(detail(request({ requestedByYou: false })));
    expect(forms(other.document)).toHaveLength(0);
    expect(other.document.body.textContent).toContain(
      'Another administrator asked for this request. Only they or the person can cancel it.',
    );
    const person = documentFor(detail(request({ origin: 'self', requestedByYou: true })));
    expect(forms(person.document)).toHaveLength(0);
    expect(person.document.body.textContent).toContain(
      'The person asked for this request themselves. Only they can cancel it.',
    );
    const fanOut = documentFor(
      detail(request({ origin: 'closure_fanout', requestedByYou: false })),
    ).document.querySelector('.auth-data-requests')!.textContent!;
    expect(fanOut).toContain("The person's account closure");
    expect(fanOut).not.toContain('can cancel it.');
    const reader = documentFor(detail(request()), {
      capabilities: {
        ...administrationSnapshot(detail(request())).capabilities!,
        canManageMemberships: false,
      },
    });
    expect(forms(reader.document)).toHaveLength(0);
  });

  it('revokes an available export the caller filed at the artifact version inside a disclosure', () => {
    const value = exported();
    const { document, snapshot } = documentFor(detail(value));
    const form = forms(document).find(
      (candidate) => candidate.getAttribute('action') === '/api/studio/data-request-revoke',
    )!;
    expect(form.closest('details.auth-danger')).not.toBeNull();
    expect(fields(form).get('version')).toBe('9007199254740993');
    expect(fields(form).get('idempotencyKey')).toBe(snapshot.formKeys[1]);
    expect(
      bffAdministrationForm('/api/studio/data-request-revoke', fields(form)).operation,
    ).toEqual({
      kind: 'revokeDataExport',
      id: requestId,
      version: '9007199254740993',
      idempotencyKey: snapshot.formKeys[1],
    });
    expect(document.body.textContent).toContain('Available until');
    expect(document.body.textContent).toContain('2 MB');
    expect(document.body.textContent).toContain('Activity records: 10 included, 12 in total');
    expect(document.body.textContent).toContain('Administrators cannot download it.');
    expect(document.body.textContent).toContain(
      'The person can download it on their own data page until the date shown.',
    );
    for (const changed of [
      exported({ requestedByYou: false }),
      exported({ origin: 'self' }),
      exported({ artifact: { ...value.artifact!, readable: false, status: 'revoked' } }),
    ])
      expect(forms(documentFor(detail(changed)).document)).toHaveLength(0);
  });

  it.each(['en', 'tr'] as const)(
    'announces a settled action in a status region and the document title (%s)',
    async (locale) => {
      const { document, snapshot } = documentFor(detail(request(), 'erasure'), inLocale(locale));
      const notice = document.querySelector('[role="status"]')!;
      expect(notice.querySelector('h2')?.textContent).toBe(
        locale === 'en' ? 'Your action has been recorded' : 'İşleminiz kaydedildi',
      );
      expect(notice.textContent).toContain(
        locale === 'en' ? 'It starts after 24 hours' : 'İşlem 24 saat sonra başlar',
      );
      vi.mocked(optionalPreparedPage).mockResolvedValueOnce({
        snapshot,
        assertActive: () => undefined,
      } as never);
      expect((await accountPageMetadata('data_request')).title).toBe(
        locale === 'en'
          ? 'Your action has been recorded – Data request'
          : 'İşleminiz kaydedildi – Veri talebi',
      );
      vi.mocked(optionalPreparedPage).mockResolvedValueOnce({
        snapshot: administrationSnapshot(detail(request()), inLocale(locale)),
        assertActive: () => undefined,
      } as never);
      expect((await accountPageMetadata('data_request')).title).toBe(
        locale === 'en' ? 'Data request' : 'Veri talebi',
      );
      // The person cancelled it since: a reloaded, restored or shared URL no longer announces the filing.
      const stale = administrationSnapshot(
        detail(
          request({ status: 'cancelled', updatedAt: '2026-11-23T11:00:00.0000000+00:00' }),
          'erasure',
        ),
        inLocale(locale),
      );
      const html = renderToStaticMarkup(
        <PreparedAccountPage snapshot={stale} kind="data_request" />,
      );
      expect(
        new DOMParser().parseFromString(html, 'text/html').querySelector('[role="status"]'),
      ).toBeNull();
      vi.mocked(optionalPreparedPage).mockResolvedValueOnce({
        snapshot: stale,
        assertActive: () => undefined,
      } as never);
      expect((await accountPageMetadata('data_request')).title).toBe(
        locale === 'en' ? 'Data request' : 'Veri talebi',
      );
    },
  );

  it('keeps a result only while it describes the request as prepared', () => {
    const at = preparedAt;
    const cases: [Parameters<typeof dataRequestResultCurrent>[0], PageDataRequest, boolean][] = [
      ['erasure', request(), true],
      ['erasure', request({ executeAt: '2026-11-23T11:00:00.0000000+00:00' }), false],
      ['erasure', request({ status: 'cancelled' }), false],
      ['erasure', exported({ status: 'requested', artifact: null }), false],
      ['export', exported({ status: 'requested', artifact: null }), true],
      ['export', exported({ status: 'fulfilling', artifact: null }), true],
      ['export', exported(), true],
      ['export', exported({ artifact: { ...exported().artifact!, readable: false } }), false],
      ['export', exported({ status: 'failed', artifact: null }), false],
      ['export', request(), false],
      ['cancelled', request({ status: 'cancelled' }), true],
      ['cancelled', request(), false],
      ['revoked', exported({ artifact: { ...exported().artifact!, readable: false } }), true],
      ['revoked', exported(), false],
      ['revoked', exported({ artifact: null }), false],
    ];
    for (const [result, item, expected] of cases)
      expect(dataRequestResultCurrent(result, item, at), result + ' ' + item.status).toBe(expected);
  });

  it('explains what was kept, why a request stopped or failed, and every Membership it covers', () => {
    const completed = request({
      status: 'completed',
      completedAt: '2026-11-24T12:00:00.0000000+00:00',
      updatedAt: '2026-11-24T12:00:00.0000000+00:00',
      retained: [
        { kind: 'consent_evidence', count: 3 },
        { kind: 'invitation_envelope', count: 1 },
        { kind: 'unserved_group', count: 0 },
      ],
      membershipIds: [uiMember.membershipId, '019a154b-0000-7000-8000-000000000099'],
      membershipCount: 25,
    });
    const text = documentFor(detail(completed)).document.querySelector(
      '.auth-data-requests',
    )!.textContent!;
    expect(text).toContain('Records of consents the person gave or withdrew: 3');
    expect(text).toContain('Encrypted copies of invitation e-mails: 1');
    expect(text).toContain('Places that could not be processed and keep the person');
    expect(text).toContain('This list is not complete');
    expect(text).toContain('Membership record 2');
    expect(text).toContain('Only the first 20 records are listed.');
    // An erasure that ran leaves every record a Revoked tombstone: none has a member page.
    expect(
      documentFor(detail(completed)).document.querySelectorAll('li a[href^="/studio/members/"]'),
    ).toHaveLength(0);
    const exportRecords = documentFor(
      detail(exported({ membershipIds: completed.membershipIds, membershipCount: 2 })),
    ).document.querySelectorAll<HTMLAnchorElement>('li > a[href^="/studio/members/"]');
    expect([...exportRecords].map((link) => link.getAttribute('href'))).toEqual(
      completed.membershipIds.map((id) => '/studio/members/' + id),
    );
    for (const link of exportRecords) {
      expect(link.childNodes).toHaveLength(1);
      expect(link.firstElementChild?.tagName).toBe('SPAN');
    }
    expect(exportRecords[1]!.textContent).toBe('Membership record 2');
    expect(text).not.toMatch(/\byou gave\b|\byour\b/i);
    const blocked = documentFor(
      detail(
        request({
          status: 'blocked',
          lastRefusal: 'blocked_last_administrator',
          updatedAt: '2026-11-24T10:01:00.0000000+00:00',
        }),
      ),
    ).document.body.textContent!;
    expect(blocked).toContain("this person is the institution's only administrator");
    expect(blocked).toContain('Ended');
    const failed = documentFor(
      detail(
        exported({
          status: 'failed',
          failureReason: 'export_too_large',
          artifact: null,
          completedAt: null,
        }),
      ),
    ).document.body.textContent!;
    expect(failed).toContain('Ask the platform operator about the export size limit.');
  });
});

describe("Studio data requests: the member's section", () => {
  const empty = { items: [], complete: true, preparedAt };

  it('files an export for this Membership and sends erasure through the revocation', () => {
    const { document, snapshot } = documentFor(memberView('Active', empty));
    const section = document.querySelector(
      'section[aria-labelledby="member-data-requests-title"]',
    )!;
    expect(section.querySelector('h2')?.textContent).toBe('Data requests');
    const [form, ...rest] = forms(document);
    expect(rest).toHaveLength(0);
    expect(form?.getAttribute('action')).toBe('/api/studio/data-request-export');
    expect(fields(form!).get('idempotencyKey')).toBe(snapshot.formKeys[32]);
    expect(bffAdministrationForm('/api/studio/data-request-export', fields(form!))).toEqual({
      operation: {
        kind: 'createDataExportForSubject',
        membershipId: uiMember.membershipId,
        idempotencyKey: snapshot.formKeys[32],
      },
      destination: '/studio/members/' + uiMember.membershipId,
    });
    expect(section.textContent).toContain(
      'This membership is still active or suspended. It has to end before an administrator can ask for its erasure.',
    );
    expect(section.textContent).toContain(
      'The revocation is recorded as its own action, and the page where its erasure is asked for then opens.',
    );
    // The erasure itself is filed on the page the revocation leads to, never here.
    expect(document.querySelector('form[action="/api/studio/data-request-erasure"]')).toBeNull();
    expect(section.querySelector('details')).toBeNull();
    expect(section.textContent).toContain('No data requests about this member');
    // Every idempotency key on the member page stays distinct.
    const keys = [
      ...document.querySelectorAll<HTMLInputElement>('input[name="idempotencyKey"]'),
    ].map((input) => input.value);
    expect(new Set(keys).size).toBe(keys.length);
  });

  it('withholds a filing the visible list already proves futile, and lists only this member', () => {
    const active = documentFor(
      memberView('Suspended', {
        items: [
          exported({ status: 'requested', artifact: null, completedAt: null }),
          request({ id: '019a154b-0000-7000-8000-000000000045', origin: 'self' }),
        ],
        complete: true,
        preparedAt,
      }),
    ).document;
    expect(forms(active)).toHaveLength(0);
    expect(active.body.textContent).toContain(
      'An export for this member is already being prepared.',
    );
    expect(
      [
        ...active.querySelectorAll(
          'section[aria-labelledby="member-data-requests-title"] article a',
        ),
      ].map((link) => link.getAttribute('href')),
    ).toEqual([
      '/studio/data-requests/' + requestId,
      '/studio/data-requests/019a154b-0000-7000-8000-000000000045',
    ]);
    const available = documentFor(
      memberView('Active', { items: [exported({ origin: 'self' })], complete: true, preparedAt }),
    ).document;
    expect(forms(available)).toHaveLength(0);
    expect(available.body.textContent).toContain(
      'A copy of the data is already available to download.',
    );
  });

  it('says what it could not check, and keeps the filings', () => {
    const partial = documentFor(memberView('Active', { items: [], complete: false, preparedAt }));
    expect(partial.document.body.textContent).toContain('Only the 100 most recent requests');
    const unavailable = documentFor(
      memberView('Active', { items: null, complete: false, preparedAt }),
    );
    expect(unavailable.document.body.textContent).toContain('could not be checked just now');
    expect(unavailable.document.body.textContent).not.toContain(
      'Only the 100 most recent requests',
    );
    expect(forms(unavailable.document)).toHaveLength(1);
  });

  it('sends another scope to its own access, and shows nothing without the capability', () => {
    const outside = documentFor(memberView('Active', undefined, false)).document;
    expect(forms(outside)).toHaveLength(0);
    expect(outside.body.textContent).toContain("made in the member's own scope");
    const { snapshot } = documentFor(memberView('Active', empty));
    const without = documentFor(memberView('Active', empty), {
      capabilities: { ...snapshot.capabilities!, canManageMemberships: false },
    }).document;
    expect(without.querySelector('#member-data-requests-title')).toBeNull();
  });
});

describe('Studio data requests: the erasure page of a Revoked Membership (ADR-0106 §§ 3, 11)', () => {
  it.each(['en', 'tr'] as const)(
    'confirms the erasure explicitly, stating what goes and what stays (%s)',
    (locale) => {
      const { document, snapshot } = documentFor(erasurePage(), inLocale(locale));
      expect(document.querySelectorAll('h1')).toHaveLength(1);
      expect(document.querySelector('h1')?.textContent).toBe(
        locale === 'en'
          ? "Erase a revoked member's data"
          : 'Sonlandırılmış bir üyenin verilerini sil',
      );
      const text = document.querySelector('.auth-data-requests')!.textContent!;
      // The page states only what it knows: it files the erasure of the membership its address names, and says what a
      // revoked membership looks like. It never claims that this one was revoked, because it reads nothing about it.
      expect(text).toContain(
        locale === 'en'
          ? 'This page asks for the erasure of the membership named below.'
          : 'Bu sayfa, aşağıda adı geçen üyeliğin silinmesini ister.',
      );
      expect(text).not.toContain(
        locale === 'en' ? 'This membership was revoked.' : 'Bu üyelik sonlandırıldı.',
      );
      // The identifier from the address is shown, so the administrator can match it against the revocation they made.
      expect(document.querySelector('.auth-data-requests code')?.textContent).toBe(
        uiMember.membershipId,
      );
      // Nothing else about the Membership was read: no name, and no link to a member page that answers 404.
      expect(document.querySelector('a[href^="/studio/members/"]')).toBeNull();
      const [form, ...rest] = forms(document);
      expect(rest).toHaveLength(0);
      expect(form!.getAttribute('action')).toBe('/api/studio/data-request-erasure');
      const disclosure = form!.closest('details.auth-danger')!;
      expect(disclosure.querySelector('summary')?.textContent).toBe(
        locale === 'en' ? 'Review the erasure' : 'Silme işlemini gözden geçir',
      );
      expect(disclosure.querySelectorAll('h2')).toHaveLength(2);
      expect(disclosure.querySelectorAll('ul')).toHaveLength(2);
      expect(disclosure.textContent).toContain(locale === 'en' ? '24 hours' : '24 saat');
      expect(disclosure.textContent).toContain(
        locale === 'en'
          ? 'the person can cancel it on their own data page'
          : 'kişi talebi kendi veri sayfasından',
      );
      expect(disclosure.textContent).toContain(
        locale === 'en'
          ? 'If the person is admitted to this scope again within those 24 hours, the erasure still runs.'
          : 'Kişi bu 24 saat içinde bu kapsama yeniden üye olsa bile silme işlemi yine yapılır.',
      );
      const fieldset = form!.querySelector('fieldset')!;
      expect(fieldset.querySelector('legend')?.textContent).toBe(
        locale === 'en' ? 'Confirm the erasure' : 'Silme talebini onaylayın',
      );
      const box = fieldset.querySelector<HTMLInputElement>('input[type="checkbox"]')!;
      expect(box.required).toBe(true);
      expect(box.getAttribute('aria-required')).toBe('true');
      expect(box.closest('label')?.textContent).toContain(locale === 'en' ? 'Required' : 'Zorunlu');
      expect(form!.checkValidity()).toBe(false);
      box.checked = true;
      expect(form!.checkValidity()).toBe(true);
      const values = fields(form!);
      expect([...values.keys()]).toEqual([
        'csrfToken',
        'idempotencyKey',
        'membershipId',
        'confirm',
      ]);
      expect(bffAdministrationForm('/api/studio/data-request-erasure', values)).toEqual({
        operation: {
          kind: 'createDataErasureForSubject',
          membershipId: uiMember.membershipId,
          idempotencyKey: snapshot.formKeys[0],
        },
        destination: '/studio/data-requests/erasure?membership=' + uiMember.membershipId,
      });
      expect(form!.querySelector('button')?.getAttribute('aria-describedby')).toBe(
        'data-erasure-timing',
      );
      expect(document.getElementById('data-erasure-timing')).not.toBeNull();
      expect(document.querySelector('script')).toBeNull();
    },
  );

  it('offers no filing when the page was opened without a Membership, nor without the capability', () => {
    const missing = documentFor(erasurePage(null)).document;
    expect(forms(missing)).toHaveLength(0);
    expect(missing.body.textContent).toContain('No membership was named.');
    expect(missing.querySelector('.auth-data-requests a[href="/studio/members"]')).not.toBeNull();
    const { snapshot } = documentFor(erasurePage());
    const without = documentFor(erasurePage(), {
      capabilities: { ...snapshot.capabilities!, canManageMemberships: false },
    }).document;
    expect(forms(without)).toHaveLength(0);
    expect(without.querySelector('.auth-data-requests')).toBeNull();
  });
});

describe("the administrator's derivations follow the person's page", () => {
  it('derives due and cancellable exactly as the person page does for a request the caller filed', () => {
    const times = [
      '2026-11-24T09:59:59.000Z',
      '2026-11-24T10:00:00.000Z',
      '2026-11-24T10:00:01.000Z',
    ];
    for (const kind of ['export', 'erasure'] as const)
      for (const status of [
        'requested',
        'scheduled',
        'fulfilling',
        'awaiting_privileged_step',
        'completed',
        'blocked',
        'failed',
        'cancelled',
      ] as const)
        for (const at of times) {
          const item = request({
            kind,
            status,
            executeAt: kind === 'erasure' ? '2026-11-24T10:00:00.0000000+00:00' : null,
          });
          expect(dataRequestDisplayState(item, at)).toBe(dataRequestState(item, at));
          expect(administratorMayCancel(item, at)).toBe(dataRequestCancellable(item, at));
          expect(administratorMayCancel({ ...item, requestedByYou: false }, at)).toBe(false);
          expect(administratorMayCancel({ ...item, origin: 'self' }, at)).toBe(false);
        }
    expect(administratorMayRevoke(exported())).toBe(true);
    expect(administratorMayRevoke(exported({ requestedByYou: false }))).toBe(false);
    expect(administratorMayRevoke(exported({ origin: 'self' }))).toBe(false);
  });
});

describe('one file, one date on both surfaces', () => {
  it('shows a file as no longer available since the earlier of its expiry and its revocation', () => {
    const expiresAt = '2026-11-08T10:00:00.0000000+00:00';
    const cases = [
      // Purged three hours after the expiry: the late purge ended nothing, so the expiry is the date.
      { purgedAt: '2026-11-08T13:00:00.0000000+00:00', expected: expiresAt },
      // Revoked a day before the expiry: reads stopped then.
      {
        purgedAt: '2026-11-07T10:00:00.0000000+00:00',
        expected: '2026-11-07T10:00:00.0000000+00:00',
      },
      // Never purged.
      { purgedAt: null, expected: expiresAt },
    ] as const;
    for (const { purgedAt, expected } of cases) {
      expect(dataRequestUnavailableSince({ expiresAt, purgedAt }), String(purgedAt)).toBe(expected);
      expect(
        dataRequestAdministratorUnavailableSince({ expiresAt, purgedAt }),
        String(purgedAt),
      ).toBe(expected);
    }
  });
});
