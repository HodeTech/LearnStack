import { readFileSync } from 'node:fs';
import { runInNewContext } from 'node:vm';

import {
  DATA_EXPORT_ARTIFACT_STATUSES,
  DATA_REQUEST_FAILURE_REASONS,
  DATA_REQUEST_KINDS,
  DATA_REQUEST_LAST_REFUSALS,
  DATA_REQUEST_ORIGINS,
  DATA_REQUEST_RETAINED_KINDS,
  DATA_REQUEST_STATUSES,
} from '@learnstack/sdk/administration-server';
import type { OwnDataRequest } from '@learnstack/sdk/administration-server';
import { describe, expect, it, vi } from 'vitest';

import { dataRequestKeys, prepareDataRequestCatalogues } from './data-request-copy';
import {
  DATA_REQUEST_HISTORY_SOURCE,
  DATA_REQUEST_VOCABULARY,
  dataRequestCancellable,
  dataRequestHistorySource,
  dataRequestState,
  renderDataRequestDocument,
} from './data-request-html';
import type { DataRequestDocument, DataRequestPage } from './data-request-html';
import { DATA_REQUEST_REFUSALS } from './native-data-request-http';
import snapshot from '../../../../../../backend/openapi/v1.json';
import en from '../../i18n/messages/en/data.json';
import tr from '../../i18n/messages/tr/data.json';
import {
  ENVELOPE_REASONS,
  PERSON_ROUTES,
  identityDataRequestReasons,
} from '../../test/data-request-reasons';
import { checkCatalogues, publicCallsiteKeys } from '../../test/ui-catalogue-checks';

const nonce = 'N'.repeat(22) + '==';
const proof = 'c'.repeat(43);
const keys = Array.from({ length: 8 }, (_, index) => String(index).repeat(43));
const select = prepareDataRequestCatalogues({ en, tr });
const base = (value: Partial<OwnDataRequest> = {}): OwnDataRequest => ({
  id: '019a154b-0000-7000-8000-000000000041',
  kind: 'export',
  origin: 'self',
  status: 'requested',
  createdAt: '2026-11-23T10:00:00Z',
  updatedAt: null,
  completedAt: null,
  lastRefusal: null,
  failureReason: null,
  retained: [],
  artifact: null,
  version: '3',
  executeAt: null,
  ...value,
});
const page = (value: Partial<DataRequestPage> = {}): DataRequestPage => ({
  kind: 'data_requests',
  mode: 'session',
  scope: 'organization',
  group: null,
  scopes: null,
  requests: {
    items: [],
    pageInfo: { nextCursor: null, previousCursor: null, hasNext: false, hasPrevious: false },
  },
  step: null,
  result: null,
  preparedAt: '2026-11-23T12:00:00.000Z',
  entryExpiresAt: null,
  csrfToken: proof,
  formKeys: keys,
  ...value,
});
const render = (value: Partial<DataRequestPage> = {}, locale: 'en' | 'tr' = 'en') =>
  new DOMParser().parseFromString(
    renderDataRequestDocument(
      { kind: 'page', page: page(value) },
      { locale, phrase: select(locale === 'tr' ? 'tr' : 'en').phrase, nonce },
    ),
    'text/html',
  );

