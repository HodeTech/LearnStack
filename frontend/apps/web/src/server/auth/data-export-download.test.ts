// @vitest-environment node
import { afterEach, describe, expect, it, vi } from 'vitest';

import { DATA_EXPORT_STALL_MS, relayDataExport } from './data-export-download';
import type { DataExportSink } from './data-export-download';
import type { NativeOperationResult } from './native-operation-contract';

const full = 128 * 1024;
const ticket = 'T'.repeat(42) + 'A';
const renewed = 'R'.repeat(42) + 'A';
const start = Date.parse('2026-11-23T12:00:00Z');
const ok = (data: unknown) =>
  ({
    kind: 'success',
    status: 200,
    data,
    etag: null,
    location: null,
    replayed: false,
  }) as NativeOperationResult;
const refusal = (status: number, code: string, retryAfter?: number) =>
  ({
    kind: 'api-error',
    status,
    error: { code, ...(retryAfter === undefined ? {} : { retryAfter }) },
    problem: {
      type: 't',
      title: 't',
      status,
      instance: '/',
      code,
      messageKey: 'lockey_' + code,
      correlationId: 'c',
    },
  }) as NativeOperationResult;
const notFound = refusal(404, 'not_found');
const limited = (retryAfter?: number) => refusal(429, 'rate_limited', retryAfter);
const unavailable = (retryAfter?: number) => refusal(503, 'audit_unavailable', retryAfter);
const minted = (
  value: Partial<{ ticket: string; chunkCount: number; byteCount: number; expiresAt: string }> = {},
) =>
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
const bundle = () => Buffer.concat([bytes(0), bytes(1), bytes(2)]);

/**
 * A socket stand-in that takes a buffer only when it acknowledges the write, a microtask later (or when a held write
 * is released): a buffer zeroed before that point arrives as zeros, exactly as it would on a real socket.
 */
