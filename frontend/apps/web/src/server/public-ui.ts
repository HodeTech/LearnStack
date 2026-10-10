import 'server-only';

import { notFound } from 'next/navigation';
import { getLocale, getTranslations } from 'next-intl/server';
import { cache } from 'react';

import type { PublicTranslate } from '@/i18n/catalogues';
import { textDirection } from '@/i18n/locale';

import { assertPublicRequestActive, getPublicRequest } from './public-request';

/** Uses the plugin's verified request configuration, never a callsite locale override. */
const loadPublicUi = cache(async () => {
  const request = await getPublicRequest();
  if (!request) notFound();
  assertPublicRequestActive(request);
  const [locale, translate] = await Promise.all([getLocale(), getTranslations('public')]);
  assertPublicRequestActive(request);
  const t: PublicTranslate = (key, ...args) => {
    assertPublicRequestActive(request);
    return translate(key as string, args[0]);
  };
  return { request, ui: { locale, direction: textDirection(locale), t } };
});

/** React cache may replay a settled result after the response has completed. */
export async function getPublicUi() {
  const { request, ui } = await loadPublicUi();
  assertPublicRequestActive(request);
  return ui;
}
