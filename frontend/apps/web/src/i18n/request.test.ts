// @vitest-environment node
import { createTranslator } from 'next-intl';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { getAdministrationCatalogue } from './administration-catalogues';
import { getAuthCatalogue } from './auth-catalogues';
import * as catalogues from './catalogues';
import type { NativePageSnapshot } from '../server/auth/page-contract';
import en from './messages/en/public.json';
import tr from './messages/tr/public.json';
import requestConfig from './request';

const dependencies = vi.hoisted(() => ({
  request: vi.fn(),
  active: vi.fn(),
  page: vi.fn(),
  notFound: vi.fn(() => {
    throw new Error('Admission refused');
  }),
}));
vi.mock('@/server/public-request', () => ({
  getPublicRequest: dependencies.request,
  assertPublicRequestActive: dependencies.active,
}));
vi.mock('next/navigation', () => ({ notFound: dependencies.notFound }));
vi.mock('@/server/auth/page', () => ({ optionalPreparedPage: dependencies.page }));
// Execute the production callback directly; next-intl's request cache is outside
// this unit boundary. The real formatter consumes the returned config below.
vi.mock('next-intl/server', () => ({ getRequestConfig: (callback: unknown) => callback }));

const configure = () => requestConfig({ requestLocale: Promise.resolve('forged-locale') });

beforeEach(() => {
  vi.clearAllMocks();
  dependencies.active.mockReset();
  dependencies.page.mockReset().mockResolvedValue(null);
});

describe('verified public next-intl request configuration', () => {
  it.each([
    ['tr-Latn-TR', 'tr', tr],
    ['en-GB', 'en', en],
    ['ar', 'en', en],
    [null, 'en', en],
  ])(
    'selects the public namespace for admitted content locale %s',
    async (locale, uiLocale, messages) => {
      dependencies.request.mockResolvedValue({ locale });
      const config = await configure();
      expect(dependencies.request).toHaveBeenCalledOnce();
      expect(dependencies.request).toHaveBeenCalledWith();
      expect(dependencies.notFound).not.toHaveBeenCalled();
      expect(config.locale).toBe(uiLocale);
      expect(config.messages).toEqual({
        public: messages,
        auth: getAuthCatalogue(String(locale ?? 'en')).messages,
        ...getAdministrationCatalogue(String(locale ?? 'en')).messages,
      });
      const translate = createTranslator({
        ...config,
        locale: config.locale!,
        messages: config.messages!,
        namespace: 'public',
      });
      expect(translate('catalog.title')).toBe(messages.catalog.title);
    },
  );

  it('refuses before selecting any catalogue or English scaffold', async () => {
    dependencies.request.mockResolvedValue(null);
    const select = vi.spyOn(catalogues, 'getPublicCatalogue');
    await expect(configure()).rejects.toThrow('Admission refused');
    expect(dependencies.notFound).toHaveBeenCalledOnce();
    expect(dependencies.notFound).toHaveBeenCalledWith();
    expect(select).not.toHaveBeenCalled();
    expect(dependencies.page).not.toHaveBeenCalled();
  });

  it.each([
    ['available', 'tr', ['en', 'tr'], 'tr'],
    ['available', 'tr', ['en'], 'en'],
    ['missing', null, ['en', 'tr'], 'en'],
    ['unavailable', 'tr', ['en', 'tr'], 'en'],
  ] as const)(
    'selects all protected catalogues from exact own profile %s/%s and enabled site locales',
    async (profileState, preferredLocale, enabledLocales, expected) => {
      dependencies.request.mockResolvedValue({ locale: 'en' });
      const snapshot: NativePageSnapshot = {
        target: '/portal/profile',
        site: {
          displayName: 'School',
          defaultLocale: 'en',
          enabledLocales,
          theme: null,
          showPlatformAttribution: true,
        },
        session: {
          state: 'authenticated',
          displayName: 'School',
          expiresAt: 1800000300000,
          limitedByAbsolute: false,
          csrfToken: 'A'.repeat(43),
        },
        scopeKind: 'organization',
        capabilities: null,
        profileState,
        ownProfile:
          preferredLocale === null ? null : { displayName: 'Actor', preferredLocale, version: '0' },
        formKeys: [],
        outcome: { kind: 'ready', view: { kind: 'profile', canWrite: false } },
      };
      const assertActive = vi.fn();
      dependencies.page.mockResolvedValue({ snapshot, assertActive });
      const configured = await configure();
      expect(assertActive).toHaveBeenCalledOnce();
      expect(configured.locale).toBe(expected);
      expect(configured.messages).toEqual({
        public: expected === 'tr' ? tr : en,
        auth: getAuthCatalogue(expected).messages,
        ...getAdministrationCatalogue(expected).messages,
      });
    },
  );

  it('withholds catalogue selection if the prepared snapshot is invalidated after its await', async () => {
    dependencies.request.mockResolvedValue({ locale: 'en' });
    dependencies.page.mockResolvedValue({
      assertActive: () => {
        throw new Error('Prepared page closed');
      },
    });
    const select = vi.spyOn(catalogues, 'getPublicCatalogue');
    await expect(configure()).rejects.toThrow('Prepared page closed');
    expect(select).not.toHaveBeenCalled();
  });

  it('refuses a completed request after the admission await before catalogue selection', async () => {
    dependencies.request.mockResolvedValue({ locale: 'tr' });
    dependencies.active.mockImplementationOnce(() => {
      throw new Error('Public admission request completed');
    });
    const select = vi.spyOn(catalogues, 'getPublicCatalogue');
    await expect(configure()).rejects.toThrow('Public admission request completed');
    expect(select).not.toHaveBeenCalled();
    expect(dependencies.notFound).not.toHaveBeenCalled();
  });

  it.each(['tr-TR', 'ar'])('bounds formatter failures in the actual %s config', async (locale) => {
    dependencies.request.mockResolvedValue({ locale });
    const error = vi.spyOn(console, 'error').mockImplementation(() => {});
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => {});
    const log = vi.spyOn(console, 'log').mockImplementation(() => {});
    const config = await configure();
    const translate = createTranslator({
      ...config,
      locale: config.locale!,
      messages: config.messages!,
      namespace: 'public',
    });
    const fallback =
      locale === 'tr-TR' ? tr.page.unavailable.description : en.page.unavailable.description;
    expect(translate('catalog.course_count')).toBe(fallback);
    expect(translate('lockey_private_wire_key', { secret: 'private-parameter' })).toBe(fallback);
    expect(error).not.toHaveBeenCalled();
    expect(warn).not.toHaveBeenCalled();
    expect(log).not.toHaveBeenCalled();
  });
});
