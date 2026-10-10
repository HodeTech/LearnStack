// @vitest-environment node
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { getPublicUi } from './public-ui';

const dependencies = vi.hoisted(() => ({
  locale: vi.fn(),
  translations: vi.fn(),
  translate: vi.fn(),
  request: vi.fn(),
  active: true,
  cached: undefined as Promise<unknown> | undefined,
}));
vi.mock('react', () => ({
  cache: (load: () => Promise<unknown>) => () => (dependencies.cached ??= load()),
}));
vi.mock('./public-request', () => ({
  getPublicRequest: dependencies.request,
  assertPublicRequestActive: () => {
    if (!dependencies.active) throw new Error('Public admission request completed');
  },
}));
vi.mock('next/navigation', () => ({
  notFound: () => {
    throw new Error('NEXT_NOT_FOUND');
  },
}));
vi.mock('next-intl/server', () => ({
  getLocale: dependencies.locale,
  getTranslations: dependencies.translations,
}));

beforeEach(() => {
  vi.clearAllMocks();
  dependencies.active = true;
  dependencies.cached = undefined;
  dependencies.request.mockReset().mockResolvedValue({ locale: 'tr' });
  dependencies.translations.mockResolvedValue(dependencies.translate);
  dependencies.translate.mockImplementation((key: string) => `translated:${key}`);
});

describe('public server UI wrapper', () => {
  it.each([
    ['tr', 'ltr'],
    ['en', 'ltr'],
    ['ar', 'rtl'],
  ])('uses the configured %s language and %s direction', async (locale, direction) => {
    dependencies.locale.mockResolvedValue(locale);
    const ui = await getPublicUi();
    expect(ui).toMatchObject({ locale, direction });
    expect(dependencies.locale).toHaveBeenCalledOnce();
    expect(dependencies.locale).toHaveBeenCalledWith();
    expect(dependencies.translations).toHaveBeenCalledOnce();
    expect(dependencies.translations).toHaveBeenCalledWith('public');
    expect(ui.t('catalog.title')).toBe('translated:catalog.title');
    expect(dependencies.translate).toHaveBeenCalledOnce();
    expect(dependencies.translate).toHaveBeenCalledWith('catalog.title', undefined);
    expect(ui.t).not.toHaveProperty('rich');
    expect(ui.t).not.toHaveProperty('raw');
    expect(ui.t).not.toHaveProperty('has');
  });

  it.each(['catalog.course_count', 'course.lesson_count'] as const)(
    'forwards the exact numeric argument object for %s',
    async (key) => {
      dependencies.locale.mockResolvedValue('tr');
      const ui = await getPublicUi();
      const values = { count: 1000 };
      expect(ui.t(key, values)).toBe(`translated:${key}`);
      expect(dependencies.translate).toHaveBeenCalledOnce();
      expect(dependencies.translate).toHaveBeenCalledWith(key, values);
      expect(dependencies.translate.mock.calls[0]?.[1]).toBe(values);
    },
  );
  it('checks settled UI memo reuse and previously returned translators after completion', async () => {
    dependencies.locale.mockResolvedValue('tr');
    const first = await getPublicUi();
    expect(await getPublicUi()).toBe(first);
    expect(dependencies.locale).toHaveBeenCalledOnce();
    dependencies.active = false;
    await expect(getPublicUi()).rejects.toThrow('Public admission request completed');
    expect(() => first.t('catalog.title')).toThrow('Public admission request completed');
    expect(dependencies.translate).not.toHaveBeenCalled();
    expect(dependencies.locale).toHaveBeenCalledOnce();
  });

  it('checks lifetime after formatter work resolves', async () => {
    dependencies.locale.mockImplementationOnce(async () => {
      dependencies.active = false;
      return 'tr';
    });
    await expect(getPublicUi()).rejects.toThrow('Public admission request completed');
    expect(dependencies.translate).not.toHaveBeenCalled();
  });

  it('does not invoke formatters without admission', async () => {
    dependencies.request.mockResolvedValue(null);
    await expect(getPublicUi()).rejects.toThrow('NEXT_NOT_FOUND');
    expect(dependencies.locale).not.toHaveBeenCalled();
    expect(dependencies.translations).not.toHaveBeenCalled();
  });
});
