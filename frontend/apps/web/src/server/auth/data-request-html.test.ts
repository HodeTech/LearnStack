import { readdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';

import {
  DATA_REQUEST_FAILURE_REASONS,
  DATA_REQUEST_KINDS,
  DATA_REQUEST_LAST_REFUSALS,
  DATA_REQUEST_RETAINED_KINDS,
  DATA_REQUEST_STATUSES,
} from '@learnstack/sdk/administration-server';
import type { OwnDataRequest } from '@learnstack/sdk/administration-server';
import { describe, expect, it } from 'vitest';

import { dataRequestKeys, prepareDataRequestCatalogues } from './data-request-copy';
import {
  DATA_REQUEST_VOCABULARY,
  dataRequestCancellable,
  dataRequestState,
  renderDataRequestDocument,
} from './data-request-html';
import type { DataRequestPage } from './data-request-html';
import { DATA_REQUEST_REFUSALS } from './native-data-request-http';
import snapshot from '../../../../../../backend/openapi/v1.json';
import en from '../../i18n/messages/en/data.json';
import tr from '../../i18n/messages/tr/data.json';
import { checkCatalogues, publicCallsiteKeys } from '../../test/ui-catalogue-checks';

const repository = join(process.cwd(), '../../..');
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

/** Every `lockey_*` reason the person's data-request routes document, and every one the Identity module spells. */
function apiReasons(): Set<string> {
  const reasons = new Set<string>();
  for (const [path, item] of Object.entries(
    snapshot.paths as Record<
      string,
      Record<string, { responses?: Record<string, { description?: string }> }>
    >,
  )) {
    if (
      !path.startsWith('/api/v1/identity/me/data-requests') &&
      !path.startsWith('/api/v1/identity/data-request-entry')
    )
      continue;
    for (const operation of Object.values(item))
      for (const response of Object.values(operation.responses ?? {}))
        for (const match of (response.description ?? '').matchAll(
          // A reason spelled with its wire prefix, or an artifact reason the document names without it.
          /\b(?:lockey_([a-z_]+)|(data_export_[a-z_]+))\b/g,
        ))
          reasons.add('lockey_' + (match[1] ?? match[2]));
  }
  const directory = join(
    repository,
    'backend/src/Modules/Identity/LearnStack.Modules.Identity.Application/DataRequests',
  );
  for (const file of readdirSync(directory).filter((name) => name.endsWith('.cs')))
    for (const match of readFileSync(join(directory, file), 'utf8').matchAll(/"(lockey_[a-z_]+)"/g))
      reasons.add(match[1]!);
  return reasons;
}
/** The generic envelope a refusal maps by HTTP status and code, never by a reason sentence. */
const ENVELOPE = new Set([
  'lockey_business_rule_violation',
  'lockey_not_found',
  'lockey_forbidden',
  'lockey_dependency_unavailable',
  'lockey_validation_failed',
  'lockey_invalid_value',
  'lockey_concurrency_conflict',
  'lockey_identifier_required',
]);

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
  });

  it('has one sentence for every refusal reason the API documents or emits', () => {
    const reasons = apiReasons();
    expect(reasons.size).toBeGreaterThan(12);
    const unmapped = [...reasons].filter(
      (reason) => !Object.hasOwn(DATA_REQUEST_REFUSALS, reason) && !ENVELOPE.has(reason),
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
        { kind: 'error', message: 'refusal.blocked_hold', back: { kind: 'page', group: 'tenant' } },
        { locale: 'tr', phrase: select('tr').phrase, nonce },
      ),
      'text/html',
    );
    expect(failure.querySelector('[role="alert"]')?.textContent).toContain(tr.refusal.blocked_hold);
    expect(failure.querySelector('a.action')?.getAttribute('href')).toBe(
      '/account/data?group=tenant',
    );
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
