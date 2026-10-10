import type { Metadata } from 'next';
import type { ReactNode } from 'react';

import { PublicTheme } from '@/components/public/theme';
import { textDirection } from '@/i18n/locale';
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
  const { request } = await requirePublicResource();
  assertPublicRequestActive(request);
  const locale = request.locale ?? 'en';
  return (
    <html lang={locale} dir={textDirection(locale)}>
      <head>
        <PublicTheme theme={request.site.theme} />
      </head>
      <body className="bg-ls-bg text-ls-fg font-sans antialiased">{children}</body>
    </html>
  );
}
