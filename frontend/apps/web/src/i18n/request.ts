import 'server-only';

import { notFound } from 'next/navigation';
import { getRequestConfig } from 'next-intl/server';

import { optionalPreparedPage } from '@/server/auth/page';
import { assertPublicRequestActive, getPublicRequest } from '@/server/public-request';

import { getAdministrationCatalogue, preferredPageLocale } from './administration-catalogues';
import { getAuthCatalogue } from './auth-catalogues';
import { getPublicCatalogue, publicFormattingOptions } from './catalogues';

export default getRequestConfig(async () => {
  const request = await getPublicRequest();
  if (!request) notFound();
  const prepared = await optionalPreparedPage();
  prepared?.assertActive();
  assertPublicRequestActive(request);
  // English applies only to an admitted non-localized scaffold, never a refusal.
  const contentLocale = prepared
    ? preferredPageLocale(prepared.snapshot)
    : (request.locale ?? 'en');
  const catalogue = getPublicCatalogue(contentLocale);
  const administration = getAdministrationCatalogue(contentLocale);
  return {
    locale: catalogue.locale,
    messages: {
      public: catalogue.messages,
      auth: getAuthCatalogue(contentLocale).messages,
      ...administration.messages,
    },
    ...publicFormattingOptions(contentLocale),
  };
});
