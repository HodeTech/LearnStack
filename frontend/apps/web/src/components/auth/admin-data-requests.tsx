/* eslint-disable @next/next/no-html-link-for-pages -- Protected views use freshly admitted full document navigation. */
import type { AdminCopy, DataRequestUiCopy } from '@/i18n/administration-catalogues';
import type {
  NativePageSnapshot,
  PageDataRequest,
  PageMemberDetail,
  PageView,
} from '@/server/auth/page-contract';

import {
  administratorMayCancel,
  administratorMayRevoke,
  dataRequestCollectionLabel,
  dataRequestDisplayState,
  dataRequestFailure,
  dataRequestFiledBy,
  dataRequestKindLabel,
  dataRequestLastRefusal,
  dataRequestNextStep,
  dataRequestResultCurrent,
  dataRequestUnavailableSince,
  dataRequestResultSentence,
  dataRequestRetainedLabel,
  dataRequestStateIcon,
  dataRequestStatusLabel,
} from './admin-data-request-labels';
import { FormIdentityFields, MutationFields } from './admin-forms';

type Copy = Readonly<{ admin: AdminCopy; dataRequest: DataRequestUiCopy; locale: string }>;

/** Inline presentational glyphs (the same shapes as the person's page); the status text carries the meaning. */
const ICONS = {
  waiting:
    'M12 2a10 10 0 1 0 0 20 10 10 0 0 0 0-20Zm0 2a8 8 0 1 1 0 16 8 8 0 0 1 0-16Zm-1 3v6l5 3 1-1.7-4-2.4V7h-2Z',
  working:
    'M12 4V1L8 5l4 4V6a6 6 0 0 1 5.7 7.9l1.5 1.5A8 8 0 0 0 12 4Zm0 14a6 6 0 0 1-5.7-7.9L4.8 8.6A8 8 0 0 0 12 20v3l4-4-4-4v3Z',
  person: 'M12 12a4 4 0 1 0 0-8 4 4 0 0 0 0 8Zm0 2c-4.4 0-8 2.2-8 5v1h16v-1c0-2.8-3.6-5-8-5Z',
  done: 'M9 16.2 4.8 12l-1.4 1.4L9 19 21 7l-1.4-1.4L9 16.2Z',
  stopped:
    'M12 2a10 10 0 1 0 0 20 10 10 0 0 0 0-20Zm0 2a8 8 0 0 1 6.3 12.9L7.1 5.7A8 8 0 0 1 12 4ZM5.7 7.1l11.2 11.2A8 8 0 0 1 5.7 7.1Z',
  problem: 'M1 21h22L12 2 1 21Zm12-3h-2v-2h2v2Zm0-4h-2v-4h2v4Z',
} as const;
const ACTIVE_EXPORT = ['requested', 'fulfilling'];

function Instant({ value, locale }: { readonly value: string; readonly locale: string }) {
  const text = new Intl.DateTimeFormat(locale, {
    dateStyle: 'long',
    timeStyle: 'short',
    timeZone: 'UTC',
  }).format(new Date(value));
  return <time dateTime={value}>{text} UTC</time>;
}
function Size({ bytes, locale }: { readonly bytes: number; readonly locale: string }) {
  const large = bytes >= 1024 * 1024;
  return (
    <data value={bytes}>
      {new Intl.NumberFormat(locale, {
        style: 'unit',
        unit: large ? 'megabyte' : 'kilobyte',
        unitDisplay: 'short',
        maximumFractionDigits: 1,
      }).format(large ? bytes / (1024 * 1024) : Math.max(0.1, bytes / 1024))}
    </data>
  );
}
function Count({ value, locale }: { readonly value: number; readonly locale: string }) {
  return <data value={value}>{new Intl.NumberFormat(locale).format(value)}</data>;
}

/**
 * Who the request concerns, as the member list shows a member: the name, or a truthful label when no Membership of
 * the snapshot could be named (all Revoked, so none was read, or no read answered).
 */
function SubjectName({
  item,
  admin,
}: {
  readonly item: PageDataRequest;
  readonly admin: AdminCopy;
}) {
  if (item.subject === 'revoked') return <>{admin('data_requests.subject_revoked')}</>;
  if (!item.subject) return <>{admin('data_requests.name_unavailable')}</>;
  return <bdi>{item.subject.displayName ?? admin('members.unnamed')}</bdi>;
}

