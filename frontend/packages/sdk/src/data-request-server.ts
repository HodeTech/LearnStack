import type {
  AdministrationCreateOptions,
  AdministrationFetch,
  AdministrationPageOptions,
  AdministrationReadOptions,
  AdministrationResult,
  AdministrationVersion,
  AdministrationWriteOptions,
} from './administration-contract.js';
import { identifier, object, text, version } from './administration-policy.js';
import type {
  DataExportChunk,
  DataExportTicket,
  DataRequestAccepted,
  DataRequestGroup,
  OwnDataRequest,
  OwnDataRequestPage,
  RetainedDataRequestScopePage,
} from './data-request-contract.js';
import {
  dataExportChunkResponse,
  dataExportTicket,
  dataExportTicketResponse,
  dataRequestAccepted,
  dataRequestGroup,
  ownDataRequest,
  ownDataRequestPage,
  retainedScopePage,
} from './data-request-policy.js';
import { appError, parseProblem } from './response-policy.js';
export type * from './data-request-contract.js';

const own = '/api/v1/identity/me/data-requests';
const entry = '/api/v1/identity/data-request-entry';
const maximumBody = 256 * 1024;

class BodyLimit extends Error {
  constructor() {
    super('Data request response refused');
  }
}
/** Reads one bounded UTF-8 body. Every raw buffer is zeroed: a chunk body is decrypted plaintext. */
async function read(response: Response, signal?: AbortSignal): Promise<unknown> {
  const reader = response.body?.getReader();
  if (!reader) throw new SyntaxError('Data request response refused');
  let cancelling: Promise<void> | undefined;
  const cancel = () => {
    cancelling ??= reader.cancel().catch(() => undefined);
  };
  signal?.addEventListener('abort', cancel, { once: true });
  const chunks: Uint8Array[] = [];
  let bytes: Uint8Array | undefined;
  let size = 0;
  try {
    if (signal?.aborted) cancel();
    for (;;) {
      const chunk = await reader.read();
      if (signal?.aborted) {
        chunk.value?.fill(0);
        throw new DOMException('Cancelled', 'AbortError');
      }
      if (chunk.done) break;
      size += chunk.value.byteLength;
      if (size > maximumBody) {
        chunk.value.fill(0);
        cancel();
        throw new BodyLimit();
      }
      chunks.push(chunk.value);
    }
    bytes = new Uint8Array(size);
    let offset = 0;
    for (const chunk of chunks) {
      bytes.set(chunk, offset);
      offset += chunk.byteLength;
    }
    try {
      return JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(bytes)) as unknown;
    } catch {
      throw new SyntaxError('Data request response refused');
    }
  } finally {
    bytes?.fill(0);
    for (const chunk of chunks) chunk.fill(0);
    signal?.removeEventListener('abort', cancel);
    if (cancelling) await cancelling;
    reader.releaseLock();
  }
}

type Plan = Readonly<{
  path: string | undefined;
  method: 'GET' | 'POST';
  body?: unknown;
  /** Present for the keyed creations, cancellations and revocations; never for a ticket or chunk. */
  idempotencyKey?: string;
  /** A strong precondition: the request's version, or for a revocation the artifact's. */
  version?: string;
  /** ETag rule of the success: the body's own `version`, or none. */
  etag: 'version' | 'none';
  /** The exact Location of an own creation; every other success carries none. */
  location?: (value: Record<string, unknown>) => string;
}>;

/**
 * ADR-0106 § 9 and ADR-0107 § 4: the subject's own and retained-entry data-request operations. The injected native
 * caller owns credentials, host, scope and lifetime; nothing here selects a tenant, organization or credential.
 */
