import 'server-only';

import type { ApiResult, ServerSdk } from '@learnstack/sdk/server';
import { notFound, redirect } from 'next/navigation';

import { getPublicRequest } from './public-request';
import type { PublicRequest } from './public-request';
import { publicPagination } from './public-route';
import type { PublicPagination } from './public-route';
import { requestMemo } from './request-memo';

type Success<T> = T extends { kind: 'success'; data: infer Data } ? Data : never;
type Catalog = Success<Awaited<ReturnType<ServerSdk['getCourses']>>>;
type Course = Success<Awaited<ReturnType<ServerSdk['getCourse']>>>;
type Lesson = Success<Awaited<ReturnType<ServerSdk['getLesson']>>>;
export type PublicFailureState = 'invalid_cursor' | 'rate_limited' | 'unavailable';
export type PublicResource =
  | {
      readonly kind: 'catalog';
      readonly request: PublicRequest;
      readonly data: Catalog;
      readonly pagination: PublicPagination;
    }
  | {
      readonly kind: 'course';
      readonly request: PublicRequest;
      readonly data: Course;
      readonly pagination: PublicPagination;
    }
  | { readonly kind: 'lesson'; readonly request: PublicRequest; readonly data: Lesson }
  | { readonly kind: 'status'; readonly request: PublicRequest }
  | { readonly kind: 'scaffold'; readonly request: PublicRequest }
  | { readonly kind: 'missing'; readonly request: PublicRequest }
  | {
      readonly kind: 'failure';
      readonly request: PublicRequest;
      readonly state: PublicFailureState;
    }
  | { readonly kind: 'refused' };

function failure<T>(
  result: Exclude<ApiResult<T>, { kind: 'success' }>,
  request: PublicRequest,
): PublicResource {
  if (result.kind === 'api-error') {
    if (result.status === 404) return { kind: 'missing', request };
    if (result.status === 429) return { kind: 'failure', request, state: 'rate_limited' };
    if (result.error.code === 'validation_failed')
      return { kind: 'failure', request, state: 'invalid_cursor' };
  }
  return { kind: 'failure', request, state: 'unavailable' };
}

/** Metadata, root and page share a single content read; the next document re-reads. */
export const getPublicResource = requestMemo(async (): Promise<PublicResource> => {
  const request = await getPublicRequest();
  if (!request) return { kind: 'refused' };
  const { route, client, context } = request;
  if (route.kind === 'status') return { kind: 'status', request };
  if (route.kind === 'scaffold') return { kind: 'scaffold', request };
  if (route.kind === 'lesson') {
    const result = await client.getLesson({ slug: route.slug, lessonSlug: route.lessonSlug });
    if (result.kind !== 'success') return failure(result, request);
    if (result.data.content.state === 'ready' && result.data.content.rendererKey !== 'default-card')
      // This memoized resource read is shared by metadata, layout and page.
      // The API diagnoses unavailable definitions; only an unsupported future
      // composite needs a renderer diagnostic, without its key or field values.
      console.warn('Public lesson presentation unavailable', {
        state: 'unsupported_renderer',
        count: 1,
      });
    return { kind: 'lesson', request, data: result.data };
  }
  const pagination = publicPagination(context.target, route.kind === 'course');
  if (!pagination) return { kind: 'failure', request, state: 'invalid_cursor' };
  if (route.kind === 'catalog') {
    const result = await client.getCourses({ cursor: pagination.cursor, limit: pagination.limit });
    return result.kind === 'success'
      ? { kind: 'catalog', request, data: result.data, pagination }
      : failure(result, request);
  }
  const result = await client.getCourse(
    { slug: route.slug },
    {
      lessonCursor: pagination.cursor,
      lessonLimit: pagination.limit,
    },
  );
  return result.kind === 'success'
    ? { kind: 'course', request, data: result.data, pagination }
    : failure(result, request);
});

/** Each consumer honors admission; layouts do not serialize child execution. */
export async function requirePublicResource(): Promise<
  Exclude<PublicResource, { kind: 'missing' | 'refused' }>
> {
  const resource = await getPublicResource();
  if (resource.kind === 'refused') notFound();
  if (resource.kind === 'missing') redirect(`/${resource.request.locale}/status/not-found`);
  return resource;
}
