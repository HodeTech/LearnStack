import { randomBytes } from 'node:crypto';
import type { IncomingMessage, ServerResponse } from 'node:http';
import { Readable } from 'node:stream';

import { createServerSdk } from '@learnstack/sdk/public-server';

import type { IngressContext } from '../ingress.js';
import type { PublicAdmissionHolder } from '../public-admission-runtime.js';
import { PUBLIC_API_DEADLINE_MS, MAX_PUBLIC_RESPONSE_BYTES } from '../public-api-limits.js';
import { BFF_COOKIES, bffCookies, bffCookie, clearBffCookie } from './cookies.js';
import { bffForm, bffQuery, exactBffOrigin } from './http-input.js';
import type { BffHttpRequest } from './http-input.js';
import { BffInputError } from './input-error.js';
import type { InvitationResponseLease } from './invitation-session.js';
import type { InvitationPage } from './native-invitation-contract.js';
import { BffSessionError } from './session-contract.js';
import type { BffSessionStore, SessionBinding } from './session-contract.js';

export function nativeInvitationTarget(target: string): boolean {
  const path = target.split('?', 1)[0]!;
  return (
    path === '/invitations' ||
    path === '/invitations/confirm' ||
    path.startsWith('/api/auth/invitation/')
  );
}

/** One fixed public bootstrap, with the same SDK projection and trusted host/source authority. */
async function site(
  configuration: Readonly<{ apiOrigin: string; secret: string }>,
  binding: IngressContext,
  signal: AbortSignal,
  transport: typeof fetch,
) {
  const sdk = createServerSdk(async (path) => {
    if (path !== '/api/v1/public/site') throw new Error('Invitation host admission refused');
    const current = AbortSignal.any([signal, AbortSignal.timeout(PUBLIC_API_DEADLINE_MS)]);
    let reader: ReadableStreamDefaultReader<Uint8Array> | undefined;
    let cancellation: Promise<void> | undefined;
    const cancel = () => {
      if (reader) cancellation ??= reader.cancel().catch(() => undefined);
    };
    try {
      current.throwIfAborted();
      const response = await transport(configuration.apiOrigin + path, {
        method: 'GET',
        redirect: 'error',
        cache: 'no-store',
        credentials: 'omit',
        signal: current,
        headers: {
          accept: 'application/json',
          'X-LearnStack-Host': binding.host,
          'X-LearnStack-Hop-Secret': configuration.secret,
          'X-LearnStack-Visitor-Address': binding.peer,
          traceparent:
            '00-' + randomBytes(16).toString('hex') + '-' + randomBytes(8).toString('hex') + '-00',
        },
      });
      reader = response.body?.getReader();
      current.addEventListener('abort', cancel, { once: true });
      current.throwIfAborted();
      const media = response.headers.get('content-type')?.split(';', 1)[0]?.trim().toLowerCase();
      if (
        media !== (response.ok ? 'application/json' : 'application/problem+json') ||
        response.redirected ||
        response.headers.has('set-cookie')
      )
        throw new Error('Invitation host admission refused');
      const chunks: Uint8Array[] = [];
      let length = 0;
      while (reader) {
        const chunk = await reader.read();
        current.throwIfAborted();
        if (chunk.done) break;
        length += chunk.value.length;
        if (length > MAX_PUBLIC_RESPONSE_BYTES)
          throw new Error('Invitation host admission refused');
        chunks.push(chunk.value);
      }
      const bytes = new Uint8Array(length);
      let offset = 0;
      for (const chunk of chunks) {
        bytes.set(chunk, offset);
        offset += chunk.length;
      }
      return new Response(bytes, { status: response.status, headers: response.headers });
    } finally {
      current.removeEventListener('abort', cancel);
      cancel();
      if (cancellation) await cancellation;
      reader?.releaseLock();
    }
  });
  return sdk.getSite({ signal });
}

