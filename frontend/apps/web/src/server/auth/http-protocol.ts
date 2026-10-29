import { randomBytes } from 'node:crypto';

import {
  BFF_COOKIES,
  BffInputError,
  bffCookie,
  bffCookies,
  clearBffCookie,
  clearMalformedBffCookies,
} from './cookies.js';
import {
  BFF_ADMIN_FORMS,
  bffAdministrationForm,
  bffContextHint,
  bffCursor,
  bffScope,
} from './http-forms.js';
import { bffActivity, bffForm, bffQuery, bffRoute, exactBffOrigin } from './http-input.js';
import type { BffHttpRequest } from './http-input.js';
import { NativeApiError } from './native-contract.js';
import type { NativeOperationResult } from './native-operation-contract.js';
import { BffProviderError } from './provider-error.js';
import { BffSessionError } from './session-contract.js';
import type {
  BffSessionStore,
  EntryProjection,
  NativeReturnPath,
  SelectionCompletion,
  SessionBinding,
  SessionProjection,
} from './session-contract.js';
import { isNativeReturnPath } from './session-path.js';
import { bffSecretEquals, isBffOpaque } from './session-store.js';

const opaque = () => randomBytes(32).toString('base64url');
const cookieLife = 8 * 60 * 60;
export type BffPage = Readonly<
  | { kind: 'signin'; csrfToken: string; returnPath: string; contextHint?: string }
  | { kind: 'entry'; entry: EntryProjection | null; cursor?: string }
  | {
      kind: 'entry_error';
      reason: 'reauthenticate' | 'creation_disabled' | 'denied' | 'busy' | 'unavailable';
      retryAfterSeconds?: number;
    }
  | {
      kind: 'mutation_error';
      action: string;
      destination: string;
      fields: readonly (readonly [string, string])[];
      reason: 'denied' | 'stale' | 'in_progress' | 'key_reuse' | 'unavailable';
      retryable: boolean;
    }
  | { kind: 'account_change'; csrfToken: string }
  | { kind: 'error' }
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
  family: string = BFF_COOKIES.session,
): SessionBinding | null {
  const handle = cookies.get(family);
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
const returnPath = (value: string | null): NativeReturnPath => {
  if (value === null) return '/';
  if (!isNativeReturnPath(value)) throw new BffInputError();
  return value;
};
function selected(
  request: BffHttpRequest,
  completion: SelectionCompletion,
  switching = false,
): Response {
  request.signal.throwIfAborted();
  if (completion.kind === 'destination') {
    const origin = new URL(completion.origin);
    if (
      origin.protocol !== 'https:' ||
      origin.origin !== completion.origin ||
      origin.username ||
      origin.password ||
      !isNativeReturnPath(completion.returnPath)
    )
      throw new BffInputError();
    const hint = bffContextHint(completion.contextHint);
    const query = new URLSearchParams({ returnTo: completion.returnPath, contextHint: hint! });
    return redirect(request, completion.origin + '/api/auth/signin?' + query.toString(), []);
  }
  const destination = switching
    ? completion.returnPath.startsWith('/portal')
      ? '/portal'
      : '/studio'
    : completion.returnPath;
  return redirect(request, destination, [
    bffCookie(BFF_COOKIES.session, completion.handle, cookieLife),
    bffCookie(BFF_COOKIES.browser, completion.browser, cookieLife),
    ...(switching ? [] : [clearBffCookie(BFF_COOKIES.entry)]),
  ]);
}
function document(
  request: BffHttpRequest,
  render: BffPageRenderer | undefined,
  page: BffPage,
  value: unknown,
  status = 200,
): Response {
  const response = result(request, value, status);
  if (
    !render ||
    request.method === 'HEAD' ||
    request.headers
      .get('accept')
      ?.split(',')
      .some((value) => value.trim().split(';')[0] === 'application/json')
  )
    return response;
  response.headers.set('content-type', 'text/html; charset=utf-8');
  response.headers.set('referrer-policy', 'same-origin');
  return new Response(render(page), { status, headers: response.headers });
}
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
        return Object.freeze({
          session,
          cookies: [],
          unavailable: false,
        });
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
        return path.startsWith('/api/auth/') ||
          path === '/api/auth' ||
          Object.hasOwn(BFF_ADMIN_FORMS, path)
          ? result(request, { code: 'not_found' }, 404)
          : null;
      try {
        if (request.method === 'POST')
          request = {
            ...request,
            signal: AbortSignal.any([request.signal, AbortSignal.timeout(10_000)]),
          };
        if (
          request.method !== 'POST' &&
          (request.body !== null ||
            Number(request.headers.get('content-length') ?? '0') !== 0 ||
            request.headers.has('transfer-encoding'))
        )
          throw new BffInputError();
        if (request.signal.aborted) throw new BffInputError();
        const cookies = input(request);
        const current = binding(request, cookies);
        const entryBinding = binding(request, cookies, BFF_COOKIES.entry);
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
          const fields = bffQuery(
            request.target,
            path.endsWith('/signin') ? ['returnTo', 'contextHint'] : [],
          );
          const destination = returnPath(fields.get('returnTo'));
          const contextHint = bffContextHint(fields.get('contextHint'));
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
            return new Response(
              render({ kind: 'signin', csrfToken, returnPath: destination, contextHint }),
              {
                status: response.status,
                headers: response.headers,
              },
            );
          }
          return response;
        }
        if (path === '/api/auth/entry') {
          const cursor = bffCursor(bffQuery(request.target, ['cursor']).get('cursor'));
          const entry = entryBinding
            ? await store.entry(entryBinding, request.source, cursor, request.signal)
            : null;
          request.signal.throwIfAborted();
          return document(
            request,
            render,
            { kind: 'entry', entry, cursor },
            entry ?? { state: 'anonymous' },
          );
        }
        if (path === '/api/auth/error') {
          bffQuery(request.target, []);
          const response = result(request, { code: 'authentication_failed' }, 400);
          if (
            !render ||
            request.method === 'HEAD' ||
            request.headers
              .get('accept')
              ?.split(',')
              .some((value) => value.trim().split(';')[0] === 'application/json')
          )
            return response;
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
          return redirect(request, '/api/auth/entry', [
            bffCookie(BFF_COOKIES.entry, completed.handle, 300),
            bffCookie(BFF_COOKIES.browser, completed.browser, cookieLife),
            clearBffCookie(BFF_COOKIES.login + state),
          ]);
        }
        bffQuery(request.target, []);
        exactBffOrigin(request);
        if (path === '/api/auth/signin/keycloak') {
          const fields = await bffForm(request, ['csrfToken', 'returnTo', 'contextHint']);
          if (!bffSecretEquals(fields.get('csrfToken'), cookies.get(BFF_COOKIES.csrf)))
            return result(request, { code: 'csrf' }, 403);
          const started = await store.start({
            host: request.host,
            source: request.source,
            browser: cookies.get(BFF_COOKIES.browser),
            handle: cookies.get(BFF_COOKIES.session),
            returnPath: returnPath(fields.get('returnTo')),
            contextHint: bffContextHint(fields.get('contextHint')),
            signal: request.signal,
          });
          if (request.signal.aborted) throw new BffInputError();
          return redirect(request, started.redirectUrl, [
            bffCookie(BFF_COOKIES.browser, started.browser, cookieLife),
            bffCookie(BFF_COOKIES.login + started.state, started.proof, 300),
          ]);
        }
        if (
          path === '/api/auth/entry/create' ||
          path === '/api/auth/entry/select' ||
          path === '/api/auth/switch'
        ) {
          const creating = path.endsWith('/create');
          const switching = path.endsWith('/switch');
          const fields = await bffForm(
            request,
            creating ? ['csrfToken'] : ['csrfToken', 'tenantId', 'organizationId', 'cursor'],
          );
          const csrf = fields.get('csrfToken');
          const selectedBinding = switching ? current : entryBinding;
          if (!selectedBinding || !isBffOpaque(csrf)) return result(request, { code: 'csrf' }, 403);
          if (switching && !bffSecretEquals(csrf, store.peek(selectedBinding)?.csrfToken))
            return result(request, { code: 'csrf' }, 403);
          if (creating) {
            await store.create(selectedBinding, request.source, csrf, request.signal);
            request.signal.throwIfAborted();
            return redirect(request, '/api/auth/entry', []);
          }
          const scope = bffScope(fields);
          const cursor = bffCursor(fields.get('cursor'));
          try {
            return selected(
              request,
              await (switching
                ? store.switchScope(
                    selectedBinding,
                    request.source,
                    csrf,
                    scope,
                    cursor,
                    request.signal,
                  )
                : store.select(
                    selectedBinding,
                    request.source,
                    csrf,
                    scope,
                    cursor,
                    request.signal,
                  )),
              switching,
            );
          } catch (error) {
            const live = current && store.peek(current);
            if (error instanceof BffSessionError && error.reason === 'account_change' && live)
              return document(
                request,
                render,
                { kind: 'account_change', csrfToken: live.csrfToken },
                { code: 'account_change' },
                409,
              );
            throw error;
          }
        }
        const adminFields = Object.hasOwn(BFF_ADMIN_FORMS, path)
          ? BFF_ADMIN_FORMS[path]
          : undefined;
        if (adminFields) {
          const fields = await bffForm(request, adminFields);
          const session = current && store.peek(current);
          const csrf = fields.get('csrfToken');
          if (!current || !session || !bffSecretEquals(csrf, session.csrfToken))
            return result(request, { code: 'csrf' }, 403);
          const form = bffAdministrationForm(path, fields);
          let outcome: NativeOperationResult;
          try {
            outcome = await store.operate(
              current,
              request.source,
              form.operation,
              csrf!,
              request.signal,
            );
          } catch (error) {
            if (
              request.signal.aborted ||
              !store.peek(current) ||
              (error instanceof BffSessionError &&
                [
                  'revoked',
                  'expired',
                  'cancelled',
                  'transaction',
                  'csrf',
                  'identity',
                  'stopped',
                ].includes(error.reason))
            )
              throw error;
            return document(
              request,
              render,
              {
                kind: 'mutation_error',
                action: path,
                destination: form.destination,
                fields: [...fields.entries()],
                reason: 'unavailable',
                retryable: true,
              },
              { code: 'unavailable' },
              503,
            );
          }
          request.signal.throwIfAborted();
          if (outcome.kind === 'success') return redirect(request, form.destination, []);
          const status =
            outcome.kind === 'api-error'
              ? outcome.status
              : outcome.kind === 'invalid-request'
                ? 400
                : 503;
          const code = outcome.kind === 'api-error' ? outcome.error.code : 'unavailable';
          const reason =
            code === 'concurrency_conflict'
              ? 'stale'
              : code === 'request_in_progress'
                ? 'in_progress'
                : code === 'idempotency_key_reuse'
                  ? 'key_reuse'
                  : [401, 403, 404].includes(status)
                    ? 'denied'
                    : 'unavailable';
          return document(
            request,
            render,
            {
              kind: 'mutation_error',
              action: path,
              destination: form.destination,
              fields: [...fields.entries()],
              reason,
              retryable: reason === 'unavailable' || reason === 'in_progress',
            },
            { code },
            status,
          );
        }
        const fields = await bffForm(request, ['csrfToken']);
        const session = current && store.peek(current);
        if (
          path === '/api/auth/signout' &&
          entryBinding &&
          !bffSecretEquals(fields.get('csrfToken'), session?.csrfToken)
        ) {
          const csrf = fields.get('csrfToken');
          if (!isBffOpaque(csrf)) return result(request, { code: 'csrf' }, 403);
          const logout = store.discardEntry(entryBinding, csrf);
          return redirect(request, logout.redirectUrl ?? '/api/auth/signin', [
            clearBffCookie(BFF_COOKIES.entry),
            bffCookie(BFF_COOKIES.logout + logout.state, logout.proof, 300),
          ]);
        }
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
          ...(cookies.has(BFF_COOKIES.entry) ? [clearBffCookie(BFF_COOKIES.entry)] : []),
          ...[...cookies.keys()]
            .filter((name) => name.startsWith(BFF_COOKIES.login))
            .map(clearBffCookie),
          bffCookie(BFF_COOKIES.logout + logout.state, logout.proof, 300),
        ];
        return redirect(request, logout.redirectUrl ?? '/', edits);
      } catch (error) {
        const entryRequest = [
          '/api/auth/entry',
          '/api/auth/entry/create',
          '/api/auth/entry/select',
        ].includes(path);
        if (error instanceof BffSessionError || error instanceof BffProviderError) {
          const status =
            error instanceof NativeApiError && error.status !== undefined
              ? error.status
              : error.reason === 'csrf'
                ? 403
                : ['source_capacity', 'subject_capacity'].includes(error.reason)
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
          const retryAfterSeconds =
            error instanceof NativeApiError ? error.retryAfterSeconds : undefined;
          const reason =
            status === 401 ||
            ['identity', 'invalid_grant', 'reauthenticate', 'expired', 'revoked'].includes(
              error.reason,
            )
              ? 'reauthenticate'
              : status === 403 &&
                  path === '/api/auth/entry/create' &&
                  error instanceof NativeApiError
                ? 'creation_disabled'
                : status === 403 || status === 404
                  ? 'denied'
                  : status === 429
                    ? 'busy'
                    : 'unavailable';
          const response = entryRequest
            ? document(
                request,
                render,
                { kind: 'entry_error', reason, retryAfterSeconds },
                { code: error.reason },
                status,
              )
            : result(request, { code: error.reason }, status);
          // A native API hint is explicit, validated and bounded. Never substitute a guessed wait.
          if (error instanceof NativeApiError && error.status !== undefined) {
            response.headers.delete('retry-after');
            if (retryAfterSeconds !== undefined)
              response.headers.set('retry-after', String(retryAfterSeconds));
          }
          // Only the owner can authorize clearing its verified failed transaction.
          if (error instanceof BffSessionError && error.clearTransaction)
            response.headers.append(
              'set-cookie',
              clearBffCookie(BFF_COOKIES.login + error.clearTransaction),
            );
          return response;
        }
        if (entryRequest && !(error instanceof BffInputError))
          return document(
            request,
            render,
            { kind: 'entry_error', reason: 'unavailable' },
            { code: 'unavailable' },
            503,
          );
        return error instanceof BffInputError
          ? result(request, { code: 'invalid_request' }, 400)
          : result(request, { code: 'unavailable' }, 503);
      }
    },
  });
}
