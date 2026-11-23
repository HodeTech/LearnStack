import { randomBytes } from 'node:crypto';

import type { MembershipCustomFields } from '@learnstack/sdk/administration-server';

import {
  BFF_COOKIES,
  BffInputError,
  bffCookie,
  bffCookies,
  clearBffCookie,
  clearMalformedBffCookies,
} from './cookies.js';
import {
  CUSTOM_FIELD_FORMS,
  customControlName,
  customAuthorName,
  customFieldControlDescriptions,
} from './custom-field-forms.js';
import {
  BffFederationInputError,
  FEDERATION_FORMS,
  FEDERATION_SECRET_FIELDS,
} from './federation-forms.js';
import {
  BFF_ADMIN_FORMS,
  BffDataRequestConfirmationError,
  BffProfileInputError,
  BffInvitationInputError,
  DATA_REQUEST_ADMIN_FORMS,
  bffAdministrationForm,
  bffContextHint,
  bffCursor,
  bffScope,
  dataRequestAdministrationDestination,
  dataRequestAdministrationRefusal,
} from './http-forms.js';
import { bffActivity, bffForm, bffQuery, bffRoute, exactBffOrigin } from './http-input.js';
import type { BffHttpRequest } from './http-input.js';
import { DATA_REQUEST_DOCUMENT_PATHS } from './http-paths.js';
import { NativeApiError } from './native-contract.js';
import type { NativeOperationResult } from './native-operation-contract.js';
import type { NativeActionPage, NativeEntrySnapshot, ScopeChoices } from './page-contract.js';
import type { NativePagePreparation } from './page-snapshot-contract.js';
import { SnapshotAdmissionError } from './page-snapshot-pool.js';
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
/** ADR-0099 § Lifecycle and test: the one browser action that begins the administrator's test. */
const FEDERATION_TEST_PATH = '/api/studio/federation-test';
const FEDERATION_TEST_DESTINATION: NativeReturnPath = '/studio/federation';
const cookieLife = 8 * 60 * 60;
export type BffPage = Readonly<
  (
    | {
        kind: 'signin';
        csrfToken: string;
        returnPath: string;
        contextHint?: string;
        /** ADR-0099 § Sign-in exposure: the Active broker's display name, read server-side; absent hides it. */
        federation?: string;
      }
    | { kind: 'entry'; entry: EntryProjection | null; cursor?: string }
    | { kind: 'scope_choices'; selection: ScopeChoices; cursor?: string }
    | {
        kind: 'entry_error';
        reason: 'reauthenticate' | 'creation_disabled' | 'denied' | 'busy' | 'unavailable';
        retryAfterSeconds?: number;
      }
    | NativeActionPage
    | { kind: 'error' }
  ) & { locale?: string }
