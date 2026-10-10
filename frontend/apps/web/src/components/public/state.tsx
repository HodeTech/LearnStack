import type { PublicTranslate } from '@/i18n/catalogues';
import type { TextDirection } from '@/i18n/locale';
import type { PublicFailureState } from '@/server/public-resource';

type StateProps = {
  readonly state: PublicFailureState | 'missing' | 'loading';
  readonly recoveryPath: string;
  readonly locale: string;
  readonly direction: TextDirection;
  readonly t: PublicTranslate;
};

/** Closed page outcomes select owned UI keys; no Problem Details enters this view. */
export function PublicState({ state, recoveryPath, locale, direction, t }: StateProps) {
  const title =
    state === 'missing'
      ? t('page.missing.title')
      : state === 'invalid_cursor'
        ? t('page.invalid_cursor.title')
        : state === 'rate_limited'
          ? t('page.rate_limited.title')
          : state === 'loading'
            ? t('page.loading.title')
            : t('page.unavailable.title');
  const description =
    state === 'missing'
      ? t('page.missing.description')
      : state === 'invalid_cursor'
        ? t('page.invalid_cursor.description')
        : state === 'rate_limited'
          ? t('page.rate_limited.description')
          : state === 'loading'
            ? t('page.loading.description')
            : t('page.unavailable.description');
  return (
    <section
      className="public-state"
      lang={locale}
      dir={direction}
      role={state === 'loading' ? 'status' : undefined}
    >
      <h1>{title}</h1>
      <p className="public-muted">{description}</p>
      {state !== 'loading' && (
        <p>
          <a href={recoveryPath}>
            {state === 'invalid_cursor' ? t('page.invalid_cursor.reset') : t('page.recovery')}
          </a>
        </p>
      )}
    </section>
  );
}
