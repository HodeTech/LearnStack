import 'server-only';

import { createPublicTranslator } from '@/i18n/catalogues';

export function AuthSkipLink({ locale }: { readonly locale: string }) {
  const { locale: uiLocale, direction, t } = createPublicTranslator(locale);
  return (
    <a className="public-skip" href="#main-content" lang={uiLocale} dir={direction}>
      {t('chrome.skip_to_content')}
    </a>
  );
}
