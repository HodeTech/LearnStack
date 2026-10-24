import { randomBytes } from 'node:crypto';

import {
  BFF_COOKIES,
  BffInputError,
  bffCookie,
  bffCookies,
  clearBffCookie,
  clearMalformedBffCookies,
} from './cookies.js';
import { bffActivity, bffForm, bffQuery, bffRoute, exactBffOrigin } from './http-input.js';
import type { BffHttpRequest } from './http-input.js';
import { BffSessionError } from './session-contract.js';
import type { BffSessionStore, SessionBinding, SessionProjection } from './session-contract.js';
import { bffSecretEquals, isBffOpaque } from './session-store.js';

const opaque = () => randomBytes(32).toString('base64url');
const cookieLife = 8 * 60 * 60;
export type BffPage = Readonly<
  { kind: 'signin'; csrfToken: string; returnPath: string } | { kind: 'error' }
>;
export type BffPageRenderer = (page: BffPage) => string;
type BffProjection = Readonly<{
  session: SessionProjection | null;
  cookies: readonly string[];
  unavailable: boolean;
}>;
function result(
  request: BffHttpRequest,
  value: unknown,
  status = 200,
  cookies: readonly string[] = [],
): Response {
  const headers = new Headers({
    'cache-control': 'no-store',
    'content-type': 'application/json; charset=utf-8',
    'referrer-policy': 'no-referrer',
    'x-content-type-options': 'nosniff',
    'x-frame-options': 'DENY',
  });
  for (const cookie of cookies) headers.append('set-cookie', cookie);
  if (status === 429 || status === 503) headers.set('retry-after', '30');
  return new Response(request.method === 'HEAD' ? null : JSON.stringify(value), {
    status,
    headers,
  });
}
function redirect(request: BffHttpRequest, location: string, cookies: readonly string[]): Response {
  const response = result(request, null, 303, cookies);
  response.headers.set('location', location);
  return response;
}
function binding(
  request: BffHttpRequest,
  cookies: ReadonlyMap<string, string>,
): SessionBinding | null {
  const handle = cookies.get(BFF_COOKIES.session);
  const browser = cookies.get(BFF_COOKIES.browser);
  return handle && browser
    ? {
        host: request.host,
        handle,
        browser,
        traceparent: request.headers.get('traceparent') ?? undefined,
      }
    : null;
}
const returnPath = (value: string | null): '/' | '/studio' | '/portal' => {
  if (value === null) return '/';
  if (value !== '/' && value !== '/studio' && value !== '/portal') throw new BffInputError();
  return value;
};
function input(request: BffHttpRequest) {
  if (request.headers.has('authorization')) throw new BffInputError();
  return bffCookies(request.headers.get('cookie'));
}

