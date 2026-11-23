import type { DataExportChunk, DataExportTicket } from '@learnstack/sdk/administration-server';

import type { NativeOperationResult } from './native-operation-contract.js';

/** ADR-0107 § 4: every chunk but the last holds exactly this much plaintext. */
const CHUNK_BYTES = 128 * 1024;
/** A ticket authorizes reads for fifteen minutes; no relay outlives it. */
const TICKET_LIFETIME_MS = 15 * 60 * 1000;
/**
 * The API answers every chunk refusal with the same `404`. A ticket is bound to the credential that minted it, so a
 * refresh during a long download ends that ticket and the next chunk answers `404`; a chunk the API cannot open
 * answers the same. The OpenAPI document's recovery is a new ticket resumed at the failed index: the relay does that
 * for any `404`, at most this many times, never for a refusal of the first mint.
 */
const MAXIMUM_RESUMES = 2;
/**
 * Every API call is one request under the visitor's admission budget, so a large export meets `429` part way. A `429`,
 * or a `503` that names its `Retry-After`, is a pause, not a failure: the API refused it before reading the ticket or
 * rolled the read back, so the relay waits and repeats the same call (a chunk read with the same index and ticket, a
 * renewal as a new mint). A missing or unusable `Retry-After` on a `429` waits the default, `0` still waits a second,
 * and no pause exceeds the one-minute window.
 */
const MINIMUM_PAUSE_MS = 1_000;
const DEFAULT_PAUSE_MS = 5_000;
const MAXIMUM_PAUSE_MS = 60_000;
/** A write or end that makes no progress for this long destroys the connection, like any failure after the headers. */
export const DATA_EXPORT_STALL_MS = 60_000;

/** The response the relay owns once the first chunk is proven: headers, ordered writes, end or destruction. */
export type DataExportSink = Readonly<{
  begin: (byteCount: number) => void;
  /** Resolves after the bytes are handed to the socket; rejects when the response can take no more. */
  write: (bytes: Buffer) => Promise<void>;
  end: () => Promise<void>;
  /** Destroys the connection: a short body never completes, so no partial file reads as finished. */
  abort: () => void;
}>;
export type DataExportRelayOutcome =
  | Readonly<{ kind: 'completed' }>
  /** Refused before any byte: the caller still owns an ordinary document response. */
  | Readonly<{ kind: 'refused'; stage: 'ticket' | 'chunk'; result: NativeOperationResult | null }>
  /** Failed after the headers: the connection was destroyed. */
  | Readonly<{ kind: 'aborted' }>;

/** The wait a refusal asks for, or null when it is a failure rather than a pause. */
function pauseFor(result: NativeOperationResult): number | null {
  if (result.kind !== 'api-error' || (result.status !== 429 && result.status !== 503)) return null;
  const seconds =
    'retryAfter' in result.error &&
    typeof result.error.retryAfter === 'number' &&
    Number.isSafeInteger(result.error.retryAfter) &&
    result.error.retryAfter >= 0
      ? result.error.retryAfter
      : null;
  if (seconds === null) return result.status === 429 ? DEFAULT_PAUSE_MS : null;
  return Math.min(MAXIMUM_PAUSE_MS, Math.max(MINIMUM_PAUSE_MS, seconds * 1000));
}

/** Resolves after `ms`, or at once when the signal aborts; it never holds the process open. */
function wait(ms: number, signal: AbortSignal): Promise<void> {
  return new Promise<void>((resolve) => {
    if (signal.aborted) {
      resolve();
      return;
    }
    // `done` runs only after `timer` is assigned: from the timer itself, or from a later abort.
    const done = () => {
      clearTimeout(timer);
      signal.removeEventListener('abort', done);
      resolve();
    };
    const timer = setTimeout(done, ms);
    timer.unref();
    signal.addEventListener('abort', done, { once: true });
  });
}

/**
 * Mints a ticket, reads the chunks in index order and relays them as one body, one chunk in flight. Each chunk is
 * proven (index, count, exact size) before a byte of it is written, and its decoded buffer is zeroed after the write
 * settles. Nothing outlives the deadline: the ticket's expiry, fifteen minutes, or `ownerDeadline` (the instant the
 * response's owner ends, in `now`'s clock), whichever is first. The ticket lives only in this closure: it never
 * reaches a header, URL, page or log.
 */