export function createDataRequestServerSdk(transport: AdministrationFetch) {
  async function request<T, Status extends 200 | 202 = 200>(
    plan: Plan,
    valid: (value: unknown) => boolean,
    expected: Status,
    options?: AdministrationReadOptions,
  ): Promise<AdministrationResult<T, Status>> {
    const signal = options?.signal;
    if (signal?.aborted) return { kind: 'cancelled' };
    const headers: Record<string, string> = { Accept: 'application/json' };
    let body: string | undefined;
    try {
      if (!plan.path) return { kind: 'invalid-request' };
      if (plan.method === 'POST') {
        body = JSON.stringify(plan.body ?? {});
        if (new TextEncoder().encode(body).length > 16 * 1024) return { kind: 'invalid-request' };
        headers['Content-Type'] = 'application/json';
      }
      if (plan.idempotencyKey !== undefined) {
        if (!/^[\x21-\x7e]{8,128}$/.test(plan.idempotencyKey)) return { kind: 'invalid-request' };
        headers['Idempotency-Key'] = plan.idempotencyKey;
      }
      if (plan.version !== undefined) {
        if (!version(plan.version)) return { kind: 'invalid-request' };
        headers['If-Match'] = '"' + plan.version + '"';
      }
    } catch {
      return { kind: 'invalid-request' };
    }
    let response: Response;
    try {
      response = await transport(plan.path, {
        method: plan.method,
        cache: 'no-store',
        headers,
        ...(body === undefined ? {} : { body }),
        signal,
      });
    } catch {
      return { kind: signal?.aborted ? 'cancelled' : 'transport-error' };
    }
    if (response.status !== expected && response.status < 400) {
      await response.body?.cancel().catch(() => undefined);
      return signal?.aborted
        ? { kind: 'cancelled' }
        : { kind: 'invalid-response', reason: 'unexpected-status' };
    }
    let value: unknown;
    try {
      value = await read(response, signal);
    } catch (error) {
      if (signal?.aborted) return { kind: 'cancelled' };
      if (error instanceof BodyLimit)
        return {
          kind: 'invalid-response',
          reason: response.status === expected ? 'invalid-success' : 'invalid-problem',
        };
      return { kind: 'invalid-response', reason: 'malformed-json' };
    }
    if (signal?.aborted) return { kind: 'cancelled' };
    if (response.status !== expected) {
      const problem = parseProblem(value, response.status);
      if (!problem) return { kind: 'invalid-response', reason: 'invalid-problem' };
      const error = appError(problem, response.headers.get('Retry-After'));
      return {
        kind: 'api-error',
        status: response.status,
        problem,
        error: error.code === 'concurrency_conflict' ? { code: 'concurrency_conflict' } : error,
      };
    }
    const etag = response.headers.get('ETag');
    const location = response.headers.get('Location');
    const replayed = response.headers.get('Idempotency-Replayed');
    if (
      response.headers.get('Content-Type')?.split(';', 1)[0]?.trim().toLowerCase() !==
        'application/json' ||
      response.headers.get('Cache-Control') !== 'no-store' ||
      !valid(value) ||
      !object(value) ||
      (plan.etag === 'version' ? etag !== '"' + String(value.version) + '"' : etag !== null) ||
      location !== (plan.location ? plan.location(value) : null) ||
      (replayed !== null && (plan.idempotencyKey === undefined || replayed !== 'true'))
    )
      return { kind: 'invalid-response', reason: 'invalid-success' };
    return {
      kind: 'success',
      status: expected,
      data: value as T,
      etag,
      location,
      replayed: replayed === 'true',
    };
  }
  const page = (
    path: string,
    options: AdministrationPageOptions | undefined,
    limit: number,
    cursor: number,
    group?: string,
  ) => {
    if (
      (group !== undefined && !dataRequestGroup(group)) ||
      (options?.cursor !== undefined &&
        (!text(options.cursor, cursor) || !/^[\x21-\x7e]+$/.test(options.cursor))) ||
      (options?.limit !== undefined &&
        (!Number.isSafeInteger(options.limit) || options.limit < 1 || options.limit > limit))
    )
      return undefined;
    const query = new URLSearchParams();
    if (group !== undefined) query.set('group', group);
    if (options?.cursor !== undefined) query.set('cursor', options.cursor);
    if (options?.limit !== undefined) query.set('limit', String(options.limit));
    return path + (query.size ? '?' + query.toString() : '');
  };
  const resource = (id: string, suffix = '') =>
    identifier(id) ? own + '/' + id + suffix : undefined;
  const grouped = (group: DataRequestGroup, action: string) =>
    dataRequestGroup(group)
      ? entry + '/' + action + '?' + new URLSearchParams({ group }).toString()
      : undefined;
  const target = (id: string, extra: Record<string, unknown> = {}) =>
    identifier(id) ? { requestId: id, ...extra } : undefined;
  const receipt = (value: unknown) =>
    object(value) && Object.keys(value).length === 1 && version(value.version);
  const create =
    (kind: 'export' | 'erasure' | 'closure', path: string | undefined, ownPath: boolean) =>
    (options: AdministrationCreateOptions) =>
      request<DataRequestAccepted, 202>(
        {
          path,
          method: 'POST',
          body: {},
          idempotencyKey: options.idempotencyKey,
          etag: 'version',
          ...(ownPath ? { location: (value) => own + '/' + String(value.id) } : {}),
        },
        (value) => dataRequestAccepted(value, kind),
        202,
        options,
      );
  const keyed = (path: string | undefined, body: unknown, options: AdministrationWriteOptions) =>
    request<AdministrationVersion>(
      {
        path: body === undefined ? undefined : path,
        method: 'POST',
        body,
        idempotencyKey: options.idempotencyKey,
        version: options.version,
        etag: 'version',
      },
      receipt,
      200,
      options,
    );
  const chunk = (
    path: string | undefined,
    body: Record<string, unknown> | undefined,
    options?: AdministrationReadOptions,
  ) =>
    request<DataExportChunk>(
      { path: body === undefined ? undefined : path, method: 'POST', body, etag: 'none' },
      dataExportChunkResponse,
      200,
      options,
    );
  const index = (value: number) => Number.isSafeInteger(value) && value >= 0 && value <= 511;
  return Object.freeze({
    listOwnDataRequests: (options?: AdministrationPageOptions) =>
      request<OwnDataRequestPage>(
        { path: page(own, options, 100, 1024), method: 'GET', etag: 'none' },
        (value) => ownDataRequestPage(value, 100),
        200,
        options,
      ),
    getOwnDataRequest: (id: string, options?: AdministrationReadOptions) =>
      request<OwnDataRequest>(
        { path: resource(id), method: 'GET', etag: 'version' },
        (value) => ownDataRequest(value) && (value as { id: string }).id === id,
        200,
        options,
      ),
    createOwnDataExport: create('export', own + '/exports', true),
    createOwnDataErasure: create('erasure', own + '/erasures', true),
    createOwnAccountClosure: create('closure', own + '/closures', true),
    cancelOwnDataRequest: (id: string, options: AdministrationWriteOptions) =>
      keyed(resource(id, '/cancellations'), {}, options),
    revokeOwnDataExport: (id: string, options: AdministrationWriteOptions) =>
      keyed(resource(id, '/artifact/revocations'), {}, options),
    mintOwnDataExportTicket: (id: string, options?: AdministrationReadOptions) =>
      request<DataExportTicket>(
        { path: resource(id, '/artifact/tickets'), method: 'POST', body: {}, etag: 'none' },
        dataExportTicketResponse,
        200,
        options,
      ),
    readOwnDataExportChunk: (
      id: string,
      ticket: string,
      chunkIndex: number,
      options?: AdministrationReadOptions,
    ) =>
      chunk(
        resource(id, '/artifact/chunks'),
        dataExportTicket(ticket) && index(chunkIndex) ? { ticket, index: chunkIndex } : undefined,
        options,
      ),
    listRetainedDataRequestScopes: (options?: AdministrationPageOptions) =>
      request<RetainedDataRequestScopePage>(
        { path: page(entry + '/scopes', options, 20, 36), method: 'GET', etag: 'none' },
        retainedScopePage,
        200,
        options,
      ),
    listRetainedDataRequests: (group: DataRequestGroup, options?: AdministrationPageOptions) =>
      request<OwnDataRequestPage>(
        { path: page(entry + '/requests', options, 20, 1024, group), method: 'GET', etag: 'none' },
        (value) => ownDataRequestPage(value, 20),
        200,
        options,
      ),
    createRetainedDataExport: (group: DataRequestGroup, options: AdministrationCreateOptions) =>
      create('export', grouped(group, 'exports'), false)(options),
    createRetainedDataErasure: (group: DataRequestGroup, options: AdministrationCreateOptions) =>
      create('erasure', grouped(group, 'erasures'), false)(options),
    createRetainedAccountClosure: (group: DataRequestGroup, options: AdministrationCreateOptions) =>
      create('closure', grouped(group, 'closures'), false)(options),
    cancelRetainedDataRequest: (
      group: DataRequestGroup,
      id: string,
      options: AdministrationWriteOptions,
    ) => keyed(grouped(group, 'cancellations'), target(id), options),
    revokeRetainedDataExport: (
      group: DataRequestGroup,
      id: string,
      options: AdministrationWriteOptions,
    ) => keyed(grouped(group, 'revocations'), target(id), options),
    mintRetainedDataExportTicket: (
      group: DataRequestGroup,
      id: string,
      options?: AdministrationReadOptions,
    ) => {
      const body = target(id);
      return request<DataExportTicket>(
        {
          path: body === undefined ? undefined : grouped(group, 'tickets'),
          method: 'POST',
          body,
          etag: 'none',
        },
        dataExportTicketResponse,
        200,
        options,
      );
    },
    readRetainedDataExportChunk: (
      group: DataRequestGroup,
      id: string,
      ticket: string,
      chunkIndex: number,
      options?: AdministrationReadOptions,
    ) =>
      chunk(
        grouped(group, 'chunks'),
        dataExportTicket(ticket) && index(chunkIndex)
          ? target(id, { ticket, index: chunkIndex })
          : undefined,
        options,
      ),
  });
}
export type DataRequestServerSdk = ReturnType<typeof createDataRequestServerSdk>;
