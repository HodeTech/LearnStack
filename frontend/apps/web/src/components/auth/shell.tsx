import 'server-only';

/* eslint-disable @next/next/no-html-link-for-pages -- Full document navigation prevents protected Router Cache reuse. */
import {
  createAdministrationTranslator,
  preferredPageLocale,
} from '@/i18n/administration-catalogues';
import type { AdminCopy, ProfileCopy } from '@/i18n/administration-catalogues';
import { createAuthTranslator } from '@/i18n/auth-catalogues';
import type { AuthCopy } from '@/i18n/auth-catalogues';
import { optionalPreparedPage } from '@/server/auth/page';
import type { NativePageSnapshot, PageView } from '@/server/auth/page-contract';

import { MutationFields, ProfileFields, ProfileValues } from './admin-forms';
import { MemberDetails, MemberDirectory } from './admin-members';
import { RoleDetails, RoleDirectory } from './admin-roles';
import { SessionControls } from './session';

type PageKind = PageView['kind'];
function title(kind: PageKind, admin: AdminCopy, profile: ProfileCopy, copy: AuthCopy) {
  const titles: Record<PageKind, string> = {
    studio: copy('shell.studio'),
    portal: copy('shell.portal'),
    members: admin('members.title'),
    member: admin('members.detail_title'),
    roles: admin('roles.title'),
    role: admin('roles.detail_title'),
    profile: profile('title'),
  };
  return titles[kind];
}

function PageContents({
  snapshot,
  view,
  locale,
  admin,
  profile,
}: {
  readonly snapshot: NativePageSnapshot;
  readonly view: PageView;
  readonly locale: string;
  readonly admin: AdminCopy;
  readonly profile: ProfileCopy;
}) {
  switch (view.kind) {
    case 'members':
      return <MemberDirectory view={view} admin={admin} />;
    case 'member':
      return (
        <MemberDetails
          snapshot={snapshot}
          view={view}
          locale={locale}
          admin={admin}
          profile={profile}
        />
      );
    case 'roles':
      return <RoleDirectory view={view} admin={admin} />;
    case 'role':
      return <RoleDetails snapshot={snapshot} view={view} admin={admin} />;
    case 'profile': {
      if (snapshot.profileState !== 'available' || !snapshot.ownProfile)
        return (
          <p>{snapshot.profileState === 'missing' ? profile('missing') : profile('unavailable')}</p>
        );
      const value = snapshot.ownProfile;
      return view.canWrite && snapshot.capabilities?.canWriteOwnProfile ? (
        <form action="/api/account/profile" method="post">
          <MutationFields snapshot={snapshot} index={0} version={value.version} />
          <ProfileFields
            snapshot={snapshot}
            value={value}
            locale={locale}
            profile={profile}
            prefix="own-profile"
          />
        </form>
      ) : (
        <>
          <p>{profile('read_only')}</p>
          <ProfileValues value={value} locale={locale} profile={profile} />
          <p>{profile('fallback')}</p>
        </>
      );
    }
    case 'studio':
    case 'portal':
      return null;
  }
}