function sink(mode: 'acknowledge' | 'hold' | 'refuse' | 'stall' = 'acknowledge') {
  const written: Buffer[] = [];
  const handed: Buffer[] = [];
  const events: string[] = [];
  const held: (() => void)[] = [];
  const value: DataExportSink & {
    written: Buffer[];
    handed: Buffer[];
    events: string[];
    held: (() => void)[];
  } = {
    written,
    handed,
    events,
    held,
    begin: vi.fn((byteCount: number) => {
      events.push('begin:' + byteCount);
    }),
    write: vi.fn((buffer: Buffer) => {
      handed.push(buffer);
      if (mode === 'refuse') return Promise.reject(new Error('closed'));
      if (mode === 'stall') return new Promise<void>(() => undefined);
      return new Promise<void>((resolve) => {
        const acknowledge = () => {
          written.push(Buffer.from(buffer));
          events.push('write');
          resolve();
        };
        if (mode === 'hold') held.push(acknowledge);
        else queueMicrotask(acknowledge);
      });
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
const clock = () => start;
/** Lets every continuation that does not wait for a timer run. */
const flush = async () => {
  for (let turn = 0; turn < 200; ++turn) await Promise.resolve();
};
const relay = (
  input: Partial<Parameters<typeof relayDataExport>[0]> &
    Pick<Parameters<typeof relayDataExport>[0], 'read'>,
) =>
  relayDataExport({
    mint: async () => minted(),
    sink: sink(),
    signal: new AbortController().signal,
    now: clock,
    ...input,
  });

afterEach(() => {
  vi.useRealTimers();
});

describe('ADR-0107 § 4 download relay', () => {
  it('relays every chunk in order with the exact length, then zeroes each buffer once taken', async () => {
    const out = sink();
    const read = vi.fn(async (_ticket: string, index: number) => chunk(index));
    expect(await relay({ read, sink: out })).toEqual({ kind: 'completed' });
    expect(out.events).toEqual(['begin:' + (2 * full + 7), 'write', 'write', 'write', 'end']);
    // The sink copies at acknowledgement: a buffer zeroed before the socket took it would compare unequal.
    expect(Buffer.concat(out.written).equals(bundle())).toBe(true);
    expect(read.mock.calls.map(([, index]) => index)).toEqual([0, 1, 2]);
    for (const buffer of out.handed) expect(buffer.every((byte) => byte === 0)).toBe(true);
  });

  it('keeps one chunk in flight: the next read waits until the socket took the previous one', async () => {
    const out = sink('hold');
    const read = vi.fn(async (_ticket: string, index: number) => chunk(index));
    const run = relay({ read, sink: out });
    for (let taken = 0; taken < 3; ++taken) {
      await flush();
      expect(read).toHaveBeenCalledTimes(taken + 1);
      expect(out.held).toHaveLength(1);
      // Still held: the buffer handed to the socket is intact until the write settles.
      expect(out.handed[taken]!.equals(bytes(taken))).toBe(true);
      out.held.shift()!();
    }
    expect(await run).toEqual({ kind: 'completed' });
    expect(read).toHaveBeenCalledTimes(3);
  });

  it('refuses before any byte when the ticket is refused or the first chunk fails', async () => {
    const limit = refusal(409, 'business_rule_violation');
    const out = sink();
    expect(await relay({ mint: async () => limit, read: vi.fn(), sink: out })).toEqual({
      kind: 'refused',
      stage: 'ticket',
      result: limit,
    });
    const failed = { kind: 'transport-error' } as NativeOperationResult;
    expect(await relay({ read: async () => failed, sink: out })).toEqual({
      kind: 'refused',
      stage: 'chunk',
      result: failed,
    });
    expect(out.begin).not.toHaveBeenCalled();
    expect(out.abort).not.toHaveBeenCalled();
  });

  const malformed = [
    ['a chunk of another index', 1, chunk(1, { index: 2 })],
    ['a chunk of another count', 1, chunk(1, { count: 4 })],
    ['a short full chunk', 1, chunk(1, { bytes: Buffer.alloc(full - 1).toString('base64') })],
    ['an oversized last chunk', 2, chunk(2, { bytes: Buffer.alloc(8).toString('base64') })],
  ] as const;
  it.each(malformed)(
    'aborts after the headers on %s and never ends the body',
    async (_name, at, bad) => {
      const out = sink();
      const outcome = await relay({
        read: async (_ticket, index) => (index === at ? bad : chunk(index)),
        sink: out,
      });
      expect(outcome).toEqual({ kind: 'aborted' });
      expect(out.events.at(-1)).toBe('abort');
      expect(out.end).not.toHaveBeenCalled();
    },
  );

  it.each([
    ['a first chunk of another index', chunk(0, { index: 1 })],
    ['a first chunk of another count', chunk(0, { count: 4 })],
    ['a short first chunk', chunk(0, { bytes: Buffer.alloc(full - 1).toString('base64') })],
    ['an oversized first chunk', chunk(0, { bytes: Buffer.alloc(full + 3).toString('base64') })],
  ])('proves the first chunk before any header: %s is a clean refusal', async (_name, bad) => {
    const out = sink();
    expect(await relay({ read: async () => bad, sink: out })).toEqual({
      kind: 'refused',
      stage: 'chunk',
      result: null,
    });
    expect(out.begin).not.toHaveBeenCalled();
    expect(out.write).not.toHaveBeenCalled();
    expect(out.abort).not.toHaveBeenCalled();
  });

  it('refuses a ticket whose geometry contradicts the chunk size or whose expiry is no instant', async () => {
    for (const ticketValue of [minted({ chunkCount: 2 }), minted({ expiresAt: 'later' })]) {
      const read = vi.fn();
      expect(await relay({ mint: async () => ticketValue, read })).toEqual({
        kind: 'refused',
        stage: 'ticket',
        result: null,
      });
      expect(read).not.toHaveBeenCalled();
    }
  });

  it('resumes at the failed index with a new ticket after any 404', async () => {
    const out = sink();
    const tickets: string[] = [];
    let mints = 0;
    const outcome = await relay({
      mint: async () => minted(mints++ === 0 ? {} : { ticket: renewed }),
      read: async (value, index) => {
        tickets.push(value + ':' + index);
        return value === ticket && index === 1 ? notFound : chunk(index);
      },
      sink: out,
    });
    expect(outcome).toEqual({ kind: 'completed' });
    expect(tickets).toEqual([ticket + ':0', ticket + ':1', renewed + ':1', renewed + ':2']);
    expect(Buffer.concat(out.written).equals(bundle())).toBe(true);
  });

  it('stops after exactly two renewals, and when a renewal changes the bundle', async () => {
    let out = sink();
    let mints = 0;
    expect(
      await relay({
        mint: async () => {
          mints += 1;
          return minted();
        },
        read: async (_ticket, index) => (index === 0 ? chunk(0) : notFound),
        sink: out,
      }),
    ).toEqual({ kind: 'aborted' });
    // The first ticket and two renewals: a third renewal is never minted.
    expect(mints).toBe(3);
    out = sink();
    mints = 0;
    expect(
      await relay({
        mint: async () => minted(mints++ ? { chunkCount: 4, byteCount: 3 * full + 1 } : {}),
        read: async (_ticket, index) => (index === 0 ? chunk(0) : notFound),
        sink: out,
      }),
    ).toEqual({ kind: 'aborted' });
    expect(out.abort).toHaveBeenCalledOnce();
  });

  it('answers a renewal refused before any byte as the ticket’s refusal', async () => {
    const out = sink();
    let first = true;
    const gone = refusal(409, 'business_rule_violation');
    expect(
      await relay({
        mint: async () => {
          if (first) {
            first = false;
            return minted();
          }
          return gone;
        },
        read: async () => notFound,
        sink: out,
      }),
    ).toEqual({ kind: 'refused', stage: 'ticket', result: gone });
    expect(out.begin).not.toHaveBeenCalled();
  });

  it('destroys the connection when a renewal is refused after bytes were sent', async () => {
    const out = sink();
    let first = true;
    const gone = refusal(409, 'business_rule_violation');
    expect(
      await relay({
        mint: async () => {
          if (first) {
            first = false;
            return minted();
          }
          return gone;
        },
        read: async (_ticket, index) => (index === 0 ? chunk(0) : notFound),
        sink: out,
      }),
    ).toEqual({ kind: 'aborted' });
    expect(out.events).toEqual(['begin:' + (2 * full + 7), 'write', 'abort']);
    expect(out.end).not.toHaveBeenCalled();
  });

  it.each([
    ['a conflict', refusal(409, 'data_export_unavailable')],
    ['a server fault', refusal(500, 'internal_error')],
    ['a forbidden answer', refusal(403, 'forbidden')],
  ])('mints no new ticket for %s: only a 404 is resumed', async (_, answer) => {
    let mints = 0;
    const mint = async () => {
      mints += 1;
      return minted();
    };
    const after = sink();
    expect(
      await relay({
        mint,
        read: async (_t, index) => (index === 1 ? answer : chunk(index)),
        sink: after,
      }),
    ).toEqual({ kind: 'aborted' });
    expect(mints, 'after the headers').toBe(1);
    expect(after.abort).toHaveBeenCalledOnce();
    mints = 0;
    const before = sink();
    expect(await relay({ mint, read: async () => answer, sink: before })).toEqual({
      kind: 'refused',
      stage: 'chunk',
      result: answer,
    });
    expect(mints, 'before the headers').toBe(1);
    expect(before.begin).not.toHaveBeenCalled();
  });

  it.each([
    ['another chunk count', { chunkCount: 4, byteCount: 3 * full + 1 }],
    ['another byte count in the same chunk count', { byteCount: 2 * full + 8 }],
    ['an expiry that is no date', { expiresAt: 'not-a-date' }],
  ])('refuses a renewal that carries %s, and destroys the connection', async (_, change) => {
    const out = sink();
    let mints = 0;
    expect(
      await relay({
        mint: async () => minted(mints++ === 0 ? {} : change),
        read: async (_ticket, index) => (index === 0 ? chunk(0) : notFound),
        sink: out,
      }),
    ).toEqual({ kind: 'aborted' });
    expect(out.abort).toHaveBeenCalledOnce();
    expect(out.end).not.toHaveBeenCalled();
    expect(mints).toBe(2);
  });

  it('aborts on cancellation, on the owner’s deadline and on a sink that takes no more', async () => {
    const controller = new AbortController();
    let out = sink();
    expect(
      await relay({
        read: async (_ticket, index) => {
          if (index === 1) controller.abort();
          return chunk(index);
        },
        sink: out,
        signal: controller.signal,
      }),
    ).toEqual({ kind: 'aborted' });
    expect(out.end).not.toHaveBeenCalled();
    out = sink();
    let time = start;
    expect(
      await relay({
        read: async (_ticket, index) => {
          time += 16 * 60 * 1000;
          return chunk(index);
        },
        sink: out,
        now: () => time,
        ownerDeadline: start + 20 * 60 * 1000,
      }),
    ).toEqual({ kind: 'aborted' });
    out = sink('refuse');
    expect(await relay({ read: async (_ticket, index) => chunk(index), sink: out })).toEqual({
      kind: 'aborted',
    });
    expect(out.abort).toHaveBeenCalledOnce();
    expect(out.handed[0]!.every((byte) => byte === 0)).toBe(true);
  });

  it('ends at the owner’s end, whatever the ticket still allows', async () => {
    const out = sink();
    let time = start;
    expect(
      await relay({
        read: async (_ticket, index) => {
          if (index === 1) time += 2 * 60 * 1000;
          return chunk(index);
        },
        sink: out,
        now: () => time,
        ownerDeadline: start + 60 * 1000,
      }),
    ).toEqual({ kind: 'aborted' });
    expect(out.end).not.toHaveBeenCalled();
    // A response whose owner has already ended never starts.
    expect(
      await relay({ read: async (_ticket, index) => chunk(index), ownerDeadline: start }),
    ).toEqual({ kind: 'refused', stage: 'ticket', result: null });
  });
});

describe('ADR-0107 § 4 download relay across more than one ticket', () => {
  const third = 'S'.repeat(42) + 'A';
  const four = (index: number) =>
    ok({
      index,
      count: 4,
      bytes: Buffer.alloc(index < 3 ? full : 7, 0x30 + index).toString('base64'),
    });
  /** A mint whose ticket always lives fifteen minutes from the clock it is read on. */
  const mintFrom = (clockOf: () => number, names: string[], extra = {}) => {
    const minting = { calls: 0 };
    const mint = async () => {
      const name = names[Math.min(minting.calls, names.length - 1)]!;
      minting.calls += 1;
      return minted({
        ticket: name,
        expiresAt: new Date(clockOf() + 15 * 60 * 1000).toISOString(),
        ...extra,
      });
    };
    return { mint, minting };
  };

  it('renews the ticket shortly before it ends, so a download outlives one ticket', async () => {
    const out = sink();
    let time = start;
    const { mint, minting } = mintFrom(() => time, [ticket, renewed, third]);
    const reads: string[] = [];
    const outcome = await relay({
      mint,
      read: async (value, index) => {
        reads.push(value.slice(-1) === 'A' ? value[0]! + ':' + index : value);
        // Each chunk leaves less than one pause window on the ticket that read it.
        time += 14 * 60 * 1000;
        return chunk(index);
      },
      sink: out,
      now: () => time,
    });
    expect(outcome).toEqual({ kind: 'completed' });
    expect(reads).toEqual(['T:0', 'R:1', 'S:2']);
    expect(minting.calls).toBe(3);
    expect(Buffer.concat(out.written).equals(bundle())).toBe(true);
  });

  it('does not mint again while the ticket has more than one pause window left', async () => {
    const out = sink();
    let time = start;
    const { mint, minting } = mintFrom(() => time, [ticket, renewed]);
    expect(
      await relay({
        mint,
        read: async (_value, index) => {
          time += 60 * 1000;
          return chunk(index);
        },
        sink: out,
        now: () => time,
      }),
    ).toEqual({ kind: 'completed' });
    expect(minting.calls).toBe(1);
  });

  it('renews at most twice ahead of time, and counts those apart from the renewals a 404 forces', async () => {
    const out = sink();
    let time = start;
    const { mint, minting } = mintFrom(() => time, [ticket, renewed, third, 'U'.repeat(42) + 'A'], {
      chunkCount: 4,
      byteCount: 3 * full + 7,
    });
    const used: string[] = [];
    let answered404 = false;
    const outcome = await relay({
      mint,
      read: async (value, index) => {
        used.push(value[0]! + ':' + index);
        time += 14 * 60 * 1000;
        // The third ticket was not renewed ahead of time (the cap), so its end is met as a 404 and resumed.
        if (value === third && index === 3 && !answered404) {
          answered404 = true;
          return notFound;
        }
        return four(index);
      },
      sink: out,
      now: () => time,
    });
    expect(outcome).toEqual({ kind: 'completed' });
    expect(used).toEqual(['T:0', 'R:1', 'S:2', 'S:3', 'U:3']);
    // The first ticket, two renewals ahead of time and one resume: never more.
    expect(minting.calls).toBe(4);
  });

  it('goes on with a ticket that still reads when a renewal made ahead of time is refused', async () => {
    const out = sink();
    let time = start;
    const gone = refusal(409, 'data_export_ticket_limit');
    let mints = 0;
    const outcome = await relay({
      mint: async () => {
        mints += 1;
        return mints === 1 ? minted() : gone;
      },
      read: async (_value, index) => {
        if (index === 0) time += 14 * 60 * 1000;
        return chunk(index);
      },
      sink: out,
      now: () => time,
    });
    expect(outcome).toEqual({ kind: 'completed' });
    // The first ticket and the two attempts ahead of time, none repeated for the refusal.
    expect(mints).toBe(3);
  });

  it('answers a renewal ahead of time that is refused after the ticket ended as a failure', async () => {
    const out = sink();
    let time = start;
    const gone = refusal(409, 'data_export_ticket_limit');
    let mints = 0;
    const outcome = await relay({
      mint: async () => {
        mints += 1;
        return mints === 1 ? minted() : gone;
      },
      read: async (_value, index) => {
        if (index === 0) time += 16 * 60 * 1000;
        return chunk(index);
      },
      sink: out,
      now: () => time,
    });
    expect(outcome).toEqual({ kind: 'aborted' });
    expect(out.events).toEqual(['begin:' + (2 * full + 7), 'write', 'abort']);
  });
});

describe('ADR-0107 § 4 download relay under the API admission budget', () => {
  const paced = () => {
    vi.useFakeTimers({ now: start, toFake: ['setTimeout', 'clearTimeout', 'Date'] });
    return () => Date.now();
  };

  it('pauses on a 429 for its Retry-After, then reads the same index with the same ticket', async () => {
    const now = paced();
    const out = sink();
    const reads: string[] = [];
    let refused = false;
    let mints = 0;
    const run = relay({
      mint: async () => {
        mints += 1;
        return minted();
      },
      read: async (value, index) => {
        reads.push(value + ':' + index);
        if (index === 1 && !refused) {
          refused = true;
          return limited(7);
        }
        return chunk(index);
      },
      sink: out,
      now,
    });
    await flush();
    expect(reads).toEqual([ticket + ':0', ticket + ':1']);
    await vi.advanceTimersByTimeAsync(6999);
    expect(reads).toHaveLength(2);
    await vi.advanceTimersByTimeAsync(1);
    expect(await run).toEqual({ kind: 'completed' });
    expect(reads).toEqual([ticket + ':0', ticket + ':1', ticket + ':1', ticket + ':2']);
    expect(mints).toBe(1);
    expect(Buffer.concat(out.written).equals(bundle())).toBe(true);
  });

  it('waits the default when a 429 names no usable Retry-After, and a 503 only when it names one', async () => {
    const now = paced();
    let refused = false;
    const reads: number[] = [];
    const run = relay({
      read: async (_ticket, index) => {
        reads.push(index);
        if (index === 2 && !refused) {
          refused = true;
          return limited();
        }
        return chunk(index);
      },
      now,
    });
    await flush();
    expect(reads).toEqual([0, 1, 2]);
    await vi.advanceTimersByTimeAsync(4999);
    expect(reads).toHaveLength(3);
    await vi.advanceTimersByTimeAsync(1);
    expect(await run).toEqual({ kind: 'completed' });
    expect(reads).toEqual([0, 1, 2, 2]);

    refused = false;
    const paused = relay({
      read: async (_ticket, index) => {
        if (index === 1 && !refused) {
          refused = true;
          return unavailable(30);
        }
        return chunk(index);
      },
      now,
    });
    await flush();
    await vi.advanceTimersByTimeAsync(30_000);
    expect(await paused).toEqual({ kind: 'completed' });

    // A 503 without Retry-After is a failure: after the headers it destroys the connection.
    const out = sink();
    expect(
      await relay({
        read: async (_ticket, index) => (index === 1 ? unavailable() : chunk(index)),
        sink: out,
        now,
      }),
    ).toEqual({ kind: 'aborted' });
    expect(out.abort).toHaveBeenCalledOnce();
  });

  it('keeps pausing through repeated 429s, at least a second and at most a minute each, without spending a renewal', async () => {
    const now = paced();
    const answers = [limited(0), limited(3600), limited(2), notFound, notFound];
    const reads: string[] = [];
    let mints = 0;
    const run = relay({
      mint: async () => minted(mints++ === 0 ? {} : { ticket: renewed }),
      read: async (value, index) => {
        reads.push(value.slice(0, 1) + index);
        return index === 1 && answers.length ? answers.shift()! : chunk(index);
      },
      now,
    });
    await flush();
    expect(reads).toEqual(['T0', 'T1']);
    // Retry-After 0 still waits a second.
    await vi.advanceTimersByTimeAsync(999);
    expect(reads).toHaveLength(2);
    await vi.advanceTimersByTimeAsync(1);
    expect(reads).toHaveLength(3);
    // Retry-After 3600 is capped at one minute.
    await vi.advanceTimersByTimeAsync(59_999);
    expect(reads).toHaveLength(3);
    await vi.advanceTimersByTimeAsync(1);
    expect(reads).toHaveLength(4);
    await vi.advanceTimersByTimeAsync(2000);
    // Three pauses spent no renewal: both 404s still resume.
    expect(await run).toEqual({ kind: 'completed' });
    expect(reads).toEqual(['T0', 'T1', 'T1', 'T1', 'T1', 'R1', 'R1', 'R2']);
    expect(mints).toBe(3);
  });

  it('pauses a renewal the API refuses at admission, then mints again', async () => {
    const now = paced();
    const answers = [minted(), limited(3), minted({ ticket: renewed })];
    const reads: string[] = [];
    let mints = 0;
    const run = relay({
      mint: async () => {
        mints += 1;
        return answers.shift()!;
      },
      read: async (value, index) => {
        reads.push(value.slice(0, 1) + index);
        return value === ticket && index === 1 ? notFound : chunk(index);
      },
      now,
    });
    await flush();
    expect(mints).toBe(2);
    await vi.advanceTimersByTimeAsync(2999);
    expect(mints).toBe(2);
    await vi.advanceTimersByTimeAsync(1);
    expect(await run).toEqual({ kind: 'completed' });
    expect(mints).toBe(3);
    expect(reads).toEqual(['T0', 'T1', 'R1', 'R2']);
  });

  it('does not take a pause that would reach the owner’s end: it destroys the connection at once', async () => {
    const now = paced();
    const out = sink();
    expect(
      await relay({
        read: async (_ticket, index) => (index === 1 ? limited(30) : chunk(index)),
        sink: out,
        now,
        ownerDeadline: start + 20_000,
      }),
    ).toEqual({ kind: 'aborted' });
    expect(Date.now()).toBe(start);
  });

  it('renews the ticket after a pause that crossed its end, and goes on with the same index', async () => {
    const now = paced();
    const out = sink();
    const reads: string[] = [];
    let mints = 0;
    let limitedOnce = false;
    const run = relay({
      // The first ticket ends a hundred seconds in: a forty-second pause leaves less than one pause window on it.
      mint: async () => {
        mints += 1;
        return mints === 1
          ? minted({ expiresAt: new Date(start + 100_000).toISOString() })
          : minted({
              ticket: renewed,
              expiresAt: new Date(Date.now() + 15 * 60 * 1000).toISOString(),
            });
      },
      read: async (value, index) => {
        reads.push((value === ticket ? 'T' : 'R') + ':' + index);
        if (index === 1 && !limitedOnce) {
          limitedOnce = true;
          return limited(40);
        }
        return chunk(index);
      },
      sink: out,
      now,
    });
    await flush();
    await vi.advanceTimersByTimeAsync(40_000);
    expect(await run).toEqual({ kind: 'completed' });
    expect(reads).toEqual(['T:0', 'T:1', 'R:1', 'R:2']);
    expect(mints).toBe(2);
    expect(Buffer.concat(out.written).equals(bundle())).toBe(true);
  });

  it('pauses before the headers too, but refuses cleanly when the first chunk cannot start within a minute', async () => {
    const now = paced();
    let out = sink();
    let refused = false;
    const run = relay({
      read: async (_ticket, index) => {
        if (index === 0 && !refused) {
          refused = true;
          return limited(20);
        }
        return chunk(index);
      },
      sink: out,
      now,
    });
    await flush();
    // Nothing is promised to the browser while the first chunk waits.
    expect(out.begin).not.toHaveBeenCalled();
    await vi.advanceTimersByTimeAsync(20_000);
    expect(await run).toEqual({ kind: 'completed' });
    expect(out.events[0]).toBe('begin:' + (2 * full + 7));

    out = sink();
    const busy = limited(60);
    expect(
      await relay({
        read: async (_ticket, index) => (index === 0 ? busy : chunk(index)),
        sink: out,
        now,
      }),
    ).toEqual({ kind: 'refused', stage: 'chunk', result: busy });
    expect(out.begin).not.toHaveBeenCalled();
    expect(Date.now()).toBe(start + 20_000);
  });

  it('ends a pause at once when the response is cancelled', async () => {
    const now = paced();
    const controller = new AbortController();
    const out = sink();
    const run = relay({
      read: async (_ticket, index) => (index === 1 ? limited(30) : chunk(index)),
      sink: out,
      signal: controller.signal,
      now,
    });
    await flush();
    controller.abort();
    expect(await run).toEqual({ kind: 'aborted' });
    expect(Date.now()).toBe(start);
  });
});

describe('ADR-0107 § 4 download relay against a stalled client', () => {
  const paced = () => {
    vi.useFakeTimers({ now: start, toFake: ['setTimeout', 'clearTimeout', 'Date'] });
    return () => Date.now();
  };

  it('destroys the connection when a write makes no progress, and zeroes the held buffer', async () => {
    const now = paced();
    const out = sink('stall');
    const run = relay({ read: async (_ticket, index) => chunk(index), sink: out, now });
    await flush();
    await vi.advanceTimersByTimeAsync(DATA_EXPORT_STALL_MS - 1);
    expect(out.abort).not.toHaveBeenCalled();
    await vi.advanceTimersByTimeAsync(1);
    expect(await run).toEqual({ kind: 'aborted' });
    expect(out.abort).toHaveBeenCalledOnce();
    expect(out.end).not.toHaveBeenCalled();
    expect(out.handed).toHaveLength(1);
    expect(out.handed[0]!.every((byte) => byte === 0)).toBe(true);
  });

  it('cuts a pending write at the owner’s end when that comes first, and a stalled end too', async () => {
    const now = paced();
    let out = sink('stall');
    const run = relay({
      read: async (_ticket, index) => chunk(index),
      sink: out,
      now,
      ownerDeadline: start + 10_000,
    });
    await flush();
    await vi.advanceTimersByTimeAsync(10_000);
    expect(await run).toEqual({ kind: 'aborted' });
    expect(out.abort).toHaveBeenCalledOnce();

    out = sink();
    const never = new Promise<void>(() => undefined);
    const stalledEnd = relay({
      read: async (_ticket, index) => chunk(index),
      sink: { ...out, end: () => never },
      now,
    });
    await flush();
    await vi.advanceTimersByTimeAsync(DATA_EXPORT_STALL_MS);
    expect(await stalledEnd).toEqual({ kind: 'aborted' });
    expect(out.abort).toHaveBeenCalledOnce();
  });

  it('stops waiting for a write as soon as the response is cancelled', async () => {
    const now = paced();
    const controller = new AbortController();
    const out = sink('stall');
    const run = relay({
      read: async (_ticket, index) => chunk(index),
      sink: out,
      signal: controller.signal,
      now,
    });
    await flush();
    controller.abort();
    expect(await run).toEqual({ kind: 'aborted' });
    expect(Date.now()).toBe(start);
  });
});
