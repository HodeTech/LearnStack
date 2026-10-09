import type { ReactNode } from 'react';

import { PublicChrome } from '@/components/public/chrome';
import { ErrorLabelsProvider } from '@/components/public/error-labels';
import { requirePublicResource } from '@/server/public-resource';
import { getPublicUi } from '@/server/public-ui';

// ADR-0053: no public representation may survive into another host/request.
export const dynamic = 'force-dynamic';
export const revalidate = 0;
export const fetchCache = 'force-no-store';

type PublicLayoutProps = {
  readonly children: ReactNode;
};

export default async function PublicLayout({ children }: PublicLayoutProps) {
  const { request } = await requirePublicResource();
  const ui = await getPublicUi();
  const contentLocale = request.locale ?? 'en';
  const recoveryPath = `/${contentLocale}/courses`;
  return (
    <PublicChrome
      site={request.site}
      contentLocale={contentLocale}
      uiLocale={ui.locale}
      uiDirection={ui.direction}
      t={ui.t}
    >
      <ErrorLabelsProvider
        labels={{
          locale: ui.locale,
          direction: ui.direction,
          title: ui.t('page.error.title'),
          description: ui.t('page.error.description'),
          retry: ui.t('page.error.retry'),
          recovery: ui.t('page.recovery'),
          recoveryPath,
        }}
      >
        {children}
      </ErrorLabelsProvider>
    </PublicChrome>
  );
}
