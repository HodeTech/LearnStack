import { describe, expect, it, vi } from 'vitest';

import { selectUiLocale, textDirection } from './locale';

describe('public UI locale selection', () => {
  it.each([
    ['en', 'en'],
    ['tr', 'tr'],
    ['tr-TR', 'tr'],
    ['tr-Latn-TR', 'tr'],
    ['en-Latn-US', 'en'],
    ['zh-Hans-CN', 'en'],
    ['ar', 'en'],
    ['und', 'en'],
  ])('selects one whole catalogue for admitted %s', (contentLocale, expected) => {
    expect(selectUiLocale(contentLocale)).toBe(expected);
  });

  it.each([
    ['ar', 'rtl'],
    ['ar-EG', 'rtl'],
    ['he', 'rtl'],
    ['az-Arab', 'rtl'],
    ['az-Latn', 'ltr'],
    ['en', 'ltr'],
    ['tr-TR', 'ltr'],
    ['und', 'ltr'],
    ['not_a_locale', 'ltr'],
  ])('derives %s direction from runtime data', (locale, expected) => {
    expect(textDirection(locale)).toBe(expected);
  });

  it('uses deterministic ltr when runtime locale information is unavailable', () => {
    const unavailable = new RangeError('unavailable runtime data');
    const constructor = vi.spyOn(Intl, 'Locale').mockImplementation(function () {
      throw unavailable;
    });
    expect(textDirection('ar')).toBe('ltr');
    expect(constructor).toHaveBeenCalledExactlyOnceWith('ar');
    expect(constructor.mock.results[0]?.type).toBe('throw');
    expect(constructor.mock.results[0]?.value).toBe(unavailable);
  });
});
