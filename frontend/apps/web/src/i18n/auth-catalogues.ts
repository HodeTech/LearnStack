import 'server-only';

import { createTranslator } from 'next-intl';

import { selectUiLocale, textDirection } from './locale';
import en from './messages/en/auth.json';
import tr from './messages/tr/auth.json';

const catalogues = { en, tr };
type Leaves<T> = {
  [K in keyof T & string]: T[K] extends string ? K : `${K}.${Leaves<T[K]>}`;
}[keyof T & string];
export type AuthKey = Leaves<typeof en>;
export type AuthCopy = (key: AuthKey) => string;
export function getAuthCatalogue(contentLocale: string) {
  const locale = selectUiLocale(contentLocale);
  return Object.freeze({ locale, direction: textDirection(locale), messages: catalogues[locale] });
}
export function createAuthTranslator(contentLocale: string) {
  const { locale, messages } = getAuthCatalogue(contentLocale);
  const translate = createTranslator({
    locale,
    messages: { auth: messages },
    namespace: 'auth',
    onError: () => {},
    getMessageFallback: () => messages.error.description,
  });
  return Object.freeze({ locale, direction: textDirection(locale), copy: translate as AuthCopy });
}
