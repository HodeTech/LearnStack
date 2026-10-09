import type { Metadata } from 'next';

import type { PublicTranslate } from '@/i18n/catalogues';
import type { TextDirection } from '@/i18n/locale';

import { publicRedirect } from './public-entry';
import { catalogPath, coursePath } from './public-paths';
import type { requirePublicResource } from './public-resource';

type Resource = Awaited<ReturnType<typeof requirePublicResource>>;
type PublicUi = {
  readonly locale: string;
  readonly direction: TextDirection;
  readonly t: PublicTranslate;
};

function unavailable(t: PublicTranslate): Metadata {
  return {
    title: t('page.unavailable.title'),
    description: t('page.unavailable.description'),
    robots: { index: false, follow: false },
  };
}

function publicUrl(host: string, path: string | null): string | null {
  if (path === null) return null;
  try {
    return publicRedirect(host, path);
  } catch {
    return null;
  }
}

/** Pure projection of the shared resource read; UI fallback never supplies URL locale. */
export function publicMetadata(resource: Resource, { t }: PublicUi): Metadata {
  if (resource.kind === 'status') {
    return {
      title: t('page.missing.title'),
      description: t('page.missing.description'),
      robots: { index: false, follow: false },
    };
  }
  if (resource.kind === 'failure') {
    if (resource.state === 'invalid_cursor') {
      return {
        title: t('page.invalid_cursor.title'),
        description: t('page.invalid_cursor.description'),
        robots: { index: false, follow: false },
      };
    }
    if (resource.state === 'rate_limited') {
      return {
        title: t('page.rate_limited.title'),
        description: t('page.rate_limited.description'),
        robots: { index: false, follow: false },
      };
    }
    return unavailable(t);
  }
  // Lesson metadata is extended with the actual lesson page in P02d-6 Step 3.
  if (resource.kind === 'lesson' || resource.kind === 'scaffold') {
    return { title: resource.request.site.displayName, robots: { index: false, follow: false } };
  }
  const { request, pagination } = resource;
  const { locale, site, context } = request;
  if (locale === null || !site.enabledLocales.includes(locale)) return unavailable(t);
  const path =
    resource.kind === 'catalog'
      ? catalogPath(locale)
      : coursePath(locale, resource.data.course.slug);
  const canonical = publicUrl(context.host, path);
  if (canonical === null) return unavailable(t);

  const title = resource.kind === 'catalog' ? t('catalog.title') : resource.data.course.title;
  const summary = resource.kind === 'course' ? resource.data.course.summary : null;
  const description = summary?.trim() ? summary : undefined;
  const candidates =
    resource.kind === 'catalog'
      ? site.enabledLocales.map((language) => [language, catalogPath(language)] as const)
      : resource.data.alternates.map(
          ({ locale: language, slug }) => [language, coursePath(language, slug)] as const,
        );
  // Resource alternates intentionally exclude the current API locale.
  const languages: Record<string, string> = { [locale]: canonical };
  for (const [language, alternatePath] of candidates) {
    if (language === locale || !site.enabledLocales.includes(language)) continue;
    const alternate = publicUrl(context.host, alternatePath);
    if (alternate !== null) languages[language] = alternate;
  }
  return {
    title,
    ...(description === undefined ? {} : { description }),
    robots: { index: !pagination.isPaginated, follow: true },
    alternates: { canonical, ...(Object.keys(languages).length ? { languages } : {}) },
    openGraph: {
      type: 'website',
      title,
      ...(description === undefined ? {} : { description }),
      url: canonical,
      siteName: site.displayName,
      locale,
      alternateLocale: Object.keys(languages).filter((language) => language !== locale),
    },
  };
}
