import type { OwnDataRequest } from '@learnstack/sdk/administration-server';

import type { DataRequestCopy, DataRequestKey } from './data-request-copy.js';
import type { NativeDataRequestSnapshot } from './page-contract.js';

const uuid = (value: string) =>
  /^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/.test(value);

/**
 * A leaving or restored private document stays veiled until a fresh fixed GET is admitted. The fixed destination
 * keeps history restoration from replaying a POST result, exactly as the consent entry's guard does: it is the bare
 * page, or a retained group's page, so a reload or a restored page reopens that group while the entry lives instead
 * of sending the person back to the chooser. No result, step or cursor survives.
 */
export function dataRequestHistorySource(group: string | null = null): string {
  if (group !== null && group !== 'tenant' && !uuid(group))
    throw new Error('Data request response unavailable');
  const destination = '/account/data' + (group === null ? '' : '?group=' + group);
  return `(()=>{try{history.replaceState(null,'','${destination}')}catch{document.documentElement.style.visibility='hidden';location.replace('${destination}');return}let leaving=false;addEventListener('pagehide',()=>{leaving=true;document.documentElement.style.visibility='hidden'});addEventListener('pageshow',e=>{if(e.persisted||leaving){document.documentElement.style.visibility='hidden';location.replace('${destination}')}})})();`;
}
/** The guard of every document that shows no retained group: the session page, the chooser, sign-in and refusals. */
export const DATA_REQUEST_HISTORY_SOURCE = dataRequestHistorySource();

export type DataRequestPage = Extract<NativeDataRequestSnapshot, { kind: 'data_requests' }>;
/** The closed document set: a page, a sign-in prompt, or one refusal sentence with a fixed way back. */
export type DataRequestDocument =
  | Readonly<{ kind: 'page'; page: DataRequestPage }>
  | Readonly<{ kind: 'signin' }>
  | Readonly<{
      kind: 'error';
      message: DataRequestKey;
      /**
       * Where "back" leads: the fixed GET (never a submitted group, which may be the refusal's cause), in the session
       * mode the confirmation step a refused confirmation came from, and the owner mode's own exit; or sign-in after a
       * 401.
       */
      back:
        | Readonly<{ kind: 'page'; mode: 'session' | 'entry' | null; step?: 'erasure' | 'closure' }>
        | Readonly<{ kind: 'signin' }>;
    }>;

