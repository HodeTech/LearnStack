import type { components, operations } from './generated/schema';

export type PublicGetOperation =
  | 'GetPublicSite'
  | 'GetPublicCourses'
  | 'GetPublicCourse'
  | 'GetPublicLesson';
export type PublicSuccess<T extends PublicGetOperation> =
  operations[T]['responses'][200]['content']['application/json'];
export type ApiProblem = components['schemas']['ProblemDetails'];

/** Closed UI error vocabulary from Standards 09. Unrecognized API codes remain unknown. */
export type AppError =
  | { code: 'validation_failed'; fieldErrors: Record<string, string[]> }
  | { code: 'not_found'; resource?: string }
  | { code: 'concurrency_conflict'; latestVersion?: number }
  | { code: 'request_in_progress' }
  | { code: 'idempotency_key_reuse' }
  | { code: 'idempotency_outcome_unavailable' }
  | { code: 'method_not_allowed' }
  | { code: 'payload_too_large' }
  | { code: 'unsupported_media_type' }
  | { code: 'request_rejected' }
  | { code: 'dependency_unavailable'; provider?: string; retryAfter?: number }
  | { code: 'audit_unavailable'; retryAfter?: number }
  | { code: 'audit_unclassified_operation'; correlationId?: string }
  | { code: 'rate_limited'; retryAfter?: number }
  | { code: 'forbidden' }
  | { code: 'unauthorized' }
  | { code: 'recording_consent_required' }
  | { code: 'unknown'; correlationId?: string };

function record(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}
function arrayOf(value: unknown, element: (value: unknown) => boolean): boolean {
  return Array.isArray(value) && value.every(element);
}
function label(value: unknown): boolean {
  return record(value) && typeof value.value === 'string' && typeof value.locale === 'string';
}
function named(value: unknown): boolean {
  return record(value) && typeof value.slug === 'string' && typeof value.title === 'string';
}
function page(value: unknown): boolean {
  return (
    record(value) &&
    (value.nextCursor === null || typeof value.nextCursor === 'string') &&
    value.previousCursor === null &&
    typeof value.hasNext === 'boolean' &&
    value.hasPrevious === false
  );
}
function summary(value: unknown): boolean {
  return (
    named(value) &&
    record(value) &&
    (value.summary === null || typeof value.summary === 'string') &&
    (value.contentAccess === 'public' || value.contentAccess === 'enrollment_required') &&
    (value.level === null ||
      (record(value.level) &&
        ((value.level.state === 'ready' && label(value.level.label)) ||
          (value.level.state === 'unavailable' && value.level.label === null))))
  );
}
function outline(value: unknown): boolean {
  return (
    record(value) &&
    page(value.pageInfo) &&
    arrayOf(
      value.items,
      (item) =>
        named(item) &&
        record(item) &&
        typeof item.sort === 'number' &&
        Number.isInteger(item.sort) &&
        item.sort >= 0,
    )
  );
}
function theme(value: unknown): boolean {
  return (
    value === null ||
    (record(value) &&
      ['primary', 'background', 'foreground', 'muted'].every(
        (key) => typeof value[key] === 'string',
      ))
  );
}
function content(value: unknown): boolean {
  return (
    record(value) &&
    (value.state === 'unavailable' ||
      (value.state === 'ready' &&
        typeof value.rendererKey === 'string' &&
        label(value.label) &&
        arrayOf(
          value.fields,
          (field) =>
            record(field) &&
            typeof field.name === 'string' &&
            label(field.label) &&
            typeof field.value === 'string',
        )))
  );
}