describe('the closed data-request copy', () => {
  it('has equal, plain en/tr catalogues used through literal keys only', () => {
    const source = readFileSync('src/server/auth/data-request-html.ts', 'utf8');
    const used = publicCallsiteKeys(source, 'data-request-html.ts', 'text');
    expect(used.length).toBeGreaterThan(80);
    checkCatalogues([en, tr], [...used, ...dataRequestKeys]);
    expect(new Set(dataRequestKeys).size).toBe(dataRequestKeys.length);
    expect(select('tr-TR,tr;q=0.9').phrase('common.title')).toBe(tr.common.title);
    expect(select('de').phrase('common.title')).toBe(en.common.title);
    expect(select(null).locale).toBe('en');
    expect(() => publicCallsiteKeys('text(problem.messageKey)', 'dirty.ts', 'text')).toThrow(
      'literal',
    );
  });

  it('refuses a catalogue that is incomplete, extended, parameterized or marked up', () => {
    const missing = structuredClone(tr) as Record<string, Record<string, unknown>>;
    delete missing.refusal!.subject_not_admitted;
    const extra = structuredClone(en) as Record<string, Record<string, unknown>>;
    extra.refusal!.unknown = 'Unknown';
    const parameter = structuredClone(en);
    parameter.export.action = 'Ask for {count} copies';
    const markup = structuredClone(tr);
    markup.export.action = '<b>Kopya</b>';
    for (const catalogues of [
      { en, tr: missing },
      { en: extra, tr },
      { en: parameter, tr },
      { en, tr: markup },
    ])
      expect(() => prepareDataRequestCatalogues(catalogues)).toThrow(
        'Data request catalogue unavailable',
      );
  });

  it('names every closed wire value the SDK admits', () => {
    expect(Object.keys(DATA_REQUEST_VOCABULARY.KINDS).sort()).toEqual(
      [...DATA_REQUEST_KINDS].sort(),
    );
    expect(Object.keys(DATA_REQUEST_VOCABULARY.STATUSES).sort()).toEqual(
      [...DATA_REQUEST_STATUSES, 'due'].sort(),
    );
    expect(Object.keys(DATA_REQUEST_VOCABULARY.LAST_REFUSALS).sort()).toEqual(
      [...DATA_REQUEST_LAST_REFUSALS].sort(),
    );
    expect(Object.keys(DATA_REQUEST_VOCABULARY.FAILURES).sort()).toEqual(
      [...DATA_REQUEST_FAILURE_REASONS].sort(),
    );
    expect(Object.keys(DATA_REQUEST_VOCABULARY.RETAINED).sort()).toEqual(
      [...DATA_REQUEST_RETAINED_KINDS].sort(),
    );
    const schemas = snapshot.components.schemas as unknown as Record<
      string,
      { properties: Record<string, { enum?: (string | null)[]; items?: unknown }> }
    >;
    const own = schemas.OwnDataRequestResponse!.properties;
    expect([...DATA_REQUEST_STATUSES].sort()).toEqual([...own.status!.enum!].sort());
    expect([...DATA_REQUEST_KINDS].sort()).toEqual([...own.kind!.enum!].sort());
    expect([...DATA_REQUEST_LAST_REFUSALS, null]).toEqual(own.lastRefusal!.enum);
    expect([...DATA_REQUEST_FAILURE_REASONS, null]).toEqual(own.failureReason!.enum);
    expect([...DATA_REQUEST_RETAINED_KINDS].sort()).toEqual(
      [...schemas.DataRequestRetainedResponse!.properties.kind!.enum!].sort(),
    );
    // The two lists a receipt is gated by that the renderer does not print by name: one drift refuses the whole page.
    expect([...DATA_REQUEST_ORIGINS].sort()).toEqual([...own.origin!.enum!].sort());
    expect([...DATA_EXPORT_ARTIFACT_STATUSES].sort()).toEqual(
      [...schemas.DataExportArtifactResponse!.properties.status!.enum!].sort(),
    );
  });

  it('has one sentence for every refusal reason the API documents or emits', () => {
    const reasons = identityDataRequestReasons(PERSON_ROUTES);
    expect(reasons.size).toBeGreaterThan(12);
    const unmapped = [...reasons].filter(
      (reason) => !Object.hasOwn(DATA_REQUEST_REFUSALS, reason) && !ENVELOPE_REASONS.has(reason),
    );
    expect(unmapped).toEqual([]);
    for (const key of Object.values(DATA_REQUEST_REFUSALS)) expect(dataRequestKeys).toContain(key);
    // The brief's closed list is all present, so a key removed from the map fails here too.
    expect(Object.keys(DATA_REQUEST_REFUSALS).sort()).toEqual(
      [
        'lockey_request_already_active',
        'lockey_blocked_hold',
        'lockey_blocked_last_administrator',
        'lockey_subject_export_available',
        'lockey_subject_membership_current',
        'lockey_data_export_unavailable',
        'lockey_data_request_window_ended',
        'lockey_data_request_not_cancellable',
        'lockey_subject_is_requester',
        'lockey_snapshot_too_large',
        'lockey_data_export_ticket_limit',
        'lockey_subject_not_admitted',
      ].sort(),
    );
  });
});

