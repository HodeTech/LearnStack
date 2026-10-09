import type { PublicTranslate } from '@/i18n/catalogues';
import { textDirection } from '@/i18n/locale';
import type { TextDirection } from '@/i18n/locale';
import { catalogPath, coursePath, paginationPath } from '@/server/public-paths';
import type { PublicResource } from '@/server/public-resource';

type CatalogResource = Extract<PublicResource, { kind: 'catalog' }>;
type PublicCatalogProps = {
  readonly resource: CatalogResource;
  readonly ui: {
    readonly locale: string;
    readonly direction: TextDirection;
    readonly t: PublicTranslate;
  };
};

export function PublicCatalog({ resource, ui }: PublicCatalogProps) {
  const { data, pagination } = resource;
  const routeLocale = resource.request.locale ?? '';
  const basePath = catalogPath(routeLocale);
  const nextPath =
    basePath && data.pageInfo.hasNext && data.pageInfo.nextCursor !== null
      ? paginationPath(basePath, pagination, data.pageInfo.nextCursor, false)
      : null;
  const restartPath =
    basePath && pagination.isPaginated ? paginationPath(basePath, pagination, null, false) : null;

  return (
    <section className="public-catalog" lang={data.locale} dir={textDirection(data.locale)}>
      <h1 lang={ui.locale} dir={ui.direction}>
        {ui.t('catalog.title')}
      </h1>
      {data.items.length === 0 ? (
        <p className="public-muted" lang={ui.locale} dir={ui.direction}>
          {ui.t('catalog.empty')}
        </p>
      ) : (
        <>
          <p className="public-muted" lang={ui.locale} dir={ui.direction}>
            {ui.t('catalog.course_count', { count: data.items.length })}
          </p>
          <ul className="public-course-list">
            {data.items.map((course) => {
              const path = coursePath(routeLocale, course.slug);
              return (
                <li key={course.slug} className="public-course-card">
                  <h2>{path ? <a href={path}>{course.title}</a> : course.title}</h2>
                  {course.summary?.trim() && <p className="public-summary">{course.summary}</p>}
                  {course.level &&
                    (course.level.state === 'ready' && course.level.label ? (
                      <p
                        className="public-muted"
                        lang={course.level.label.locale}
                        dir={textDirection(course.level.label.locale)}
                      >
                        {course.level.label.value}
                      </p>
                    ) : (
                      <p className="public-muted" lang={ui.locale} dir={ui.direction}>
                        {ui.t('catalog.level_unavailable')}
                      </p>
                    ))}
                </li>
              );
            })}
          </ul>
        </>
      )}
      {(restartPath || nextPath) && (
        <nav
          className="public-pagination"
          lang={ui.locale}
          dir={ui.direction}
          aria-label={ui.t('catalog.title')}
        >
          {restartPath && <a href={restartPath}>{ui.t('catalog.restart')}</a>}
          {nextPath && <a href={nextPath}>{ui.t('catalog.next')}</a>}
        </nav>
      )}
    </section>
  );
}