function StatusLine({
  item,
  preparedAt,
  copy,
}: {
  readonly item: PageDataRequest;
  readonly preparedAt: string;
  readonly copy: Copy;
}) {
  const state = dataRequestDisplayState(item, preparedAt);
  return (
    <p className="auth-data-request-status">
      <svg viewBox="0 0 24 24" fill="currentColor" aria-hidden="true" focusable="false">
        <path d={ICONS[dataRequestStateIcon(state)]} />
      </svg>
      <span>
        {copy.dataRequest('list.status')}: {dataRequestStatusLabel(state, copy.dataRequest)}
      </span>
    </p>
  );
}

/** The request's dates and sizes; a `<dl>` reflows to one column at 320 px. */
function Facts({
  item,
  copy,
  subject,
}: {
  readonly item: PageDataRequest;
  readonly copy: Copy;
  readonly subject: boolean;
}) {
  const { admin, dataRequest, locale } = copy;
  const artifact = item.artifact;
  const ended = item.completedAt === null && item.updatedAt !== null;
  return (
    <dl className="auth-values auth-data-request-facts">
      {subject && (
        <>
          <dt>{admin('data_requests.concerns')}</dt>
          <dd>
            {item.subject !== null && item.subject !== 'revoked' ? (
              // Only a Membership whose read named the person has a member page to open.
              <a href={'/studio/members/' + item.subject.membershipId}>
                <span>
                  <SubjectName item={item} admin={admin} />
                </span>
              </a>
            ) : (
              <SubjectName item={item} admin={admin} />
            )}
          </dd>
        </>
      )}
      <dt>{admin('data_requests.filed_by')}</dt>
      <dd>{dataRequestFiledBy(item, admin)}</dd>
      <dt>{dataRequest('list.requested')}</dt>
      <dd>
        <Instant value={item.createdAt} locale={locale} />
      </dd>
      {item.executeAt !== null && item.status === 'scheduled' && (
        <>
          <dt>{dataRequest('list.runs_after')}</dt>
          <dd>
            <Instant value={item.executeAt} locale={locale} />
          </dd>
        </>
      )}
      {item.completedAt !== null && (
        <>
          <dt>{dataRequest('list.completed')}</dt>
          <dd>
            <Instant value={item.completedAt} locale={locale} />
          </dd>
        </>
      )}
      {ended && ['cancelled', 'blocked', 'failed'].includes(item.status) && (
        <>
          <dt>{dataRequest('list.ended')}</dt>
          <dd>
            <Instant value={item.updatedAt!} locale={locale} />
          </dd>
        </>
      )}
      {artifact !== null && (
        <>
          <dt>
            {artifact.readable
              ? dataRequest('list.available_until')
              : dataRequest('list.expired_on')}
          </dt>
          <dd>
            <Instant
              value={artifact.readable ? artifact.expiresAt : dataRequestUnavailableSince(artifact)}
              locale={locale}
            />
          </dd>
          <dt>{dataRequest('list.size')}</dt>
          <dd>
            <Size bytes={artifact.byteCount} locale={locale} />
          </dd>
        </>
      )}
    </dl>
  );
}

function Retained({ item, copy }: { readonly item: PageDataRequest; readonly copy: Copy }) {
  const { admin, dataRequest, locale } = copy;
  if (
    item.kind !== 'erasure' ||
    (item.retained.length === 0 && !['awaiting_privileged_step', 'completed'].includes(item.status))
  )
    return null;
  return (
    <section aria-labelledby="data-request-retained-title">
      <h2 id="data-request-retained-title">{dataRequest('retained.title')}</h2>
      {item.retained.length > 0 && (
        <>
          <p>{dataRequest('retained.intro')}</p>
          <ul>
            {item.retained.map((entry) => (
              <li key={entry.kind}>
                {dataRequestRetainedLabel(entry.kind, admin, dataRequest)}:{' '}
                <Count value={entry.count} locale={locale} />
              </li>
            ))}
          </ul>
        </>
      )}
      {item.status !== 'completed' && <p>{dataRequest('retained.pending')}</p>}
      <p>{dataRequest('retained.partial')}</p>
      <p>{dataRequest('retained.audit_retention')}</p>
    </section>
  );
}

