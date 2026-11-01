// @vitest-environment node
import type { ServerResponse } from 'node:http';

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
    // Finish occurred, but the actual callbacks are deliberately still held.
    wire.destroy();
    expect(pending).toBe(2);
    wire.drainCallbacks();
    expect(pending).toBe(0);
    expect(written).toHaveBeenCalledOnce();
    expect(ended).toHaveBeenCalledOnce();
    expect(check).toHaveBeenCalled();
    expect(started).toHaveBeenCalled();
    expect(wire.listenerCount('finish')).toBe(0);
    expect(wire.listenerCount('close')).toBe(0);
  });

  it('retires only a close-before-finish end observation and preserves an issued write', () => {
    const wire = new SnapshotWire();
    wire.native.end = function (this: ServerResponse, ...args: unknown[]) {
      const callback = args[args.length - 1];
      if (typeof callback !== 'function') throw new Error('Expected end callback');
      this.once('finish', callback as () => void);
      wire.writableEnded = true;
      return this;
    };
    let pending = 0;
    protectPageResponse(
      wire.native,
      () => {},
      () => {},
      () => {
        ++pending;
        return () => {
          --pending;
        };
      },
    );
    const written = vi.fn();
    const ended = vi.fn();
    wire.native.write('issued', written);
    expect(wire.native.end('last', 'utf8', ended)).toBe(wire);
    expect(pending).toBe(2);
    wire.destroy();
    expect(pending).toBe(1);
    expect(ended).not.toHaveBeenCalled();
    expect(written).not.toHaveBeenCalled();
    expect(wire.listenerCount('finish')).toBe(0);
    expect(wire.listenerCount('close')).toBe(0);
    wire.drainCallbacks();
    expect(pending).toBe(0);
    expect(written).toHaveBeenCalledOnce();
    wire.emit('close');
    wire.emit('finish');
    expect(pending).toBe(0);
    expect(ended).not.toHaveBeenCalled();
  });

  it.each(['empty', 'callback', 'body', 'encoding', 'body-callback', 'encoding-callback'])(
    'preserves the native end overload: %s',
    (overload) => {
      const wire = new SnapshotWire();
      let pending = 0;
      protectPageResponse(
        wire.native,
        () => {},
        () => {},
        () => {
          ++pending;
          return () => {
            --pending;
          };
        },
      );
      const callback = vi.fn();
      const args: unknown[] =
        overload === 'empty' ? [] : overload === 'callback' ? [callback] : ['body'];
      if (overload.startsWith('encoding')) args.push('utf8');
      if (overload.endsWith('-callback')) args.push(callback);
      expect(Reflect.apply(wire.native.end, wire, args)).toBe(wire);
      expect(pending).toBe(1);
      wire.drainCallbacks();
      expect(pending).toBe(0);
      expect(callback).toHaveBeenCalledTimes(overload.includes('callback') ? 1 : 0);
      expect(wire.listenerCount('finish')).toBe(0);
      expect(wire.listenerCount('close')).toBe(0);
    },
  );

  it('removes end observers and releases credit after a synchronous native throw', () => {
    const wire = new SnapshotWire();
    wire.native.end = () => {
      throw new Error('Native end refused');
    };
    let pending = 0;
    protectPageResponse(
      wire.native,
      () => {},
      () => {},
      () => {
        ++pending;
        return () => {
          --pending;
        };
      },
    );
    const callback = vi.fn();
    expect(() => wire.native.end(callback)).toThrow('Native end refused');
    expect(pending).toBe(0);
    expect(wire.listenerCount('finish')).toBe(0);
    expect(wire.listenerCount('close')).toBe(0);
    wire.destroy();
    expect(pending).toBe(0);
    expect(callback).not.toHaveBeenCalled();
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
