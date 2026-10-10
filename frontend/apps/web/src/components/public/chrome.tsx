import type { ReactNode } from 'react';

import type { PublicTranslate } from '@/i18n/catalogues';
import type { TextDirection } from '@/i18n/locale';
import type { PublicSite } from '@/server/public-entry';

type PublicChromeProps = {
  readonly site: PublicSite;
  readonly contentLocale: string;
  readonly uiLocale: string;
  readonly uiDirection: TextDirection;
  readonly t: PublicTranslate;
  readonly children: ReactNode;
};

export function PublicChrome({
  site,
  contentLocale,
  uiLocale,
  uiDirection,
  t,
  children,
}: PublicChromeProps) {
  const catalogPath = `/${contentLocale}/courses`;
  return (
    <>
      <a className="public-skip" href="#main-content" lang={uiLocale} dir={uiDirection}>
        {t('chrome.skip_to_content')}
      </a>
      <header className="public-header">
        <p className="public-institution">{site.displayName}</p>
        <nav lang={uiLocale} dir={uiDirection} aria-label={t('chrome.catalog')}>
          <a href={catalogPath}>{t('chrome.catalog')}</a>
        </nav>
      </header>
      <main id="main-content" tabIndex={-1} className="public-main">
        {children}
      </main>
      {site.showPlatformAttribution && (
        <footer className="public-footer" lang={uiLocale} dir={uiDirection}>
          <p>{t('chrome.attribution')}</p>
        </footer>
      )}
    </>
  );
}
