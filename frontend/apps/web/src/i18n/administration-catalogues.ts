import 'server-only';

import { createTranslator } from 'next-intl';

import type { NativePageSnapshot } from '@/server/auth/page-contract';

import { selectUiLocale, textDirection } from './locale';
import enAdmin from './messages/en/admin.json';
import enData from './messages/en/data.json';
import enProfile from './messages/en/profile.json';
import trAdmin from './messages/tr/admin.json';
import trData from './messages/tr/data.json';
import trProfile from './messages/tr/profile.json';

const catalogues = {
  en: { admin: enAdmin, profile: enProfile, data: enData },
  tr: { admin: trAdmin, profile: trProfile, data: trData },
};
type Leaves<T> = {
  [K in keyof T & string]: T[K] extends string ? K : `${K}.${Leaves<T[K]>}`;
}[keyof T & string];
export type AdminCopy = (key: Leaves<typeof enAdmin>) => string;
export type ProfileCopy = (key: Leaves<typeof enProfile>) => string;
/**
 * The person-facing data-request catalogue (ADR-0106), read in Studio only where its wording is exactly right for an
 * administrator too: the closed status vocabulary, neutral labels and the refusals worded for both readers.
 */
export type DataRequestUiCopy = (key: Leaves<typeof enData>) => string;

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
  const dataRequest = createTranslator({
    locale: selected.locale,
    messages: selected.messages.data,
    onError: () => {},
    getMessageFallback: () => selected.messages.admin.failure.unavailable,
  }) as DataRequestUiCopy;
  return Object.freeze({
    locale: selected.locale,
    direction: selected.direction,
    admin,
    profile,
    dataRequest,
  });
}

/** Only the current actor's exact enabled Membership preference selects protected UI. */
export function preferredPageLocale(snapshot: NativePageSnapshot): string {
  const preferred =
    snapshot.profileState === 'available' ? snapshot.ownProfile?.preferredLocale : null;
  return preferred && snapshot.site.enabledLocales.includes(preferred)
    ? preferred
    : snapshot.site.defaultLocale;
}