const escape = (value: string) =>
  value.replace(
    /[&<>"']/g,
    (character) =>
      ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[character]!,
  );
const hidden = (name: string, value: string) =>
  '<input type="hidden" name="' + name + '" value="' + escape(value) + '">';
const proof = (value: string) => /^[A-Za-z0-9_-]{43}$/.test(value);

const css = `*{box-sizing:border-box}html{color:#152238;background:#fff}body{margin:0;font:1rem/1.6 system-ui,sans-serif}main{width:min(100% - 2rem,48rem);margin:2rem auto;overflow-wrap:anywhere}h1,h2,h3,h4{line-height:1.25}a{color:#114499;text-underline-offset:.15em}a.action{display:inline-flex;align-items:center;min-height:44px;padding-block:.25rem}button{display:inline-flex;align-items:center;justify-content:center;min-height:44px;min-width:44px;max-width:100%;padding:.5rem 1rem;font:inherit;white-space:normal;color:#152238;background:#fff;border:2px solid currentColor;border-radius:.25rem;cursor:pointer}button.danger{color:#8a1c1c}section,article{margin-block:1.5rem}section>h2{border-top:1px solid #8a94a6;padding-top:1rem}ul.requests{list-style:none;padding:0}ul.requests>li{border:1px solid #8a94a6;border-radius:.5rem;padding:0 1rem;margin-block:1rem}dl{display:grid;grid-template-columns:minmax(0,max-content) minmax(0,1fr);gap:.25rem 1rem}dt{font-weight:700}dd{margin:0}.status{display:flex;gap:.5rem;align-items:center;font-weight:700}.status svg{flex:none;width:1.25rem;height:1.25rem}.notice{border:2px solid currentColor;border-radius:.5rem;padding:0 1rem;margin-block:1rem}fieldset{border:2px solid currentColor;border-radius:.5rem;margin-block:1rem;padding:.5rem 1rem}legend{font-weight:700;padding-inline:.25rem}label.check{display:flex;gap:.75rem;align-items:flex-start;min-height:44px;padding-block:.5rem;cursor:pointer}label.check input{flex:none;width:1.5rem;height:1.5rem;margin:.1rem 0 0}form{margin-block:1rem}:focus-visible{outline:3px solid #0759b8;outline-offset:3px}main:focus{outline:none}.skip{position:absolute;left:1rem;top:-10rem;background:#fff;padding:.75rem;z-index:1}.skip:focus{top:1rem}@media (max-width:30rem){dl{grid-template-columns:minmax(0,1fr)}dd{margin-bottom:.5rem}}@media (prefers-reduced-motion:reduce){*{animation:none!important;transition:none!important;scroll-behavior:auto!important}}`;

/** Inline, presentational glyphs; the status text beside each one carries the meaning. */
const icons = {
  waiting:
    '<path d="M12 2a10 10 0 1 0 0 20 10 10 0 0 0 0-20Zm0 2a8 8 0 1 1 0 16 8 8 0 0 1 0-16Zm-1 3v6l5 3 1-1.7-4-2.4V7h-2Z"/>',
  working:
    '<path d="M12 4V1L8 5l4 4V6a6 6 0 0 1 5.7 7.9l1.5 1.5A8 8 0 0 0 12 4Zm0 14a6 6 0 0 1-5.7-7.9L4.8 8.6A8 8 0 0 0 12 20v3l4-4-4-4v3Z"/>',
  person:
    '<path d="M12 12a4 4 0 1 0 0-8 4 4 0 0 0 0 8Zm0 2c-4.4 0-8 2.2-8 5v1h16v-1c0-2.8-3.6-5-8-5Z"/>',
  done: '<path d="M9 16.2 4.8 12l-1.4 1.4L9 19 21 7l-1.4-1.4L9 16.2Z"/>',
  stopped:
    '<path d="M12 2a10 10 0 1 0 0 20 10 10 0 0 0 0-20Zm0 2a8 8 0 0 1 6.3 12.9L7.1 5.7A8 8 0 0 1 12 4ZM5.7 7.1l11.2 11.2A8 8 0 0 1 5.7 7.1Z"/>',
  problem: '<path d="M1 21h22L12 2 1 21Zm12-3h-2v-2h2v2Zm0-4h-2v-4h2v4Z"/>',
} as const;
const icon = (name: keyof typeof icons) =>
  '<svg viewBox="0 0 24 24" fill="currentColor" aria-hidden="true" focusable="false">' +
  icons[name] +
  '</svg>';

/** The closed status a person sees: an erasure past its window but not yet run reads "starting soon". */
export function dataRequestState(
  item: OwnDataRequest,
  preparedAt: string,
): OwnDataRequest['status'] | 'due' {
  return item.kind === 'erasure' &&
    item.status === 'scheduled' &&
    item.executeAt !== null &&
    Date.parse(preparedAt) >= Date.parse(item.executeAt)
    ? 'due'
    : item.status;
}
/**
 * The instant an export's file stopped being readable: reads end at the expiry or at a revocation, whichever came first,
 * and a purge after the expiry, however late its job ran, ended nothing. The administrator's pages use the same rule
 * (`dataRequestUnavailableSince` in the administrator labels), so one file carries one date on both surfaces.
 */
export function dataRequestUnavailableSince(
  artifact: Pick<NonNullable<OwnDataRequest['artifact']>, 'expiresAt' | 'purgedAt'>,
): string {
  return artifact.purgedAt !== null &&
    Date.parse(artifact.purgedAt) < Date.parse(artifact.expiresAt)
    ? artifact.purgedAt
    : artifact.expiresAt;
}
/**
 * Whether a closed result the page was redirected with still describes the list as prepared. The Studio drops its notice
 * by the same rule (`dataRequestResultCurrent` in the administrator labels), so a request that moved on, or was never
 * there, is not announced as "recorded". A closure is announced only while it is still waiting for its operator.
 */
export function dataRequestResultCurrent(
  result: NonNullable<DataRequestPage['result']>,
  items: readonly OwnDataRequest[],
  preparedAt: string,
): boolean {
  switch (result) {
    case 'export':
      return items.some(
        (item) =>
          item.kind === 'export' &&
          (item.status === 'requested' ||
            item.status === 'fulfilling' ||
            (item.status === 'completed' && item.artifact?.readable === true)),
      );
    case 'erasure':
      return items.some(
        (item) => item.kind === 'erasure' && dataRequestState(item, preparedAt) === 'scheduled',
      );
    case 'closure':
      return items.some((item) => item.kind === 'closure' && item.status === 'scheduled');
    case 'cancelled':
      return items.some((item) => item.status === 'cancelled');
    case 'revoked':
      return items.some(
        (item) => item.kind === 'export' && item.artifact !== null && !item.artifact.readable,
      );
  }
}
/** ADR-0106 § 11: an export until it starts, an erasure until its window ends, a closure until Begin commits. */
export function dataRequestCancellable(item: OwnDataRequest, preparedAt: string): boolean {
  if (item.origin === 'closure_fanout') return false;
  if (item.kind === 'export') return item.status === 'requested';
  if (item.kind === 'closure') return item.status === 'scheduled';
  return item.status === 'scheduled' && dataRequestState(item, preparedAt) === 'scheduled';
}
/** Closed wire vocabularies to their sentences; a new wire value fails the type check here. */
const KINDS: Readonly<Record<OwnDataRequest['kind'], DataRequestKey>> = {
  export: 'kind.export',
  erasure: 'kind.erasure',
  closure: 'kind.closure',
};
const STATUSES: Readonly<Record<OwnDataRequest['status'] | 'due', DataRequestKey>> = {
  requested: 'status.requested',
  scheduled: 'status.scheduled',
  due: 'status.due',
  fulfilling: 'status.fulfilling',
  awaiting_privileged_step: 'status.awaiting_privileged_step',
  completed: 'status.completed',
  blocked: 'status.blocked',
  failed: 'status.failed',
  cancelled: 'status.cancelled',
};
const LAST_REFUSALS: Readonly<Record<NonNullable<OwnDataRequest['lastRefusal']>, DataRequestKey>> =
  {
    blocked_window: 'last_refusal.blocked_window',
    blocked_hold: 'last_refusal.blocked_hold',
    blocked_last_administrator: 'last_refusal.blocked_last_administrator',
  };
/**
 * Why an erasure stopped at its execution re-check (ADR-0106 § 7): the two codes `blocked` is stored with, each with
 * what the person can do. A new request is refused for the same reason while it lasts, so none promises one.
 */
const BLOCKED_REASONS: Readonly<
  Record<Exclude<NonNullable<OwnDataRequest['lastRefusal']>, 'blocked_window'>, DataRequestKey>
> = {
  blocked_hold: 'blocked_reason.blocked_hold',
  blocked_last_administrator: 'blocked_reason.blocked_last_administrator',
};
const FAILURES: Readonly<Record<NonNullable<OwnDataRequest['failureReason']>, DataRequestKey>> = {
  export_too_large: 'failure.export_too_large',
  execution_failed: 'failure.execution_failed',
};
const RETAINED: Readonly<Record<OwnDataRequest['retained'][number]['kind'], DataRequestKey>> = {
  consent_evidence: 'retained.kind.consent_evidence',
  invitation_record: 'retained.kind.invitation_record',
  invitation_envelope: 'retained.kind.invitation_envelope',
  audit_actor_reference: 'retained.kind.audit_actor_reference',
  audit_entity_reference: 'retained.kind.audit_entity_reference',
  audit_metadata_reference: 'retained.kind.audit_metadata_reference',
  unscoped_audit_rows: 'retained.kind.unscoped_audit_rows',
  transport_evidence: 'retained.kind.transport_evidence',
  execution_evidence: 'retained.kind.execution_evidence',
  provider_event_record: 'retained.kind.provider_event_record',
  idempotency_cache: 'retained.kind.idempotency_cache',
  attribution_reference: 'retained.kind.attribution_reference',
  federation_actor_reference: 'retained.kind.federation_actor_reference',
  request_receipt: 'retained.kind.request_receipt',
  external_copy: 'retained.kind.external_copy',
  unserved_group: 'retained.kind.unserved_group',
};
const RESULTS: Readonly<Record<NonNullable<DataRequestPage['result']>, DataRequestKey>> = {
  export: 'result.export',
  erasure: 'result.erasure',
  closure: 'result.closure',
  cancelled: 'result.cancelled',
  revoked: 'result.revoked',
};
export const DATA_REQUEST_VOCABULARY = Object.freeze({
  KINDS,
  STATUSES,
  LAST_REFUSALS,
  BLOCKED_REASONS,
  FAILURES,
  RETAINED,
  RESULTS,
});
/** Pages that tell the person the outcome is not known yet, rather than that the action failed. */
const UNCONFIRMED = new Set<DataRequestKey>([
  'error.unconfirmed',
  'error.outcome_unavailable',
  'error.in_progress',
]);
const active = (items: readonly OwnDataRequest[], kind: OwnDataRequest['kind']) =>
  items.some(
    (item) =>
      item.kind === kind &&
      ['requested', 'scheduled', 'fulfilling', 'awaiting_privileged_step'].includes(item.status),
  );

/** No Next, RSC, credentials or owner imports belong to this renderer; every value it prints is escaped. */
export function renderDataRequestDocument(
  document: DataRequestDocument,
  input: Readonly<{ locale: 'en' | 'tr'; phrase: DataRequestCopy; nonce: string }>,
): string {
  const { locale, phrase, nonce } = input;
  if (!/^[A-Za-z0-9+/]{22}==$/.test(nonce)) throw new Error('Data request response unavailable');
  // `text` takes literal keys only (the catalogue census reads them); `sentence` maps a closed wire value.
  const text = (key: DataRequestKey) => escape(phrase(key));
  const sentence = (key: DataRequestKey) => escape(phrase(key));
  const time = (value: string) =>
    '<time datetime="' +
    escape(value) +
    '">' +
    escape(
      new Intl.DateTimeFormat(locale, {
        dateStyle: 'medium',
        timeStyle: 'short',
        timeZone: 'UTC',
      }).format(new Date(value)) + ' UTC',
    ) +
    '</time>';
  const count = (value: number) =>
    '<data value="' + value + '">' + new Intl.NumberFormat(locale).format(value) + '</data>';
  const size = (bytes: number) =>
    escape(
      new Intl.NumberFormat(locale, {
        style: 'unit',
        unit: bytes < 1024 * 1024 ? 'kilobyte' : 'megabyte',
        unitDisplay: 'short',
        maximumFractionDigits: 1,
      }).format(bytes < 1024 * 1024 ? Math.max(0.1, bytes / 1024) : bytes / (1024 * 1024)),
    );
  const pageHref = (group: string | null, extra: Record<string, string> = {}) => {
    const query = new URLSearchParams({ ...(group === null ? {} : { group }), ...extra });
    return '/account/data' + (query.size ? '?' + query.toString() : '');
  };
  const kept = () =>
    '<h3>' +
    text('erasure.kept_title') +
    '</h3><p>' +
    text('erasure.kept_intro') +
    '</p><ul><li>' +
    text('erasure.kept_consent') +
    '</li><li>' +
    text('erasure.kept_invitations') +
    '</li><li>' +
    text('erasure.kept_audit') +
    '</li><li>' +
    text('erasure.kept_attribution') +
    '</li><li>' +
    text('erasure.kept_receipt') +
    '</li></ul><p>' +
    text('retained.audit_retention') +
    '</p><p>' +
    text('erasure.kept_uncounted') +
    '</p><p>' +
    text('erasure.kept_partial') +
    '</p>';

  let title = phrase('common.title');
  let body: string;
  let view: string;
  // Only a retained group's page keeps its group in the address the history guard restores.
  let historyGroup: string | null = null;
  if (document.kind === 'signin') {
    view = 'signin';
    body =
      '<p>' +
      text('common.signin') +
      '</p><p><a class="action" href="/api/auth/signin">' +
      text('common.signin_action') +
      '</a></p>';
  } else if (document.kind === 'error') {
    view = 'error';
    // An unknown outcome is not a failure: its heading must not tell the person that nothing was recorded.
    const heading = UNCONFIRMED.has(document.message)
      ? text('error.title_unconfirmed')
      : text('error.title');
    title =
      (UNCONFIRMED.has(document.message)
        ? phrase('error.title_unconfirmed')
        : phrase('error.title')) +
      ' – ' +
      title;
    const back = document.back;
    body =
      '<div role="alert"><h2>' +
      heading +
      '</h2><p>' +
      sentence(document.message) +
      '</p></div>' +
      (back.kind === 'signin'
        ? '<p><a class="action" href="/api/auth/signin">' +
          text('common.signin_action') +
          '</a></p>'
        : '<p><a class="action" href="' +
          // A retained step needs its group, so only the session mode returns to its step.
          escape(pageHref(null, back.mode === 'session' && back.step ? { step: back.step } : {})) +
          '">' +
          text('common.back_page') +
          '</a></p>' +
          (back.mode === 'entry'
            ? '<p><a class="action" href="/api/auth/entry">' +
              text('common.back_entry') +
              '</a></p>'
            : back.mode === 'session'
              ? '<p><a class="action" href="/portal">' + text('common.back_portal') + '</a></p>'
              : ''));
  } else {
    const page = document.page;
    if (!proof(page.csrfToken) || !page.formKeys.every(proof))
      throw new Error('Data request response unavailable');
    const entry = page.mode === 'entry';
    const group = entry ? page.group : null;
    if (group !== null && group !== 'tenant' && !uuid(group))
      throw new Error('Data request response unavailable');
    const scoped = entry ? '/api/auth/entry/data-requests/' : '/api/account/data-requests/';
    let key = 0;
    const formKey = () => {
      const value = page.formKeys[key++];
      if (value === undefined) throw new Error('Data request response unavailable');
      return value;
    };
    const fields = (keyed: boolean) =>
      hidden('csrfToken', page.csrfToken) +
      (keyed ? hidden('idempotencyKey', formKey()) : '') +
      (group === null ? '' : hidden('group', group));
    const items = page.requests?.items ?? [];
    // The retained entry is never extended: a download or a confirmation still running when it ends is cut off. The
    // page-level notice (`entryTimeLimit`) states when; beside these two actions the page says what that means.
    const entryDeadline = (purpose: 'download' | 'step') =>
      entry && page.entryExpiresAt !== null
        ? '<p>' +
          (purpose === 'download' ? text('entry.download_deadline') : text('entry.step_deadline')) +
          '</p>'
        : '';
    view = entry ? (group === null ? 'entry_scopes' : 'entry') : 'session';
    historyGroup = group;
    // The notice is a statement about the list on the page: it is printed only while the list still shows what it says
    // (a reload, a restored or a shared address keeps the query, and a script-free browser never rewrites it).
    const shownResult =
      page.result !== null && dataRequestResultCurrent(page.result, items, page.preparedAt)
        ? page.result
        : null;
    const result =
      shownResult === null
        ? ''
        : '<div class="notice" role="status"><h2>' +
          text('result.title') +
          '</h2><p>' +
          sentence(RESULTS[shownResult]) +
          '</p></div>';
    // The result notice and its title prefix appear together, only on the documents that show the notice.
    const resultTitle = (base: string) =>
      shownResult === null ? base : phrase('result.title') + ' – ' + base;
    /**
     * The retained entry's owner lives at most five minutes from the start of sign-in and is never extended, so every
     * retained document says so, with the instant it ends and the time that was left when the page was prepared. The
     * one helper for every retained notice of that limit.
     */
    const entryTimeLimit = () => {
      if (!entry || page.entryExpiresAt === null) return '';
      const ends = Date.parse(page.entryExpiresAt);
      const prepared = Date.parse(page.preparedAt);
      if (!Number.isFinite(ends) || !Number.isFinite(prepared))
        throw new Error('Data request response unavailable');
      const minutes = Math.floor(Math.max(0, ends - prepared) / 60_000);
      return (
        '<div class="notice"><p>' +
        text('scope.entry_time_limit') +
        '</p><dl><dt>' +
        text('scope.entry_time_until') +
        '</dt><dd>' +
        time(page.entryExpiresAt) +
        '</dd><dt>' +
        text('scope.entry_time_left') +
        '</dt><dd>' +
        (minutes < 1
          ? text('scope.entry_time_under_minute')
          : escape(
              new Intl.NumberFormat(locale, {
                style: 'unit',
                unit: 'minute',
                unitDisplay: 'long',
              }).format(minutes),
            )) +
        '</dd></dl></div>'
      );
    };
    const scopeText = entry
      ? '<p>' +
        text('scope.entry_intro') +
        '</p>' +
        (group === null
          ? ''
          : '<p><strong>' +
            (group === 'tenant'
              ? text('scope.entry_selected_tenant')
              : text('scope.entry_selected_organization')) +
            '</strong></p><p><a class="action" href="/account/data">' +
            text('scope.change') +
            '</a></p>') +
        entryTimeLimit()
      : '<p>' +
        (page.scope === 'organization'
          ? text('scope.session_organization')
          : text('scope.session_tenant')) +
        '</p><p>' +
        text('scope.session_retained') +
        '</p><p><a class="action" href="/api/auth/entry">' +
        text('scope.switch') +
        '</a></p>';
    if (entry && group === null) {
      const scopes = page.scopes;
      if (!scopes) throw new Error('Data request response unavailable');
      title = resultTitle(phrase('scope.entry_choose') + ' – ' + title);
      let branch = 0;
      body =
        result +
        '<p>' +
        text('common.intro') +
        '</p>' +
        scopeText +
        '<section aria-labelledby="scope-title"><h2 id="scope-title">' +
        text('scope.entry_choose') +
        '</h2>' +
        (scopes.items.length === 0
          ? '<p>' + text('scope.entry_empty') + '</p>'
          : (scopes.items.some((item) => item.organizationId !== null)
              ? '<p>' + text('scope.entry_names') + '</p>'
              : '') +
            '<ul>' +
            scopes.items
              .map((item, index) => {
                const value = item.organizationId ?? 'tenant';
                const label =
                  item.organizationId === null
                    ? text('scope.entry_tenant')
                    : text('scope.entry_organization') + ' ' + String(++branch);
                // Each "Show requests" button is named by its own text and the place it opens.
                const labelId = 'scope-' + String(index + 1);
                return (
                  '<li><form method="post" action="/api/auth/entry/data-requests">' +
                  hidden('csrfToken', page.csrfToken) +
                  hidden('group', value) +
                  '<p id="' +
                  labelId +
                  '"><strong>' +
                  label +
                  '</strong></p><button type="submit" id="' +
                  labelId +
                  '-view" aria-labelledby="' +
                  labelId +
                  '-view ' +
                  labelId +
                  '">' +
                  text('scope.view') +
                  '</button></form></li>'
                );
              })
              .join('') +
            '</ul>') +
        (scopes.pageInfo.nextCursor === null
          ? ''
          : '<p><a class="action" href="' +
            escape(pageHref(null, { scopeCursor: scopes.pageInfo.nextCursor })) +
            '">' +
            text('common.next_scopes') +
            '</a></p>') +
        '</section>';
    } else if (page.step !== null) {
      const erasure = page.step === 'erasure';
      view += '_' + page.step;
      // An irreversible confirmation is announced by its own title, never by the list's.
      title =
        (erasure ? phrase('erasure.step_title') : phrase('closure.step_title')) + ' – ' + title;
      const blocked = active(items, page.step);
      body =
        '<p>' +
        (erasure ? text('erasure.summary') : text('closure.summary')) +
        '</p>' +
        scopeText +
        '<section aria-labelledby="step-title"><h2 id="step-title">' +
        (erasure ? text('erasure.step_title') : text('closure.step_title')) +
        '</h2>' +
        (erasure
          ? '<h3>' +
            text('erasure.erased_title') +
            '</h3><ul><li>' +
            text('erasure.erased_profile') +
            '</li><li>' +
            text('erasure.erased_roles') +
            '</li><li>' +
            text('erasure.erased_exports') +
            '</li><li>' +
            text('erasure.erased_invitations') +
            '</li></ul>' +
            kept() +
            '<p>' +
            text('erasure.timing') +
            '</p>'
          : '<div class="notice"><h3>' +
            text('closure.warning_title') +
            '</h3><ul><li>' +
            text('closure.warning_everywhere') +
            '</li><li>' +
            text('closure.warning_email') +
            '</li><li>' +
            text('closure.warning_cancel') +
            '</li><li><strong>' +
            text('closure.warning_stops') +
            '</strong></li><li><strong>' +
            text('closure.warning_no_status') +
            '</strong></li><li>' +
            text('closure.warning_data') +
            '</li><li><strong>' +
            text('closure.warning_unserved') +
            '</strong></li></ul></div>' +
            kept()) +
        (blocked
          ? '<p>' + (erasure ? text('erasure.active') : text('closure.active')) + '</p>'
          : entryDeadline('step') +
            '<form method="post" action="' +
            scoped +
            page.step +
            '">' +
            fields(true) +
            '<fieldset><legend>' +
            (erasure ? text('erasure.legend') : text('closure.legend')) +
            '</legend>' +
            (erasure
              ? '<label class="check"><input type="checkbox" name="confirm" value="erase" required aria-required="true"><span>' +
                text('erasure.confirm') +
                ' (' +
                text('common.required') +
                ')</span></label>'
              : '<label class="check"><input type="checkbox" name="confirm" value="close" required aria-required="true"><span>' +
                text('closure.confirm_irreversible') +
                ' (' +
                text('common.required') +
                ')</span></label><label class="check"><input type="checkbox" name="acknowledge" value="no_status" required aria-required="true"><span>' +
                text('closure.confirm_no_status') +
                ' (' +
                text('common.required') +
                ')</span></label>') +
            '</fieldset><button type="submit" class="danger">' +
            (erasure ? text('erasure.action') : text('closure.action')) +
            '</button></form>') +
        '<p><a class="action" href="' +
        escape(pageHref(group)) +
        '">' +
        (erasure ? text('erasure.back') : text('closure.back')) +
        '</a></p></section>';
    } else {
      const requests = page.requests;
      if (!requests) throw new Error('Data request response unavailable');
      const exportActive = active(items, 'export');
      title = resultTitle(title);
      const list = items
        .map((item, index) => {
          const state = dataRequestState(item, page.preparedAt);
          const iconName =
            state === 'completed'
              ? 'done'
              : state === 'cancelled'
                ? 'stopped'
                : state === 'blocked' || state === 'failed'
                  ? 'problem'
                  : state === 'awaiting_privileged_step'
                    ? 'person'
                    : state === 'fulfilling' || state === 'due'
                      ? 'working'
                      : 'waiting';
          const artifact = item.artifact;
          const rows: string[] = [
            '<dt>' + text('list.requested') + '</dt><dd>' + time(item.createdAt) + '</dd>',
          ];
          if (item.executeAt !== null && ['scheduled'].includes(item.status))
            rows.push(
              '<dt>' +
                (item.kind === 'closure' ? text('list.earliest') : text('list.runs_after')) +
                '</dt><dd>' +
                time(item.executeAt) +
                '</dd>',
            );
          if (item.completedAt !== null)
            rows.push(
              '<dt>' + text('list.completed') + '</dt><dd>' + time(item.completedAt) + '</dd>',
            );
          else if (
            item.updatedAt !== null &&
            ['cancelled', 'blocked', 'failed'].includes(item.status)
          )
            rows.push('<dt>' + text('list.ended') + '</dt><dd>' + time(item.updatedAt) + '</dd>');
          if (artifact !== null) {
            const unavailableSince = dataRequestUnavailableSince(artifact);
            rows.push(
              '<dt>' +
                (artifact.readable ? text('list.available_until') : text('list.expired_on')) +
                '</dt><dd>' +
                time(artifact.readable ? artifact.expiresAt : unavailableSince) +
                '</dd>',
            );
            rows.push(
              '<dt>' + text('list.size') + '</dt><dd>' + size(artifact.byteCount) + '</dd>',
            );
          }
          const next: DataRequestKey =
            item.status === 'cancelled'
              ? 'next.cancelled'
              : item.status === 'failed'
                ? 'next.failed'
                : item.status === 'blocked'
                  ? 'next.blocked'
                  : item.kind === 'export'
                    ? item.status === 'completed'
                      ? artifact?.readable
                        ? 'next.export_ready'
                        : 'next.export_gone'
                      : 'next.export_requested'
                    : item.kind === 'erasure'
                      ? state === 'due'
                        ? 'next.erasure_due'
                        : item.status === 'scheduled'
                          ? 'next.erasure_scheduled'
                          : item.status === 'fulfilling'
                            ? 'next.erasure_fulfilling'
                            : item.status === 'awaiting_privileged_step'
                              ? 'next.erasure_awaiting'
                              : 'next.erasure_completed'
                      : item.status === 'scheduled'
                        ? 'next.closure_scheduled'
                        : item.status === 'fulfilling'
                          ? 'next.closure_fulfilling'
                          : 'next.closure_completed';
          const notes: string[] = ['<p>' + sentence(next) + '</p>'];
          if (item.origin === 'administrator')
            notes.push('<p>' + text('list.by_administrator') + '</p>');
          if (item.origin === 'closure_fanout')
            notes.push('<p>' + text('list.by_closure') + '</p>');
          if (item.lastRefusal !== null && item.status === 'scheduled')
            notes.push('<p>' + sentence(LAST_REFUSALS[item.lastRefusal]) + '</p>');
          // A stopped erasure always carries the code it stopped for; the reason says what the person can do.
          if (
            item.status === 'blocked' &&
            item.lastRefusal !== null &&
            item.lastRefusal !== 'blocked_window'
          )
            notes.push('<p>' + sentence(BLOCKED_REASONS[item.lastRefusal]) + '</p>');
          if (item.failureReason !== null && item.status === 'failed')
            notes.push('<p>' + sentence(FAILURES[item.failureReason]) + '</p>');
          // Each shortened part as label and value pairs, so no language's word order is fixed around a number.
          if (artifact !== null && artifact.truncations.length > 0)
            notes.push(
              '<p>' +
                text('artifact.truncated') +
                '</p><ul>' +
                artifact.truncations
                  .map(
                    (part) =>
                      '<li>' +
                      (part.collection === 'audit.entries'
                        ? text('artifact.collection_audit_entries')
                        : part.collection === 'identity.invitations_issued'
                          ? text('artifact.collection_invitations_issued')
                          : text('artifact.collection_other')) +
                      '<dl><dt>' +
                      text('artifact.part_included') +
                      '</dt><dd>' +
                      count(part.included) +
                      '</dd><dt>' +
                      text('artifact.part_total') +
                      '</dt><dd>' +
                      count(part.total) +
                      '</dd></dl></li>',
                  )
                  .join('') +
                '</ul>',
            );
          if (
            item.kind !== 'export' &&
            (item.retained.length > 0 ||
              ['awaiting_privileged_step', 'completed'].includes(item.status))
          )
            notes.push(
              '<h4>' +
                text('retained.title') +
                '</h4>' +
                (item.retained.length > 0
                  ? '<p>' +
                    text('retained.intro') +
                    '</p><ul>' +
                    item.retained
                      .map(
                        (entryItem) =>
                          '<li>' +
                          sentence(RETAINED[entryItem.kind]) +
                          ': ' +
                          count(entryItem.count) +
                          '</li>',
                      )
                      .join('') +
                    '</ul>'
                  : '') +
                // Counts are provisional only until the operator's completion replaces the audit ones.
                (['fulfilling', 'awaiting_privileged_step'].includes(item.status)
                  ? '<p>' + text('retained.pending') + '</p>'
                  : '') +
                '<p>' +
                text('retained.partial') +
                '</p><p>' +
                text('retained.audit_retention') +
                '</p>',
            );
          // Every repeated button is named by its own text followed by its request's heading (the kind and the date),
          // so a list of buttons tells them apart while the visible label still begins the name.
          const label = 'request-' + String(index + 1);
          const named = (action: 'download' | 'delete' | 'cancel') =>
            ' id="' +
            label +
            '-' +
            action +
            '" aria-labelledby="' +
            label +
            '-' +
            action +
            ' ' +
            label +
            '"';
          const actions: string[] = [];
          if (artifact?.readable)
            actions.push(
              '<form method="post" action="' +
                scoped +
                'download">' +
                fields(false) +
                hidden('requestId', item.id) +
                '<p>' +
                text('artifact.download_hint') +
                '</p>' +
                entryDeadline('download') +
                '<button type="submit"' +
                named('download') +
                '>' +
                text('artifact.download') +
                '</button></form><form method="post" action="' +
                scoped +
                'revoke">' +
                fields(true) +
                hidden('requestId', item.id) +
                hidden('version', artifact.version) +
                '<p>' +
                text('artifact.delete_hint') +
                '</p><button type="submit" class="danger"' +
                named('delete') +
                '>' +
                text('artifact.delete') +
                '</button></form>',
            );
          if (dataRequestCancellable(item, page.preparedAt))
            actions.push(
              '<form method="post" action="' +
                scoped +
                'cancel">' +
                fields(true) +
                hidden('requestId', item.id) +
                hidden('version', item.version) +
                '<button type="submit"' +
                named('cancel') +
                '>' +
                (item.kind === 'closure' ? text('action.cancel_closure') : text('action.cancel')) +
                '</button></form>',
            );
          return (
            '<li><article aria-labelledby="' +
            label +
            '"><h3 id="' +
            label +
            '">' +
            sentence(KINDS[item.kind]) +
            ' (' +
            time(item.createdAt) +
            ')</h3><p class="status">' +
            icon(iconName) +
            '<span>' +
            text('list.status') +
            ': ' +
            sentence(STATUSES[state]) +
            '</span></p><dl>' +
            rows.join('') +
            '</dl>' +
            notes.join('') +
            actions.join('') +
            '</article></li>'
          );
        })
        .join('');
      // The actions look at the list for what is already active, and only the newest page shows that: an older page offers
      // none of them and points back, rather than inviting a request the API would refuse after a full confirmation.
      const actionSections = requests.pageInfo.hasPrevious
        ? '<p>' +
          text('list.older') +
          '</p><p><a class="action" href="' +
          escape(pageHref(group, {})) +
          '">' +
          text('list.newest') +
          '</a></p>'
        : '<section aria-labelledby="export-title"><h2 id="export-title">' +
          text('export.title') +
          '</h2><p>' +
          text('export.description') +
          '</p><p>' +
          text('export.audit_note') +
          '</p><p>' +
          text('export.timing') +
          '</p>' +
          (exportActive
            ? '<p>' + text('export.active') + '</p>'
            : '<form method="post" action="' +
              scoped +
              'export">' +
              fields(true) +
              '<button type="submit">' +
              text('export.action') +
              '</button></form>') +
          '</section><section aria-labelledby="erasure-title"><h2 id="erasure-title">' +
          text('erasure.title') +
          '</h2><p>' +
          text('erasure.summary') +
          '</p>' +
          (active(items, 'erasure')
            ? '<p>' + text('erasure.active') + '</p>'
            : '<p><a class="action" href="' +
              escape(pageHref(group, { step: 'erasure' })) +
              '">' +
              text('erasure.start') +
              '</a></p>') +
          '</section><section aria-labelledby="closure-title"><h2 id="closure-title">' +
          text('closure.title') +
          '</h2><p>' +
          text('closure.summary') +
          '</p>' +
          (active(items, 'closure')
            ? '<p>' + text('closure.active') + '</p>'
            : '<p><a class="action" href="' +
              escape(pageHref(group, { step: 'closure' })) +
              '">' +
              text('closure.start') +
              '</a></p>') +
          +'</section>';
      body =
        result +
        '<p>' +
        text('common.intro') +
        '</p>' +
        scopeText +
        actionSections +
        '<section aria-labelledby="list-title"><h2 id="list-title">' +
        text('list.title') +
        '</h2>' +
        (items.length
          ? // WebKit drops the list role of a list styled without markers; the explicit role keeps "list, N items".
            '<ul class="requests" role="list">' + list + '</ul>'
          : '<p>' + text('list.empty') + '</p>') +
        (requests.pageInfo.nextCursor === null
          ? ''
          : '<p><a class="action" href="' +
            escape(pageHref(group, { cursor: requests.pageInfo.nextCursor })) +
            '">' +
            text('common.next_page') +
            '</a></p>') +
        '</section>';
    }
    body += entry
      ? '<p><a class="action" href="/api/auth/entry">' + text('common.back_entry') + '</a></p>'
      : '<p><a class="action" href="/portal">' + text('common.back_portal') + '</a></p>';
  }
  return (
    '<!doctype html><html lang="' +
    locale +
    '" dir="ltr"><head><meta charset="utf-8"><script nonce="' +
    nonce +
    '">' +
    dataRequestHistorySource(historyGroup) +
    '</script><meta name="viewport" content="width=device-width,initial-scale=1"><meta name="robots" content="noindex,nofollow"><title>' +
    escape(title) +
    '</title><style>' +
    css +
    '</style></head><body><a class="skip" href="#main">' +
    text('common.skip') +
    '</a><main id="main" tabindex="-1" data-data-request-view="' +
    view +
    '"><h1>' +
    text('common.title') +
    '</h1>' +
    body +
    '<noscript><p>' +
    text('common.script_note') +
    '</p></noscript></main></body></html>'
  );
}
