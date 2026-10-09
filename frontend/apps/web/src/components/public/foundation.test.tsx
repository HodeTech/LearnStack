import { render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';

import PublicError from '@/app/(public)/error';
import { createPublicTranslator } from '@/i18n/catalogues';
import type { PublicSite } from '@/server/public-entry';

import { PublicChrome } from './chrome';
import { ErrorLabelsProvider } from './error-labels';
import { PublicState } from './state';
import { PublicTheme, publicThemeCss } from './theme';

const palette = {
  primary: '#1f6feb',
  background: '#ffffff',
  foreground: '#0f172a',
  muted: '#64748b',
};
const site: PublicSite = {
  displayName: '<script>private institution</script>',
  enabledLocales: ['tr', 'en', 'ar'],
  defaultLocale: 'tr',
  theme: palette,
  showPlatformAttribution: true,
};

describe('P02d-6 document foundation', () => {
  it('Public_Theme_Emits_Only_Validated_Color_Tokens', () => {
    expect(publicThemeCss(palette)).toBe(
      ':root{--ls-primary:#1f6feb;--ls-bg:#ffffff;--ls-fg:#0f172a;--ls-muted:#64748b;}',
    );
    const { container } = render(<PublicTheme theme={palette} />);
    expect(container.querySelectorAll('style')).toHaveLength(1);
    expect(container.querySelector('[style]')).toBeNull();
    expect(container.querySelector('style')?.textContent).toBe(publicThemeCss(palette));
  });

  it.each([
    null,
    {},
    [],
    { ...palette, muted: null },
    { ...palette, primary: '#abc' },
    { ...palette, background: 'red' },
    { ...palette, foreground: '#ffffff;}' },
    { ...palette, muted: '</style><script>private</script>' },
    { ...palette, url: 'https://private.invalid' },
    { ...palette, font: 'private' },
  ])('rejects the whole malformed theme without partial tokens: %j', (theme) => {
    expect(publicThemeCss(theme)).toBeNull();
    const { container } = render(<PublicTheme theme={theme} />);
    expect(container.innerHTML).toBe('');
  });

  it('provides one main/skip target, inert institution text and labelled fallback chrome', () => {
    const ui = createPublicTranslator('ar');
    const { container } = render(
      <PublicChrome
        site={site}
        contentLocale="ar"
        uiLocale={ui.locale}
        uiDirection={ui.direction}
        t={ui.t}
      >
        <h1 lang="ar">عنوان</h1>
      </PublicChrome>,
    );
    expect(screen.getAllByRole('main')).toHaveLength(1);
    const main = screen.getByRole('main');
    expect(main.id).toBe('main-content');
    expect(main).toHaveAttribute('tabindex', '-1');
    expect(screen.getByRole('link', { name: 'Skip to content' })).toHaveAttribute(
      'href',
      '#main-content',
    );
    const nav = screen.getByRole('navigation');
    expect(nav).toHaveAttribute('lang', 'en');
    expect(nav).toHaveAttribute('dir', 'ltr');
    expect(screen.getByRole('link', { name: 'Course catalog' })).toHaveAttribute(
      'href',
      '/ar/courses',
    );
    expect(screen.getByText(site.displayName)).toBeInTheDocument();
    expect(container.querySelector('script')).toBeNull();
    expect(screen.getByText('Powered by LearnStack')).toBeInTheDocument();
  });

  it('uses only the effective attribution value independently of the safe theme', () => {
    const ui = createPublicTranslator('tr');
    render(
      <PublicChrome
        site={{ ...site, showPlatformAttribution: false }}
        contentLocale="tr"
        uiLocale={ui.locale}
        uiDirection={ui.direction}
        t={ui.t}
      >
        <h1>Başlık</h1>
      </PublicChrome>,
    );
    expect(screen.queryByRole('contentinfo')).toBeNull();
    expect(screen.getByRole('link', { name: 'Kurs kataloğu' })).toHaveAttribute(
      'href',
      '/tr/courses',
    );
    expect(publicThemeCss(site.theme)).not.toBeNull();
  });

  it.each(['missing', 'invalid_cursor', 'rate_limited', 'unavailable', 'loading'] as const)(
    'renders a bounded translated %s state with one descriptive heading',
    (state) => {
      const ui = createPublicTranslator('tr');
      const { container } = render(
        <PublicState
          state={state}
          recoveryPath="/tr/courses"
          locale={ui.locale}
          direction={ui.direction}
          t={ui.t}
        />,
      );
      expect(screen.getAllByRole('heading', { level: 1 })).toHaveLength(1);
      expect(container.querySelector('section')).toHaveAttribute('lang', 'tr');
      expect(container.querySelector('section')).toHaveAttribute('dir', 'ltr');
      expect(screen.queryAllByRole('link')).toHaveLength(state === 'loading' ? 0 : 1);
      expect(container.textContent).not.toMatch(/lockey_|public\.|private/);
    },
  );

  it('keeps unexpected error details outside the interactive boundary and permits retry', () => {
    const ui = createPublicTranslator('tr');
    const reset = vi.fn();
    const labels = {
      locale: ui.locale,
      direction: ui.direction,
      title: ui.t('page.error.title'),
      description: ui.t('page.error.description'),
      retry: ui.t('page.error.retry'),
      recovery: ui.t('page.recovery'),
      recoveryPath: '/tr/courses',
    };
    const { container } = render(
      <ErrorLabelsProvider labels={labels}>
        <PublicError error={new Error('private-secret-raw-key')} reset={reset} />
      </ErrorLabelsProvider>,
    );
    expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent(labels.title);
    screen.getByRole('button', { name: labels.retry }).click();
    expect(reset).toHaveBeenCalledOnce();
    expect(container.textContent).not.toContain('private-secret-raw-key');
    expect(screen.getByRole('link')).toHaveAttribute('href', '/tr/courses');
  });
});
