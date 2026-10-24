import 'server-only';

/* eslint-disable @next/next/no-html-link-for-pages -- Authentication is an owned full document flow (ADR-0070), never a prefetched route. */

import { createAuthTranslator } from '@/i18n/auth-catalogues';
import { bffSessionEnabled } from '@/server/auth/session-owner';

export function SignInLink({ locale }: { readonly locale: string }) {
  if (!bffSessionEnabled()) return null;
  const { copy } = createAuthTranslator(locale);
  return <a href="/api/auth/signin">{copy('shell.signin')}</a>;
}
