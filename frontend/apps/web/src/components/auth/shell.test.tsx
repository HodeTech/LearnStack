import { act, fireEvent, render, screen } from '@testing-library/react';
import { renderToStaticMarkup } from 'react-dom/server';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { generateMetadata as portalMetadata } from '@/app/(portal)/portal/page';
import { generateMetadata as studioMetadata } from '@/app/(studio)/studio/page';

import { AccountPage, AccountShell, accountPageMetadata } from './shell';
import { extendBffSession } from '../../client/bff-session';
import en from '../../i18n/messages/en/auth.json';
import tr from '../../i18n/messages/tr/auth.json';
import { administrationSnapshot } from '../../test/administration-ui-fixture';

const { prepared } = vi.hoisted(() => ({ prepared: vi.fn() }));
const state = vi.hoisted(() => ({
  locale: 'en',
  kind: 'studio' as 'studio' | 'portal',
  active: vi.fn(),
}));
vi.mock('@/server/auth/page', () => ({ optionalPreparedPage: prepared }));
vi.mock('@/client/bff-session', () => ({ extendBffSession: vi.fn() }));
vi.mock('next/navigation', () => ({ redirect: vi.fn() }));

beforeEach(() => {
  vi.clearAllMocks();
  prepared.mockImplementation(async () => ({
    snapshot: administrationSnapshot(
      { kind: state.kind },
      {
        site: {
          displayName: 'Institution',
          defaultLocale: state.locale,
          enabledLocales: [state.locale],
          theme: null,
          showPlatformAttribution: true,
        },
        ownProfile: null,
        profileState: 'missing',
      },
    ),
    assertActive: state.active,
  }));
  vi.mocked(extendBffSession).mockRejectedValue(new Error('private-provider-canary'));
});

describe('protected shell document metadata', () => {
  it('renders no private content when speculative admission has no prepared snapshot', async () => {
    prepared.mockResolvedValue(null);
    for (const kind of [
      'studio',
      'portal',
      'members',
      'member',
      'roles',
      'role',
      'profile',
    ] as const) {
      expect(await AccountPage({ kind })).toBeNull();
    }
    expect(prepared).toHaveBeenCalledTimes(7);
    expect(state.active).not.toHaveBeenCalled();
    expect(extendBffSession).not.toHaveBeenCalled();
  });
  it('returns only noindex metadata without a private title when no snapshot was prepared', async () => {
    prepared.mockResolvedValue(null);
    for (const kind of [
      'studio',
      'portal',
      'members',
      'member',
      'roles',
      'role',
      'profile',
    ] as const) {
      expect(await accountPageMetadata(kind)).toEqual({ robots: { index: false, follow: false } });
    }
    expect(prepared).toHaveBeenCalledTimes(7);
    expect(state.active).not.toHaveBeenCalled();
    expect(extendBffSession).not.toHaveBeenCalled();
  });
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
    state.locale = locale;
    state.kind = kind;
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
