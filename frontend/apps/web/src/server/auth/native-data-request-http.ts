import { randomBytes } from 'node:crypto';
import type { IncomingMessage, ServerResponse } from 'node:http';
import { Readable } from 'node:stream';

import type {
  OwnDataRequestPage,
  RetainedDataRequestScopePage,
} from '@learnstack/sdk/administration-server';

import type { IngressContext } from '../ingress.js';
import type { NativeDataRequestResponseLease } from './consent-response-contract.js';
import { BFF_COOKIES, bffCookies } from './cookies.js';
import { relayDataExport } from './data-export-download.js';
import type { DataExportSink } from './data-export-download.js';
import { prepareDataRequestCatalogues } from './data-request-copy.js';
import type { DataRequestCatalogues, DataRequestKey } from './data-request-copy.js';
import { renderDataRequestDocument } from './data-request-html.js';
import type { DataRequestDocument } from './data-request-html.js';
import { bffForm, bffQuery, exactBffOrigin } from './http-input.js';
import type { BffHttpRequest } from './http-input.js';
import { DATA_REQUEST_DOCUMENT_PATHS } from './http-paths.js';
import { BffInputError } from './input-error.js';
import type {
  NativeOperationResult,
  OwnDataRequestOperation,
  RetainedDataRequestOperation,
} from './native-operation-contract.js';
import type { DataRequestResult, NativeDataRequestSnapshot } from './page-contract.js';
import { SnapshotAdmissionError } from './page-snapshot-pool.js';
import type { BffSessionStore, SessionBinding } from './session-contract.js';
import { BffSessionError } from './session-contract.js';
import { bffSecretEquals } from './session-store.js';

export function nativeDataRequestTarget(target: string): boolean {
  return DATA_REQUEST_DOCUMENT_PATHS.includes(target.split('?', 1)[0]!);
}

/**
 * The person's refusal sentences, one for every `errors.*` reason the data-request API emits on the person's
 * routes (ADR-0106 § 7, ADR-0107 §§ 4 and 7). A reason missing here falls back to `error.conflict`; the catalogue
 * test fails when the API documents or emits a reason this map lacks.
 */
export const DATA_REQUEST_REFUSALS: Readonly<Record<string, DataRequestKey>> = Object.freeze({
  lockey_request_already_active: 'refusal.request_already_active',
  lockey_blocked_hold: 'refusal.blocked_hold',
  lockey_blocked_last_administrator: 'refusal.blocked_last_administrator',
  lockey_subject_export_available: 'refusal.subject_export_available',
  lockey_subject_membership_current: 'refusal.subject_membership_current',
  lockey_data_export_unavailable: 'refusal.data_export_unavailable',
  lockey_data_request_window_ended: 'refusal.data_request_window_ended',
  lockey_data_request_not_cancellable: 'refusal.data_request_not_cancellable',
  lockey_subject_is_requester: 'refusal.subject_is_requester',
  lockey_snapshot_too_large: 'refusal.snapshot_too_large',
  lockey_data_export_ticket_limit: 'refusal.data_export_ticket_limit',
  lockey_subject_not_admitted: 'refusal.subject_not_admitted',
});
const RESULTS = new Set<DataRequestResult>([
  'export',
  'erasure',
  'closure',
  'cancelled',
  'revoked',
]);
const uuid = (value: string | null): value is string =>
  value !== null &&
  /^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/.test(value);
const groupValue = (value: string | null): value is string => value === 'tenant' || uuid(value);
const opaque = (value: string | null): value is string =>
  value !== null && /^[A-Za-z0-9_-]{43}$/.test(value);
const sealed = (value: string | null): value is string =>
  value !== null && value.length <= 1024 && /^[A-Za-z0-9_-]+$/.test(value);
const versionValue = (value: string | null): value is string =>
  value !== null &&
  /^(0|[1-9][0-9]{0,18})$/.test(value) &&
  (value.length < 19 || value <= '9223372036854775807');

