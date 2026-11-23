import type { IncomingMessage } from 'node:http';

import type { OwnDataRequest } from '@learnstack/sdk/administration-server';
import { vi } from 'vitest';

import { bffFixture, bffHost, bffSource } from './bff-fixture';
import { snapshotRequest, SnapshotWire } from './page-response-fixture';
import en from '../i18n/messages/en/data.json';
import tr from '../i18n/messages/tr/data.json';
import {
  createNativeAdministrationCaller,
  createNativeRetainedDataRequestCaller,
} from '../server/auth/native-administration-client';
import { createNativeDataRequestHandler } from '../server/auth/native-data-request-http';
import type {
  NativeAdministrationCaller,
  NativeOperationResult,
  NativeRetainedDataRequestCaller,
} from '../server/auth/native-operation-contract';

/**
 * The person's own data-request page, driven through the real native handler, the real session store and the real
 * request runtime. The API behind it is either a canned operation mock (the default) or the real native callers and
 * SDK over a fake `fetch` (`transport`), as the administrator's route test does.
 */
export const id = '019a154b-0000-7000-8000-000000000041';
export const second = '019a154b-0000-7000-8000-000000000042';
export const organization = '019a154b-0000-7000-8000-0000000000a1';
export const ticket = 'T'.repeat(42) + 'A';
export const now = Date.parse('2026-11-23T12:00:00Z');
export const pageInfo = {
  nextCursor: null,
  previousCursor: null,
  hasNext: false,
  hasPrevious: false,
};
export const configuration = {
  apiOrigin: 'http://127.0.0.1:5100',
  secret: 'synthetic-person-data-request-hop-'.padEnd(48, 'x'),
  nativeSourceSecret: Buffer.from(Array.from({ length: 32 }, (_, index) => index + 61)).toString(
    'base64url',
  ),
};
export const ok = (data: unknown, extra: Partial<{ etag: string; location: string }> = {}) =>
  ({
    kind: 'success',
    status: 200,
    data,
    etag: extra.etag ?? null,
    location: extra.location ?? null,
    replayed: false,
  }) as NativeOperationResult;
export const refused = (status: number, code: string, errors?: Record<string, { key: string }[]>) =>
  ({
    kind: 'api-error',
    status,
    error: { code: 'unknown', correlationId: 'c' },
    problem: {
      type: 'https://errors.learnstack.dev/' + code,
      title: 'Refused',
      status,
      instance: '/api/v1/identity/me/data-requests',
      code,
      messageKey: 'lockey_' + code,
      correlationId: 'c',
      ...(errors ? { errors } : {}),
    },
  }) as NativeOperationResult;
export const artifact = (value: Partial<NonNullable<OwnDataRequest['artifact']>> = {}) => ({
  id: second,
  status: 'available' as const,
  expiresAt: '2026-11-30T10:00:00.0000000+00:00',
  purgedAt: null,
  byteCount: 131073,
  chunkCount: 2,
  truncations: [],
  version: '9007199254740993',
  readable: true,
  ...value,
});
export const request = (value: Partial<OwnDataRequest> = {}): OwnDataRequest => ({
  id,
  kind: 'export',
  origin: 'self',
  status: 'requested',
  createdAt: '2026-11-23T10:00:00.0000000+00:00',
  updatedAt: null,
  completedAt: null,
  lastRefusal: null,
  failureReason: null,
  retained: [],
  artifact: null,
  version: '3',
  executeAt: null,
  ...value,
});
export const chunkBytes = (index: number) =>
  Buffer.alloc(index === 0 ? 131072 : 1, index === 0 ? 0x61 : 0x62);

/**
 * A transport sink that takes each written buffer only when it acknowledges the write, a microtask later, as a socket
 * does: the relay zeroes the buffer after the write settles, so one zeroed early would arrive here as zeros.
 */
export class DownloadWire extends SnapshotWire {
  readonly bytes: Buffer[] = [];
  override write(chunk: string, ...args: unknown[]) {
    this.flushHeaders();
    const callback = args[args.length - 1];
    queueMicrotask(() => {
      if (typeof chunk === 'string') this.chunks.push(chunk);
      else this.bytes.push(Buffer.from(chunk as unknown as Buffer));
      if (typeof callback === 'function') Reflect.apply(callback, this, []);
    });
    return true;
  }
  override end(...args: unknown[]) {
    const callback = args[args.length - 1];
    const result = super.end(...args.filter((value) => typeof value !== 'function'));
    if (typeof callback === 'function') queueMicrotask(() => Reflect.apply(callback, this, []));
    return result;
  }
}

const closes: (() => Promise<void>)[] = [];
/** Registered by each test file with `afterEach`; stops every fixture it created. */
export async function closeDataRequestFixtures() {
  for (const close of closes.splice(0).reverse()) await close();
}

const OWN_OPERATIONS = [
  'listOwnDataRequests',
  'createOwnDataExport',
  'createOwnDataErasure',
  'createOwnAccountClosure',
  'cancelOwnDataRequest',
  'revokeOwnDataExport',
  'mintOwnDataExportTicket',
  'readOwnDataExportChunk',
];

/**
 * `session` is a live session only, `entry` the retained entry only, and `both` the two cookie families a browser
 * holds when it signs in again while its session is still live: an entry owner whose replacement is that session.
 */