describe('accessible native data-request documents', () => {
  it('has one h1, landmarks, a skip link, list semantics and no table', () => {
    const document = render({
      requests: {
        items: [
          base(),
          base({
            id: '019a154b-0000-7000-8000-000000000042',
            kind: 'closure',
            status: 'scheduled',
            executeAt: '2026-11-24T10:00:00Z',
          }),
        ],
        pageInfo: { nextCursor: null, previousCursor: null, hasNext: false, hasPrevious: false },
      },
    });
    expect(document.querySelectorAll('h1')).toHaveLength(1);
    expect(document.querySelector('main#main')?.getAttribute('tabindex')).toBe('-1');
    expect(document.querySelector('a.skip')?.getAttribute('href')).toBe('#main');
    expect(document.documentElement.getAttribute('lang')).toBe('en');
    expect(document.querySelectorAll('ul.requests > li > article')).toHaveLength(2);
    for (const article of document.querySelectorAll('article'))
      expect(document.getElementById(article.getAttribute('aria-labelledby')!)?.tagName).toBe('H3');
    for (const section of document.querySelectorAll('section'))
      expect(document.getElementById(section.getAttribute('aria-labelledby')!)?.tagName).toBe('H2');
    expect(document.querySelector('table')).toBeNull();
    for (const icon of document.querySelectorAll('svg')) {
      expect(icon.getAttribute('aria-hidden')).toBe('true');
      expect(icon.getAttribute('focusable')).toBe('false');
      expect(icon.parentElement?.textContent).toContain(en.list.status + ':');
    }
    expect(document.querySelector('time')?.getAttribute('datetime')).toBe('2026-11-23T10:00:00Z');
    for (const button of document.querySelectorAll('button'))
      expect(button.textContent?.trim()).not.toBe('');
    const css = document.querySelector('style')!.textContent!;
    expect(css).toContain('min-height:44px');
    expect(css).toContain(':focus-visible');
    expect(css).toContain('prefers-reduced-motion');
  });

  it('groups each confirmation in a fieldset with a legend and labelled required boxes', () => {
    for (const [step, boxes] of [
      ['erasure', 1],
      ['closure', 2],
    ] as const) {
      const document = render({ step });
      const fieldset = document.querySelector('form fieldset')!;
      expect(fieldset.querySelector('legend')?.textContent).toBe(en[step].legend);
      const inputs = fieldset.querySelectorAll('input[type="checkbox"]');
      expect(inputs).toHaveLength(boxes);
      for (const input of inputs) {
        expect(input.hasAttribute('required')).toBe(true);
        expect(input.closest('label')?.textContent?.length).toBeGreaterThan(20);
      }
      expect(document.querySelector('main')?.getAttribute('data-data-request-view')).toBe(
        'session_' + step,
      );
    }
  });

  it('announces a result without script and keeps errors in an alert', () => {
    const done = render({ result: 'revoked' });
    expect(done.querySelector('[role="status"]')?.textContent).toContain(en.result.revoked);
    expect(done.title.startsWith(en.result.title)).toBe(true);
    const failure = new DOMParser().parseFromString(
      renderDataRequestDocument(
        { kind: 'error', message: 'refusal.blocked_hold', back: { kind: 'page', mode: 'entry' } },
        { locale: 'tr', phrase: select('tr').phrase, nonce },
      ),
      'text/html',
    );
    expect(failure.querySelector('[role="alert"]')?.textContent).toContain(tr.refusal.blocked_hold);
    expect([...failure.querySelectorAll('a.action')].map((a) => a.getAttribute('href'))).toEqual([
      '/account/data',
      '/api/auth/entry',
    ]);
  });

  it('derives due and cancellable from the closed status and executeAt only', () => {
    const at = '2026-11-23T12:00:00.000Z';
    const scheduled = base({
      kind: 'erasure',
      status: 'scheduled',
      executeAt: '2026-11-23T12:00:01Z',
    });
    const due = base({ kind: 'erasure', status: 'scheduled', executeAt: '2026-11-23T12:00:00Z' });
    expect(dataRequestState(scheduled, at)).toBe('scheduled');
    expect(dataRequestState(due, at)).toBe('due');
    expect(dataRequestCancellable(scheduled, at)).toBe(true);
    expect(dataRequestCancellable(due, at)).toBe(false);
    const closure = base({
      kind: 'closure',
      status: 'scheduled',
      executeAt: '2026-11-20T12:00:00Z',
    });
    expect(dataRequestCancellable(closure, at)).toBe(true);
    expect(dataRequestCancellable({ ...closure, status: 'fulfilling' }, at)).toBe(false);
    expect(dataRequestCancellable(base(), at)).toBe(true);
    expect(dataRequestCancellable(base({ status: 'fulfilling' }), at)).toBe(false);
    expect(
      dataRequestCancellable(
        base({ kind: 'erasure', origin: 'closure_fanout', status: 'scheduled' }),
        at,
      ),
    ).toBe(false);
  });

  it('escapes every printed value and refuses a malformed proof, key, group or nonce', () => {
    expect(() =>
      renderDataRequestDocument(
        { kind: 'page', page: page({ csrfToken: '<x>' }) },
        { locale: 'en', phrase: select('en').phrase, nonce },
      ),
    ).toThrow('Data request response unavailable');
    expect(() =>
      renderDataRequestDocument(
        { kind: 'page', page: page({ mode: 'entry', scope: null, group: '<script>' }) },
        { locale: 'en', phrase: select('en').phrase, nonce },
      ),
    ).toThrow('Data request response unavailable');
    expect(() =>
      renderDataRequestDocument(
        { kind: 'signin' },
        { locale: 'en', phrase: select('en').phrase, nonce: '"><script>' },
      ),
    ).toThrow('Data request response unavailable');
    expect(() =>
      renderDataRequestDocument(
        { kind: 'page', page: page({ formKeys: [] }) },
        { locale: 'en', phrase: select('en').phrase, nonce },
      ),
    ).toThrow('Data request response unavailable');
  });
});

