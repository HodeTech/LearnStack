// @vitest-environment node
import { describe, expect, it, vi } from 'vitest';

import { relayDataExport } from './data-export-download';
import type { DataExportSink } from './data-export-download';
import type { NativeOperationResult } from './native-operation-contract';

const full = 128 * 1024;
const ticket = 'T'.repeat(42) + 'A';
const renewed = 'R'.repeat(42) + 'A';
const ok = (data: unknown) =>
  ({
    kind: 'success',
    status: 200,
    data,
    etag: null,
    location: null,
    replayed: false,
  }) as NativeOperationResult;
const notFound = {
  kind: 'api-error',
  status: 404,
  error: { code: 'not_found' },
  problem: {
    type: 't',
    title: 't',
    status: 404,
    instance: '/',
    code: 'not_found',
    messageKey: 'lockey_not_found',
    correlationId: 'c',
  },
} as NativeOperationResult;
const minted = (value: Partial<{ ticket: string; chunkCount: number; byteCount: number }> = {}) =>
  ok({
    ticket,
    expiresAt: '2026-11-23T12:15:00Z',
    chunkCount: 3,
    byteCount: 2 * full + 7,
    ...value,
  });
const bytes = (index: number) => Buffer.alloc(index < 2 ? full : 7, 0x30 + index);
const chunk = (
  index: number,
  value: Partial<{ index: number; count: number; bytes: string }> = {},
) => ok({ index, count: 3, bytes: bytes(index).toString('base64'), ...value });

function sink(refuse = false) {
  const written: Buffer[] = [];
  const handed: Buffer[] = [];
  const events: string[] = [];
  const value: DataExportSink & { written: Buffer[]; handed: Buffer[]; events: string[] } = {
    written,
    handed,
    events,
    begin: vi.fn((byteCount: number) => {
      events.push('begin:' + byteCount);
    }),
    write: vi.fn(async (buffer: Buffer) => {
      handed.push(buffer);
      if (refuse) throw new Error('closed');
      written.push(Buffer.from(buffer));
      events.push('write');
    }),
    end: vi.fn(async () => {
      events.push('end');
    }),
    abort: vi.fn(() => {
      events.push('abort');
    }),
  };
  return value;
}
const clock = () => Date.parse('2026-11-23T12:00:00Z');

