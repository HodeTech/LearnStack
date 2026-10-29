import type { ReactNode } from 'react';

import type { ProfileCopy } from '@/i18n/administration-catalogues';
import type { NativePageSnapshot, PageProfile } from '@/server/auth/page-contract';

import { languageLabel } from './admin-labels';

export function MutationFields({
  snapshot,
  index,
  version,
  target,
}: {
  readonly snapshot: NativePageSnapshot;
  readonly index: number;
  readonly version: string;
  readonly target?: readonly ['membershipId' | 'roleId', string];
}) {
  const key = snapshot.formKeys[index];
  if (!key || !/^[A-Za-z0-9_-]{43}$/.test(key)) throw new Error('Prepared form unavailable');
  return (
    <>
      <input type="hidden" name="csrfToken" value={snapshot.session.csrfToken} />
      <input type="hidden" name="version" value={version} />
      <input type="hidden" name="idempotencyKey" value={key} />
      {target && <input type="hidden" name={target[0]} value={target[1]} />}
    </>
  );
}

export function ProfileFields({
  value,
  snapshot,
  locale,
  profile,
  prefix,
  children,
}: {
  readonly value: PageProfile;
  readonly snapshot: NativePageSnapshot;
  readonly locale: string;
  readonly profile: ProfileCopy;
  readonly prefix: string;
  readonly children?: ReactNode;
}) {
  const selected =
    value.preferredLocale && snapshot.site.enabledLocales.includes(value.preferredLocale)
      ? value.preferredLocale
      : '';
  return (
    <>
      <div className="auth-field">
        <label htmlFor={prefix + '-name'}>{profile('display_name')}</label>
        <input
          id={prefix + '-name'}
          name="displayName"
          defaultValue={value.displayName ?? ''}
          maxLength={128}
          aria-describedby={prefix + '-name-hint'}
        />
        <p id={prefix + '-name-hint'}>{profile('display_name_hint')}</p>
      </div>
      <div className="auth-field">
        <label htmlFor={prefix + '-locale'}>{profile('preferred_locale')}</label>
        <select
          id={prefix + '-locale'}
          name="preferredLocale"
          defaultValue={selected}
          aria-describedby={prefix + '-locale-hint'}
        >
          <option value="">{profile('site_default')}</option>
          {snapshot.site.enabledLocales.map((tag) => (
            <option value={tag} key={tag}>
              {languageLabel(tag, locale)}
            </option>
          ))}
        </select>
        <p id={prefix + '-locale-hint'}>
          {profile('locale_hint')} {profile('fallback')}
        </p>
      </div>
      {children}
      <button type="submit">{profile('save')}</button>
    </>
  );
}

export function ProfileValues({
  value,
  locale,
  profile,
}: {
  readonly value: PageProfile;
  readonly locale: string;
  readonly profile: ProfileCopy;
}) {
  return (
    <dl className="auth-values">
      <dt>{profile('display_name')}</dt>
      <dd>{value.displayName ?? profile('no_name')}</dd>
      <dt>{profile('preferred_locale')}</dt>
      <dd>
        {value.preferredLocale
          ? languageLabel(value.preferredLocale, locale)
          : profile('site_default')}
      </dd>
    </dl>
  );
}
