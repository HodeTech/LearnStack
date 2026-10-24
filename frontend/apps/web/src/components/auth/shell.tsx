import 'server-only';

/* eslint-disable @next/next/no-html-link-for-pages -- Full document navigation prevents protected Router Cache reuse (ADR-0070). */

import { redirect } from 'next/navigation';

import { SessionControls } from '@/components/auth/session';
import { createAuthTranslator } from '@/i18n/auth-catalogues';
import { auth } from '@/server/auth/auth';
import { assertPublicRequestActive } from '@/server/public-request';
import { requirePublicResource } from '@/server/public-resource';

export async function AccountShell({ kind }: { readonly kind: 'studio' | 'portal' }) {
  const { request } = await requirePublicResource();
  const session = await auth();
  assertPublicRequestActive(request);
  if (!session) redirect('/api/auth/signin?returnTo=' + encodeURIComponent('/' + kind));
  const { locale, direction, copy } = createAuthTranslator(request.site.defaultLocale);
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
  };
  return (
    <section className="auth-shell" lang={locale} dir={direction}>
      <h1>{kind === 'studio' ? copy('shell.studio') : copy('shell.portal')}</h1>
      <SessionControls initial={session} labels={labels} locale={locale}>
        <p>
          {copy('shell.active')}: {session.displayName}
        </p>
        <h2>{copy('shell.account')}</h2>
        <nav aria-label={copy('shell.account')}>
          <a href="/studio">{copy('shell.studio')}</a> <a href="/portal">{copy('shell.portal')}</a>{' '}
          <a href={'/' + request.site.defaultLocale + '/courses'}>{copy('shell.catalog')}</a>
        </nav>
        <form action="/api/auth/signout" method="post">
          <input name="csrfToken" type="hidden" value={session.csrfToken} />
          <button type="submit">{copy('shell.signout')}</button>
          <p>{copy('shell.signout_scope')}</p>
        </form>
      </SessionControls>
    </section>
  );
}

export async function accountShellMetadata(kind: 'studio' | 'portal') {
  const { request } = await requirePublicResource();
  assertPublicRequestActive(request);
  const { copy } = createAuthTranslator(request.site.defaultLocale);
  return {
    title: kind === 'studio' ? copy('shell.studio') : copy('shell.portal'),
    robots: { index: false, follow: false },
  };
}