/** Called only after independent live-host bootstrap. No public SDK or browser bearer forwarding. */
export function createBffHttpProtocol(store: BffSessionStore) {
  return Object.freeze({
    async project(
      request: BffHttpRequest,
      activity: 'read' | 'passive' = bffActivity(request.method, request.headers),
    ): Promise<BffProjection> {
      const anonymous = (cookies: readonly string[] = [], unavailable = false): BffProjection =>
        Object.freeze({ session: null, cookies, unavailable });
      let cookies: ReadonlyMap<string, string>;
      try {
        cookies = bffCookies(request.headers.get('cookie'));
      } catch (error) {
        if (!(error instanceof BffInputError)) throw error;
        return anonymous(
          request.method === 'GET' && activity === 'read'
            ? clearMalformedBffCookies(request.headers.get('cookie'))
            : [],
        );
      }
      // Authorization is never a BFF credential and must not prevent public reading.
      if (request.headers.has('authorization')) return anonymous();
      const current = binding(request, cookies);
      if (!current) return anonymous();
      try {
        const session = await store.admit(current, request.source, activity, request.signal);
        return Object.freeze({ session, cookies: [], unavailable: false });
      } catch (error) {
        if (!(error instanceof BffSessionError)) throw error;
        // A failed plain read may leave a live owner. Protected reads return 503;
        // public rendering stays anonymous without deleting that recoverable session.
        return anonymous([], store.peek(current) !== null);
      }
    },
    async handle(request: BffHttpRequest, render?: BffPageRenderer): Promise<Response | null> {
      const path = request.target.split('?', 1)[0]!;
      // An inert public query is not a logout action. Only an owned proof-cookie
      // family selects the root return protocol; unrecognized states touch nothing.
      const rootFields =
        path === '/' ? new URL(request.target, 'https://inert.invalid').searchParams : null;
      const rootState = rootFields?.get('state');
      const rootReturn =
        path === '/' &&
        isBffOpaque(rootState) &&
        (request.headers.get('cookie') ?? '')
          .split(';')
          .some((pair) => pair.trim().startsWith(BFF_COOKIES.logout + rootState + '='));
      if (!bffRoute(request.method, path) && !rootReturn)
        return path.startsWith('/api/auth/') || path === '/api/auth'
          ? result(request, { code: 'not_found' }, 404)
          : null;
      try {
        if (request.signal.aborted) throw new BffInputError();
        const cookies = input(request);
        const current = binding(request, cookies);
        if (rootReturn) {
          if (request.method !== 'GET') return result(request, { code: 'not_found' }, 404);
          const fields = bffQuery(request.target, ['state']);
          const state = fields.get('state');
          const browser = cookies.get(BFF_COOKIES.browser);
          const proof = state && cookies.get(BFF_COOKIES.logout + state);
          if (
            !isBffOpaque(state) ||
            !browser ||
            !proof ||
            !store.consumeLogout({ host: request.host, browser, state, proof })
          )
            return result(request, { code: 'transaction' }, 400);
          return redirect(request, '/', [clearBffCookie(BFF_COOKIES.logout + state)]);
        }
        if (path === '/api/auth/signin' || path === '/api/auth/csrf') {
          const fields = bffQuery(request.target, path.endsWith('/signin') ? ['returnTo'] : []);
          const destination = returnPath(fields.get('returnTo'));
          // HEAD is passive, including an absent presession token.
          if (request.method === 'HEAD') return result(request, null);
          const csrfToken = cookies.get(BFF_COOKIES.csrf) ?? opaque();
          const response = result(
            request,
            { csrfToken },
            200,
            cookies.has(BFF_COOKIES.csrf) ? [] : [bffCookie(BFF_COOKIES.csrf, csrfToken, 300)],
          );
          if (render && path.endsWith('/signin')) {
            response.headers.set('content-type', 'text/html; charset=utf-8');
            // Fetch serializes navigation POST Origin as null under no-referrer.
            // This credential-free form needs its exact same-origin CSRF proof;
            // provider redirects, callbacks and logout keep no-referrer.
            response.headers.set('referrer-policy', 'same-origin');
            return new Response(render({ kind: 'signin', csrfToken, returnPath: destination }), {
              status: response.status,
              headers: response.headers,
            });
          }
          return response;
        }
        if (path === '/api/auth/error') {
          bffQuery(request.target, []);
          const response = result(request, { code: 'authentication_failed' }, 400);
          if (!render || request.method === 'HEAD') return response;
          response.headers.set('content-type', 'text/html; charset=utf-8');
          return new Response(render({ kind: 'error' }), {
            status: 400,
            headers: response.headers,
          });
        }
        if (path === '/api/auth/session') {
          bffQuery(request.target, []);
          return result(
            request,
            current ? (store.peek(current) ?? { state: 'anonymous' }) : { state: 'anonymous' },
          );
        }
        if (path === '/api/auth/callback/keycloak') {
          const fields = bffQuery(request.target, [
            'state',
            'code',
            'iss',
            'session_state',
            'error',
            'error_description',
            'error_uri',
          ]);
          const state = fields.get('state');
          const browser = cookies.get(BFF_COOKIES.browser);
          const proof = state && cookies.get(BFF_COOKIES.login + state);
          if (!isBffOpaque(state) || !browser || !proof) throw new BffInputError();
          const completed = await store.complete({
            host: request.host,
            source: request.source,
            traceparent: request.headers.get('traceparent') ?? undefined,
            state,
            browser,
            proof,
            responseUrl: 'https://' + request.host + request.target,
            signal: request.signal,
          });
          if (request.signal.aborted) throw new BffInputError();
          return redirect(request, completed.returnPath, [
            bffCookie(BFF_COOKIES.session, completed.handle, cookieLife),
            bffCookie(BFF_COOKIES.browser, completed.browser, cookieLife),
            clearBffCookie(BFF_COOKIES.login + state),
          ]);
        }
        bffQuery(request.target, []);
        exactBffOrigin(request);
        if (path === '/api/auth/signin/keycloak') {
          const fields = await bffForm(request, ['csrfToken', 'returnTo']);
          if (!bffSecretEquals(fields.get('csrfToken'), cookies.get(BFF_COOKIES.csrf)))
            return result(request, { code: 'csrf' }, 403);
          const started = await store.start({
            host: request.host,
            source: request.source,
            browser: cookies.get(BFF_COOKIES.browser),
            handle: cookies.get(BFF_COOKIES.session),
            returnPath: returnPath(fields.get('returnTo')),
            signal: request.signal,
          });
          if (request.signal.aborted) throw new BffInputError();
          return redirect(request, started.redirectUrl, [
            bffCookie(BFF_COOKIES.browser, started.browser, cookieLife),
            bffCookie(BFF_COOKIES.login + started.state, started.proof, 300),
          ]);
        }
        const fields = await bffForm(request, ['csrfToken']);
        const session = current && store.peek(current);
        if (!current || !session || !bffSecretEquals(fields.get('csrfToken'), session.csrfToken))
          return result(request, { code: 'csrf' }, 403);
        if (path === '/api/auth/extend')
          return result(
            request,
            await store.admit(current, request.source, 'extend', request.signal),
          );
        const logout = store.signout(current, fields.get('csrfToken')!);
        const edits = [
          clearBffCookie(BFF_COOKIES.session),
          ...[...cookies.keys()]
            .filter((name) => name.startsWith(BFF_COOKIES.login))
            .map(clearBffCookie),
          bffCookie(BFF_COOKIES.logout + logout.state, logout.proof, 300),
        ];
        return redirect(request, logout.redirectUrl ?? '/', edits);
      } catch (error) {
        if (error instanceof BffSessionError) {
          const status = ['source_capacity', 'subject_capacity'].includes(error.reason)
            ? 429
            : [
                  'pending_capacity',
                  'global_capacity',
                  'stopped',
                  'deadline',
                  'transport',
                  'provider_refused',
                  'configuration',
                  'response',
                ].includes(error.reason)
              ? 503
              : 400;
          // Only the owner can authorize clearing its verified failed transaction.
          const edits = error.clearTransaction
            ? [clearBffCookie(BFF_COOKIES.login + error.clearTransaction)]
            : [];
          return result(request, { code: error.reason }, status, edits);
        }
        return error instanceof BffInputError
          ? result(request, { code: 'invalid_request' }, 400)
          : result(request, { code: 'unavailable' }, 503);
      }
    },
  });
}
