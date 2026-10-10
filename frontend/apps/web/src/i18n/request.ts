import 'server-only';

import { notFound } from 'next/navigation';
import { getRequestConfig } from 'next-intl/server';

import { assertPublicRequestActive, getPublicRequest } from '@/server/public-request';

import { getPublicCatalogue, publicFormattingOptions } from './catalogues';

export default getRequestConfig(async () => {
  const request = await getPublicRequest();
  if (!request) notFound();
  assertPublicRequestActive(request);
  // English applies only to an admitted non-localized scaffold, never a refusal.
  const contentLocale = request.locale ?? 'en';
  const catalogue = getPublicCatalogue(contentLocale);
  return {
    locale: catalogue.locale,
    messages: { public: catalogue.messages },
    ...publicFormattingOptions(contentLocale),
  };
});
