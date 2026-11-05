/* eslint-disable @next/next/no-html-link-for-pages -- Protected views use freshly admitted full document navigation. */
import type { AdminCopy } from '@/i18n/administration-catalogues';
import type { NativePageSnapshot, PageInvitation, PageView } from '@/server/auth/page-contract';

import { FormIdentityFields, MutationFields } from './admin-forms';
import { roleLabel } from './admin-labels';

function invitationStatus(value: PageInvitation, admin: AdminCopy): string {
  return {
    Pending: admin('invitations.pending'),
    Accepted: admin('invitations.accepted'),
    Revoked: admin('invitations.revoked'),
    Expired: admin('invitations.expired'),
  }[value.status];
}
function deliveryStatus(value: PageInvitation, admin: AdminCopy): string {
  return {
    Queued: admin('invitations.queued'),
    SubmissionAccepted: admin('invitations.submission_accepted'),
    Failed: admin('invitations.failed'),
    Unresolved: admin('invitations.uncertain'),
    Unavailable: admin('invitations.unavailable'),
  }[value.deliveryStatus];
}
function InvitationFacts({
  value,
  locale,
  admin,
}: {
  readonly value: PageInvitation;
  readonly locale: string;
  readonly admin: AdminCopy;
}) {
  const expiresAt = new Intl.DateTimeFormat(locale, {
    dateStyle: 'long',
    timeStyle: 'short',
    timeZone: 'UTC',
  }).format(new Date(value.expiresAt));
  return (
    <>
      <dl className="auth-values">
        <dt>{admin('invitations.recipient')}</dt>
        <dd>
          <bdi className="auth-invitation-address">{value.email}</bdi>
        </dd>
        <dt>{admin('invitations.offered_role')}</dt>
        <dd>
          {value.offeredRole
            ? roleLabel(value.offeredRole, admin)
            : admin('invitations.role_unavailable')}
        </dd>
        <dt>{admin('invitations.scope')}</dt>
        <dd>
          {value.organizationScoped
            ? admin('scope.branch_member')
            : admin('scope.institution_member')}
        </dd>
        <dt>{admin('invitations.state')}</dt>
        <dd>{invitationStatus(value, admin)}</dd>
        <dt>{admin('invitations.expires')}</dt>
        <dd>
          <time dateTime={value.expiresAt}>{expiresAt} UTC</time>
        </dd>
        <dt>{admin('invitations.delivery')}</dt>
        <dd>{deliveryStatus(value, admin)}</dd>
      </dl>
      {value.acceptanceDisposition === 'already_member' && (
        <p>{admin('invitations.already_member')}</p>
      )}
      {value.acceptanceDisposition === 'created' && <p>{admin('invitations.created_member')}</p>}
    </>
  );
}

export function InvitationDirectory({
  snapshot,
  view,
  locale,
  admin,
}: {
  readonly snapshot: NativePageSnapshot;
  readonly view: Extract<PageView, { kind: 'invitations' }>;
  readonly locale: string;
  readonly admin: AdminCopy;
}) {
  const next =
    view.hasNextPage && view.endCursor !== null
      ? '/studio/invitations?' +
        new URLSearchParams({ cursor: view.endCursor, limit: String(view.limit) })
      : null;
  return (
    <>
      <section aria-labelledby="invitation-create-title">
        <h2 id="invitation-create-title">{admin('invitations.create')}</h2>
        <p>{admin('invitations.create_hint')}</p>
        {snapshot.capabilities?.canManageMemberships && view.roles.length > 0 ? (
          <form action="/api/studio/invitation-create" method="post">
            <FormIdentityFields snapshot={snapshot} index={0} />
            <div className="auth-field">
              <label htmlFor="invitation-email">
                {admin('invitations.recipient')} ({admin('invitations.required')})
              </label>
              <input
                id="invitation-email"
                name="email"
                type="email"
                maxLength={254}
                required
                aria-required="true"
                autoComplete="off"
                spellCheck={false}
                aria-describedby="invitation-email-hint"
              />
              <p id="invitation-email-hint">{admin('invitations.email_hint')}</p>
            </div>
            <div className="auth-field">
              <label htmlFor="invitation-role">
                {admin('invitations.offered_role')} ({admin('invitations.required')})
              </label>
              <select
                id="invitation-role"
                name="offeredRole"
                required
                aria-required="true"
                defaultValue=""
              >
                <option value="" disabled>
                  {admin('invitations.choose_role')}
                </option>
                {view.roles.map((role) => (
                  <option key={role.roleId} value={role.roleId + '.' + role.version}>
                    {roleLabel(role, admin)}
                  </option>
                ))}
              </select>
            </div>
            <button type="submit">{admin('invitations.send')}</button>
          </form>
        ) : (
          <p>{admin('invitations.no_roles')}</p>
        )}
      </section>
      <section aria-labelledby="invitation-list-title">
        <h2 id="invitation-list-title">{admin('invitations.list')}</h2>
        {view.items.length === 0 ? (
          <p>{admin('invitations.empty')}</p>
        ) : (
          <ul className="auth-resource-list">
            {view.items.map((value) => (
              <li key={value.invitationId}>
                <h3>
                  <a href={'/studio/invitations/' + value.invitationId}>
                    <bdi className="auth-invitation-address">{value.email}</bdi>
                  </a>
                </h3>
                <InvitationFacts value={value} locale={locale} admin={admin} />
              </li>
            ))}
          </ul>
        )}
        <nav aria-label={admin('invitations.list')}>
          <a href="/studio/invitations">{admin('members.first')}</a>
          {next && <a href={next}>{admin('invitations.next')}</a>}
        </nav>
      </section>
    </>
  );
}