type Action = 'view' | 'export' | 'erasure' | 'closure' | 'cancel' | 'revoke' | 'download';
const ACTION_FIELDS: Readonly<Record<Action, readonly string[]>> = {
  view: ['csrfToken', 'group'],
  export: ['csrfToken', 'idempotencyKey', 'group'],
  erasure: ['csrfToken', 'idempotencyKey', 'group', 'confirm'],
  closure: ['csrfToken', 'idempotencyKey', 'group', 'confirm', 'acknowledge'],
  cancel: ['csrfToken', 'idempotencyKey', 'group', 'requestId', 'version'],
  revoke: ['csrfToken', 'idempotencyKey', 'group', 'requestId', 'version'],
  download: ['csrfToken', 'group', 'requestId'],
};
/** The fixed actions and their mode; nothing else reaches an operation. */
function route(path: string): Readonly<{ mode: 'session' | 'entry'; action: Action }> | null {
  if (path === '/api/auth/entry/data-requests') return { mode: 'entry', action: 'view' };
  for (const [prefix, mode] of [
    ['/api/auth/entry/data-requests/', 'entry'],
    ['/api/account/data-requests/', 'session'],
  ] as const) {
    const action = path.startsWith(prefix) ? path.slice(prefix.length) : null;
    if (
      action !== null &&
      ['export', 'erasure', 'closure', 'cancel', 'revoke', 'download'].includes(action)
    )
      return { mode, action: action as Action };
  }
  return null;
}

/** One API outcome to the closed status and sentence a person sees; no API text is ever shown. */
export function dataRequestRefusal(
  result: NativeOperationResult | null,
  mutation: boolean,
): Readonly<{ status: number; message: DataRequestKey; retryAfter: number | null }> {
  if (!result || result.kind !== 'api-error') {
    if (result?.kind === 'invalid-request')
      return { status: 400, message: 'error.input', retryAfter: null };
    return {
      status: 503,
      message: mutation ? 'error.unconfirmed' : 'error.unavailable',
      retryAfter: 30,
    };
  }
  const status = result.status >= 400 && result.status <= 599 ? result.status : 503;
  const hint =
    'retryAfter' in result.error && typeof result.error.retryAfter === 'number'
      ? Math.min(result.error.retryAfter, 3600)
      : null;
  if (status === 409) {
    const code = result.problem.code;
    if (code === 'concurrency_conflict')
      return { status, message: 'error.stale', retryAfter: null };
    if (code === 'request_in_progress')
      return { status, message: 'error.in_progress', retryAfter: null };
    if (code === 'idempotency_key_reuse')
      return { status, message: 'error.key_reuse', retryAfter: null };
    if (code === 'idempotency_outcome_unavailable')
      return { status, message: 'error.outcome_unavailable', retryAfter: null };
    const reasons = [
      ...(result.problem.errors?.['$'] ?? []),
      ...(result.problem.errors?.['artifact'] ?? []),
    ];
    const known = reasons
      .map((reason) => DATA_REQUEST_REFUSALS[reason.key])
      .find((key) => key !== undefined);
    return { status, message: known ?? 'error.conflict', retryAfter: null };
  }
  const message: DataRequestKey =
    status === 400
      ? 'error.input'
      : status === 401
        ? 'error.unauthorized'
        : status === 403
          ? 'error.forbidden'
          : status === 404
            ? 'error.not_found'
            : status === 429
              ? 'error.busy'
              : status >= 500
                ? mutation && status !== 503
                  ? 'error.unconfirmed'
                  : 'error.unavailable'
                : 'error.conflict';
  return {
    status,
    message,
    retryAfter: status === 429 || status === 503 ? (hint ?? 30) : null,
  };
}

/**
 * The person's own data-request document and its fixed native actions (ADR-0106 §§ 9–11, ADR-0107 §§ 4, 6, 7).
 * Like the consent entry it needs no public-site bootstrap; unlike it, a live session's owner also serves it, for
 * the session's own group. The browser chooses no tenant, organization, credential or owner.
 */
