import 'server-only';

import { createTranslator } from 'next-intl';

import type { NativePageSnapshot } from '@/server/auth/page-contract';

import { selectUiLocale, textDirection } from './locale';
import enAdmin from './messages/en/admin.json';
import enProfile from './messages/en/profile.json';
import trAdmin from './messages/tr/admin.json';
import trProfile from './messages/tr/profile.json';

const catalogues = {
  en: { admin: enAdmin, profile: enProfile },
  tr: { admin: trAdmin, profile: trProfile },
};
type Leaves<T> = {
  [K in keyof T & string]: T[K] extends string ? K : `${K}.${Leaves<T[K]>}`;
}[keyof T & string];
export type AdminCopy = (key: Leaves<typeof enAdmin>) => string;
export type ProfileCopy = (key: Leaves<typeof enProfile>) => string;

export function getAdministrationCatalogue(locale: string) {
  const selected = selectUiLocale(locale);
  return Object.freeze({
    locale: selected,
    direction: textDirection(selected),
    messages: catalogues[selected],
  });
}

export function createAdministrationTranslator(locale: string) {
  const selected = getAdministrationCatalogue(locale);
  const admin = createTranslator({
    locale: selected.locale,
    messages: selected.messages.admin,
    onError: () => {},
    getMessageFallback: () => selected.messages.admin.failure.unavailable,
  }) as AdminCopy;
  const profile = createTranslator({
    locale: selected.locale,
    messages: selected.messages.profile,
    onError: () => {},
    getMessageFallback: () => selected.messages.profile.unavailable,
  }) as ProfileCopy;
  return Object.freeze({ locale: selected.locale, direction: selected.direction, admin, profile });
}

/** Only the current actor's exact enabled Membership preference selects protected UI. */
export function preferredPageLocale(snapshot: NativePageSnapshot): string {
  const preferred =
    snapshot.profileState === 'available' ? snapshot.ownProfile?.preferredLocale : null;
  return preferred && snapshot.site.enabledLocales.includes(preferred)
    ? preferred
    : snapshot.site.defaultLocale;
}
