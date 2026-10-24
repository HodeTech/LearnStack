import { act, fireEvent, render, screen } from '@testing-library/react';
import { renderToStaticMarkup } from 'react-dom/server';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { generateMetadata as portalMetadata } from '@/app/(portal)/portal/page';
import { generateMetadata as studioMetadata } from '@/app/(studio)/studio/page';

import { AccountShell } from './shell';
import { extendBffSession } from '../../client/bff-session';
import en from '../../i18n/messages/en/auth.json';
import tr from '../../i18n/messages/tr/auth.json';

const { resource, session } = vi.hoisted(() => ({ resource: vi.fn(), session: vi.fn() }));
const state = vi.hoisted(() => ({ locale: 'en', active: vi.fn() }));
vi.mock('@/server/public-resource', () => ({ requirePublicResource: resource }));
vi.mock('@/server/public-request', () => ({ assertPublicRequestActive: state.active }));
vi.mock('@/server/auth/auth', () => ({ auth: session }));
vi.mock('@/client/bff-session', () => ({ extendBffSession: vi.fn() }));
vi.mock('next/navigation', () => ({ redirect: vi.fn() }));

beforeEach(() => {
  vi.clearAllMocks();
  resource.mockImplementation(async () => ({ request: { site: { defaultLocale: state.locale } } }));
  session.mockResolvedValue({
    state: 'authenticated',
    displayName: 'Admitted institution',
    expiresAt: Date.now() + 180_000,
    csrfToken: 'A'.repeat(43),
    limitedByAbsolute: false,
  });
  vi.mocked(extendBffSession).mockRejectedValue(new Error('private-provider-canary'));
});

describe('protected shell document metadata', () => {
  it.each([
    ['en', 'Studio', 'Your portal'],
    ['tr-TR', 'Stüdyo', 'Portalınız'],
  ])(
    'uses the admitted institution locale %s and keeps both shells out of search',
    async (locale, studio, portal) => {
      state.locale = locale;
      expect(await studioMetadata()).toEqual({
        title: studio,
        robots: { index: false, follow: false },
      });
      expect(await portalMetadata()).toEqual({
        title: portal,
        robots: { index: false, follow: false },
      });
      expect(state.active).toHaveBeenCalled();
    },
  );
});

it.each([
  ['studio', 'en'],
  ['portal', 'en'],
  ['studio', 'tr'],
  ['portal', 'tr'],
] as const)(
  'keeps %s recovery on its closed route and %s locale after failure',
  async (kind, locale) => {
    resource.mockResolvedValue({ request: { site: { defaultLocale: locale } } });
    const copy = locale === 'en' ? en : tr;
    const element = await AccountShell({ kind });
    const html = renderToStaticMarkup(element);
    expect(html).toContain(copy.session.javascript_required);
    expect(html).not.toContain(copy.session.extend);
    expect(html).toContain('action="/api/auth/signout"');
    render(element);
    expect(screen.getByText('Admitted institution', { exact: false })).toBeInTheDocument();
    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: copy.session.extend }));
    });
    expect(screen.queryByText('Admitted institution', { exact: false })).toBeNull();
    expect(screen.getByRole('alert')).toHaveTextContent(copy.session.failed);
    const signout = screen.getByRole('button', { name: copy.shell.signout }).closest('form')!;
    expect(signout).toHaveAttribute('action', '/api/auth/signout');
    expect(signout).toHaveAttribute('method', 'post');
    expect(signout.querySelector('input[name=csrfToken]')).toHaveValue('A'.repeat(43));
    expect(screen.getByRole('link', { name: copy.session.reauthenticate })).toHaveAttribute(
      'href',
      '/api/auth/signin?returnTo=' + encodeURIComponent('/' + kind),
    );
    expect(screen.getByRole('button', { name: copy.session.retry })).toBeEnabled();
    expect(document.body.textContent).not.toContain('private-provider-canary');
  },
);