describe('hostile request values never leave their attribute', () => {
  it('escapes an id and a version in every hidden field the page prints', () => {
    const hostile = '"><img src=x onerror=alert(1)><input value="';
    const version = "3' onfocus='x\"><b>bold</b>";
    const items = [
      base({ id: hostile, version }),
      base({
        id: hostile + '2',
        status: 'completed',
        completedAt: '2026-11-22T10:00:00Z',
        artifact: {
          id: '019a154b-0000-7000-8000-0000000000f1',
          status: 'available',
          expiresAt: '2026-11-30T10:00:00Z',
          purgedAt: null,
          byteCount: 1536,
          chunkCount: 1,
          truncations: [],
          version,
          readable: true,
        },
      }),
    ];
    for (const mode of ['session', 'entry'] as const) {
      const html = renderDataRequestDocument(
        {
          kind: 'page',
          page: page({
            mode,
            scope: mode === 'session' ? 'organization' : null,
            group: mode === 'entry' ? 'tenant' : null,
            requests: { items, pageInfo: page().requests!.pageInfo },
          }),
        },
        { locale: 'en', phrase: select('en').phrase, nonce },
      );
      // No markup of the hostile value survives in the document text…
      expect(html).not.toContain('<img');
      expect(html).not.toContain('<b>bold');
      expect(html).toContain('&quot;&gt;&lt;img src=x onerror=alert(1)&gt;');
      // …and read back as a document, each field holds the value whole.
      const parsed = new DOMParser().parseFromString(html, 'text/html');
      expect(parsed.querySelector('img')).toBeNull();
      expect(parsed.querySelector('b')).toBeNull();
      const field = (name: string) =>
        [
          ...parsed.querySelectorAll<HTMLInputElement>('input[type="hidden"][name="' + name + '"]'),
        ].map((input) => input.value);
      // Cancel, download and revoke each carry the request; cancel and revoke carry a version.
      expect(field('requestId')).toEqual([hostile, hostile + '2', hostile + '2']);
      expect(field('version')).toEqual([version, version]);
      expect(parsed.querySelectorAll('form')).toHaveLength(3);
    }
  });
});

/** Every closed state a person can observe, with the codes the backend stores beside each. */
const everyState = (): OwnDataRequest[] => {
  const item = (n: number, value: Partial<OwnDataRequest>) =>
    base({ id: '019a154b-0000-7000-8000-0000000000' + String(n).padStart(2, '0'), ...value });
  const artifact = (value: Partial<NonNullable<OwnDataRequest['artifact']>> = {}) => ({
    id: '019a154b-0000-7000-8000-0000000000f1',
    status: 'available' as const,
    expiresAt: '2026-11-30T10:00:00Z',
    purgedAt: null,
    byteCount: 1536,
    chunkCount: 1,
    truncations: [],
    version: '9007199254740993',
    readable: true,
    ...value,
  });
  return [
    item(1, {}),
    item(2, {
      status: 'completed',
      completedAt: '2026-11-22T10:00:00Z',
      artifact: artifact({ truncations: [{ collection: 'audit.entries', included: 5, total: 9 }] }),
    }),
    item(3, {
      status: 'completed',
      completedAt: '2026-11-01T10:00:00Z',
      artifact: artifact({ status: 'expired', readable: false, expiresAt: '2026-11-08T10:00:00Z' }),
    }),
    item(4, { kind: 'erasure', status: 'scheduled', executeAt: '2026-11-24T10:00:00Z' }),
    item(5, { kind: 'erasure', status: 'scheduled', executeAt: '2026-11-23T11:00:00Z' }),
    item(6, {
      kind: 'erasure',
      status: 'awaiting_privileged_step',
      origin: 'administrator',
      executeAt: '2026-11-20T10:00:00Z',
      retained: [{ kind: 'consent_evidence', count: 12345 }],
    }),
    item(7, {
      kind: 'erasure',
      status: 'completed',
      completedAt: '2026-11-21T10:00:00Z',
      retained: [{ kind: 'audit_actor_reference', count: 12 }],
    }),
    item(8, {
      kind: 'erasure',
      status: 'blocked',
      lastRefusal: 'blocked_hold',
      updatedAt: '2026-11-19T10:00:00Z',
    }),
    item(9, {
      status: 'failed',
      failureReason: 'execution_failed',
      updatedAt: '2026-11-18T10:00:00Z',
    }),
    item(10, { status: 'cancelled', updatedAt: '2026-11-17T10:00:00Z' }),
    item(11, {
      kind: 'closure',
      status: 'scheduled',
      executeAt: '2026-11-23T11:00:00Z',
      lastRefusal: 'blocked_last_administrator',
    }),
    item(12, { kind: 'erasure', origin: 'closure_fanout', status: 'fulfilling' }),
  ];
};
const listed = (items: OwnDataRequest[], value: Partial<DataRequestPage> = {}) => ({
  requests: {
    items,
    pageInfo: { nextCursor: null, previousCursor: null, hasNext: false, hasPrevious: false },
  },
  formKeys: Array.from({ length: 2 + 2 * items.length }, (_, index) =>
    String.fromCharCode(97 + (index % 26)).repeat(43),
  ),
  ...value,
});
const documentOf = (value: DataRequestDocument, locale: 'en' | 'tr' = 'en') =>
  new DOMParser().parseFromString(
    renderDataRequestDocument(value, { locale, phrase: select(locale).phrase, nonce }),
    'text/html',
  );
