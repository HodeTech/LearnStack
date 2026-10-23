import type { ApiResult } from './api-result';
import type { AUTHENTICATED_GET_CONTRACTS, GetContractOwnership } from './authenticated-contract';
import type { operations, paths } from './generated/schema';
import { appError, isSuccess, parseProblem } from './response-policy';
import type { PublicGetOperation, PublicSuccess } from './response-policy';

export type { ApiProblem, AppError } from './response-policy';

/** The supplied Fetch-compatible transport resolves relative URLs. P02d-5 owns the trusted server hop. */
export type PublicFetch = (url: string, init: RequestInit) => Promise<Response>;
export type RequestOptions = { readonly signal?: AbortSignal };

/** An injected transport refused local operation construction before network I/O. */
export class PublicRequestRefusalError extends Error {
  constructor() {
    super('Public request refused');
    this.name = 'PublicRequestRefusalError';
  }
}

export type { ApiResult } from './api-result';

type Query<T extends PublicGetOperation> = operations[T]['parameters']['query'];
type Path<T extends PublicGetOperation> = operations[T]['parameters']['path'];
export type ServerSdk = {
  getSite(options?: RequestOptions): Promise<ApiResult<PublicSuccess<'GetPublicSite'>>>;
  getCourses(
    query: Query<'GetPublicCourses'>,
    options?: RequestOptions,
  ): Promise<ApiResult<PublicSuccess<'GetPublicCourses'>>>;
  getCourse(
    path: Path<'GetPublicCourse'>,
    query: Query<'GetPublicCourse'>,
    options?: RequestOptions,
  ): Promise<ApiResult<PublicSuccess<'GetPublicCourse'>>>;
  getLesson(
    path: Path<'GetPublicLesson'>,
    query: Query<'GetPublicLesson'>,
    options?: RequestOptions,
  ): Promise<ApiResult<PublicSuccess<'GetPublicLesson'>>>;
};

/** The exact public subset; generated GETs never acquire public ownership implicitly. */
export const PUBLIC_GET_CONTRACTS = Object.freeze({
  '/api/v1/public/site': 'GetPublicSite',
  '/api/v1/public/courses': 'GetPublicCourses',
  '/api/v1/public/courses/{slug}': 'GetPublicCourse',
  '/api/v1/public/courses/{slug}/lessons/{lessonSlug}': 'GetPublicLesson',
} as const satisfies Record<string, PublicGetOperation>);

export type PublicGetPath = keyof typeof PUBLIC_GET_CONTRACTS;

/** Both path and operation ownership must remain disjoint, correctly paired and exhaustive. */
export const GET_CONTRACT_OWNERSHIP = true satisfies GetContractOwnership<
  paths,
  operations,
  typeof PUBLIC_GET_CONTRACTS,
  typeof AUTHENTICATED_GET_CONTRACTS
>;

/** Every declared public GET has a wrapper; authenticated GETs have their own contract. */
type PublicWrappers = {
  [P in PublicGetPath]: (typeof PUBLIC_GET_CONTRACTS)[P] extends `GetPublic${infer Resource}`
    ? Uncapitalize<`Get${Resource}`> & keyof ServerSdk
    : never;
};

export const PUBLIC_GET_WRAPPERS = Object.freeze({
  '/api/v1/public/site': 'getSite',
  '/api/v1/public/courses': 'getCourses',
  '/api/v1/public/courses/{slug}': 'getCourse',
  '/api/v1/public/courses/{slug}/lessons/{lessonSlug}': 'getLesson',
} as const satisfies PublicWrappers);

function queryString(query: Record<string, string | undefined>): string {
  const parameters = new URLSearchParams();
  for (const [name, value] of Object.entries(query))
    if (value !== undefined) parameters.set(name, value);
  return '?' + parameters.toString();
}

// URL-resolving transports normalize these instead of preserving a resource segment.
function isDotSegment(value: string): boolean {
  return value === '.' || value === '..';
}

/** Four public GET contracts, with no default fetch, tenant selector, header options or implicit host lookup. */
export function createServerSdk(transport: PublicFetch): ServerSdk {
  async function request<T extends PublicGetOperation>(
    operation: T,
    buildUrl: () => string | undefined,
    options?: RequestOptions,
  ): Promise<ApiResult<PublicSuccess<T>>> {
    if (options?.signal?.aborted) return { kind: 'cancelled' };
    let url: string | undefined;
    try {
      url = buildUrl();
    } catch {
      return { kind: 'invalid-request' };
    }
    if (url === undefined) return { kind: 'invalid-request' };
    let response: Response;
    try {
      response = await transport(url, {
        method: 'GET',
        cache: 'no-store',
        headers: { Accept: 'application/json' },
        signal: options?.signal,
      });
    } catch (error) {
      if (options?.signal?.aborted) return { kind: 'cancelled' };
      return error instanceof PublicRequestRefusalError
        ? { kind: 'invalid-request' }
        : { kind: 'transport-error' };
    }
    if (options?.signal?.aborted) return { kind: 'cancelled' };
    if (response.status < 400 && response.status !== 200)
      return { kind: 'invalid-response', reason: 'unexpected-status' };
    let body: unknown;
    try {
      body = await response.json();
    } catch (error) {
      if (options?.signal?.aborted) return { kind: 'cancelled' };
      return error instanceof SyntaxError
        ? { kind: 'invalid-response', reason: 'malformed-json' }
        : { kind: 'transport-error' };
    }
    if (options?.signal?.aborted) return { kind: 'cancelled' };
    if (response.status === 200)
      return isSuccess(operation, body)
        ? { kind: 'success', status: 200, data: body }
        : { kind: 'invalid-response', reason: 'invalid-success' };
    const problem = parseProblem(body, response.status);
    return problem === null
      ? { kind: 'invalid-response', reason: 'invalid-problem' }
      : {
          kind: 'api-error',
          status: response.status,
          problem,
          error: appError(problem, response.headers.get('Retry-After')),
        };
  }
  return {
    getSite: (options) => request('GetPublicSite', () => '/api/v1/public/site', options),
    getCourses: (query, options) =>
      request(
        'GetPublicCourses',
        () =>
          '/api/v1/public/courses' +
          queryString({ locale: query.locale, cursor: query.cursor, limit: query.limit }),
        options,
      ),
    getCourse: (path, query, options) =>
      request(
        'GetPublicCourse',
        () =>
          isDotSegment(path.slug)
            ? undefined
            : '/api/v1/public/courses/' +
              encodeURIComponent(path.slug) +
              queryString({
                locale: query.locale,
                lessonCursor: query.lessonCursor,
                lessonLimit: query.lessonLimit,
              }),
        options,
      ),
    getLesson: (path, query, options) =>
      request(
        'GetPublicLesson',
        () =>
          isDotSegment(path.slug) || isDotSegment(path.lessonSlug)
            ? undefined
            : '/api/v1/public/courses/' +
              encodeURIComponent(path.slug) +
              '/lessons/' +
              encodeURIComponent(path.lessonSlug) +
              queryString({ locale: query.locale }),
        options,
      ),
  };
}