export function InvitationDetails({
  snapshot,
  view,
  locale,
  admin,
}: {
  readonly snapshot: NativePageSnapshot;
  readonly view: Extract<PageView, { kind: 'invitation' }>;
  readonly locale: string;
  readonly admin: AdminCopy;
}) {
  const value = view.invitation;
  const target = ['invitationId', value.invitationId] as const;
  return (
    <>
      <p>
        <a href="/studio/invitations">{admin('invitations.back')}</a>
      </p>
      <InvitationFacts value={value} locale={locale} admin={admin} />
      <p>{admin('invitations.delivery_hint')}</p>
      {(value.deliveryStatus === 'Unresolved' || value.deliveryStatus === 'Unavailable') && (
        <p className="auth-notice">{admin('invitations.uncertain_hint')}</p>
      )}
      {!value.inCurrentScope && <p>{admin('invitations.other_scope')}</p>}
      {snapshot.capabilities?.canManageMemberships && value.inCurrentScope && (
        <>
          {value.canReissue ? (
            <section aria-labelledby="invitation-reissue-title">
              <h2 id="invitation-reissue-title">{admin('invitations.reissue')}</h2>
              <p id="invitation-reissue-hint">{admin('invitations.reissue_hint')}</p>
              <form action="/api/studio/invitation-reissue" method="post">
                <MutationFields
                  snapshot={snapshot}
                  index={0}
                  version={value.version}
                  target={target}
                />
                <button type="submit" aria-describedby="invitation-reissue-hint">
                  {admin('invitations.reissue')}
                </button>
              </form>
            </section>
          ) : (
            value.canRevoke && <p>{admin('invitations.role_changed')}</p>
          )}
          {value.canRevoke && (
            <details className="auth-danger">
              <summary>{admin('invitations.revoke')}</summary>
              <p id="invitation-revoke-hint">{admin('invitations.revoke_hint')}</p>
              <form action="/api/studio/invitation-revoke" method="post">
                <MutationFields
                  snapshot={snapshot}
                  index={1}
                  version={value.version}
                  target={target}
                />
                <button type="submit" aria-describedby="invitation-revoke-hint">
                  {admin('invitations.revoke')}
                </button>
              </form>
            </details>
          )}
        </>
      )}
    </>
  );
}

export function InvitationPolicy({
  snapshot,
  view,
  admin,
}: {
  readonly snapshot: NativePageSnapshot;
  readonly view: Extract<PageView, { kind: 'invitation_policy' }>;
  readonly admin: AdminCopy;
}) {
  const policy = view.policy;
  return (
    <>
      <p>{admin('invitations.policy_hint')}</p>
      <p>
        {policy.present ? admin('invitations.policy_stored') : admin('invitations.policy_default')}
      </p>
      {policy.canEdit && snapshot.scopeKind === 'tenant' ? (
        <form action="/api/studio/invitation-policy" method="post">
          <FormIdentityFields snapshot={snapshot} index={0} />
          <input type="hidden" name="presence" value={policy.present ? 'present' : 'absent'} />
          {policy.present && policy.version !== null && (
            <input type="hidden" name="version" value={policy.version} />
          )}
          <div className="auth-field">
            <label htmlFor="invitation-lifetime">
              {admin('invitations.policy_days')} ({admin('invitations.required')})
            </label>
            <input
              id="invitation-lifetime"
              name="days"
              type="number"
              min={1}
              max={30}
              step={1}
              required
              aria-required="true"
              defaultValue={policy.days}
              aria-describedby="invitation-lifetime-hint"
            />
            <p id="invitation-lifetime-hint">{admin('invitations.policy_range')}</p>
          </div>
          <button type="submit">{admin('invitations.policy_save')}</button>
        </form>
      ) : (
        <>
          <p>
            {admin('invitations.policy_days')}: {policy.days}
          </p>
          <p>{admin('invitations.policy_read_only')}</p>
        </>
      )}
    </>
  );
}
