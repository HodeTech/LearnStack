import { BFF_POST_PATHS } from './http-paths.js';
import { validTarget } from '../ingress.js';
import { AUTH_INPUT_LIMIT, BffInputError } from './cookies.js';

export type BffHttpRequest = Readonly<{
  host: string;
  source: string;
  method: string;
  target: string;
  headers: Headers;
  body: ReadableStream<Uint8Array> | null;
  signal: AbortSignal;
}>;
export { BFF_POST_PATHS } from './http-paths.js';
const readable = new Set([
  '/api/auth/signin',
  '/api/auth/csrf',
  '/api/auth/session',
  '/api/auth/error',
]);
export function bffRoute(method: string, path: string): boolean {
  return method === 'POST'
    ? BFF_POST_PATHS.includes(path)
    : (method === 'GET' && path === '/api/auth/callback/keycloak') ||
        ((method === 'GET' || method === 'HEAD') && readable.has(path));
}
export function bffActivity(method: string, headers: Headers): 'read' | 'passive' {
  if (
    method !== 'GET' ||
    headers.has('next-router-prefetch') ||
    headers.get('purpose') === 'prefetch' ||
    headers
      .get('sec-purpose')
      ?.toLowerCase()
      .split(/[;\s]+/)
      .includes('prefetch')
  )
    return 'passive';
  return headers.get('rsc') === '1' || headers.get('sec-fetch-dest') === 'document'
    ? 'read'
    : 'passive';
}
export function exactBffOrigin(request: BffHttpRequest) {
  if (request.headers.get('origin') !== 'https://' + request.host) throw new BffInputError();
}
function fields(text: string, allowed: readonly string[]) {
  const query = new URLSearchParams();
  try {
    for (const pair of text ? text.split('&') : []) {
      const equal = pair.indexOf('=');
      if (!pair || equal < 0) throw new BffInputError();
      const decode = (value: string) => decodeURIComponent(value.replace(/\+/g, ' '));
      const key = decode(pair.slice(0, equal));
      if (!allowed.includes(key) || query.has(key)) throw new BffInputError();
      query.set(key, decode(pair.slice(equal + 1)));
    }
  } catch {
    throw new BffInputError();
  }
  return query;
}
export function bffQuery(target: string, allowed: readonly string[]): URLSearchParams {
  if (!validTarget(target)) throw new BffInputError();
  const question = target.indexOf('?');
  return fields(question < 0 ? '' : target.slice(question + 1), allowed);
}

/** Native/Next stream, one finite input budget. Cancellation is not fabricated completion. */
export async function bffForm(
  request: BffHttpRequest,
  allowed: readonly string[],
): Promise<URLSearchParams> {
  const content = request.headers
    .get('content-type')
    ?.toLowerCase()
    .split(';')
    .map((value) => value.trim());
  const length = request.headers.get('content-length');
  if (
    content?.[0] !== 'application/x-www-form-urlencoded' ||
    content.slice(1).some((value) => value !== 'charset=utf-8') ||
    (length !== null && (!/^[0-9]+$/.test(length) || Number(length) > AUTH_INPUT_LIMIT))
  )
    throw new BffInputError();
  const deadline = AbortSignal.timeout(10_000);
  const signal = AbortSignal.any([request.signal, deadline]);
  const reader = request.body?.getReader();
  const cancel = () => {
    void reader?.cancel().catch(() => {});
  };
  signal.addEventListener('abort', cancel, { once: true });
  try {
    if (signal.aborted) throw new BffInputError();
    const chunks: Uint8Array[] = [];
    let bytes = 0;
    while (reader) {
      const chunk = await reader.read();
      if (signal.aborted) throw new BffInputError();
      if (chunk.done) break;
      bytes += chunk.value.byteLength;
      if (bytes > AUTH_INPUT_LIMIT) throw new BffInputError();
      chunks.push(chunk.value);
    }
    const body = new Uint8Array(bytes);
    let offset = 0;
    for (const chunk of chunks) {
      body.set(chunk, offset);
      offset += chunk.byteLength;
    }
    let text: string;
    try {
      text = new TextDecoder('utf-8', { fatal: true }).decode(body);
    } catch {
      throw new BffInputError();
    }
    return fields(text, allowed);
  } finally {
    signal.removeEventListener('abort', cancel);
    await reader?.cancel().catch(() => {});
    reader?.releaseLock();
  }
}