export function createNativeDataRequestHandler(
  input: Readonly<{
    store: BffSessionStore;
    catalogues: DataRequestCatalogues;
    responseLease: (binding: IngressContext) => NativeDataRequestResponseLease;
    /** Wall clock for "due" and the download deadline; tests inject one, production uses Date.now. */
    now?: () => number;
  }>,
) {
  const select = prepareDataRequestCatalogues(input.catalogues);
  const now = input.now ?? Date.now;
  return async (
    binding: IngressContext,
    request: IncomingMessage,
    response: ServerResponse,
  ): Promise<void> => {
    const nonce = randomBytes(16).toString('base64');
    const headers = new Headers();
    for (const [name, value] of Object.entries(request.headers))
      if (value !== undefined) headers.set(name, Array.isArray(value) ? value.join(',') : value);
    const { locale, phrase } = select(headers.get('accept-language'));
    let lease: NativeDataRequestResponseLease | undefined;
    let published = false;
    const write = (
      status: number,
      body = '',
      location?: string,
      retryAfter: number | null = null,
    ) => {
      if (lease && !published) {
        lease.publish({ kind: 'data_request_result', status });
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
        ...(retryAfter !== null ? { 'retry-after': String(retryAfter) } : {}),
        ...(location ? { location } : {}),
      });
      response.end(binding.method === 'HEAD' ? undefined : body);
    };
    const render = (document: DataRequestDocument) =>
      renderDataRequestDocument(document, { locale, phrase, nonce });
    let group: string | null = null;
    let mutation = false;
    const refuse = (
      outcome: Readonly<{ status: number; message: DataRequestKey; retryAfter: number | null }>,
    ) =>
      write(
        outcome.status,
        render({
          kind: 'error',
          message: outcome.message,
          back: outcome.status === 401 ? { kind: 'signin' } : { kind: 'page', group },
        }),
        undefined,
        outcome.retryAfter,
      );
    const path = binding.target.split('?', 1)[0]!;
    const posted = binding.method === 'POST' ? route(path) : null;
    if (
      !(
        (path === '/account/data' && ['GET', 'HEAD'].includes(binding.method)) ||
        (binding.method === 'POST' && posted !== null)
      )
    ) {
      write(404);
      return;
    }
    try {
      const query = bffQuery(
        binding.target,
        posted ? [] : ['cursor', 'group', 'scopeCursor', 'step', 'result'],
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
      const browser = cookies.get(BFF_COOKIES.browser);
      const owned = (family: string): SessionBinding | null => {
        const handle = cookies.get(family);
        return handle && browser
          ? {
              host: binding.host,
              handle,
              browser,
              traceparent: headers.get('traceparent') ?? undefined,
            }
          : null;
      };
      const entryOwner = owned(BFF_COOKIES.entry);
      const sessionOwner = owned(BFF_COOKIES.session);
      // A posted action names its mode by path; a page prefers the entry owner, which exists only during entry.
      const candidates: readonly (readonly ['session' | 'entry', SessionBinding | null])[] = posted
        ? [[posted.mode, posted.mode === 'entry' ? entryOwner : sessionOwner]]
        : [
            ['entry', entryOwner],
            ['session', sessionOwner],
          ];
      if (!candidates.some(([, owner]) => owner !== null)) {
        if (posted) refuse({ status: 401, message: 'error.unauthorized', retryAfter: null });
        else write(200, render({ kind: 'signin' }));
        return;
      }
      const pinned = input.responseLease(binding);
      lease = pinned;
      let mode: 'session' | 'entry' | undefined;
      let owner: SessionBinding | undefined;
      let retention: ReturnType<BffSessionStore['retainDataRequests']> | undefined;
      let refusal: unknown;
      for (const [candidateMode, candidate] of candidates) {
        if (!candidate) continue;
        try {
          retention = input.store.retainDataRequests(candidate, pinned, candidateMode);
          mode = candidateMode;
          owner = candidate;
          break;
        } catch (error) {
          // A stale entry cookie gives way to a live session; any other failure is the answer.
          if (!(error instanceof BffSessionError) || !['revoked', 'expired'].includes(error.reason))
            throw error;
          refusal = error;
        }
      }
      if (!retention || !mode || !owner) throw refusal ?? new BffSessionError('revoked');
      const csrf = retention.csrfToken;
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
      const currentMode = mode;
      const currentOwner = owner;
      const own = (operation: OwnDataRequestOperation) =>
        input.store.ownDataRequest(currentOwner, binding.peer, operation, csrf, pinned.signal);
      const retained = (operation: RetainedDataRequestOperation) =>
        input.store.retainedDataRequest(currentOwner, binding.peer, operation, csrf, pinned.signal);

      /** ADR-0107 § 4: mint, then relay every chunk in order as one attachment, or refuse before any byte. */
      const download = async (requestId: string) => {
        const retainedGroup = group!;
        let begun = false;
        const sink: DataExportSink = {
          begin(byteCount) {
            pinned.publish({ kind: 'data_request_result', status: 200 });
            published = true;
            begun = true;
            response.writeHead(200, {
              'content-type': 'application/json; charset=utf-8',
              'content-disposition': 'attachment; filename="data-export.json"',
              'content-length': String(byteCount),
              'cache-control': 'no-store',
              'referrer-policy': 'no-referrer',
              'x-content-type-options': 'nosniff',
              'x-frame-options': 'DENY',
              'content-security-policy': "default-src 'none'; sandbox; frame-ancestors 'none'",
            });
          },
          write: (bytes) =>
            new Promise<void>((resolve, reject) => {
              const closed = () => reject(new Error('Data export response closed'));
              response.once('close', closed);
              response.write(bytes, (error) => {
                response.removeListener('close', closed);
                if (error) reject(error);
                else resolve();
              });
            }),
          end: () =>
            new Promise<void>((resolve, reject) => {
              const closed = () => reject(new Error('Data export response closed'));
              response.once('close', closed);
              response.end(() => {
                response.removeListener('close', closed);
                resolve();
              });
            }),
          abort() {
            response.destroy();
          },
        };
        const outcome = await relayDataExport({
          mint: () =>
            currentMode === 'session'
              ? own({ kind: 'mintOwnDataExportTicket', id: requestId })
              : retained({
                  kind: 'mintRetainedDataExportTicket',
                  group: retainedGroup,
                  id: requestId,
                }),
          read: (ticket, index) =>
            currentMode === 'session'
              ? own({ kind: 'readOwnDataExportChunk', id: requestId, ticket, index })
              : retained({
                  kind: 'readRetainedDataExportChunk',
                  group: retainedGroup,
                  id: requestId,
                  ticket,
                  index,
                }),
          sink,
          signal: pinned.signal,
          now,
        });
        if (outcome.kind !== 'refused' || begun) return;
        const refused = dataRequestRefusal(outcome.result, false);
        // A ticket refusal has its own sentence (the limit, a gone file); a failed first chunk is "could not start".
        refuse(
          outcome.result === null ||
            (outcome.stage === 'chunk' && ![401, 429].includes(refused.status))
            ? { ...refused, message: 'error.download' }
            : refused,
        );
      };

      let cursor = query.get('cursor');
      let scopeCursor = query.get('scopeCursor');
      let step = query.get('step');
      const resultParameter = query.get('result');
      group = query.get('group');
      if (posted) {
        exactBffOrigin(http);
        const fields = await bffForm(http, ACTION_FIELDS[posted.action]);
        if (!bffSecretEquals(fields.get('csrfToken'), csrf)) throw new BffSessionError('csrf');
        group = fields.get('group');
        if (currentMode === 'entry' ? !groupValue(group) : group !== null)
          throw new BffInputError();
        const action = posted.action;
        if (action !== 'view') {
          const key = fields.get('idempotencyKey');
          const requestId = fields.get('requestId');
          const version = fields.get('version');
          if (
            (['export', 'erasure', 'closure', 'cancel', 'revoke'].includes(action) &&
              !opaque(key)) ||
            (['cancel', 'revoke', 'download'].includes(action) && !uuid(requestId)) ||
            (['cancel', 'revoke'].includes(action) && !versionValue(version))
          )
            throw new BffInputError();
          if (
            (action === 'erasure' && fields.get('confirm') !== 'erase') ||
            (action === 'closure' &&
              (fields.get('confirm') !== 'close' || fields.get('acknowledge') !== 'no_status'))
          ) {
            // A refused confirmation leads back to the confirmation step it came from, not to the list.
            write(
              400,
              render({
                kind: 'error',
                message: 'error.confirmation',
                back: { kind: 'page', group, step: action },
              }),
            );
            return;
          }
          if (action === 'download') {
            await download(requestId!);
            return;
          }
          const retainedGroup = group!;
          let operation: OwnDataRequestOperation | RetainedDataRequestOperation;
          const idempotencyKey = key!;
          if (currentMode === 'session')
            operation =
              action === 'export'
                ? { kind: 'createOwnDataExport', idempotencyKey }
                : action === 'erasure'
                  ? { kind: 'createOwnDataErasure', idempotencyKey }
                  : action === 'closure'
                    ? { kind: 'createOwnAccountClosure', idempotencyKey }
                    : {
                        kind: action === 'cancel' ? 'cancelOwnDataRequest' : 'revokeOwnDataExport',
                        id: requestId!,
                        version: version!,
                        idempotencyKey,
                      };
          else
            operation =
              action === 'export'
                ? { kind: 'createRetainedDataExport', group: retainedGroup, idempotencyKey }
                : action === 'erasure'
                  ? { kind: 'createRetainedDataErasure', group: retainedGroup, idempotencyKey }
                  : action === 'closure'
                    ? { kind: 'createRetainedAccountClosure', group: retainedGroup, idempotencyKey }
                    : {
                        kind:
                          action === 'cancel'
                            ? 'cancelRetainedDataRequest'
                            : 'revokeRetainedDataExport',
                        group: retainedGroup,
                        id: requestId!,
                        version: version!,
                        idempotencyKey,
                      };
          mutation = true;
          const outcome =
            currentMode === 'session'
              ? await own(operation as OwnDataRequestOperation)
              : await retained(operation as RetainedDataRequestOperation);
          lease.signal.throwIfAborted();
          if (outcome.kind !== 'success') {
            refuse(dataRequestRefusal(outcome, true));
            return;
          }
          const result: DataRequestResult =
            action === 'cancel' ? 'cancelled' : action === 'revoke' ? 'revoked' : action;
          const destination = new URLSearchParams({
            ...(currentMode === 'entry' ? { group: retainedGroup } : {}),
            result,
          });
          write(303, '', '/account/data?' + destination.toString());
          return;
        }
        // A posted view selects the group by a proved form, as the consent entry does; its history guard then records
        // only that group's fixed page, so a reload reopens the group and never a cursor, step or result.
        cursor = null;
        scopeCursor = null;
        step = null;
      } else if (resultParameter !== null && !RESULTS.has(resultParameter as DataRequestResult))
        throw new BffInputError();
      if (
        (step !== null && step !== 'erasure' && step !== 'closure') ||
        (cursor !== null && !sealed(cursor)) ||
        (scopeCursor !== null && !sealed(scopeCursor)) ||
        (currentMode === 'session' && (group !== null || scopeCursor !== null)) ||
        (currentMode === 'entry' &&
          (group === null
            ? cursor !== null || step !== null || resultParameter !== null
            : !groupValue(group) || scopeCursor !== null))
      )
        throw new BffInputError();
      let scopes: RetainedDataRequestScopePage | null = null;
      let requests: OwnDataRequestPage | null = null;
      if (currentMode === 'entry' && group === null) {
        const read = await retained({
          kind: 'listRetainedDataRequestScopes',
          limit: 20,
          ...(scopeCursor ? { cursor: scopeCursor } : {}),
        });
        if (read.kind !== 'success') {
          refuse(dataRequestRefusal(read, false));
          return;
        }
        scopes = read.data as RetainedDataRequestScopePage;
      } else {
        const read =
          currentMode === 'session'
            ? await own({ kind: 'listOwnDataRequests', limit: 20, ...(cursor ? { cursor } : {}) })
            : await retained({
                kind: 'listRetainedDataRequests',
                group: group!,
                limit: 20,
                ...(cursor ? { cursor } : {}),
              });
        if (read.kind !== 'success') {
          refuse(dataRequestRefusal(read, false));
          return;
        }
        requests = read.data as OwnDataRequestPage;
      }
      const snapshot: NativeDataRequestSnapshot = lease.publish({
        kind: 'data_requests',
        mode: currentMode,
        scope: retention.scope,
        group: currentMode === 'entry' ? group : null,
        scopes,
        requests,
        step: step as 'erasure' | 'closure' | null,
        result: posted ? null : (resultParameter as DataRequestResult | null),
        preparedAt: new Date(now()).toISOString(),
        entryExpiresAt:
          retention.expiresAt === null ? null : new Date(retention.expiresAt).toISOString(),
        csrfToken: csrf,
        // One unused key per keyed form: the export, the confirmation step, and a revoke and cancel per request.
        formKeys: Array.from({ length: 2 + 2 * (requests?.items.length ?? 0) }, () =>
          randomBytes(32).toString('base64url'),
        ),
      });
      published = true;
      if (snapshot.kind !== 'data_requests') throw new BffSessionError('response');
      lease.signal.throwIfAborted();
      write(200, render({ kind: 'page', page: snapshot }));
    } catch (error) {
      if (response.headersSent) {
        response.destroy();
        return;
      }
      if (error instanceof BffInputError) {
        refuse({ status: 400, message: 'error.input', retryAfter: null });
        return;
      }
      if (error instanceof BffSessionError && error.reason === 'csrf') {
        refuse({ status: 403, message: 'error.csrf', retryAfter: null });
        return;
      }
      if (error instanceof BffSessionError && ['revoked', 'expired'].includes(error.reason)) {
        refuse({ status: 401, message: 'error.unauthorized', retryAfter: null });
        return;
      }
      if (error instanceof SnapshotAdmissionError) {
        refuse({
          status: error.status,
          message: error.status === 429 ? 'error.busy' : 'error.unavailable',
          retryAfter: 30,
        });
        return;
      }
      refuse({
        status: 503,
        message: mutation ? 'error.unconfirmed' : 'error.unavailable',
        retryAfter: 30,
      });
    }
  };
}
