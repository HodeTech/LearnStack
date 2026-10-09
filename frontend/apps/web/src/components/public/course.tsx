import type { PublicTranslate } from '@/i18n/catalogues';
import { textDirection } from '@/i18n/locale';
import type { TextDirection } from '@/i18n/locale';
import { coursePath, lessonPath, paginationPath } from '@/server/public-paths';
import type { PublicResource } from '@/server/public-resource';

type CourseResource = Extract<PublicResource, { kind: 'course' }>;
type PublicCourseProps = {
  readonly resource: CourseResource;
  readonly ui: {
    readonly locale: string;
    readonly direction: TextDirection;
    readonly t: PublicTranslate;
  };
};

export function PublicCourse({ resource, ui }: PublicCourseProps) {
  const { data, pagination } = resource;
  const { course } = data;
  const routeLocale = resource.request.locale ?? '';
  // Access policy wins even when an inconsistent response supplies an outline.
  const lessons = course.contentAccess === 'public' ? data.lessons : null;
  const basePath = coursePath(routeLocale, course.slug);
  const nextPath =
    basePath && lessons?.pageInfo.hasNext && lessons.pageInfo.nextCursor !== null
      ? paginationPath(basePath, pagination, lessons.pageInfo.nextCursor, true)
      : null;
  const restartPath =
    basePath && course.contentAccess === 'public' && pagination.isPaginated
      ? paginationPath(basePath, pagination, null, true)
      : null;

  return (
    <article className="public-course" lang={data.locale} dir={textDirection(data.locale)}>
      <h1>{course.title}</h1>
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
            {ui.t('course.level_unavailable')}
          </p>
        ))}
      {course.contentAccess !== 'public' ? (
        <p className="public-muted" lang={ui.locale} dir={ui.direction}>
          {ui.t('course.restricted_outline')}
        </p>
      ) : (
        <section className="public-outline">
          <h2 lang={ui.locale} dir={ui.direction}>
            {ui.t('course.outline')}
          </h2>
          {!lessons || lessons.items.length === 0 ? (
            <p className="public-muted" lang={ui.locale} dir={ui.direction}>
              {ui.t('course.empty_outline')}
            </p>
          ) : (
            <>
              <p className="public-muted" lang={ui.locale} dir={ui.direction}>
                {ui.t('course.lesson_count', { count: lessons.items.length })}
              </p>
              <ol className="public-lesson-list">
                {lessons.items.map((lesson) => {
                  const path = lessonPath(routeLocale, course.slug, lesson.slug);
                  return (
                    <li key={lesson.slug}>
                      {path ? <a href={path}>{lesson.title}</a> : lesson.title}
                    </li>
                  );
                })}
              </ol>
            </>
          )}
          {(restartPath || nextPath) && (
            <nav
              className="public-pagination"
              lang={ui.locale}
              dir={ui.direction}
              aria-label={ui.t('course.outline')}
            >
              {restartPath && <a href={restartPath}>{ui.t('course.restart')}</a>}
              {nextPath && <a href={nextPath}>{ui.t('course.next')}</a>}
            </nav>
          )}
        </section>
      )}
    </article>
  );
}