export async function relayDataExport(
  input: Readonly<{
    mint: () => Promise<NativeOperationResult>;
    read: (ticket: string, index: number) => Promise<NativeOperationResult>;
    sink: DataExportSink;
    signal: AbortSignal;
    now: () => number;
    ownerDeadline?: number;
  }>,
): Promise<DataExportRelayOutcome> {
  const deadlineOf = (granted: DataExportTicket) =>
    Math.min(
      Date.parse(granted.expiresAt),
      input.now() + TICKET_LIFETIME_MS,
      input.ownerDeadline ?? Number.POSITIVE_INFINITY,
    );
  // The first mint pauses for nothing: no ticket is held yet, so its refusal is the person's answer.
  const minted = await input.mint();
  if (minted.kind !== 'success') return { kind: 'refused', stage: 'ticket', result: minted };
  const first = minted.data as DataExportTicket;
  let ticket = first.ticket;
  const count = first.chunkCount;
  const byteCount = first.byteCount;
  let deadline = deadlineOf(first);
  if (count !== Math.ceil(byteCount / CHUNK_BYTES) || Number.isNaN(deadline))
    return { kind: 'refused', stage: 'ticket', result: null };
  // Before the headers the browser waits with nothing to show: the download starts within one pause window, or the
  // person gets the refusal document instead.
  const startBy = input.now() + MAXIMUM_PAUSE_MS;
  let begun = false;
  let resumes = 0;
  const fail = (result: NativeOperationResult | null): DataExportRelayOutcome => {
    if (!begun) return { kind: 'refused', stage: 'chunk', result };
    input.sink.abort();
    return { kind: 'aborted' };
  };
  /**
   * One API call, repeated after each pause it asks for. A pause that would reach the deadline (or, before the
   * headers, the start bound) is not taken: the refusal is returned at once. Null means the response was cancelled.
   */
  const call = async (
    operation: () => Promise<NativeOperationResult>,
  ): Promise<NativeOperationResult | null> => {
    for (;;) {
      const result = await operation();
      if (input.signal.aborted) return null;
      const pause = pauseFor(result);
      const limit = begun ? deadline : Math.min(deadline, startBy);
      if (pause === null || input.now() + pause >= limit) return result;
      await wait(pause, input.signal);
      if (input.signal.aborted) return null;
    }
  };
  /**
   * Settles one sink operation, or reports that it made no progress within the stall bound, before the deadline or,
   * when given, before `cancel` aborts.
   */
  const progress = async (operation: Promise<void>, cancel?: AbortSignal): Promise<boolean> => {
    // A write still pending when the connection is destroyed rejects later; that rejection is expected.
    operation.catch(() => undefined);
    const bound = Math.min(DATA_EXPORT_STALL_MS, deadline - input.now());
    if (bound <= 0) return false;
    const settled = new AbortController();
    try {
      return await Promise.race([
        operation.then(() => true),
        wait(bound, cancel ? AbortSignal.any([settled.signal, cancel]) : settled.signal).then(
          () => false,
        ),
      ]);
    } finally {
      settled.abort();
    }
  };
  for (let index = 0; index < count; ) {
    if (input.signal.aborted || input.now() >= deadline) return fail(null);
    const read = await call(() => input.read(ticket, index));
    if (read === null) return fail(null);
    if (read.kind !== 'success') {
      if (read.kind !== 'api-error' || read.status !== 404 || resumes >= MAXIMUM_RESUMES)
        return fail(read);
      resumes += 1;
      const renewed = await call(() => input.mint());
      if (renewed === null) return fail(null);
      // A refused renewal is the ticket's own answer (the file is gone, or a bound is reached).
      if (renewed.kind !== 'success')
        return begun ? fail(renewed) : { kind: 'refused', stage: 'ticket', result: renewed };
      const next = renewed.data as DataExportTicket;
      const renewedDeadline = deadlineOf(next);
      // A resumed download continues the same bundle only: the geometry must be the one already promised.
      if (
        next.chunkCount !== count ||
        next.byteCount !== byteCount ||
        Number.isNaN(renewedDeadline)
      )
        return fail(null);
      ticket = next.ticket;
      deadline = renewedDeadline;
      continue;
    }
    const chunk = read.data as DataExportChunk;
    const expected = index < count - 1 ? CHUNK_BYTES : byteCount - (count - 1) * CHUNK_BYTES;
    if (chunk.index !== index || chunk.count !== count) return fail(null);
    const bytes = Buffer.from(chunk.bytes, 'base64');
    if (bytes.length !== expected) {
      bytes.fill(0);
      return fail(null);
    }
    try {
      if (!begun) {
        input.sink.begin(byteCount);
        begun = true;
      }
      if (!(await progress(input.sink.write(bytes), input.signal))) return fail(null);
    } catch {
      begun = true;
      return fail(null);
    } finally {
      // After a destroyed connection too: the socket no longer sends what it still held.
      bytes.fill(0);
    }
    index += 1;
  }
  try {
    // A finished response ends its own lease, so the end is judged by its own settlement and not by the signal: a
    // response that closes instead rejects it.
    if (!(await progress(input.sink.end()))) return fail(null);
  } catch {
    return fail(null);
  }
  return { kind: 'completed' };
}
