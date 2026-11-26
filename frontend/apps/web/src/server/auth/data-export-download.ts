import type { DataExportChunk, DataExportTicket } from '@learnstack/sdk/administration-server';

import type { NativeOperationResult } from './native-operation-contract.js';

/** ADR-0107 § 4: every chunk but the last holds exactly this much plaintext. */
const CHUNK_BYTES = 128 * 1024;
/** A ticket authorizes reads for fifteen minutes. */
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
/**
 * A bundle of 64 MiB is 512 chunk reads, and a visitor address that shares its budget with others can need longer than
 * one ticket's fifteen minutes. The relay therefore mints the next ticket this long before the current one ends (a read
 * and one whole pause always fit in it), at most `MAXIMUM_RENEWALS` times: a download is bounded by its owner's end and
 * by about three tickets, never by the first one's fifteen minutes. Renewals are counted apart from `MAXIMUM_RESUMES`,
 * which answer a `404`; the API admits three open tickets per credential and twenty per file, and one relay mints at
 * most five.
 */
const RENEW_AHEAD_MS = MAXIMUM_PAUSE_MS + 10_000;
const MAXIMUM_RENEWALS = 2;

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

/** Carries the outcome of a renewal that ended the relay out of the call that was waiting for it. */
class Finish extends Error {
  constructor(readonly outcome: DataExportRelayOutcome) {
    super('Data export relay finished');
  }
}

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
 * settles. Nothing outlives `ownerDeadline` (the instant the response's owner ends, in `now`'s clock); a ticket's own
 * fifteen minutes do not end the download, because the relay renews it before it ends (see `RENEW_AHEAD_MS`). The ticket
 * lives only in this closure: it never reaches a header, URL, page or log.
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
  const ownerEnd = input.ownerDeadline ?? Number.POSITIVE_INFINITY;
  /** When a ticket stops authorizing reads: its own expiry, or fifteen minutes from now, whichever is first. */
  const ticketEndOf = (granted: DataExportTicket) =>
    Math.min(Date.parse(granted.expiresAt), input.now() + TICKET_LIFETIME_MS);
  // The first mint pauses for nothing: no ticket is held yet, so its refusal is the person's answer.
  const minted = await input.mint();
  if (minted.kind !== 'success') return { kind: 'refused', stage: 'ticket', result: minted };
  const first = minted.data as DataExportTicket;
  let ticket = first.ticket;
  const count = first.chunkCount;
  const byteCount = first.byteCount;
  let ticketEnd = ticketEndOf(first);
  if (
    count !== Math.ceil(byteCount / CHUNK_BYTES) ||
    Number.isNaN(ticketEnd) ||
    input.now() >= ownerEnd
  )
    return { kind: 'refused', stage: 'ticket', result: null };
  // Before the headers the browser waits with nothing to show: the download starts within one pause window, or the
  // person gets the refusal document instead.
  const startBy = input.now() + MAXIMUM_PAUSE_MS;
  let begun = false;
  let resumes = 0;
  let renewals = 0;
  const fail = (result: NativeOperationResult | null): DataExportRelayOutcome => {
    if (!begun) return { kind: 'refused', stage: 'chunk', result };
    input.sink.abort();
    return { kind: 'aborted' };
  };
  /**
   * One API call, repeated after each pause it asks for, with `ready` run before every attempt (a read renews its
   * ticket there). A pause that would reach the owner's end (or, before the headers, the start bound) is not taken:
   * the refusal is returned at once. Null means the response was cancelled.
   */
  const call = async (
    operation: () => Promise<NativeOperationResult>,
    ready?: () => Promise<void>,
  ): Promise<NativeOperationResult | null> => {
    for (;;) {
      if (ready) await ready();
      const result = await operation();
      if (input.signal.aborted) return null;
      const pause = pauseFor(result);
      const limit = begun ? ownerEnd : Math.min(ownerEnd, startBy);
      if (pause === null || input.now() + pause >= limit) return result;
      await wait(pause, input.signal);
      if (input.signal.aborted) return null;
    }
  };
  /**
   * Replaces the ticket with a new one for the same bundle. A refusal of a renewal that a `404` forced is the ticket's
   * own answer (the file is gone, or a bound is reached); one made ahead of time, while the current ticket still
   * reads, is not a failure: the download goes on and the ticket's end decides.
   */
  const renew = async (ahead: boolean): Promise<void> => {
    const renewed = await call(() => input.mint());
    if (renewed === null) throw new Finish(fail(null));
    if (renewed.kind !== 'success') {
      if (ahead && input.now() < ticketEnd) return;
      throw new Finish(
        begun ? fail(renewed) : { kind: 'refused', stage: 'ticket', result: renewed },
      );
    }
    const next = renewed.data as DataExportTicket;
    const nextEnd = ticketEndOf(next);
    // A renewed download continues the same bundle only: the geometry must be the one already promised.
    if (next.chunkCount !== count || next.byteCount !== byteCount || Number.isNaN(nextEnd))
      throw new Finish(fail(null));
    ticket = next.ticket;
    ticketEnd = nextEnd;
  };
  /** Before each read: a ticket that would end within one pause window is replaced while it still works. */
  const fresh = async (): Promise<void> => {
    if (input.now() + RENEW_AHEAD_MS < ticketEnd || renewals >= MAXIMUM_RENEWALS) return;
    renewals += 1;
    await renew(true);
  };
  /**
   * Settles one sink operation, or reports that it made no progress within the stall bound, before the deadline or,
   * when given, before `cancel` aborts.
   */
  const progress = async (operation: Promise<void>, cancel?: AbortSignal): Promise<boolean> => {
    // A write still pending when the connection is destroyed rejects later; that rejection is expected.
    operation.catch(() => undefined);
    const bound = Math.min(DATA_EXPORT_STALL_MS, ownerEnd - input.now());
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
  try {
    for (let index = 0; index < count; ) {
      if (input.signal.aborted || input.now() >= ownerEnd) return fail(null);
      const read = await call(() => input.read(ticket, index), fresh);
      if (read === null) return fail(null);
      if (read.kind !== 'success') {
        if (read.kind !== 'api-error' || read.status !== 404 || resumes >= MAXIMUM_RESUMES)
          return fail(read);
        resumes += 1;
        await renew(false);
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
  } catch (error) {
    if (error instanceof Finish) return error.outcome;
    throw error;
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