const articles = (document: Document) => [...document.querySelectorAll('article')];
const articleText = (document: Document, index: number) => articles(document)[index]!.textContent!;
const leaves = (catalogue: object, prefix = ''): [string, string][] =>
  Object.entries(catalogue).flatMap(([key, value]) =>
    typeof value === 'string'
      ? [[prefix + key, value] as [string, string]]
      : leaves(value as object, prefix + key + '.'),
  );

describe('truthful next steps on the person’s requests', () => {
  it('names why an erasure stopped and promises no new request while that reason lasts', () => {
    const blocked = (lastRefusal: OwnDataRequest['lastRefusal']) =>
      base({ kind: 'erasure', status: 'blocked', lastRefusal, updatedAt: '2026-11-19T10:00:00Z' });
    const document = render(
      listed([blocked('blocked_hold'), blocked('blocked_last_administrator')]),
    );
    expect(articleText(document, 0)).toContain(en.next.blocked);
    expect(articleText(document, 0)).toContain(en.blocked_reason.blocked_hold);
    expect(articleText(document, 1)).toContain(en.blocked_reason.blocked_last_administrator);
    for (const text of articles(document).map((article) => article.textContent!)) {
      expect(text).not.toContain('make a new request');
      // A stopped erasure is not a scheduled one: the scheduled closure's sentences never describe it.
      expect(text).not.toContain(en.last_refusal.blocked_hold);
      expect(text).not.toContain(en.last_refusal.blocked_last_administrator);
    }
    for (const value of Object.values(en.blocked_reason)) expect(value).toContain('ask again');
    expect(en.next.blocked).not.toContain('new request');
  });

  it('sends a too-large export to the institution instead of to a new request', () => {
    const failed = (failureReason: OwnDataRequest['failureReason']) =>
      base({ status: 'failed', failureReason, updatedAt: '2026-11-18T10:00:00Z' });
    const document = render(listed([failed('export_too_large'), failed('execution_failed')]));
    expect(articleText(document, 0)).toContain(en.failure.export_too_large);
    expect(articleText(document, 0)).not.toContain('make a new request');
    expect(articleText(document, 1)).toContain(en.failure.execution_failed);
    expect(en.next.failed).not.toContain('new request');
  });

  it('calls the activity-record counts provisional only while the operator step is owed', () => {
    const erasure = (status: OwnDataRequest['status']) =>
      base({
        kind: 'erasure',
        status,
        executeAt: '2026-11-20T10:00:00Z',
        retained: [{ kind: 'audit_metadata_reference', count: 40 }],
        updatedAt: '2026-11-21T10:00:00Z',
        ...(status === 'completed' ? { completedAt: '2026-11-21T10:00:00Z' } : {}),
        ...(status === 'failed' ? { failureReason: 'execution_failed' as const } : {}),
      });
    const document = render(
      listed([
        erasure('fulfilling'),
        erasure('awaiting_privileged_step'),
        erasure('completed'),
        erasure('failed'),
      ]),
    );
    expect(articleText(document, 0)).toContain(en.retained.pending);
    expect(articleText(document, 1)).toContain(en.retained.pending);
    expect(articleText(document, 2)).not.toContain(en.retained.pending);
    expect(articleText(document, 3)).not.toContain(en.retained.pending);
  });

  it('dates an unavailable file by its expiry or its revocation, never by a late purge', () => {
    const file = (status: 'purged' | 'revoked' | 'expired', purgedAt: string | null) =>
      base({
        status: 'completed',
        completedAt: '2026-11-01T10:00:00Z',
        artifact: {
          id: '019a154b-0000-7000-8000-0000000000f1',
          status,
          expiresAt: '2026-11-08T10:00:00Z',
          purgedAt,
          byteCount: 10,
          chunkCount: 1,
          truncations: [],
          version: '4',
          readable: false,
        },
      });
    const document = render(
      listed([
        file('purged', '2026-11-10T03:00:00Z'),
        file('revoked', '2026-11-03T09:30:00Z'),
        file('expired', null),
      ]),
    );
    const since = (index: number) =>
      articles(document)[index]!.querySelectorAll('dd time')[2]!.getAttribute('datetime');
    expect(since(0)).toBe('2026-11-08T10:00:00Z');
    expect(since(1)).toBe('2026-11-03T09:30:00Z');
    expect(since(2)).toBe('2026-11-08T10:00:00Z');
  });

  it('says what a closure leaves in suspended places and what the copy and erasure keep', () => {
    const closure = render({ step: 'closure' }).body.textContent!;
    expect(closure).toContain(en.closure.warning_unserved);
    expect(en.closure.warning_data).toContain('where that is possible');
    for (const text of [en.erasure.kept_uncounted, en.retained.partial]) {
      expect(text).toContain('encrypted copies of invitation e-mails');
      expect(text).toContain('does not remove them automatically yet');
      expect(text).not.toContain('retention rules remove');
    }
    expect(en.retained.audit_retention).not.toMatch(/\d/);
    expect(en.erasure.kept_intro).not.toContain('have to');
    expect(en.retained.kind.unscoped_audit_rows).not.toContain('branch');
    expect(en.refusal.data_request_window_ended).not.toContain('has started');
    expect(en.export.timing).toContain('When a new copy is ready');
  });

  it('describes a session’s own membership and the way to a suspended or ended one', () => {
    for (const [scope, sentence] of [
      ['tenant', en.scope.session_tenant],
      ['organization', en.scope.session_organization],
    ] as const) {
      const text = render({ scope }).body.textContent!;
      expect(text).toContain(sentence);
      expect(text).toContain(en.scope.session_retained);
    }
    expect(en.scope.session_tenant).not.toContain('whole institution');
    expect(en.erasure.summary).toContain('this one membership');
  });
});