/** ADR-0106 §§ 3, 6, 13: the group's export and erasure requests, newest first; status and counts only. */
export function DataRequestDirectory({
  snapshot,
  view,
  copy,
}: {
  readonly snapshot: NativePageSnapshot;
  readonly view: Extract<PageView, { kind: 'data_requests' }>;
  readonly copy: Copy;
}) {
  const { admin, dataRequest } = copy;
  const next =
    view.hasNextPage && view.endCursor !== null
      ? '/studio/data-requests?' + new URLSearchParams({ cursor: view.endCursor })
      : null;
  return (
    <div className="auth-data-requests">
      <p>{admin('data_requests.intro')}</p>
      <p>
        {snapshot.scopeKind === 'tenant'
          ? admin('data_requests.scope_tenant')
          : admin('data_requests.scope_organization')}
      </p>
      <p>{admin('data_requests.no_download')}</p>
      <p>{admin('data_requests.no_closure')}</p>
      <p>
        {admin('data_requests.from_member')}{' '}
        <a href="/studio/members">{admin('data_requests.open_members')}</a>
      </p>
      <section aria-labelledby="data-request-list-title">
        <h2 id="data-request-list-title">{admin('data_requests.list_title')}</h2>
        {view.items.length === 0 ? (
          <p>{admin('data_requests.empty')}</p>
        ) : (
          <ul className="auth-resource-list">
            {view.items.map((item, index) => (
              <li key={item.id}>
                <article aria-labelledby={'data-request-' + String(index + 1)}>
                  <h3 id={'data-request-' + String(index + 1)}>
                    <a href={'/studio/data-requests/' + item.id}>
                      {/* One flex item: a text run beside an element would lose its separating space. */}
                      <span>
                        {dataRequestKindLabel(item.kind, admin, dataRequest)}:{' '}
                        <SubjectName item={item} admin={admin} />
                      </span>
                    </a>
                  </h3>
                  <StatusLine item={item} preparedAt={view.preparedAt} copy={copy} />
                  <Facts item={item} copy={copy} subject={false} />
                </article>
              </li>
            ))}
          </ul>
        )}
        <nav aria-label={admin('data_requests.list_title')}>
          <a href="/studio/data-requests">{admin('members.first')}</a>
          {next && <a href={next}>{admin('data_requests.next_page')}</a>}
        </nav>
      </section>
    </div>
  );
}

/** One request of the group, with the actions only the administrator who filed it may take. */
export function DataRequestDetails({
  snapshot,
  view,
  copy,
}: {
  readonly snapshot: NativePageSnapshot;
  readonly view: Extract<PageView, { kind: 'data_request' }>;
  readonly copy: Copy;
}) {
  const { admin, dataRequest, locale } = copy;
  const item = view.request;
  const state = dataRequestDisplayState(item, view.preparedAt);
  const artifact = item.artifact;
  const target = ['requestId', item.id] as const;
  // Who else may cancel: the person always, the administrator who filed it; a closure's fan-out never.
  const cancellable =
    item.origin !== 'closure_fanout' &&
    ((item.kind === 'export' && item.status === 'requested') ||
      (item.kind === 'erasure' && state === 'scheduled'));
  return (
    <div className="auth-data-requests">
      <p>
        <a href="/studio/data-requests">{admin('data_requests.back')}</a>
      </p>
      {view.result !== null && dataRequestResultCurrent(view.result, item, view.preparedAt) && (
        <div className="auth-notice" role="status" aria-labelledby="data-request-result-title">
          <h2 id="data-request-result-title">{dataRequest('result.title')}</h2>
          <p>{dataRequestResultSentence(view.result, admin, dataRequest)}</p>
        </div>
      )}
      <p className="auth-resource-name">
        {dataRequestKindLabel(item.kind, admin, dataRequest)}:{' '}
        <SubjectName item={item} admin={admin} />
      </p>
      <StatusLine item={item} preparedAt={view.preparedAt} copy={copy} />
      <Facts item={item} copy={copy} subject />
      <p>{dataRequestNextStep(item, state, admin, dataRequest)}</p>
      {item.lastRefusal !== null && <p>{dataRequestLastRefusal(item.lastRefusal, admin)}</p>}
      {item.failureReason !== null && item.status === 'failed' && (
        <p>{dataRequestFailure(item.failureReason, admin, dataRequest)}</p>
      )}
      {artifact !== null && artifact.truncations.length > 0 && (
        <>
          <p>{dataRequest('artifact.truncated')}</p>
          <ul>
            {artifact.truncations.map((part) => (
              <li key={part.collection}>
                {dataRequestCollectionLabel(part.collection, admin, dataRequest)}:{' '}
                <Count value={part.included} locale={locale} />{' '}
                {dataRequest('artifact.truncated_included')},{' '}
                <Count value={part.total} locale={locale} />{' '}
                {dataRequest('artifact.truncated_total')}
              </li>
            ))}
          </ul>
        </>
      )}
      {item.kind === 'export' && <p>{admin('data_requests.no_download')}</p>}
      {item.membershipCount > 1 && (
        <>
          <p>{admin('data_requests.memberships_note')}</p>
          <ul>
            {item.membershipIds.map((membershipId, index) => {
              const record = (
                <>
                  {admin('data_requests.membership_record')}{' '}
                  <data value={index + 1}>{index + 1}</data>
                </>
              );
              return (
                <li key={membershipId}>
                  {item.subject === 'revoked' ? (
                    // Every Membership of this snapshot is Revoked: none has a member page.
                    record
                  ) : (
                    <a href={'/studio/members/' + membershipId}>
                      <span>{record}</span>
                    </a>
                  )}
                </li>
              );
            })}
          </ul>
          {item.membershipCount > item.membershipIds.length && (
            <p>{admin('data_requests.memberships_more')}</p>
          )}
        </>
      )}
      <Retained item={item} copy={copy} />
      {snapshot.capabilities?.canManageMemberships &&
        (administratorMayCancel(item, view.preparedAt) ? (
          <form action="/api/studio/data-request-cancel" method="post">
            <MutationFields snapshot={snapshot} index={0} version={item.version} target={target} />
            <p id="data-request-cancel-hint">{admin('data_requests.cancel.hint')}</p>
            <button type="submit" aria-describedby="data-request-cancel-hint">
              {dataRequest('action.cancel')}
            </button>
          </form>
        ) : (
          cancellable && (
            <p>
              {item.origin === 'self'
                ? admin('data_requests.cancel.person')
                : admin('data_requests.cancel.other')}
            </p>
          )
        ))}
      {snapshot.capabilities?.canManageMemberships && administratorMayRevoke(item) && artifact && (
        <details className="auth-danger">
          <summary>{admin('data_requests.revoke.title')}</summary>
          <p id="data-request-revoke-hint">{admin('data_requests.revoke.hint')}</p>
          <form action="/api/studio/data-request-revoke" method="post">
            <MutationFields
              snapshot={snapshot}
              index={1}
              version={artifact.version}
              target={target}
            />
            <button type="submit" aria-describedby="data-request-revoke-hint">
              {admin('data_requests.revoke.action')}
            </button>
          </form>
        </details>
      )}
    </div>
  );
}

