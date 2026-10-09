import 'server-only';

import { getLocale, getTranslations } from 'next-intl/server';
import { cache } from 'react';

import type { PublicTranslate } from '@/i18n/catalogues';
import { textDirection } from '@/i18n/locale';

/** Uses the plugin's verified request configuration, never a callsite locale override. */
export const getPublicUi = cache(async () => {
  const [locale, translate] = await Promise.all([getLocale(), getTranslations('public')]);
  const t: PublicTranslate = (key, ...args) => translate(key as string, args[0]);
  return { locale, direction: textDirection(locale), t };
});