/** Pure view: all reads and authority decisions have settled in native preparation. */
export function PreparedAccountPage({
  snapshot,
  kind,
}: {
  readonly snapshot: NativePageSnapshot;
  readonly kind: PageKind;
}) {
  if (snapshot.outcome.kind === 'ready' && snapshot.outcome.view.kind !== kind)
    throw new Error('Prepared page mismatch');
  const preferred = preferredPageLocale(snapshot);
  const { locale, direction, admin, profile } = createAdministrationTranslator(preferred);
  const { copy } = createAuthTranslator(preferred);
  const capabilities = snapshot.capabilities;
  const labels = {
    warning: copy('session.warning'),
    until: copy('session.until'),
    extend: copy('session.extend'),
    extending: copy('session.extending'),
    extended: copy('session.extended'),
    unchanged: copy('session.unchanged'),
    maximum: copy('session.maximum'),
    failed: copy('session.failed'),
    expired: copy('session.expired'),
    reauthenticate: copy('session.reauthenticate'),
    retry: copy('session.retry'),
    javascript_required: copy('session.javascript_required'),
    unsaved: copy('session.unsaved'),
  };
  const failures = {
    denied: admin('failure.denied'),
    not_found: admin('failure.not_found'),
    invalid_cursor: admin('failure.invalid_cursor'),
    unavailable: admin('failure.unavailable'),
    busy: admin('failure.busy'),
  };
  return (
    <section
      className="auth-shell"
      lang={locale}
      dir={direction}
      data-bff-page-view={
        snapshot.outcome.kind === 'ready' ? snapshot.outcome.view.kind : 'unavailable'
      }
    >
      <h1>{title(kind, admin, profile, copy)}</h1>
      <SessionControls
        initial={snapshot.session}
        labels={labels}
        locale={locale}
        returnTo={snapshot.target}
        recoveryActions={
          <form action="/api/auth/signout" method="post">
            <input name="csrfToken" type="hidden" value={snapshot.session.csrfToken} />
            <button type="submit">{copy('shell.signout')}</button>
            <p>{copy('shell.signout_scope')}</p>
          </form>
        }
      >
        <h2>{copy('shell.account')}</h2>
        <p>
          {copy('shell.active')}: {snapshot.session.displayName}
        </p>
        <p>
          {snapshot.scopeKind === 'tenant' ? admin('scope.tenant') : admin('scope.organization')}
        </p>
        <nav aria-label={admin('navigation.title')}>
          <a href="/studio">{copy('shell.studio')}</a>
          <a href="/portal">{copy('shell.portal')}</a>
          {(capabilities?.canReadTenantMembers || capabilities?.canReadOrganizationMembers) && (
            <a href="/studio/members">{admin('navigation.members')}</a>
          )}
          {capabilities?.canReadRoles && <a href="/studio/roles">{admin('navigation.roles')}</a>}
          <a href="/portal/profile">{admin('navigation.profile')}</a>
          <a href="/api/auth/entry">{admin('navigation.contexts')}</a>
          <a href={'/' + snapshot.site.defaultLocale + '/courses'}>{copy('shell.catalog')}</a>
        </nav>
        {kind !== 'profile' && snapshot.profileState === 'unavailable' && (
          <p className="auth-notice">{profile('unavailable')}</p>
        )}
        {snapshot.outcome.kind === 'failure' ? (
          <section className="auth-notice" aria-labelledby="page-failure-title">
            <h2 id="page-failure-title">{admin('failure.title')}</h2>
            <p role="alert">{failures[snapshot.outcome.reason]}</p>
            {snapshot.outcome.retryAfterSeconds !== null && (
              <p>
                {admin('failure.retry_after')}: {snapshot.outcome.retryAfterSeconds}
              </p>
            )}
            <p>
              <a
                href={
                  snapshot.outcome.reason === 'invalid_cursor' ? '/studio/members' : snapshot.target
                }
              >
                {snapshot.outcome.reason === 'invalid_cursor'
                  ? admin('members.first')
                  : admin('failure.retry')}
              </a>
            </p>
          </section>
        ) : (
          <PageContents
            snapshot={snapshot}
            view={snapshot.outcome.view}
            locale={locale}
            admin={admin}
            profile={profile}
          />
        )}
      </SessionControls>
    </section>
  );
}

export async function AccountPage({ kind }: { readonly kind: PageKind }) {
  const prepared = await optionalPreparedPage();
  if (!prepared) return null;
  prepared.assertActive();
  return <PreparedAccountPage snapshot={prepared.snapshot} kind={kind} />;
}

export async function AccountShell({ kind }: { readonly kind: 'studio' | 'portal' }) {
  return AccountPage({ kind });
}

export async function accountPageMetadata(kind: PageKind) {
  const prepared = await optionalPreparedPage();
  if (!prepared) return { robots: { index: false, follow: false } };
  prepared.assertActive();
  const locale = preferredPageLocale(prepared.snapshot);
  const { admin, profile } = createAdministrationTranslator(locale);
  const { copy } = createAuthTranslator(locale);
  return { title: title(kind, admin, profile, copy), robots: { index: false, follow: false } };
}
export const accountShellMetadata = (kind: 'studio' | 'portal') => accountPageMetadata(kind);
