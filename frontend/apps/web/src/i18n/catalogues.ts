import 'server-only';

import { createTranslator } from 'next-intl';

import { selectUiLocale, textDirection } from './locale';
import en from './messages/en/public.json';
import tr from './messages/tr/public.json';

const catalogues = { en, tr };

type LeafKeys<T> = {
  [K in keyof T & string]: T[K] extends string ? K : `${K}.${LeafKeys<T[K]>}`;
}[keyof T & string];

export type PublicMessageKey = LeafKeys<typeof en>;
export type PublicMessageArguments<K extends PublicMessageKey> = K extends
  | 'catalog.course_count'
  | 'course.lesson_count'
  ? { count: number }
  : never;
export type PublicTranslate = <K extends PublicMessageKey>(
  key: K,
  ...args: [PublicMessageArguments<K>] extends [never]
    ? [values?: undefined]
    : [values: PublicMessageArguments<K>]
) => string;

export function getPublicCatalogue(contentLocale: string) {
  const locale = selectUiLocale(contentLocale);
  return { locale, direction: textDirection(locale), messages: catalogues[locale] };
}

/** Also supplied to next-intl's server request configuration. */
export function publicFormattingOptions(contentLocale: string) {
  const { messages } = getPublicCatalogue(contentLocale);
  return {
    // Library diagnostics may contain keys and interpolated values.
    onError: () => {},
    getMessageFallback: () => messages.page.unavailable.description,
  };
}

export function createPublicTranslator(contentLocale: string) {
  const catalogue = getPublicCatalogue(contentLocale);
  const translate = createTranslator({
    locale: catalogue.locale,
    messages: { public: catalogue.messages },
    namespace: 'public',
    ...publicFormattingOptions(contentLocale),
  });
  // JSON imports preserve key shapes but widen ICU values to string. This
  // interface restores the checked argument contract without exposing rich/raw.
  const t = translate as unknown as PublicTranslate;
  return { locale: catalogue.locale, direction: catalogue.direction, t };
}
