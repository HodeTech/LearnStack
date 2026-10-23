import { describe, expect, it } from 'vitest';

import { renderBffDocument } from './html';
import type { PublicSite } from '../public-entry';

const site = (): PublicSite => ({
  displayName: 'Institution',
  enabledLocales: ['en', 'tr'],
  defaultLocale: 'en',
  theme: null,
  showPlatformAttribution: true,
});

describe('bounded localized BFF documents', () => {
  it.each(['en', 'tr'])(
    'renders a native form and complete %s UI without a client catalogue or local password',
    (locale) => {
      const document = new DOMParser().parseFromString(
        renderBffDocument(
          { ...site(), defaultLocale: locale },
          { kind: 'signin', csrfToken: 'A'.repeat(43), returnPath: '/studio' },
        ),
        'text/html',
      );
      expect(document.documentElement.lang).toBe(locale);
      expect(document.body.firstElementChild?.getAttribute('href')).toBe('#main-content');
      expect(document.querySelector('#main-content')?.getAttribute('tabindex')).toBe('-1');
      expect(document.body.firstElementChild?.textContent).toMatch(
        locale === 'en' ? /Skip/ : /İçeriğe/,
      );
      expect(document.querySelector('form')?.getAttribute('action')).toBe(
        '/api/auth/signin/keycloak',
      );
      expect(document.querySelector('form')?.getAttribute('method')).toBe('post');
      expect(document.querySelector('[name="csrfToken"]')?.getAttribute('value')).toBe(
        'A'.repeat(43),
      );
      expect(document.querySelector('[name="returnTo"]')?.getAttribute('value')).toBe('/studio');
      expect(document.querySelector('input[type="password"], script')).toBeNull();
      expect(document.querySelector('button')?.textContent).toMatch(
        locale === 'en' ? /Continue/ : /devam/,
      );
    },
  );
  it('escapes institution text and all form attributes, and rejects arbitrary theme CSS', () => {
    const document = new DOMParser().parseFromString(
      renderBffDocument(
        {
          ...site(),
          displayName: '<img src=x onerror=alert(1)>',
          theme: { background: '</style><script>private</script>' },
        } as never,
        { kind: 'signin', csrfToken: '" autofocus onfocus=alert(1) x="', returnPath: '/studio' },
      ),
      'text/html',
    );
    expect(document.querySelector('img, script, [autofocus], [onfocus]')).toBeNull();
    expect(document.body.textContent).toContain('<img src=x');
    expect(document.querySelector('style')?.textContent).not.toContain('private');
  });
});
