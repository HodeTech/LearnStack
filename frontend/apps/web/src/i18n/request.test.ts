// @vitest-environment node
import { createTranslator } from 'next-intl';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import * as catalogues from './catalogues';
import en from './messages/en/public.json';
import tr from './messages/tr/public.json';
import requestConfig from './request';

const dependencies = vi.hoisted(() => ({
  request: vi.fn(),
  notFound: vi.fn(() => {
    throw new Error('Admission refused');
  }),
}));
vi.mock('@/server/public-request', () => ({ getPublicRequest: dependencies.request }));
vi.mock('next/navigation', () => ({ notFound: dependencies.notFound }));
// Execute the production callback directly; next-intl's request cache is outside
// this unit boundary. The real formatter consumes the returned config below.
vi.mock('next-intl/server', () => ({ getRequestConfig: (callback: unknown) => callback }));

const configure = () => requestConfig({ requestLocale: Promise.resolve('forged-locale') });

beforeEach(() => {
  vi.clearAllMocks();
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
      expect(config.messages).toEqual({ public: messages });
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