/**
 * The member page's section: this member's requests in the exact current group, and the two filings pre-filled
 * for this Membership. The API decides every refusal; the page only withholds a form the list already proves futile.
 */
export function MemberDataRequests({
  snapshot,
  member,
  requests,
  copy,
}: {
  readonly snapshot: NativePageSnapshot;
  readonly member: PageMemberDetail;
  readonly requests: Extract<PageView, { kind: 'member' }>['dataRequests'];
  readonly copy: Copy;
}) {
  const { admin, dataRequest } = copy;
  if (!snapshot.capabilities?.canManageMemberships) return null;
  const subject = ['membershipId', member.membershipId] as const;
  const items = requests?.items ?? [];
  return (
    <section aria-labelledby="member-data-requests-title" className="auth-data-requests">
      <h2 id="member-data-requests-title">{admin('data_requests.title')}</h2>
      {!member.inCurrentScope || !requests ? (
        <p>{admin('data_requests.member.other_scope')}</p>
      ) : (
        <>
          <p>{admin('data_requests.member.intro')}</p>
          <p>{admin('data_requests.no_download')}</p>
          {requests.items === null ? (
            <p>{admin('data_requests.member.unavailable')}</p>
          ) : items.length === 0 ? (
            <p>{admin('data_requests.member.none')}</p>
          ) : (
            <ul className="auth-resource-list">
              {items.map((item, index) => (
                <li key={item.id}>
                  <article aria-labelledby={'member-data-request-' + String(index + 1)}>
                    <h3 id={'member-data-request-' + String(index + 1)}>
                      <a href={'/studio/data-requests/' + item.id}>
                        {dataRequestKindLabel(item.kind, admin, dataRequest)}
                      </a>
                    </h3>
                    <StatusLine item={item} preparedAt={requests.preparedAt} copy={copy} />
                    <Facts item={item} copy={copy} subject={false} />
                  </article>
                </li>
              ))}
            </ul>
          )}
          {requests.items !== null && !requests.complete && (
            <p>{admin('data_requests.member.partial')}</p>
          )}
          <p>
            <a href="/studio/data-requests">{admin('data_requests.member.all')}</a>
          </p>
          <section aria-labelledby="member-data-export-title">
            <h3 id="member-data-export-title">{admin('data_requests.export.title')}</h3>
            <p>{admin('data_requests.export.description')}</p>
            <p>{admin('data_requests.export.replaces')}</p>
            {items.some((item) => item.kind === 'export' && ACTIVE_EXPORT.includes(item.status)) ? (
              <p>{admin('data_requests.export.active')}</p>
            ) : items.some((item) => item.artifact?.readable) ? (
              <p>{dataRequest('refusal.subject_export_available')}</p>
            ) : (
              <form action="/api/studio/data-request-export" method="post">
                <FormIdentityFields snapshot={snapshot} index={32} />
                <input type="hidden" name={subject[0]} value={subject[1]} />
                <button type="submit">{admin('data_requests.export.action')}</button>
              </form>
            )}
          </section>
          <section aria-labelledby="member-data-erasure-title">
            <h3 id="member-data-erasure-title">{admin('data_requests.erasure.title')}</h3>
            <p>{admin('data_requests.erasure.summary')}</p>
            {/* ADR-0106 § 3: this page exists only for an Active or Suspended member; a Revoked one has none. */}
            <p>{dataRequest('refusal.subject_membership_current')}</p>
            <p>{admin('data_requests.erasure.revoke_first')}</p>
          </section>
        </>
      )}
    </section>
  );
}

