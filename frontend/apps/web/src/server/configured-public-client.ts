import { randomBytes } from 'node:crypto';

import 'server-only';

import { createServerSdk } from '@learnstack/sdk/server';
import type { ServerSdk } from '@learnstack/sdk/server';

import { publicServerConfiguration, verifyProvenance } from './ingress';
import { MAX_PUBLIC_RESPONSE_BYTES, PUBLIC_API_DEADLINE_MS } from './public-api-limits';
import { canonicalRouteLocale } from './public-entry';

type ClientOptions = {
  readonly traceparent?: string | null;
  readonly signal?: AbortSignal;
};
type CourseQuery = Omit<Parameters<ServerSdk['getCourses']>[0], 'locale'>;
type CourseDetailQuery = Omit<Parameters<ServerSdk['getCourse']>[1], 'locale'>;

export type ConfiguredPublicClient = {
  getSite(): ReturnType<ServerSdk['getSite']>;
  getCourses(query?: CourseQuery): ReturnType<ServerSdk['getCourses']>;
  getCourse(
    path: Parameters<ServerSdk['getCourse']>[0],
    query?: CourseDetailQuery,
  ): ReturnType<ServerSdk['getCourse']>;
  getLesson(path: Parameters<ServerSdk['getLesson']>[0]): ReturnType<ServerSdk['getLesson']>;
};

export function validatedTraceparent(value: string | null | undefined): string | null {
  const match = /^00-([0-9a-f]{32})-([0-9a-f]{16})-[0-9a-f]{2}$/.exec(value ?? '');
  return match && match[0] === value && match[1] !== '0'.repeat(32) && match[2] !== '0'.repeat(16)
    ? (value ?? null)
    : null;
}

export function publicTraceparent(value: string | null | undefined): string {
  const incoming = validatedTraceparent(value);
  if (incoming) return incoming;
  // Bootstrap and rendering share this request-local context; no browser carrier is emitted.
  let trace: string;
  let span: string;
  do trace = randomBytes(16).toString('hex');
  while (trace === '0'.repeat(32));
  do span = randomBytes(8).toString('hex');
  while (span === '0'.repeat(16));
  return `00-${trace}-${span}-00`;
}

/** Consume decoded bytes before handing the response to the SDK's existing wire parser. */
async function boundedResponse(
  url: URL,
  headers: Readonly<Record<string, string>>,
  callerSignal: AbortSignal | undefined,
): Promise<Response> {
  const controller = new AbortController();
  const abort = () => controller.abort();
  let rejectInterrupted: () => void = () => {};
  const interrupted = new Promise<never>((_resolve, reject) => {
    rejectInterrupted = () => reject(new Error('Public API transport interrupted'));
  });
  controller.signal.addEventListener('abort', rejectInterrupted, { once: true });
  callerSignal?.addEventListener('abort', abort, { once: true });
  const deadline = setTimeout(abort, PUBLIC_API_DEADLINE_MS);
  let reader: ReadableStreamDefaultReader<Uint8Array> | undefined;
  try {
    if (callerSignal?.aborted) abort();
    const pending = (async () => {
      const response = await fetch(url, {
        method: 'GET',
        headers,
        signal: controller.signal,
        redirect: 'error',
        cache: 'no-store',
        credentials: 'omit',
      });
      if (controller.signal.aborted) {
        // Also close a late response from a transport that did not honor abort promptly.
        void response.body?.cancel().catch(() => {});
        throw new Error('Public API transport interrupted');
      }
      return response;
    })();
    const response = await Promise.race([pending, interrupted]);
    reader = response.body?.getReader();
    const mediaType = response.headers.get('content-type')?.split(';', 1)[0]?.trim().toLowerCase();
    const expectedMediaType = response.ok ? 'application/json' : 'application/problem+json';
    if (mediaType !== expectedMediaType) {
      controller.abort();
      throw new Error('Invalid public API response media type');
    }
    const chunks: Uint8Array[] = [];
    let size = 0;
    if (reader) {
      while (true) {
        const chunk = await Promise.race([reader.read(), interrupted]);
        if (chunk.done) break;
        size += chunk.value.byteLength;
        if (size > MAX_PUBLIC_RESPONSE_BYTES) {
          controller.abort();
          throw new Error('Public API response exceeds the consumer limit');
        }
        chunks.push(chunk.value);
      }
    }
    const body = new Uint8Array(size);
    let offset = 0;
    for (const chunk of chunks) {
      body.set(chunk, offset);
      offset += chunk.byteLength;
    }
    const responseHeaders = new Headers();
    for (const name of ['content-type', 'retry-after']) {
      const value = response.headers.get(name);
      if (value !== null) responseHeaders.set(name, value);
    }
    return new Response([204, 205, 304].includes(response.status) ? null : body, {
      status: response.status,
      headers: responseHeaders,
    });
  } finally {
    clearTimeout(deadline);
    callerSignal?.removeEventListener('abort', abort);
    controller.signal.removeEventListener('abort', rejectInterrupted);
    if (reader) {
      // An errored/aborted stream may reject cancellation. Cleanup must not extend the deadline.
      void reader.cancel().catch(() => {});
      reader.releaseLock();
    }
  }
}

/**
 * The sole configured public hop. Authority comes only from private configuration
 * and the authenticated native envelope; Education locale comes only from its route.
 */
export function createConfiguredPublicClient(
  envelope: string | null,
  options: ClientOptions = {},
): ConfiguredPublicClient | null {
  const configuration = publicServerConfiguration(process.env, process.cwd());
  const ingress = verifyProvenance(envelope, configuration.secret);
  if (!ingress) return null;
  const { signal } = options;
  const locale = canonicalRouteLocale(ingress.target);
  const headers = Object.freeze({
    Accept: 'application/json',
    'X-LearnStack-Host': ingress.host,
    'X-LearnStack-Hop-Secret': configuration.secret,
    'X-LearnStack-Visitor-Address': ingress.peer,
    traceparent: publicTraceparent(options.traceparent),
  });
  const sdk = createServerSdk((path) => {
    // Only the SDK's four fixed operations can call this closure. Recheck origin
    // after URL normalization before attaching credentials to the request.
    const url = new URL(path, configuration.apiOrigin);
    if (url.origin !== configuration.apiOrigin || !path.startsWith('/api/v1/public/'))
      throw new Error('Invalid public API operation');
    return boundedResponse(url, headers, signal);
  });
  const requestOptions = { signal };
  const invalidRoute = () =>
    Promise.resolve({ kind: signal?.aborted ? 'cancelled' : 'invalid-request' } as const);
  return {
    getSite: () => sdk.getSite(requestOptions),
    getCourses: (query = {}) =>
      locale === null
        ? invalidRoute()
        : sdk.getCourses({ locale, cursor: query.cursor, limit: query.limit }, requestOptions),
    getCourse: (path, query = {}) =>
      locale === null
        ? invalidRoute()
        : sdk.getCourse(
            path,
            { locale, lessonCursor: query.lessonCursor, lessonLimit: query.lessonLimit },
            requestOptions,
          ),
    getLesson: (path) =>
      locale === null ? invalidRoute() : sdk.getLesson(path, { locale }, requestOptions),
  };
}