/** Runs within the existing native response owner, before Next can observe the landing. */
export function createNativeInvitationHandler(
  input: Readonly<{
    configuration: Readonly<{ apiOrigin: string; secret: string }>;
    admission: PublicAdmissionHolder;
    store: BffSessionStore;
    responseLease: (binding: IngressContext) => InvitationResponseLease;
    render: (page: InvitationPage, nonce: string) => string;
    providerOrigin: string;
    transport: typeof fetch;
  }>,
) {
  const providerOrigin = new URL(input.providerOrigin).origin;
  if (
    providerOrigin !== input.providerOrigin ||
    !(
      providerOrigin.startsWith('https://') ||
      (new URL(providerOrigin).protocol === 'http:' &&
        ['localhost', '127.0.0.1', '[::1]'].includes(new URL(providerOrigin).hostname))
    )
  )
    throw new Error('Invitation provider unavailable');
  return async (
    binding: IngressContext,
    request: IncomingMessage,
    response: ServerResponse,
  ): Promise<void> => {
    const nonce = randomBytes(16).toString('base64');
    const render = (page: InvitationPage) => input.render(page, nonce);
    const write = (
      status: number,
      body = '',
      cookies: readonly string[] = [],
      location?: string,
    ) => {
      response.writeHead(status, {
        'content-type': 'text/html; charset=utf-8',
        'cache-control': 'no-store',
        'referrer-policy': 'same-origin',
        'x-content-type-options': 'nosniff',
        'x-frame-options': 'DENY',
        'content-security-policy':
          "default-src 'none'; script-src 'nonce-" +
          nonce +
          "'; style-src 'unsafe-inline'; form-action 'self' " +
          providerOrigin +
          "; base-uri 'none'; frame-ancestors 'none'",
        ...(cookies.length ? { 'set-cookie': [...cookies] } : {}),
        ...(location ? { location } : {}),
      });
      response.end(binding.method === 'HEAD' ? undefined : body);
    };
    const path = binding.target.split('?', 1)[0]!;
    const action = path.slice('/api/auth/invitation/'.length);
    if (
      !(
        (['/invitations', '/invitations/confirm'].includes(path) &&
          ['GET', 'HEAD'].includes(binding.method)) ||
        (binding.method === 'POST' &&
          ['capture', 'inspect', 'verify', 'accept', 'result', 'cancel'].includes(action))
      )
    ) {
      write(404);
      return;
    }
    try {
      bffQuery(binding.target, []);
    } catch {
      write(400);
      return;
    }
    const admission = input.admission.begin(binding);
    const admitted = await site(input.configuration, binding, admission.signal, input.transport);
    admission.assertActive();
    if (admitted.kind !== 'success') {
      admission.refuse();
      write(admitted.kind === 'api-error' && admitted.status === 404 ? 404 : 503);
      return;
    }
    admission.publish(admitted.data);
    const publicRead = input.admission.read(binding);
    const lease = input.responseLease(binding);
    const headers = new Headers();
    for (const [name, value] of Object.entries(request.headers))
      if (value !== undefined) headers.set(name, Array.isArray(value) ? value.join(',') : value);
    const http: BffHttpRequest = {
      host: binding.host,
      source: binding.peer,
      method: binding.method,
      target: binding.target,
      headers,
      body:
        binding.method === 'POST' ? (Readable.toWeb(request) as ReadableStream<Uint8Array>) : null,
      signal: lease.signal,
    };
    try {
      if (headers.has('authorization')) throw new BffInputError();
      if (
        binding.method !== 'POST' &&
        (Number(headers.get('content-length') ?? '0') !== 0 || headers.has('transfer-encoding'))
      )
        throw new BffInputError();
      const cookies = bffCookies(headers.get('cookie'));
      const passive =
        binding.method === 'HEAD' ||
        headers.has('rsc') ||
        headers.has('next-router-prefetch') ||
        headers.get('purpose') === 'prefetch' ||
        /(?:^|[;\s])prefetch(?:$|[;\s])/i.test(headers.get('sec-purpose') ?? '');
      if (passive) {
        write(200);
        return;
      }
      let handle = cookies.get(BFF_COOKIES.invitation);
      let browser = cookies.get(BFF_COOKIES.browser);
      const edits: string[] = [];
      if (binding.method === 'GET' && path === '/invitations') {
        const opened = input.store.openInvitation({
          host: binding.host,
          source: binding.peer,
          handle,
          browser,
          sessionHandle: cookies.get(BFF_COOKIES.session),
          entryHandle: cookies.get(BFF_COOKIES.entry),
        });
        handle = opened.handle;
        browser = opened.browser;
        edits.push(
          bffCookie(BFF_COOKIES.invitation, handle, 300),
          bffCookie(BFF_COOKIES.browser, browser, 28800),
        );
        input.store.retainInvitation({ host: binding.host, handle, browser }, lease);
        publicRead.assertActive();
        write(
          200,
          render({
            kind: 'landing',
            locale: admitted.data.defaultLocale,
            csrfToken: opened.csrfToken,
          }),
          edits,
        );
        return;
      }
      if (!handle || !browser) {
        write(303, '', [], '/invitations');
        return;
      }
      const owner: SessionBinding = { host: binding.host, handle, browser };
      input.store.retainInvitation(owner, lease);
      let state;
      if (binding.method === 'POST') {
        exactBffOrigin(http);
        const fields = await bffForm(
          http,
          action === 'capture'
            ? ['csrfToken', 'code']
            : action === 'accept'
              ? ['csrfToken', 'version']
              : ['csrfToken'],
        );
        const csrf = fields.get('csrfToken') ?? '';
        if (action === 'capture') {
          let captured;
          try {
            captured = await input.store.invitation(
              owner,
              binding.peer,
              { kind: 'capture', csrf, code: fields.get('code') ?? '' },
              lease.signal,
            );
          } catch (error) {
            if (!(error instanceof BffInputError)) throw error;
            // The owner validates CSRF before parsing the code; never echo the submitted secret.
            const current = await input.store.invitation(owner, binding.peer);
            input.store.retainInvitation(owner, lease);
            publicRead.assertActive();
            write(
              400,
              render({
                kind: 'landing',
                locale: admitted.data.defaultLocale,
                csrfToken: current.csrfToken,
                invalid: true,
              }),
            );
            return;
          }
          const capturedBinding = captured.opening
            ? { ...owner, handle: captured.opening.handle, browser: captured.opening.browser }
            : owner;
          input.store.retainInvitation(capturedBinding, lease);
          write(
            303,
            '',
            captured.opening
              ? [bffCookie(BFF_COOKIES.invitation, captured.opening.handle, 300)]
              : [],
            '/invitations/confirm',
          );
          return;
        }
        if (action === 'cancel') {
          await input.store.invitation(owner, binding.peer, { kind: 'cancel', csrf }, lease.signal);
          lease.pin(publicRead.assertActive);
          // Server custody is revoked; a late response must not erase a newer handle cookie.
          write(303, '', [], '/invitations');
          return;
        }
        state = await input.store.invitation(
          owner,
          binding.peer,
          action === 'accept'
            ? { kind: 'accept', csrf, version: fields.get('version') ?? '' }
            : { kind: action as 'inspect' | 'verify' | 'result', csrf },
          lease.signal,
        );
        if (state.login) {
          write(
            303,
            '',
            [bffCookie(BFF_COOKIES.login + state.login.state, state.login.proof, 300)],
            state.login.redirectUrl,
          );
          return;
        }
        if (state.completion) {
          edits.push(
            bffCookie(BFF_COOKIES.session, state.completion.handle, 28800),
            clearBffCookie(BFF_COOKIES.entry),
          );
        }
      } else state = await input.store.invitation(owner, binding.peer);
      // No receipt IDs, code, credentials or operation capabilities reach the renderer.
      const { login: _login, completion: _completion, opening: _opening, ...view } = state;
      void _login;
      void _completion;
      void _opening;
      const signinCsrfToken =
        cookies.get(BFF_COOKIES.csrf) ?? randomBytes(32).toString('base64url');
      if (!cookies.has(BFF_COOKIES.csrf))
        edits.push(bffCookie(BFF_COOKIES.csrf, signinCsrfToken, 300));
      input.store.retainInvitation(owner, lease);
      publicRead.assertActive();
      lease.signal.throwIfAborted();
      write(
        200,
        render({
          kind: 'confirm',
          locale: admitted.data.defaultLocale,
          ...view,
          signinCsrfToken,
        }),
        edits,
      );
    } catch (error) {
      if (response.headersSent) {
        response.destroy();
        return;
      }
      lease.pin(publicRead.assertActive);
      if (
        error instanceof BffInputError ||
        (error instanceof BffSessionError && error.reason === 'csrf')
      ) {
        write(400);
        return;
      }
      if (error instanceof BffSessionError && ['revoked', 'expired'].includes(error.reason)) {
        write(303, '', [], '/invitations');
        return;
      }
      write(503);
    }
  };
}