/**
 * ADR-0106 §§ 3, 11: the erasure filing for a Revoked Membership, the page a revocation leads to. A Revoked Membership
 * has no member page and nothing about it is read here: the statement of what is erased and kept, the 24-hour window
 * and the explicit confirmation precede the one form, and the API refuses any Membership it does not cover.
 */
export function DataRequestErasure({
  snapshot,
  view,
  copy,
}: {
  readonly snapshot: NativePageSnapshot;
  readonly view: Extract<PageView, { kind: 'data_request_erasure' }>;
  readonly copy: Copy;
}) {
  const { admin, dataRequest } = copy;
  if (!snapshot.capabilities?.canManageMemberships) return null;
  return (
    <div className="auth-data-requests">
      <p>
        <a href="/studio/data-requests">{admin('data_requests.back')}</a>
      </p>
      {view.membershipId === null ? (
        <>
          <p>{admin('data_requests.erasure_page.missing')}</p>
          <p>
            <a href="/studio/members">{admin('data_requests.open_members')}</a>
          </p>
        </>
      ) : (
        <>
          <p>{admin('data_requests.erasure_page.revoked')}</p>
          <p>
            {admin('data_requests.erasure_page.membership')}: <code>{view.membershipId}</code>
          </p>
          <p>{admin('data_requests.erasure.summary')}</p>
          <p>{admin('data_requests.erasure_page.only_revoked')}</p>
          <details className="auth-danger">
            <summary>{admin('data_requests.erasure.start')}</summary>
            <h2>{admin('data_requests.erasure.erased_title')}</h2>
            <ul>
              <li>{admin('data_requests.erasure.erased_profile')}</li>
              <li>{admin('data_requests.erasure.erased_roles')}</li>
              <li>{admin('data_requests.erasure.erased_exports')}</li>
              <li>{admin('data_requests.erasure.erased_invitations')}</li>
            </ul>
            <h2>{admin('data_requests.erasure.kept_title')}</h2>
            <p>{admin('data_requests.erasure.kept_intro')}</p>
            <ul>
              <li>{admin('data_requests.erasure.kept_consent')}</li>
              <li>{admin('data_requests.erasure.kept_invitations')}</li>
              <li>{admin('data_requests.erasure.kept_audit')}</li>
              <li>{admin('data_requests.erasure.kept_attribution')}</li>
              <li>{admin('data_requests.erasure.kept_receipt')}</li>
            </ul>
            <p>{dataRequest('retained.audit_retention')}</p>
            <p>{dataRequest('erasure.kept_uncounted')}</p>
            <p>{dataRequest('erasure.kept_partial')}</p>
            <p id="data-erasure-timing">{admin('data_requests.erasure.timing')}</p>
            <form action="/api/studio/data-request-erasure" method="post">
              <FormIdentityFields snapshot={snapshot} index={0} />
              <input type="hidden" name="membershipId" value={view.membershipId} />
              <fieldset className="auth-data-request-confirm">
                <legend>{admin('data_requests.erasure.legend')}</legend>
                <label>
                  <input
                    type="checkbox"
                    name="confirm"
                    value="erase"
                    required
                    aria-required="true"
                  />
                  <span>
                    {admin('data_requests.erasure.confirm')} ({admin('invitations.required')})
                  </span>
                </label>
              </fieldset>
              <button type="submit" aria-describedby="data-erasure-timing">
                {admin('data_requests.erasure.action')}
              </button>
            </form>
          </details>
        </>
      )}
    </div>
  );
}