/** Validate bounded success shapes while accepting additive response members. */
export function isSuccess<T extends PublicGetOperation>(
  operation: T,
  value: unknown,
): value is PublicSuccess<T> {
  if (!record(value)) return false;
  switch (operation) {
    case 'GetPublicSite':
      return (
        typeof value.displayName === 'string' &&
        arrayOf(value.enabledLocales, (locale) => typeof locale === 'string') &&
        typeof value.defaultLocale === 'string' &&
        typeof value.showPlatformAttribution === 'boolean' &&
        theme(value.theme)
      );
    case 'GetPublicCourses':
      return (
        typeof value.locale === 'string' && arrayOf(value.items, summary) && page(value.pageInfo)
      );
    case 'GetPublicCourse':
      return (
        typeof value.locale === 'string' &&
        summary(value.course) &&
        arrayOf(
          value.alternates,
          (alternate) =>
            record(alternate) &&
            typeof alternate.locale === 'string' &&
            typeof alternate.slug === 'string',
        ) &&
        (value.lessons === null || outline(value.lessons))
      );
    case 'GetPublicLesson':
      return (
        typeof value.locale === 'string' &&
        named(value.course) &&
        named(value.lesson) &&
        content(value.content) &&
        arrayOf(
          value.alternates,
          (alternate) =>
            record(alternate) &&
            typeof alternate.locale === 'string' &&
            typeof alternate.courseSlug === 'string' &&
            typeof alternate.lessonSlug === 'string',
        )
      );
  }
  return false;
}

/** Construct the known wire fields after validation; unrecognized extensions are ignored. */
export function parseProblem(value: unknown, status: number): ApiProblem | null {
  if (
    !record(value) ||
    typeof value.type !== 'string' ||
    typeof value.title !== 'string' ||
    value.status !== status ||
    !Number.isInteger(status) ||
    status < 400 ||
    status > 599 ||
    typeof value.instance !== 'string' ||
    typeof value.code !== 'string' ||
    typeof value.messageKey !== 'string' ||
    !value.messageKey.startsWith('lockey_') ||
    typeof value.correlationId !== 'string'
  )
    return null;
  let errors: ApiProblem['errors'];
  if (value.errors !== undefined) {
    if (!record(value.errors)) return null;
    const entries: [string, NonNullable<ApiProblem['errors']>[string]][] = [];
    for (const [field, messages] of Object.entries(value.errors)) {
      if (!Array.isArray(messages)) return null;
      const parsed: NonNullable<ApiProblem['errors']>[string] = [];
      for (const message of messages) {
        if (
          !record(message) ||
          typeof message.key !== 'string' ||
          !message.key.startsWith('lockey_')
        )
          return null;
        if (message.params === undefined) parsed.push({ key: message.key });
        else {
          if (!record(message.params)) return null;
          const parameters: [string, string][] = [];
          for (const [name, value] of Object.entries(message.params)) {
            if (typeof value !== 'string') return null;
            parameters.push([name, value]);
          }
          parsed.push({
            key: message.key,
            params: Object.fromEntries(parameters),
          });
        }
      }
      entries.push([field, parsed]);
    }
    errors = Object.fromEntries(entries);
  }
  return {
    type: value.type,
    title: value.title,
    status,
    instance: value.instance,
    code: value.code,
    messageKey: value.messageKey,
    correlationId: value.correlationId,
    ...(errors === undefined ? {} : { errors }),
  };
}

export function appError(problem: ApiProblem, retryAfter: string | null): AppError {
  const seconds =
    retryAfter !== null && /^[0-9]+$/.test(retryAfter) ? Number(retryAfter) : undefined;
  const retry =
    seconds !== undefined && Number.isSafeInteger(seconds) ? { retryAfter: seconds } : {};
  switch (problem.code) {
    case 'validation_failed':
      return {
        code: problem.code,
        fieldErrors: Object.fromEntries(
          Object.entries(problem.errors ?? {}).map(([field, messages]) => [
            field,
            messages.map((message) => message.key),
          ]),
        ),
      };
    case 'dependency_unavailable':
    case 'audit_unavailable':
    case 'rate_limited':
      return { code: problem.code, ...retry };
    case 'audit_unclassified_operation':
      return { code: problem.code, correlationId: problem.correlationId };
    case 'not_found':
    case 'concurrency_conflict':
    case 'request_in_progress':
    case 'idempotency_key_reuse':
    case 'idempotency_outcome_unavailable':
    case 'method_not_allowed':
    case 'payload_too_large':
    case 'unsupported_media_type':
    case 'request_rejected':
    case 'forbidden':
    case 'unauthorized':
    case 'recording_consent_required':
      return { code: problem.code };
    default:
      return { code: 'unknown', correlationId: problem.correlationId };
  }
}
