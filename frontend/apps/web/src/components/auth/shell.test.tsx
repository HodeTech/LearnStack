import { describe, expect, it, vi } from 'vitest';

import { generateMetadata as portalMetadata } from '@/app/(portal)/portal/page';
import { generateMetadata as studioMetadata } from '@/app/(studio)/studio/page';

const state = vi.hoisted(() => ({ locale: 'en', active: vi.fn() }));
vi.mock('@/server/public-resource', () => ({
  requirePublicResource: async () => ({ request: { site: { defaultLocale: state.locale } } }),
}));
vi.mock('@/server/public-request', () => ({ assertPublicRequestActive: state.active }));
vi.mock('@/server/auth/auth', () => ({ auth: vi.fn() }));

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