describe('ADR-0107 § 4 download relay', () => {
  it('relays every chunk in order with the exact length, then zeroes each buffer', async () => {
    const out = sink();
    const read = vi.fn(async (_ticket: string, index: number) => chunk(index));
    const outcome = await relayDataExport({
      mint: async () => minted(),
      read,
      sink: out,
      signal: new AbortController().signal,
      now: clock,
    });
    expect(outcome).toEqual({ kind: 'completed' });
    expect(out.events).toEqual(['begin:' + (2 * full + 7), 'write', 'write', 'write', 'end']);
    expect(Buffer.concat(out.written).equals(Buffer.concat([bytes(0), bytes(1), bytes(2)]))).toBe(
      true,
    );
    expect(read.mock.calls.map(([, index]) => index)).toEqual([0, 1, 2]);
    for (const buffer of out.handed) expect(buffer.every((byte) => byte === 0)).toBe(true);
  });

  it('refuses before any byte when the ticket is refused or the first chunk fails', async () => {
    const limit = { ...notFound, status: 409 } as NativeOperationResult;
    const out = sink();
    expect(
      await relayDataExport({
        mint: async () => limit,
        read: vi.fn(),
        sink: out,
        signal: new AbortController().signal,
        now: clock,
      }),
    ).toEqual({ kind: 'refused', stage: 'ticket', result: limit });
    const failed = { kind: 'transport-error' } as NativeOperationResult;
    expect(
      await relayDataExport({
        mint: async () => minted(),
        read: async () => failed,
        sink: out,
        signal: new AbortController().signal,
        now: clock,
      }),
    ).toEqual({ kind: 'refused', stage: 'chunk', result: failed });
    expect(out.begin).not.toHaveBeenCalled();
    expect(out.abort).not.toHaveBeenCalled();
  });

  it.each([
    ['a chunk of another index', chunk(1, { index: 2 })],
    ['a chunk of another count', chunk(1, { count: 4 })],
    ['a short full chunk', chunk(1, { bytes: Buffer.alloc(full - 1).toString('base64') })],
    ['an oversized last chunk', chunk(2, { bytes: Buffer.alloc(8).toString('base64') })],
  ])('aborts after the headers on %s and never ends the body', async (_name, bad) => {
    const out = sink();
    const outcome = await relayDataExport({
      mint: async () => minted(),
      read: async (_ticket, index) =>
        (_name.includes('last') ? index === 2 : index === 1) ? bad : chunk(index),
      sink: out,
      signal: new AbortController().signal,
      now: clock,
    });
    expect(outcome).toEqual({ kind: 'aborted' });
    expect(out.events.at(-1)).toBe('abort');
    expect(out.end).not.toHaveBeenCalled();
  });

  it('refuses a ticket whose geometry contradicts the chunk size', async () => {
    const out = sink();
    expect(
      await relayDataExport({
        mint: async () => minted({ chunkCount: 2 }),
        read: vi.fn(),
        sink: out,
        signal: new AbortController().signal,
        now: clock,
      }),
    ).toEqual({ kind: 'refused', stage: 'ticket', result: null });
  });

  it('resumes once at the failed index with a new ticket after a credential change (404)', async () => {
    const out = sink();
    const tickets: string[] = [];
    let mints = 0;
    const outcome = await relayDataExport({
      mint: async () => minted(mints++ === 0 ? {} : { ticket: renewed }),
      read: async (value, index) => {
        tickets.push(value + ':' + index);
        return value === ticket && index === 1 ? notFound : chunk(index);
      },
      sink: out,
      signal: new AbortController().signal,
      now: clock,
    });
    expect(outcome).toEqual({ kind: 'completed' });
    expect(tickets).toEqual([ticket + ':0', ticket + ':1', renewed + ':1', renewed + ':2']);
    expect(Buffer.concat(out.written).length).toBe(2 * full + 7);
  });

  it('stops resuming after two renewals, and when a renewal changes the bundle', async () => {
    let out = sink();
    expect(
      await relayDataExport({
        mint: async () => minted(),
        read: async (_ticket, index) => (index === 0 ? chunk(0) : notFound),
        sink: out,
        signal: new AbortController().signal,
        now: clock,
      }),
    ).toEqual({ kind: 'aborted' });
    out = sink();
    let mints = 0;
    expect(
      await relayDataExport({
        mint: async () => minted(mints++ ? { chunkCount: 4, byteCount: 3 * full + 1 } : {}),
        read: async (_ticket, index) => (index === 0 ? chunk(0) : notFound),
        sink: out,
        signal: new AbortController().signal,
        now: clock,
      }),
    ).toEqual({ kind: 'aborted' });
    // A renewal refused before any byte is the ticket's own answer (the file is gone).
    out = sink();
    let first = true;
    const gone = { ...notFound, status: 409 } as NativeOperationResult;
    expect(
      await relayDataExport({
        mint: async () => {
          if (first) {
            first = false;
            return minted();
          }
          return gone;
        },
        read: async () => notFound,
        sink: out,
        signal: new AbortController().signal,
        now: clock,
      }),
    ).toEqual({ kind: 'refused', stage: 'ticket', result: gone });
  });

  it('aborts on cancellation, on the ticket deadline and on a sink that takes no more', async () => {
    const controller = new AbortController();
    let out = sink();
    expect(
      await relayDataExport({
        mint: async () => minted(),
        read: async (_ticket, index) => {
          if (index === 1) controller.abort();
          return chunk(index);
        },
        sink: out,
        signal: controller.signal,
        now: clock,
      }),
    ).toEqual({ kind: 'aborted' });
    expect(out.end).not.toHaveBeenCalled();
    out = sink();
    let time = clock();
    expect(
      await relayDataExport({
        mint: async () => minted(),
        read: async (_ticket, index) => {
          time += 16 * 60 * 1000;
          return chunk(index);
        },
        sink: out,
        signal: new AbortController().signal,
        now: () => time,
      }),
    ).toEqual({ kind: 'aborted' });
    out = sink(true);
    expect(
      await relayDataExport({
        mint: async () => minted(),
        read: async (_ticket, index) => chunk(index),
        sink: out,
        signal: new AbortController().signal,
        now: clock,
      }),
    ).toEqual({ kind: 'aborted' });
    expect(out.abort).toHaveBeenCalledOnce();
    expect(out.handed[0]!.every((byte) => byte === 0)).toBe(true);
  });
});