export async function fixture(
  mode: 'session' | 'entry' | 'both',
  page: readonly OwnDataRequest[] = [],
  options: { transport?: typeof fetch } = {},
) {
  const own = vi.fn<
    (operation: Parameters<NativeAdministrationCaller>[2]) => NativeOperationResult
  >((operation) => {
    switch (operation.kind) {
      case 'listOwnDataRequests':
        return ok({ items: page, pageInfo });
      case 'mintOwnDataExportTicket':
        return ok({ ticket, expiresAt: '2026-11-23T12:15:00Z', chunkCount: 2, byteCount: 131073 });
      case 'readOwnDataExportChunk':
        return ok({
          index: operation.index,
          count: 2,
          bytes: chunkBytes(operation.index).toString('base64'),
        });
      case 'createOwnDataExport':
        return ok({ id, kind: 'export', status: 'requested', version: '0' });
      default:
        return ok({ version: '4' });
    }
  });
  const canned = vi.fn<NativeRetainedDataRequestCaller>(async (_token, _binding, operation) => {
    switch (operation.kind) {
      case 'listRetainedDataRequestScopes':
        return ok({
          items: [
            { tenantId: id, organizationId: null },
            { tenantId: id, organizationId: organization },
          ],
          pageInfo,
        });
      case 'listRetainedDataRequests':
        return ok({ items: page, pageInfo });
      case 'mintRetainedDataExportTicket':
        return ok({ ticket, expiresAt: '2026-11-23T12:15:00Z', chunkCount: 2, byteCount: 131073 });
      case 'readRetainedDataExportChunk':
        return ok({
          index: operation.index,
          count: 2,
          bytes: chunkBytes(operation.index).toString('base64'),
        });
      default:
        return ok({ version: '4' });
    }
  });
  const real = options.transport;
  const retained = real
    ? createNativeRetainedDataRequestCaller(configuration, real)
    : (canned as NativeRetainedDataRequestCaller);
  const f = bffFixture({ retainedDataRequest: retained });
  const original = f.administration.getMockImplementation()!;
  const session = real ? createNativeAdministrationCaller(configuration, real) : null;
  f.administration.mockImplementation(async (token, binding, operation, signal) =>
    OWN_OPERATIONS.includes(operation.kind)
      ? session
        ? session(token, binding, operation, signal)
        : own(operation)
      : original(token, binding, operation, signal),
  );
  const sessionOwner = mode === 'entry' ? null : await f.login();
  const entryOwner =
    mode === 'session'
      ? null
      : await f.enter(
          sessionOwner
            ? {
                browser: sessionOwner.finished.browser,
                handle: sessionOwner.finished.handle,
              }
            : {},
        );
  const sessionCsrf = sessionOwner ? f.store.peek(sessionOwner.binding)!.csrfToken : null;
  const entryCsrf = entryOwner ? entryOwner.finished.entry.csrfToken : null;
  const cookie =
    mode === 'both'
      ? '__Host-ls-entry=' +
        entryOwner!.finished.handle +
        '; __Host-ls-session=' +
        sessionOwner!.finished.handle +
        '; __Host-ls-browser=' +
        sessionOwner!.finished.browser
      : (sessionOwner ?? entryOwner)!.cookie;
  // The proof of the owner a bare request selects: the retained entry, else the live session.
  const csrf = (entryCsrf ?? sessionCsrf)!;
  f.account.mockClear();
  f.administration.mockClear();
  own.mockClear();
  const handler = createNativeDataRequestHandler({
    catalogues: { en, tr },
    store: f.store,
    responseLease: f.requestRuntime.nativeDataRequestResponse,
    now: () => now,
  });
  closes.push(async () => {
    await f.requestRuntime.stop();
    await f.store.stop();
    f.publicAdmission.shutdown();
  });
  const run = async (
    method: string,
    target: string,
    form?: Record<string, string> | URLSearchParams,
    runOptions: {
      /** Several values model a repeated `Origin` header a parser might hand over as a list. */
      origin?: string | readonly string[];
      headers?: Record<string, string>;
      wire?: SnapshotWire;
      request?: (incoming: IncomingMessage) => void;
    } = {},
  ) => {
    const incoming = snapshotRequest();
    runOptions.request?.(incoming);
    incoming.method = method;
    incoming.url = target;
    incoming.headers.cookie = cookie;
    Object.assign(incoming.headers, runOptions.headers ?? {});
    if (form) {
      incoming.headers.origin = (runOptions.origin ?? 'https://' + bffHost) as string;
      incoming.headers['content-type'] = 'application/x-www-form-urlencoded';
      incoming.complete = true;
      incoming.push(
        (form instanceof URLSearchParams ? form : new URLSearchParams(form)).toString(),
      );
      incoming.push(null);
    }
    const binding = { host: bffHost, peer: bffSource, method, target };
    const wire = runOptions.wire ?? new SnapshotWire();
    await f.publicAdmission.run(binding, incoming, wire.native, () =>
      f.requestRuntime.run(binding, incoming, wire.native, () =>
        handler(binding, incoming, wire.native),
      ),
    );
    wire.drainCallbacks();
    for (let turn = 0; turn < 16; ++turn) await Promise.resolve();
    return Object.assign(wire, { html: wire.chunks.join('') });
  };
  return {
    ...f,
    own,
    retained: canned,
    run,
    csrf,
    sessionCsrf,
    entryCsrf,
    session: sessionOwner,
    entry: entryOwner,
    owner: (sessionOwner ?? entryOwner)!,
    cookie,
  };
}
export const count = (html: string, pattern: RegExp) => [...html.matchAll(pattern)].length;
/** The renderer escapes every printed value; authored copy is compared as it appears in the document. */
export const shown = (text: string) => text.replaceAll('&', '&amp;').replaceAll("'", '&#39;');
export const formKey = (html: string) =>
  /name="idempotencyKey" value="([A-Za-z0-9_-]{43})"/.exec(html)![1]!;
/** Every unused form key a page carries, in document order. */
export const formKeys = (html: string) =>
  [...html.matchAll(/name="idempotencyKey" value="([A-Za-z0-9_-]{43})"/g)].map((m) => m[1]!);