describe('the retained entry’s documents', () => {
  const entry = (value: Partial<DataRequestPage> = {}) =>
    render({ mode: 'entry', scope: null, group: 'tenant', ...value });

  it('introduces the chooser for every membership status and names its own title', () => {
    const document = entry({
      group: null,
      requests: null,
      scopes: {
        items: [
          { tenantId: '019a154b-0000-7000-8000-0000000000a0', organizationId: null },
          {
            tenantId: '019a154b-0000-7000-8000-0000000000a0',
            organizationId: '019a154b-0000-7000-8000-0000000000a1',
          },
        ],
        pageInfo: { nextCursor: null, previousCursor: null, hasNext: false, hasPrevious: false },
      },
    });
    expect(document.body.textContent).toContain(en.scope.entry_intro);
    expect(en.scope.entry_intro).not.toMatch(/^Your membership here is/);
    expect(document.title).toBe(en.scope.entry_choose + ' – ' + en.common.title);
    // Each button is named by its own text and the place it opens; the numbering restarts per page.
    const buttons = [...document.querySelectorAll('section button')];
    expect(
      buttons.map((button) =>
        button
          .getAttribute('aria-labelledby')!
          .split(' ')
          .map((id) => document.getElementById(id)?.textContent)
          .join(' '),
      ),
    ).toEqual([
      en.scope.view + ' ' + en.scope.entry_tenant,
      en.scope.view + ' ' + en.scope.entry_organization + ' 1',
    ]);
    expect(document.body.textContent).toContain(en.scope.entry_names);
  });

  it('states the entry’s limit, when it ends and the time that was left', () => {
    const at = (entryExpiresAt: string) =>
      entry({ entryExpiresAt }).querySelector('main > .notice:not([role])')!;
    const notice = at('2026-11-23T12:04:30.000Z');
    expect(notice.textContent).toContain(en.scope.entry_time_limit);
    expect(notice.querySelector('time')?.getAttribute('datetime')).toBe('2026-11-23T12:04:30.000Z');
    expect(notice.textContent).toContain(en.scope.entry_time_until);
    expect(notice.querySelectorAll('dd')[1]?.textContent).toBe('4 minutes');
    expect(at('2026-11-23T12:00:40.000Z').querySelectorAll('dd')[1]?.textContent).toBe(
      en.scope.entry_time_under_minute,
    );
    // Every retained document says it: the chooser and the confirmation steps as well as the list.
    for (const value of [
      { group: null, requests: null, scopes: { items: [], pageInfo: page().requests!.pageInfo } },
      { step: 'erasure' as const },
    ])
      expect(
        entry({ entryExpiresAt: '2026-11-23T12:04:30.000Z', ...value }).body.textContent,
      ).toContain(en.scope.entry_time_limit);
    expect(render({ entryExpiresAt: null }).body.textContent).not.toContain(
      en.scope.entry_time_limit,
    );
    expect(() => entry({ entryExpiresAt: 'later' })).toThrow('Data request response unavailable');
  });

  it('restores a retained group, never a result, step or cursor, and refuses a malformed group', () => {
    const install = (source: string) => {
      const listeners = new Map<string, (event?: { persisted?: boolean }) => void>();
      const replace = vi.fn();
      const replaceState = vi.fn();
      runInNewContext(source, {
        addEventListener: (name: string, callback: (event?: { persisted?: boolean }) => void) =>
          listeners.set(name, callback),
        document: { documentElement: { style: { visibility: '' } } },
        history: { replaceState },
        location: { replace, reload: vi.fn() },
      });
      listeners.get('pagehide')!();
      listeners.get('pageshow')!({ persisted: true });
      return [replaceState.mock.calls[0]?.[2], replace.mock.calls[0]?.[0]];
    };
    const organization = '019a154b-0000-7000-8000-0000000000a1';
    expect(install(dataRequestHistorySource(organization))).toEqual([
      '/account/data?group=' + organization,
      '/account/data?group=' + organization,
    ]);
    expect(install(DATA_REQUEST_HISTORY_SOURCE)).toEqual(['/account/data', '/account/data']);
    for (const group of ["tenant'", '019a154b-0000-4000-8000-0000000000a1', 'x'])
      expect(() => dataRequestHistorySource(group)).toThrow('Data request response unavailable');
    const script = (document: Document) => document.querySelector('script')!.textContent;
    expect(script(entry({ result: 'export', step: null }))).toBe(
      dataRequestHistorySource('tenant'),
    );
    expect(script(entry({ step: 'closure' }))).toBe(dataRequestHistorySource('tenant'));
    expect(script(render({ result: 'export' }))).toBe(DATA_REQUEST_HISTORY_SOURCE);
    expect(script(documentOf({ kind: 'signin' }))).toBe(DATA_REQUEST_HISTORY_SOURCE);
    // The reload hint no longer sends a retained person to a chooser: it asks them to come back.
    for (const text of [en.next.export_requested, en.result.export])
      expect(text).not.toContain('reload');
  });
});

