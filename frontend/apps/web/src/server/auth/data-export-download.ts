import type { DataExportChunk, DataExportTicket } from '@learnstack/sdk/administration-server';

import type { NativeOperationResult } from './native-operation-contract.js';

/** ADR-0107 § 4: every chunk but the last holds exactly this much plaintext. */
const CHUNK_BYTES = 128 * 1024;
/** A ticket authorizes reads for fifteen minutes; no relay outlives it. */
const TICKET_LIFETIME_MS = 15 * 60 * 1000;
/**
 * A ticket is bound to the credential that minted it, so a refresh during a long download ends that ticket and
 * the API answers the next chunk `404`. The OpenAPI document's recovery is a new ticket resumed at the failed index;
 * the relay does that at most this many times, and only for a `404`, never for a refusal of the mint itself.
 */
const MAXIMUM_RESUMES = 2;

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

/**
 * Mints a ticket, reads the chunks in index order and relays them as one body, one chunk in flight. Each chunk is
 * proven (index, count, exact size) before a byte of it is written, and its decoded buffer is zeroed after the write.
 * The ticket lives only in this closure: it never reaches a header, URL, page or log.
 */
export async function relayDataExport(
  input: Readonly<{
    mint: () => Promise<NativeOperationResult>;
    read: (ticket: string, index: number) => Promise<NativeOperationResult>;
    sink: DataExportSink;
    signal: AbortSignal;
    now: () => number;
  }>,
): Promise<DataExportRelayOutcome> {
  const minted = await input.mint();
  if (minted.kind !== 'success') return { kind: 'refused', stage: 'ticket', result: minted };
  const first = minted.data as DataExportTicket;
  let ticket = first.ticket;
  const count = first.chunkCount;
  const byteCount = first.byteCount;
  if (count !== Math.ceil(byteCount / CHUNK_BYTES))
    return { kind: 'refused', stage: 'ticket', result: null };
  let deadline = Math.min(Date.parse(first.expiresAt), input.now() + TICKET_LIFETIME_MS);
  let begun = false;
  let resumes = 0;
  const fail = (result: NativeOperationResult | null): DataExportRelayOutcome => {
    if (!begun) return { kind: 'refused', stage: 'chunk', result };
    input.sink.abort();
    return { kind: 'aborted' };
  };
  for (let index = 0; index < count; ) {
    if (input.signal.aborted || input.now() >= deadline) return fail(null);
    const read = await input.read(ticket, index);
    if (input.signal.aborted) return fail(null);
    if (read.kind !== 'success') {
      if (read.kind !== 'api-error' || read.status !== 404 || resumes >= MAXIMUM_RESUMES)
        return fail(read);
      resumes += 1;
      const renewed = await input.mint();
      if (input.signal.aborted) return fail(null);
      // A refused renewal is the ticket's own answer (the file is gone, or a bound is reached).
      if (renewed.kind !== 'success')
        return begun ? fail(renewed) : { kind: 'refused', stage: 'ticket', result: renewed };
      const next = renewed.data as DataExportTicket;
      // A resumed download continues the same bundle only: the geometry must be the one already promised.
      if (next.chunkCount !== count || next.byteCount !== byteCount) return fail(null);
      ticket = next.ticket;
      deadline = Math.min(Date.parse(next.expiresAt), input.now() + TICKET_LIFETIME_MS);
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
      await input.sink.write(bytes);
    } catch {
      begun = true;
      return fail(null);
    } finally {
      bytes.fill(0);
    }
    index += 1;
  }
  try {
    await input.sink.end();
  } catch {
    return fail(null);
  }
  return { kind: 'completed' };
}
