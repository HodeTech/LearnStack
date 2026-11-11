import { randomBytes } from 'node:crypto';
import type { IncomingMessage, ServerResponse } from 'node:http';
import { Readable } from 'node:stream';

import type { ConsentHistory, ConsentScopes } from '@learnstack/sdk/administration-server';

import type { IngressContext } from '../ingress.js';
import type { NativeConsentResponseLease } from './consent-response-contract.js';
import { BFF_COOKIES, bffCookies } from './cookies.js';
import { customFieldForm } from './custom-field-forms.js';
import { bffForm, bffQuery, exactBffOrigin } from './http-input.js';
import type { BffHttpRequest } from './http-input.js';
import { BffInputError } from './input-error.js';
import type { BffSessionStore, SessionBinding } from './session-contract.js';
import { BffSessionError } from './session-contract.js';
import { bffSecretEquals } from './session-store.js';
const escape = (value: string) =>
  value.replace(
    /[&<>"']/g,
    (character) =>
      ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[character]!,
  );
const hidden = (name: string, value: string) =>
  '<input type="hidden" name="' + name + '" value="' + escape(value) + '">';
const uuid = (value: string) =>
  /^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/.test(value);
export function nativeConsentTarget(target: string): boolean {
  const path = target.split('?', 1)[0];
  return (
    path === '/account/consents' ||
    path === '/api/auth/entry/consents' ||
    path === '/api/auth/entry/consents/withdraw'
  );
}
// A leaving/restored private document stays veiled until a fresh fixed GET is admitted.
// The constant destination also prevents history restoration from replaying a POST result.
export const CONSENT_HISTORY_SOURCE = `(()=>{try{history.replaceState(null,'','/account/consents')}catch{document.documentElement.style.visibility='hidden';location.replace('/account/consents');return}let leaving=false;addEventListener('pagehide',()=>{leaving=true;document.documentElement.style.visibility='hidden'});addEventListener('pageshow',e=>{if(e.persisted||leaving){document.documentElement.style.visibility='hidden';location.replace('/account/consents')}})})();`;

const consentKeys = [
  'title',
  'intro',
  'signin',
  'empty',
  'scope',
  'view',
  'next',
  'withdraw',
  'confirm',
  'granted',
  'withdrawn',
  'error',
  'back',
  'note',
] as const;
type ConsentCopy = Readonly<Record<(typeof consentKeys)[number], string>>;
function prepareCopy(value: unknown): ConsentCopy {
  if (!value || typeof value !== 'object' || Array.isArray(value))
    throw new BffSessionError('configuration');
  const entries = Object.entries(value);
  if (
    entries.length !== consentKeys.length ||
    entries.some(
      ([key, text]) =>
        !consentKeys.includes(key as (typeof consentKeys)[number]) ||
        typeof text !== 'string' ||
        !text.trim() ||
        text.length > 1000 ||
        /[{}]/.test(text) ||
        text.includes(String.fromCharCode(0)),
    )
  )
    throw new BffSessionError('configuration');
  return Object.freeze(Object.fromEntries(entries)) as ConsentCopy;
}

/** Fixed consent-only shell. It deliberately needs neither public site nor CurrentSession admission. */
export function createNativeConsentHandler(
  input: Readonly<{
    store: BffSessionStore;
    catalogues: Readonly<{ en: unknown; tr: unknown }>;
    responseLease: (binding: IngressContext) => NativeConsentResponseLease;
  }>,
) {
  const catalogues = { en: prepareCopy(input.catalogues.en), tr: prepareCopy(input.catalogues.tr) };
  return async (
    binding: IngressContext,
    request: IncomingMessage,
    response: ServerResponse,
  ): Promise<void> => {
    const nonce = randomBytes(16).toString('base64');
    let lease: NativeConsentResponseLease | undefined;
    let published = false;
    const write = (status: number, body = '', location?: string) => {
      if (lease && !published) {
        lease.publish({ kind: 'consent_result', status });
        published = true;
      }
      if (Buffer.byteLength(body) > 256 * 1024) throw new BffSessionError('response');
      response.writeHead(status, {
        'content-type': 'text/html; charset=utf-8',
        'cache-control': 'no-store',
        'referrer-policy': 'same-origin',
        'x-content-type-options': 'nosniff',
        'x-frame-options': 'DENY',
        'content-security-policy':
          "default-src 'none'; script-src 'nonce-" +
          nonce +
          "'; style-src 'unsafe-inline'; form-action 'self'; base-uri 'none'; frame-ancestors 'none'",
        ...(location ? { location } : {}),
      });
      response.end(binding.method === 'HEAD' ? undefined : body);
    };
    const headers = new Headers();
    for (const [name, value] of Object.entries(request.headers))
      if (value !== undefined) headers.set(name, Array.isArray(value) ? value.join(',') : value);
    const tr = /^tr(?:[-,; ]|$)/i.test(headers.get('accept-language') ?? '');
    const copy = catalogues[tr ? 'tr' : 'en'];
    const document = (body: string) =>
      '<!doctype html><html lang="' +
      (tr ? 'tr' : 'en') +
      '"><head><meta charset="utf-8"><script nonce="' +
      nonce +
      '">' +
      CONSENT_HISTORY_SOURCE +
      '</script><meta name="viewport" content="width=device-width,initial-scale=1"><meta name="robots" content="noindex,nofollow"><title>' +
      escape(copy.title) +
      '</title><style>body{font:1rem system-ui;line-height:1.6;max-width:52rem;margin:auto;padding:1.5rem;overflow-wrap:anywhere;color:#152238;background:#fff}a{color:#114499}button,input{font:inherit}li,fieldset{margin-block:1rem;padding:.75rem}button{min-height:44px;max-width:100%;white-space:normal}input{min-height:24px}:focus-visible{outline:3px solid #0759b8;outline-offset:3px}.notice{white-space:pre-wrap;overflow-wrap:anywhere}</style></head><body><main><h1>' +
      escape(copy.title) +
      '</h1><p>' +
      escape(copy.intro) +
      '</p>' +
      body +
      '<p><a href="/api/auth/entry">' +
      escape(copy.back) +
      '</a></p></main></body></html>';
    const path = binding.target.split('?', 1)[0]!;
    if (
      !(
        (path === '/account/consents' && ['GET', 'HEAD'].includes(binding.method)) ||
        (binding.method === 'POST' && path.startsWith('/api/auth/entry/consents'))
      )
    ) {
      write(404);
      return;
    }
    try {
      const query = bffQuery(
        binding.target,
        path === '/account/consents' ? ['membershipId', 'cursor', 'scopeCursor'] : [],
      );
      if (
        headers.has('authorization') ||
        (binding.method !== 'POST' &&
          (Number(headers.get('content-length') ?? '0') !== 0 || headers.has('transfer-encoding')))
      )
        throw new BffInputError();
      if (
        binding.method === 'HEAD' ||
        headers.has('rsc') ||
        headers.has('next-router-prefetch') ||
        /prefetch/i.test(headers.get('purpose') ?? '') ||
        /prefetch/i.test(headers.get('sec-purpose') ?? '')
      ) {
        write(200);
        return;
      }
      const cookies = bffCookies(headers.get('cookie'));
      const handle = cookies.get(BFF_COOKIES.entry);
      const browser = cookies.get(BFF_COOKIES.browser);
      if (!handle || !browser) {
        write(200, document('<p><a href="/api/auth/signin">' + escape(copy.signin) + '</a></p>'));
        return;
      }
      const owner: SessionBinding = {
        host: binding.host,
        handle,
        browser,
        traceparent: headers.get('traceparent') ?? undefined,
      };
      const responseLease = input.responseLease(binding);
      const csrf = input.store.retainConsent(owner, responseLease);
      lease = responseLease;
      const http: BffHttpRequest = {
        host: binding.host,
        source: binding.peer,
        method: binding.method,
        target: binding.target,
        headers,
        body:
          binding.method === 'POST'
            ? (Readable.toWeb(request) as ReadableStream<Uint8Array>)
            : null,
        signal: lease.signal,
      };
      let membershipId = query.get('membershipId');
      let cursor = query.get('cursor');
      let scopeCursor = query.get('scopeCursor');
      if (binding.method === 'POST') {
        exactBffOrigin(http);
        const withdrawing = path.endsWith('/withdraw');
        const fields = await bffForm(
          http,
          withdrawing
            ? [
                'csrfToken',
                'idempotencyKey',
                'membershipId',
                'evidenceId',
                'expectedSequence',
                'expectedEvidenceId',
                'confirmed',
              ]
            : ['csrfToken', 'membershipId', 'cursor', 'scopeCursor'],
        );
        if (!bffSecretEquals(fields.get('csrfToken'), csrf)) throw new BffSessionError('csrf');
        membershipId = fields.get('membershipId');
        cursor = fields.get('cursor');
        scopeCursor = fields.get('scopeCursor');
        if (withdrawing) {
          const form = customFieldForm('/api/account/consent-withdraw', fields);
          if (form.operation.kind !== 'withdrawOwnFieldConsent') throw new BffInputError();
          const outcome = await input.store.retainedConsent(
            owner,
            binding.peer,
            form.operation,
            csrf,
            lease.signal,
          );
          if (outcome.kind !== 'success') {
            write(
              outcome.kind === 'api-error' ? outcome.status : 503,
              document(
                '<p role="alert">' +
                  escape(copy.error) +
                  '</p><a href="/account/consents?membershipId=' +
                  encodeURIComponent(membershipId ?? '') +
                  '">' +
                  escape(copy.view) +
                  '</a>',
              ),
            );
            return;
          }
          write(303, '', '/account/consents?membershipId=' + encodeURIComponent(membershipId!));
          return;
        }
      }
      if (membershipId !== null && !uuid(membershipId)) throw new BffInputError();
      for (const value of [cursor, scopeCursor])
        if (value !== null && (value.length > 1024 || !value || !/^[A-Za-z0-9_-]+$/.test(value)))
          throw new BffInputError();
      const scopesResult = await input.store.retainedConsent(
        owner,
        binding.peer,
        {
          kind: 'listOwnConsentScopes',
          limit: 20,
          ...(scopeCursor ? { cursor: scopeCursor } : {}),
        },
        csrf,
        lease.signal,
      );
      if (scopesResult.kind !== 'success') {
        write(
          scopesResult.kind === 'api-error' ? scopesResult.status : 503,
          document('<p role="alert">' + escape(copy.error) + '</p>'),
        );
        return;
      }
      const rawScopes = scopesResult.data as ConsentScopes;
      let history: ConsentHistory | null = null;
      if (membershipId !== null) {
        const historyResult = await input.store.retainedConsent(
          owner,
          binding.peer,
          { kind: 'listOwnConsentHistory', membershipId, limit: 20, ...(cursor ? { cursor } : {}) },
          csrf,
          lease.signal,
        );
        if (historyResult.kind !== 'success') {
          write(
            historyResult.kind === 'api-error' ? historyResult.status : 503,
            document('<p role="alert">' + escape(copy.error) + '</p>'),
          );
          return;
        }
        history = historyResult.data as ConsentHistory;
      }
      const snapshot = lease.publish({
        kind: 'consents',
        scopes: rawScopes,
        history,
        membershipId,
        csrfToken: csrf,
        formKeys: Array.from({ length: 20 }, () => randomBytes(32).toString('base64url')),
      });
      published = true;
      if (snapshot.kind !== 'consents') throw new BffSessionError('response');
      const scopes = snapshot.scopes;
      history = snapshot.history;
      let body =
        '<ul>' +
        scopes.items
          .map(
            (scope) =>
              '<li><form method="post" action="/api/auth/entry/consents">' +
              hidden('csrfToken', csrf) +
              hidden('membershipId', scope.membershipId) +
              '<span>' +
              escape(copy.scope) +
              ' ' +
              escape(scope.membershipId) +
              '</span> <button>' +
              escape(copy.view) +
              '</button></form></li>',
          )
          .join('') +
        '</ul>';
      if (scopes.items.length === 0) body += '<p>' + escape(copy.empty) + '</p>';
      if (scopes.pageInfo.nextCursor)
        body +=
          '<a href="/account/consents?scopeCursor=' +
          encodeURIComponent(scopes.pageInfo.nextCursor) +
          '">' +
          escape(copy.next) +
          '</a>';
      if (membershipId !== null && history !== null) {
        body +=
          '<h2>' +
          escape(copy.scope) +
          ' ' +
          escape(membershipId) +
          '</h2>' +
          (history.items.length
            ? '<ul>' +
              history.items
                .map((item, index) => {
                  const labelLocale = Object.hasOwn(item.labels, tr ? 'tr' : 'en')
                    ? tr
                      ? 'tr'
                      : 'en'
                    : Object.keys(item.labels)[0]!;
                  const label = item.labels[labelLocale] ?? item.purposeKey;
                  return (
                    '<li><h3 lang="' +
                    escape(labelLocale) +
                    '">' +
                    escape(label) +
                    '</h3><p>' +
                    escape(item.isGranted ? copy.granted : copy.withdrawn) +
                    '</p><p class="notice" lang="' +
                    escape(item.noticeLocale) +
                    '">' +
                    escape(item.noticeText) +
                    '</p>' +
                    (item.isGranted
                      ? '<form method="post" action="/api/auth/entry/consents/withdraw">' +
                        hidden('csrfToken', csrf) +
                        hidden('membershipId', membershipId!) +
                        hidden('evidenceId', item.evidenceId) +
                        hidden('expectedEvidenceId', item.evidenceId) +
                        hidden('expectedSequence', item.sequence) +
                        hidden('idempotencyKey', snapshot.formKeys[index]!) +
                        '<p>' +
                        escape(copy.note) +
                        '</p><label><input type="checkbox" name="confirmed" value="true" required> ' +
                        escape(copy.confirm) +
                        '</label><p><button>' +
                        escape(copy.withdraw) +
                        '</button></p></form>'
                      : '') +
                    '</li>'
                  );
                })
                .join('') +
              '</ul>'
            : '<p>' + escape(copy.empty) + '</p>');
        if (history.pageInfo.nextCursor)
          body +=
            '<a href="/account/consents?membershipId=' +
            encodeURIComponent(membershipId) +
            '&amp;cursor=' +
            encodeURIComponent(history.pageInfo.nextCursor) +
            '">' +
            escape(copy.next) +
            '</a>';
      }
      lease.signal.throwIfAborted();
      write(200, document(body));
    } catch (error) {
      if (response.headersSent) {
        response.destroy();
        return;
      }
      const status =
        error instanceof BffInputError
          ? 400
          : error instanceof BffSessionError && error.reason === 'csrf'
            ? 403
            : error instanceof BffSessionError && ['revoked', 'expired'].includes(error.reason)
              ? 401
              : 503;
      write(
        status,
        document(
          '<p role="alert">' +
            escape(copy.error) +
            '</p><p><a href="/api/auth/signin">' +
            escape(copy.signin) +
            '</a></p>',
        ),
      );
    }
  };
}