describe('accessible names, titles and refusal headings', () => {
  it('titles each confirmation step and names each request by its kind and date', () => {
    expect(render({ step: 'erasure' }).title).toBe(en.erasure.step_title + ' – ' + en.common.title);
    expect(render({ step: 'closure', result: 'export' }).title).toBe(
      en.closure.step_title + ' – ' + en.common.title,
    );
    const document = render(listed(everyState()));
    const list = document.querySelector('ul.requests')!;
    expect(list.getAttribute('role')).toBe('list');
    const headings = [...document.querySelectorAll('article > h3')];
    expect(headings[0]?.textContent).toBe(en.kind.export + ' (Nov 23, 2026, 10:00 AM UTC)');
    expect(headings[0]?.querySelector('time')?.getAttribute('datetime')).toBe(
      '2026-11-23T10:00:00Z',
    );
    // Each repeated button's name is its own visible text followed by its request's heading.
    const buttons = [...document.querySelectorAll('article button')];
    expect(buttons.length).toBeGreaterThan(4);
    const names = buttons.map((button) => {
      const [self, item, ...rest] = button.getAttribute('aria-labelledby')!.split(' ');
      expect(rest).toEqual([]);
      expect(self).toBe(button.id);
      expect(button.closest('article')?.getAttribute('aria-labelledby')).toBe(item);
      expect(document.getElementById(item!)?.tagName).toBe('H3');
      return button.textContent + ' ' + document.getElementById(item!)!.textContent;
    });
    expect(new Set(buttons.map((button) => button.id)).size).toBe(buttons.length);
    expect(new Set(names).size).toBe(names.length);
    expect(names).toContain(
      en.action.cancel + ' ' + en.kind.export + ' (Nov 23, 2026, 10:00 AM UTC)',
    );
  });

  it('heads an unconfirmed outcome as unconfirmed and every other refusal neutrally', () => {
    for (const message of [
      'error.unconfirmed',
      'error.outcome_unavailable',
      'error.in_progress',
    ] as const) {
      const document = documentOf({ kind: 'error', message, back: { kind: 'page', mode: null } });
      expect(document.querySelector('[role="alert"] h2')?.textContent).toBe(
        en.error.title_unconfirmed,
      );
      expect(document.title).toBe(en.error.title_unconfirmed + ' – ' + en.common.title);
    }
    const refused = documentOf(
      { kind: 'error', message: 'error.not_found', back: { kind: 'page', mode: null } },
      'tr',
    );
    expect(refused.querySelector('[role="alert"] h2')?.textContent).toBe(tr.error.title);
    expect(en.error.title).not.toContain('could not be completed');
  });

  it('leads a refused session confirmation back to its step and never names a group', () => {
    const links = (mode: 'session' | 'entry' | null, step?: 'erasure' | 'closure') =>
      [
        ...documentOf({
          kind: 'error',
          message: 'error.confirmation',
          back: { kind: 'page', mode, ...(step ? { step } : {}) },
        }).querySelectorAll('a.action'),
      ].map((link) => link.getAttribute('href'));
    expect(links('session', 'erasure')).toEqual(['/account/data?step=erasure', '/portal']);
    expect(links('session', 'closure')).toEqual(['/account/data?step=closure', '/portal']);
    expect(links('session')).toEqual(['/account/data', '/portal']);
    // A retained step needs its group, and a refusal never repeats a submitted one: the way back is the chooser.
    expect(links('entry', 'closure')).toEqual(['/account/data', '/api/auth/entry']);
    expect(links(null)).toEqual(['/account/data']);
    expect(render().querySelector('noscript')?.textContent).toBe(en.common.script_note);
  });

  it('escapes hostile identifiers and versions in every form it prints', () => {
    const hostile = '"><script>alert(1)</script>';
    const html = renderDataRequestDocument(
      {
        kind: 'page',
        page: page(
          listed([
            base({ id: hostile, version: hostile }),
            base({
              id: hostile,
              status: 'completed',
              completedAt: '2026-11-22T10:00:00Z',
              artifact: {
                id: hostile,
                status: 'available',
                expiresAt: '2026-11-30T10:00:00Z',
                purgedAt: null,
                byteCount: 10,
                chunkCount: 1,
                truncations: [],
                version: hostile,
                readable: true,
              },
            }),
          ]),
        ),
      },
      { locale: 'en', phrase: select('en').phrase, nonce },
    );
    expect(html).not.toContain('<script>alert');
    expect(html.match(/<script/g)).toHaveLength(1);
    // The cancel form's requestId and version, the download's requestId, the revoke's requestId and artifact version.
    expect(html.split('value="&quot;&gt;&lt;script&gt;alert(1)&lt;/script&gt;"').length - 1).toBe(
      5,
    );
  });
});

