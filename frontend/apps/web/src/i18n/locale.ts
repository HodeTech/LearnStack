export const uiLocales = ['en', 'tr'] as const;
export type UiLocale = (typeof uiLocales)[number];
export type TextDirection = 'ltr' | 'rtl';

/** Selects UI copy only; callers retain the admitted content locale. */
export function selectUiLocale(contentLocale: string): UiLocale {
  let candidate = contentLocale;
  while (candidate) {
    const authored = uiLocales.find((locale) => locale === candidate);
    if (authored) return authored;
    const separator = candidate.lastIndexOf('-');
    if (separator < 0) break;
    candidate = candidate.slice(0, separator);
  }
  return 'en';
}

/** Runtime data supplies script direction, including script-qualified tags. */
export function textDirection(locale: string): TextDirection {
  try {
    const data = new Intl.Locale(locale) as Intl.Locale & {
      getTextInfo?: () => { direction?: string };
      textInfo?: { direction?: string };
    };
    const direction = data.getTextInfo?.().direction ?? data.textInfo?.direction;
    return direction === 'rtl' ? 'rtl' : 'ltr';
  } catch {
    return 'ltr';
  }
}
