// @vitest-environment node
import { describe, expect, it, vi } from 'vitest';

import { protectPageResponse } from './page-response';
import { SnapshotWire } from '../../test/page-response-fixture';

describe('native protected output fence', () => {
  it('preserves backpressure and actual callbacks, including end(data)', () => {
    const wire = new SnapshotWire();
    let pending = 0;
    const check = vi.fn();
    const started = vi.fn();
    protectPageResponse(wire.native, check, started, () => {
      ++pending;
      return () => {
        --pending;
      };
    });
    const written = vi.fn();
    const ended = vi.fn();
    expect(wire.native.write('first', written)).toBe(false);
    expect(wire.native.end('last', ended)).toBe(wire);
    expect(wire.chunks).toEqual(['first', 'last']);
    expect(pending).toBe(2);
    expect(written).not.toHaveBeenCalled();
    expect(ended).not.toHaveBeenCalled();
    wire.drainCallbacks();
    expect(pending).toBe(0);
    expect(written).toHaveBeenCalledOnce();
    expect(ended).toHaveBeenCalledOnce();
    expect(check).toHaveBeenCalled();
    expect(started).toHaveBeenCalled();
  });

  it.each(['writeHead', 'flushHeaders', 'write', 'end'] as const)(
    'withholds an invalid first %s and strips retained protected headers',
    (method) => {
      const wire = new SnapshotWire();
      wire.headers.set('set-cookie', 'private-fixture-cookie');
      wire.headers.set('location', '/private-fixture-target');
      protectPageResponse(
        wire.native,
        () => {
          throw new Error('closed');
        },
        () => {},
        () => () => {},
      );
      if (method === 'writeHead') wire.native.writeHead(200);
      else if (method === 'flushHeaders') wire.native.flushHeaders();
      else wire.native[method]('private-fixture-body');
      expect(wire.chunks).toEqual([]);
      expect(wire.statusCode).toBe(503);
      expect(Object.fromEntries(wire.headers)).toEqual({
        'cache-control': 'no-store',
        'content-length': '0',
      });
    },
  );

  it.each(['writeHead', 'flushHeaders', 'write', 'end'] as const)(
    'destroys a started response before invalid late %s bytes',
    async (method) => {
      const wire = new SnapshotWire();
      let live = true;
      protectPageResponse(
        wire.native,
        () => {
          if (!live) throw new Error('closed');
        },
        () => {},
        () => () => {},
      );
      wire.native.write('admitted');
      live = false;
      const rejected = vi.fn();
      if (method === 'writeHead') wire.native.writeHead(200);
      else if (method === 'flushHeaders') wire.native.flushHeaders();
      else wire.native[method]('withheld', rejected);
      await Promise.resolve();
      expect(wire.destroyed).toBe(true);
      expect(wire.chunks).toEqual(['admitted']);
      if (method === 'write' || method === 'end') expect(rejected).toHaveBeenCalledOnce();
      wire.drainCallbacks();
    },
  );
});
