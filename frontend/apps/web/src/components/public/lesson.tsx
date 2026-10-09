import type { PublicTranslate } from '@/i18n/catalogues';
import { textDirection } from '@/i18n/locale';
import type { TextDirection } from '@/i18n/locale';
import { coursePath } from '@/server/public-paths';
import type { PublicResource } from '@/server/public-resource';

type LessonResource = Extract<PublicResource, { kind: 'lesson' }>;
type PublicLessonProps = {
  readonly resource: LessonResource;
  readonly ui: {
    readonly locale: string;
    readonly direction: TextDirection;
    readonly t: PublicTranslate;
  };
};

/** Renders the API's ordered text fields without introducing active content sinks. */
export function PublicLesson({ resource, ui }: PublicLessonProps) {
  const { data } = resource;
  const { content } = data;
  const backPath = coursePath(resource.request.locale ?? '', data.course.slug);

  return (
    <article className="public-lesson" lang={data.locale} dir={textDirection(data.locale)}>
      {backPath && <a href={backPath}>{data.course.title}</a>}
      <h1>{data.lesson.title}</h1>
      {content.state !== 'ready' || content.rendererKey !== 'default-card' ? (
        <p className="public-muted" lang={ui.locale} dir={ui.direction}>
          {ui.t('lesson.unavailable')}
        </p>
      ) : (
        <section className="public-lesson-content">
          <h2 lang={content.label.locale} dir={textDirection(content.label.locale)}>
            {content.label.value}
          </h2>
          {content.fields.length === 0 ? (
            <p className="public-muted" lang={ui.locale} dir={ui.direction}>
              {ui.t('lesson.empty')}
            </p>
          ) : (
            <dl className="public-lesson-fields">
              {content.fields.map((field) => (
                <div className="public-lesson-field" key={field.name}>
                  <dt lang={field.label.locale} dir={textDirection(field.label.locale)}>
                    {field.label.value}
                  </dt>
                  <dd>{field.value}</dd>
                </div>
              ))}
            </dl>
          )}
        </section>
      )}
    </article>
  );
}