describe('the Turkish document', () => {
  it('renders every closed state in Turkish with Turkish dates, sizes and numbers', () => {
    const document = render(listed(everyState()), 'tr');
    expect(document.documentElement.getAttribute('lang')).toBe('tr');
    const text = document.body.textContent! + document.title;
    for (const [key, value] of leaves(en))
      if (value !== leaves(tr).find(([candidate]) => candidate === key)?.[1])
        expect(text, key).not.toContain(value);
    for (const status of [
      'requested',
      'completed',
      'scheduled',
      'due',
      'awaiting_privileged_step',
      'blocked',
      'failed',
      'cancelled',
      'fulfilling',
    ] as const)
      expect(text).toContain(tr.list.status + ': ' + tr.status[status]);
    for (const sentence of [
      tr.next.blocked,
      tr.blocked_reason.blocked_hold,
      tr.last_refusal.blocked_last_administrator,
      tr.retained.pending,
      tr.artifact.part_included,
    ])
      expect(text).toContain(sentence);
    expect(document.querySelector('time')?.textContent).toBe('23 Kas 2026 10:00 UTC');
    expect(text).toContain('1,5 kB');
    expect(text).toContain('12.345');
    // The shortened part reads as label and value pairs, never as "9 toplam".
    const part = document.querySelector('article li dl')!;
    expect([...part.querySelectorAll('dt')].map((dt) => dt.textContent)).toEqual([
      tr.artifact.part_included,
      tr.artifact.part_total,
    ]);
    expect([...part.querySelectorAll('dd')].map((dd) => dd.textContent)).toEqual(['5', '9']);
    expect(text).not.toContain('makbuz');
    expect(JSON.stringify(tr)).not.toMatch(/\bizin|makbuz|Son indirme/);
    expect(tr.result.title).not.toBe(tr.status.completed);
  });
});