>;
export type BffPageRenderer = (page: BffPage) => string;
type BffProjection = Readonly<{
  session: SessionProjection | null;
  cookies: readonly string[];
  unavailable: boolean;
  refusalStatus?: 400 | 429 | 503;
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
/**
 * ADR-0099 Amendment 7: the federation detail load is prepared only for `Sec-Fetch-Site` `same-origin` or `none` on a
 * top-level document navigation, or `same-origin` on an RSC navigation. A request without `Sec-Fetch-Site` (a browser
 * that sends no fetch metadata) is not decided here; that is the recorded residual (D4-5).
 */
function federationNavigation(headers: Headers): boolean {
  const site = headers.get('sec-fetch-site');
  if (site === null) return true;
  const mode = headers.get('sec-fetch-mode');
  const dest = headers.get('sec-fetch-dest');
  if ((site === 'same-origin' || site === 'none') && mode === 'navigate' && dest === 'document')
    return true;
  return (
    site === 'same-origin' &&
    headers.get('rsc') === '1' &&
    (mode === 'cors' || mode === 'same-origin') &&
    dest === 'empty'
  );
}
function input(request: BffHttpRequest) {
  if (request.headers.has('authorization')) throw new BffInputError();
  return bffCookies(request.headers.get('cookie'));
}

/** Called only after independent live-host bootstrap. No public SDK or browser bearer forwarding. */
export function createBffHttpProtocol(store: BffSessionStore, preparation?: NativePagePreparation) {
  const ticket = (request: BffHttpRequest) => {
    if (!preparation) throw new SnapshotAdmissionError('unavailable');
    return preparation.current({
      host: request.host,
      peer: request.source,
      method: request.method,
      target: request.target,
    });
  };
  const entryDocument = (
    request: BffHttpRequest,
    render: BffPageRenderer | undefined,
    snapshot: NativeEntrySnapshot,
  ): Response => {
    const view = snapshot.view;
    if (view.kind === 'entry')
      return document(
        request,
        render,
        { kind: 'entry', entry: view.entry, cursor: view.cursor ?? undefined },
        view.entry ?? { state: 'anonymous' },
      );
    if (view.kind === 'scopes')
      return document(
        request,
        render,
        { kind: 'scope_choices', selection: view.selection, cursor: view.cursor ?? undefined },
        view.selection,
      );
    if (view.kind === 'mutation_error' || view.kind === 'account_change')
      throw new SnapshotAdmissionError('unavailable');
    const reason =
      view.reason === 'not_found'
        ? 'denied'
        : view.reason === 'invalid_cursor'
          ? 'unavailable'
          : view.reason;
    const response = document(
      request,
      render,
      { kind: 'entry_error', reason, retryAfterSeconds: view.retryAfterSeconds ?? undefined },
      { code: view.code ?? view.reason },
      view.status ??
        (reason === 'busy'
          ? 429
          : reason === 'denied' || reason === 'creation_disabled'
            ? 403
            : reason === 'reauthenticate'
              ? 401
              : 503),
    );
    response.headers.delete('retry-after');
    if (view.retryAfterSeconds !== null)
      response.headers.set('retry-after', String(view.retryAfterSeconds));
    return response;
  };
  const actionDocument = async (
    request: BffHttpRequest,
    render: BffPageRenderer | undefined,
    current: SessionBinding,
    page: NativeActionPage,
    value: unknown,
    status: number,
  ): Promise<Response> => {
    // A secret never enters a recovery document, whatever the action (ADR-0099).
    if (page.kind === 'mutation_error')
      page = {
        ...page,
        fields: page.fields.filter(([name]) => !FEDERATION_SECRET_FIELDS.includes(name)),
      };
    const prepared = await store.preparePage(current, request.source, {
      kind: 'response',
      ticket: ticket(request),
      page:
        page.kind === 'mutation_error' && page.reason === 'validation'
          ? {
              ...page,
              fields: page.fields.map(
                ([name, value]) => [name, name === 'idempotencyKey' ? opaque() : value] as const,
              ),
            }
          : page,
    });
    if (
      'session' in prepared ||
      (prepared.view.kind !== 'mutation_error' && prepared.view.kind !== 'account_change')
    )
      throw new SnapshotAdmissionError('unavailable');
    return document(request, render, { ...prepared.view, locale: prepared.locale }, value, status);
  };
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
        const path = request.target.split('?', 1)[0]!;
        const protectedPage = path !== '/' && isNativeReturnPath(path);
        let session: SessionProjection | null;
        if (protectedPage && request.method === 'GET') {
          const native = ticket(request);
          // ADR-0099 (Step 2 review S-E, Step 4 review S4-1): the federation detail load reads the provider back and
          // may begin a corrective write, so only a same-origin or user-initiated navigation prepares it. A
          // cross-site or same-site request (which SameSite=Lax still sends cookies on) prepares nothing.
          if (
            native.prefetch ||
            (path === '/studio/federation' && !federationNavigation(request.headers))
          )
            session = await store.admit(current, request.source, 'passive', request.signal);
          else {
            const page = await store.preparePage(current, request.source, {
              kind: 'document',
              ticket: native,
            });
            if (!('session' in page)) return anonymous([], store.peek(current) !== null);
            session = page.session;
          }
        } else
          session = await store.admit(
            current,
            request.source,
            request.method === 'HEAD' ? 'passive' : activity,
            request.signal,
          );
        return Object.freeze({
          session,
          cookies: [],
          unavailable: false,
        });
      } catch (error) {
        if (error instanceof BffInputError)
          return Object.freeze({ ...anonymous([], false), refusalStatus: 400 });
        if (error instanceof SnapshotAdmissionError)
          return Object.freeze({ ...anonymous([], true), refusalStatus: error.status });
        if (!(error instanceof BffSessionError)) throw error;
        // A failed plain read may leave a live owner. Protected reads return 503;
        // public rendering stays anonymous without deleting that recoverable session.
        return anonymous([], store.peek(current) !== null);
      }
    },
    async handle(request: BffHttpRequest, render?: BffPageRenderer): Promise<Response | null> {
      const httpSignal = request.signal;
      let postDeadline: AbortSignal | undefined;
      const path = request.target.split('?', 1)[0]!;
      // Recipient and data-request actions belong only to their pre-Next native handlers.
      if (path.startsWith('/api/auth/invitation/') || DATA_REQUEST_DOCUMENT_PATHS.includes(path))
        return result(request, { code: 'not_found' }, 404);
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
          path === FEDERATION_TEST_PATH ||
          Object.hasOwn(BFF_ADMIN_FORMS, path)
          ? result(request, { code: 'not_found' }, 404)
          : null;
      try {
        if (request.method === 'POST') {
          postDeadline = AbortSignal.timeout(10_000);
          request = {
            ...request,
            signal: AbortSignal.any([httpSignal, postDeadline]),
          };
        }
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
            // A missing, slow or failed public read hides only the federation option.
            const option = await store.signInOption(request.host, request.source, request.signal);
            response.headers.set('content-type', 'text/html; charset=utf-8');
            // Fetch serializes navigation POST Origin as null under no-referrer.
            // This credential-free form needs its exact same-origin CSRF proof;
            // provider redirects, callbacks and logout keep no-referrer.
            response.headers.set('referrer-policy', 'same-origin');
            return new Response(
              render({
                kind: 'signin',
                csrfToken,
                returnPath: destination,
                contextHint,
                ...(option ? { federation: option.displayName } : {}),
              }),
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
          const owner = entryBinding ?? current;
          if (!owner)
            return document(
              request,
              render,
              { kind: 'entry', entry: null, cursor },
              { state: 'anonymous' },
            );
          const native = ticket(request);
          if (native.prefetch) return result(request, null);
          const snapshot = await store.preparePage(owner, request.source, {
            kind: entryBinding ? 'entry' : 'scopes',
            ticket: native,
            cursor,
          });
          request.signal.throwIfAborted();
          if ('session' in snapshot) throw new SnapshotAdmissionError('unavailable');
          return entryDocument(request, render, snapshot);
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
            'kc_action',
            'kc_action_status',
            'error',
            'error_description',
            'error_uri',
          ]);
          const state = fields.get('state');
          const browser = cookies.get(BFF_COOKIES.browser);
          const proof = state && cookies.get(BFF_COOKIES.login + state);
          if (!isBffOpaque(state) || !browser || !proof) throw new BffInputError();
          const completed = await store.completeCallback({
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
          if (completed.kind === 'federation_test')
            // ADR-0099: nothing is published; the administrator's session ends and the browser leaves through the
            // provider end-session, because the shared provider session now carries the brokered note.
            return redirect(request, completed.logout.redirectUrl ?? '/', [
              clearBffCookie(BFF_COOKIES.session),
              ...(cookies.has(BFF_COOKIES.entry) ? [clearBffCookie(BFF_COOKIES.entry)] : []),
              clearBffCookie(BFF_COOKIES.login + state),
              bffCookie(BFF_COOKIES.logout + completed.logout.state, completed.logout.proof, 300),
            ]);
          return redirect(
            request,
            completed.invitation ? '/invitations/confirm' : '/api/auth/entry',
            [
              bffCookie(BFF_COOKIES.entry, completed.handle, 300),
              bffCookie(BFF_COOKIES.browser, completed.browser, cookieLife),
              clearBffCookie(BFF_COOKIES.login + state),
            ],
          );
        }
        bffQuery(request.target, []);
        exactBffOrigin(request);
        if (path === '/api/auth/signin/keycloak') {
          const fields = await bffForm(request, [
            'csrfToken',
            'returnTo',
            'contextHint',
            'invitation',
            'federation',
          ]);
          const invitation = fields.get('invitation');
          // The browser chooses only "federation"; the alias is read server-side by the owner.
          const federation = fields.get('federation');
          if (federation !== null && (federation !== '1' || invitation !== null))
            throw new BffInputError();
          if (
            invitation !== null &&
            (invitation !== 'continue' || !cookies.has(BFF_COOKIES.invitation))
          )
            throw new BffInputError();
          if (!bffSecretEquals(fields.get('csrfToken'), cookies.get(BFF_COOKIES.csrf)))
            return result(request, { code: 'csrf' }, 403);
          const started = await store.start({
            host: request.host,
            source: request.source,
            browser: cookies.get(BFF_COOKIES.browser),
            handle: cookies.get(BFF_COOKIES.session),
            returnPath: returnPath(fields.get('returnTo')),
            contextHint: bffContextHint(fields.get('contextHint')),
            ...(invitation === 'continue'
              ? { invitation: cookies.get(BFF_COOKIES.invitation)! }
              : {}),
            ...(federation === '1' ? { federation: true } : {}),
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
            const invitationBinding = binding(request, cookies, BFF_COOKIES.invitation);
            if (invitationBinding && store.invitationEntry(invitationBinding, selectedBinding)) {
              await store.create(selectedBinding, request.source, csrf, request.signal);
              return redirect(request, '/invitations/confirm', []);
            }
            const snapshot = await store.preparePage(selectedBinding, request.source, {
              kind: 'create',
              ticket: ticket(request),
              csrf,
              signal: request.signal,
            });
            request.signal.throwIfAborted();
            if ('session' in snapshot) throw new SnapshotAdmissionError('unavailable');
            if (snapshot.view.kind === 'failure') return entryDocument(request, render, snapshot);
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
              return await actionDocument(
                request,
                render,
                current!,
                { kind: 'account_change', csrfToken: live.csrfToken },
                { code: 'account_change' },
                409,
              );
            throw error;
          }
        }
        if (path === FEDERATION_TEST_PATH) {
          const fields = await bffForm(request, ['csrfToken']);
          const csrf = fields.get('csrfToken');
          const session = current && store.peek(current);
          if (!current || !session || !bffSecretEquals(csrf, session.csrfToken))
            return result(request, { code: 'csrf' }, 403);
          try {
            const started = await store.startFederationTest(
              current,
              request.source,
              csrf!,
              request.signal,
            );
            if (request.signal.aborted) throw new BffInputError();
            return redirect(request, started.redirectUrl, [
              bffCookie(BFF_COOKIES.browser, started.browser, cookieLife),
              bffCookie(BFF_COOKIES.login + started.state, started.proof, 300),
            ]);
          } catch (error) {
            const live = store.peek(current);
            if (
              httpSignal.aborted ||
              !live ||
              !(error instanceof BffSessionError || error instanceof BffProviderError) ||
              (error instanceof BffSessionError &&
                ['revoked', 'expired', 'csrf', 'identity', 'stopped', 'host_binding'].includes(
                  error.reason,
                ))
            )
              throw error;
            const status =
              error instanceof NativeApiError && error.status !== undefined ? error.status : 503;
            return await actionDocument(
              request,
              render,
              current,
              {
                kind: 'mutation_error',
                action: FEDERATION_TEST_PATH,
                destination: FEDERATION_TEST_DESTINATION,
                fields: [['csrfToken', csrf!]],
                reason:
                  status === 409
                    ? 'stale'
                    : [401, 403, 404].includes(status)
                      ? 'denied'
                      : 'unavailable',
                retryable: false,
              },
              { code: status === 409 ? 'concurrency_conflict' : 'unavailable' },
              status,
            );
          }
        }
        const adminFields = Object.hasOwn(BFF_ADMIN_FORMS, path)
          ? BFF_ADMIN_FORMS[path]
          : undefined;
        if (adminFields) {
          const fields = await bffForm(
            request,
            adminFields,
            path === '/api/account/custom-fields' || path === '/api/studio/membership-custom-fields'
              ? customControlName
              : path === '/api/studio/custom-field-create' ||
                  path === '/api/studio/custom-field-revise'
                ? customAuthorName
                : undefined,
          );
          const session = current && store.peek(current);
          const csrf = fields.get('csrfToken');
          if (!current || !session || !bffSecretEquals(csrf, session.csrfToken))
            return result(request, { code: 'csrf' }, 403);
          // ADR-0106: a data-request action's recovery names its closed refusal, or none for a generic outcome.
          const dataRequestForm = Object.hasOwn(DATA_REQUEST_ADMIN_FORMS, path);
          let customFields: MembershipCustomFields | undefined;
          if (
            (path === '/api/account/custom-fields' ||
              path === '/api/studio/membership-custom-fields') &&
            [...fields.keys()].some(customControlName)
          ) {
            const metadata = await store.operate(
              current,
              request.source,
              path === '/api/account/custom-fields'
                ? { kind: 'getOwnCustomFields' }
                : { kind: 'getMembershipCustomFields', id: fields.get('membershipId') ?? '' },
              undefined,
              request.signal,
            );
            if (metadata.kind !== 'success')
              return await actionDocument(
                request,
                render,
                current,
                {
                  kind: 'mutation_error',
                  action: path,
                  destination:
                    path === '/api/account/custom-fields' ? '/portal/profile' : '/studio/members',
                  fields: [...fields.entries()],
                  reason:
                    metadata.kind === 'api-error' && [401, 403, 404].includes(metadata.status)
                      ? 'denied'
                      : 'unavailable',
                  retryable: false,
                },
                {
                  code:
                    metadata.kind === 'api-error' && [401, 403, 404].includes(metadata.status)
                      ? 'forbidden'
                      : 'unavailable',
                },
                metadata.kind === 'api-error'
                  ? metadata.status
                  : metadata.kind === 'invalid-request'
                    ? 400
                    : 503,
              );
            customFields = metadata.data as MembershipCustomFields;
          }
          let customFieldControls = customFields
            ? customFieldControlDescriptions(fields, customFields)
            : undefined;
          let form: ReturnType<typeof bffAdministrationForm>;
          try {
            form = bffAdministrationForm(path, fields, customFields);
          } catch (error) {
            if (Object.hasOwn(CUSTOM_FIELD_FORMS, path) && error instanceof BffInputError) {
              const selected = fields.get('membershipId') ?? fields.get('definitionId');
              const safeId =
                selected !== null &&
                /^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/.test(
                  selected,
                );
              const destination = path.startsWith('/api/account/')
                ? '/portal/profile'
                : path === '/api/studio/membership-custom-fields'
                  ? safeId
                    ? '/studio/members/' + selected
                    : '/studio/members'
                  : safeId
                    ? '/studio/custom-fields/' + selected
                    : '/studio/custom-fields';
              return await actionDocument(
                request,
                render,
                current,
                {
                  kind: 'mutation_error',
                  action: path,
                  destination,
                  fields: [...fields.entries()],
                  customFieldControls,
                  reason: 'validation',
                  retryable: false,
                },
                { code: 'validation_failed' },
                400,
              );
            }
            if (error instanceof BffDataRequestConfirmationError)
              return await actionDocument(
                request,
                render,
                current,
                {
                  kind: 'mutation_error',
                  action: path,
                  // Thrown only after the Membership identifier passed its canonical grammar.
                  destination: '/studio/members/' + fields.get('membershipId')!,
                  fields: [['csrfToken', csrf!]],
                  reason: 'validation',
                  retryable: false,
                  dataRequest: { refusal: null },
                },
                { code: 'validation_failed' },
                400,
              );
            if (error instanceof BffFederationInputError)
              return await actionDocument(
                request,
                render,
                current,
                {
                  kind: 'mutation_error',
                  action: path,
                  destination: '/studio/federation',
                  fields: [['csrfToken', csrf!]],
                  reason: 'validation',
                  retryable: false,
                  federation: { invalid: error.invalidFields, outcome: null },
                },
                { code: 'validation_failed' },
                400,
              );
            if (
              !(error instanceof BffProfileInputError) &&
              !(error instanceof BffInvitationInputError)
            )
              throw error;
            return await actionDocument(
              request,
              render,
              current,
              {
                kind: 'mutation_error',
                action: path,
                destination:
                  path === '/api/studio/invitation-create'
                    ? '/studio/invitations'
                    : path === '/api/studio/invitation-policy'
                      ? '/studio/invitation-policy'
                      : path === '/api/account/profile'
                        ? '/portal/profile'
                        : '/studio/members/' + fields.get('membershipId')!,
                fields: [...fields.entries()],
                reason: 'validation',
                invalidFields: error.invalidFields,
                retryable: false,
              },
              { code: 'validation_failed' },
              400,
            );
          }
          let outcome: NativeOperationResult;
          try {
            outcome = await store.operate(
              current,
              request.source,
              form.operation,
              csrf!,
              request.signal,
            );
            // A deadline after API settlement is still an unconfirmed mutation.
            request.signal.throwIfAborted();
          } catch (error) {
            const live = store.peek(current);
            const localDeadline = postDeadline?.aborted === true && !httpSignal.aborted;
            if (
              httpSignal.aborted ||
              !live ||
              !bffSecretEquals(csrf, live.csrfToken) ||
              (error instanceof BffSessionError &&
                (['revoked', 'expired', 'transaction', 'csrf', 'identity', 'stopped'].includes(
                  error.reason,
                ) ||
                  (error.reason === 'cancelled' && !localDeadline)))
            )
              throw error;
            return await actionDocument(
              request,
              render,
              current,
              {
                kind: 'mutation_error',
                action: path,
                destination: form.destination,
                fields: [...fields.entries()],
                customFieldControls,
                reason: 'unavailable',
                // An unconfirmed federation write is settled by Resume, never resubmitted (ADR-0099).
                retryable: !Object.hasOwn(FEDERATION_FORMS, path),
                ...(dataRequestForm ? { dataRequest: { refusal: null } } : {}),
              },
              { code: 'unavailable' },
              503,
            );
          }
          if (outcome.kind === 'success') {
            // ADR-0099: every settled federation outcome is a 200; only applied and recorded return to the page
            // directly. Others are explained without provider text, and nothing is retried automatically.
            const settled =
              Object.hasOwn(FEDERATION_FORMS, path) &&
              (outcome.data as { outcome?: unknown } | null)?.outcome;
            if (typeof settled === 'string' && !['applied', 'recorded'].includes(settled))
              return await actionDocument(
                request,
                render,
                current,
                {
                  kind: 'mutation_error',
                  action: path,
                  destination: form.destination,
                  fields: [['csrfToken', csrf!]],
                  reason: settled === 'failed' || settled === 'drift' ? 'denied' : 'unavailable',
                  retryable: false,
                  federation: { invalid: [], outcome: settled },
                },
                { code: settled },
                200,
              );
            if (dataRequestForm) {
              const destination = dataRequestAdministrationDestination(
                path,
                form.destination,
                outcome.location,
              );
              if (destination === null) throw new BffSessionError('response');
              return redirect(request, destination, []);
            }
            if (path === '/api/studio/invitation-create') {
              const location = outcome.location;
              const match =
                /^\/api\/v1\/identity\/invitations\/([0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12})$/.exec(
                  location ?? '',
                );
              if (!match) throw new BffSessionError('response');
              return redirect(request, '/studio/invitations/' + match[1], []);
            }
            return redirect(request, form.destination, []);
          }
          const status =
            outcome.kind === 'api-error'
              ? outcome.status
              : outcome.kind === 'invalid-request'
                ? 400
                : 503;
          const code =
            outcome.kind === 'api-error'
              ? outcome.error.code
              : outcome.kind === 'invalid-request' &&
                  (path.startsWith('/api/studio/invitation-') ||
                    Object.hasOwn(CUSTOM_FIELD_FORMS, path))
                ? 'validation_failed'
                : 'unavailable';
          if (
            outcome.kind === 'api-error' &&
            outcome.status === 400 &&
            outcome.error.code === 'validation_failed' &&
            customFieldControls === undefined &&
            (path === '/api/account/custom-fields' ||
              path === '/api/studio/membership-custom-fields')
          ) {
            try {
              const metadata = await store.operate(
                current,
                request.source,
                path === '/api/account/custom-fields'
                  ? { kind: 'getOwnCustomFields' }
                  : { kind: 'getMembershipCustomFields', id: fields.get('membershipId') ?? '' },
                undefined,
                request.signal,
              );
              request.signal.throwIfAborted();
              if (metadata.kind === 'success') {
                const currentFields = metadata.data as MembershipCustomFields;
                if (
                  path === '/api/account/custom-fields' ||
                  currentFields.membershipId === fields.get('membershipId')
                )
                  customFieldControls = customFieldControlDescriptions(fields, currentFields);
              }
            } catch (error) {
              if (request.signal.aborted || error instanceof BffSessionError) throw error;
              // The rejected mutation remains rejected; unavailable metadata grants no editing shape.
            }
          }
          const profileErrors =
            outcome.kind === 'api-error' && outcome.error.code === 'validation_failed'
              ? Object.keys(outcome.error.fieldErrors)
              : [];
          // ADR-0106 § 7: a closed business refusal is final for this request; it is explained, never retried.
          const refusal =
            dataRequestForm &&
            outcome.kind === 'api-error' &&
            outcome.status === 409 &&
            outcome.problem.code === 'business_rule_violation'
              ? dataRequestAdministrationRefusal(outcome.problem.errors)
              : null;
          const reason =
            refusal !== null
              ? 'denied'
              : code === 'validation_failed' &&
                  (path === '/api/account/profile' ||
                    path === '/api/studio/membership-profile' ||
                    path.startsWith('/api/studio/invitation-') ||
                    Object.hasOwn(CUSTOM_FIELD_FORMS, path) ||
                    Object.hasOwn(FEDERATION_FORMS, path))
                ? 'validation'
                : code === 'concurrency_conflict'
                  ? 'stale'
                  : code === 'request_in_progress'
                    ? 'in_progress'
                    : code === 'idempotency_key_reuse'
                      ? 'key_reuse'
                      : [401, 403, 404].includes(status)
                        ? 'denied'
                        : 'unavailable';
          return await actionDocument(
            request,
            render,
            current,
            {
              kind: 'mutation_error',
              action: path,
              destination: form.destination,
              fields: [...fields.entries()],
              customFieldControls,
              reason,
              ...(reason === 'validation' &&
              outcome.kind === 'api-error' &&
              outcome.error.code === 'validation_failed'
                ? {
                    invalidFields: (
                      ['displayName', 'preferredLocale', 'email', 'offeredRole', 'days'] as const
                    ).filter((name) =>
                      profileErrors.some((key) => key.toLowerCase() === name.toLowerCase()),
                    ),
                  }
                : {}),
              retryable:
                !Object.hasOwn(FEDERATION_FORMS, path) &&
                (reason === 'unavailable' || reason === 'in_progress'),
              ...(Object.hasOwn(FEDERATION_FORMS, path)
                ? { federation: { invalid: [], outcome: null } }
                : {}),
              ...(dataRequestForm ? { dataRequest: { refusal } } : {}),
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
        if (
          path === '/api/auth/callback/keycloak' &&
          error instanceof BffSessionError &&
          error.clearTransaction
        ) {
          const cookies = bffCookies(request.headers.get('cookie'));
          if (cookies.has(BFF_COOKIES.invitation))
            return redirect(request, '/invitations/confirm', [
              clearBffCookie(BFF_COOKIES.login + error.clearTransaction),
            ]);
        }
        const entryRequest = [
          '/api/auth/entry',
          '/api/auth/entry/create',
          '/api/auth/entry/select',
        ].includes(path);
        if (error instanceof SnapshotAdmissionError) {
          const response = entryRequest
            ? document(
                request,
                render,
                { kind: 'entry_error', reason: error.status === 429 ? 'busy' : 'unavailable' },
                { code: 'unavailable' },
                error.status,
              )
            : result(request, { code: 'unavailable' }, error.status);
          response.headers.delete('retry-after');
          return response;
        }
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
                        'host_unavailable',
                      ].includes(error.reason)
                    ? 503
                    : 400;
          const retryAfterSeconds =
            error instanceof NativeApiError ? error.retryAfterSeconds : undefined;
          const reason =
            status === 401 ||
            [
              'identity',
              'invalid_grant',
              'reauthenticate',
              'expired',
              'revoked',
              'host_binding',
            ].includes(error.reason)
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
