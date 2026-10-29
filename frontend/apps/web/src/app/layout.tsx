import type { Metadata } from 'next';
import Script from 'next/script';
import type { ReactNode } from 'react';

import { BFF_HISTORY_SOURCE } from '@/components/auth/history';
import { AuthSkipLink } from '@/components/auth/skip-link';
import { PublicTheme } from '@/components/public/theme';
import { getAdministrationCatalogue, preferredPageLocale } from '@/i18n/administration-catalogues';
import { textDirection } from '@/i18n/locale';
import { optionalPreparedPage } from '@/server/auth/page';
import { bffSessionEnabled } from '@/server/auth/session-owner';
import { assertPublicRequestActive } from '@/server/public-request';
import { requirePublicResource } from '@/server/public-resource';

import './globals.css';

export const metadata: Metadata = {
  title: 'LearnStack',
};

type RootLayoutProps = {
  readonly children: ReactNode;
};

export default async function RootLayout({ children }: RootLayoutProps) {
  // Do not let loading.tsx flush a shell before the missing-resource redirect.
  const { request, kind } = await requirePublicResource();
  const prepared = await optionalPreparedPage();
  prepared?.assertActive();
  assertPublicRequestActive(request);
  const locale = prepared
    ? getAdministrationCatalogue(preferredPageLocale(prepared.snapshot)).locale
    : (request.locale ?? 'en');
  const protectedShell = kind === 'scaffold' && bffSessionEnabled();
  return (
    <html lang={locale} dir={textDirection(locale)}>
      <head>
        <PublicTheme theme={request.site.theme} />
        {protectedShell && (
          <Script id="bff-history" strategy="beforeInteractive">
            {BFF_HISTORY_SOURCE}
          </Script>
        )}
      </head>
      <body className="bg-ls-bg text-ls-fg font-sans antialiased">
        {protectedShell && <AuthSkipLink locale={locale} />}
        {children}
      </body>
    </html>
  );
}
