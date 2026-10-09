// @vitest-environment node
import { createHash } from 'node:crypto';
import { createServer } from 'node:http';
import type { IncomingMessage } from 'node:http';
import { PassThrough } from 'node:stream';

import { afterEach, describe, expect, it, vi } from 'vitest';

import { delegateDevelopmentHmr } from '../../scripts/development-hmr.mjs';

const key = Buffer.alloc(16, 7).toString('base64');
const request = {
  method: 'GET',
  url: '/_next/webpack-hmr?client=1',
  headers: {
    upgrade: 'WebSocket',
    connection: 'keep-alive, Upgrade',
    'sec-websocket-version': '13',
    'sec-websocket-key': key,
  },
} as IncomingMessage;
const accept = createHash('sha1')
  .update(key + '258EAFA5-E914-47DA-95CA-C5AB0DC85B11')
  .digest('base64');
const handshake =
  'HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\n' +
  'Connection: Upgrade\r\nSec-WebSocket-Accept: ' +
  accept +
  '\r\n\r\n';

afterEach(() => vi.useRealTimers());

describe('development HMR handshake ownership', () => {
  it.each([
    { method: 'POST' },
    { url: '/_next/webpack-hmr-extra' },
    { url: '/_next/webpack-hmr/child' },
    { headers: { ...request.headers, upgrade: 'other' } },
    { headers: { ...request.headers, connection: 'keep-alive' } },
    { headers: { ...request.headers, 'sec-websocket-version': '8' } },
    { headers: { ...request.headers, 'sec-websocket-key': 'malformed' } },
  ])('refuses unsupported upgrade before delegation: %j', (override) => {
    const socket = new PassThrough();
    const sink = createServer();
    const delegate = vi.fn();
    sink.on('upgrade', delegate);
    delegateDevelopmentHmr(
      { ...request, ...override } as IncomingMessage,
      socket,
      Buffer.alloc(0),
      sink,
    );
    expect(socket.destroyed).toBe(true);
    expect(delegate).not.toHaveBeenCalled();
  });

  it('restores the writer after a split valid 101 and leaves established HMR alive', async () => {
    vi.useFakeTimers();
    const socket = new PassThrough();
    socket.resume();
    const originalWrite = socket.write;
    const sink = createServer();
    let callbackRan = false;
    let result: boolean | undefined;
    sink.on('upgrade', () => {
      result = socket.write(handshake.slice(0, 40));
      socket.write(Buffer.from(handshake.slice(40)), () => {
        callbackRan = true;
      });
    });
    delegateDevelopmentHmr(request, socket, Buffer.alloc(0), sink, 20);
    expect(result).toBe(true);
    expect(socket.write).toBe(originalWrite);
    await vi.advanceTimersByTimeAsync(30);
    expect(callbackRan).toBe(true);
    expect(socket.destroyed).toBe(false);
    expect(socket.listenerCount('error')).toBe(0);
    socket.destroy();
  });

  it.each(['missing', 'ignored', 'throws', 'wrong-accept', 'non-101', 'oversized'])(
    'closes %s delegation rather than treating emit as successful handoff',
    async (mode) => {
      vi.useFakeTimers();
      const socket = new PassThrough();
      socket.resume();
      const originalWrite = socket.write;
      const sink = createServer();
      if (mode !== 'missing')
        sink.on('upgrade', () => {
          if (mode === 'throws') throw new Error('Fixture delegation failed');
          if (mode === 'wrong-accept') socket.write(handshake.replace(accept, 'incorrect'));
          if (mode === 'non-101') socket.write('HTTP/1.1 400 Bad Request\r\n\r\n');
          if (mode === 'oversized') socket.write('x'.repeat(8193));
        });
      delegateDevelopmentHmr(request, socket, Buffer.alloc(0), sink, 20);
      if (mode === 'ignored') expect(socket.destroyed).toBe(false);
      await vi.advanceTimersByTimeAsync(21);
      expect(socket.destroyed).toBe(true);
      expect(socket.write).toBe(originalWrite);
      expect(socket.listenerCount('error')).toBe(0);
    },
  );

  it.each(['close', 'error'])('cleans its timer and writer on early %s', (event) => {
    vi.useFakeTimers();
    const socket = new PassThrough();
    const originalWrite = socket.write;
    const sink = createServer();
    sink.on('upgrade', () => {});
    delegateDevelopmentHmr(request, socket, Buffer.alloc(0), sink, 20);
    socket.emit(event, ...(event === 'error' ? [new Error('Fixture socket error')] : []));
    expect(socket.write).toBe(originalWrite);
    expect(vi.getTimerCount()).toBe(0);
    socket.destroy();
  });
});
