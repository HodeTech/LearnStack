// @vitest-environment node
import { createServer, request } from 'node:http';
import type { ServerResponse } from 'node:http';
import type { Socket } from 'node:net';
import { setImmediate as nextTurn } from 'node:timers/promises';
import { createGzip } from 'node:zlib';

import { describe, expect, it } from 'vitest';

import {
  createResponseCompressionOwner,
  joinResponseCompression,
} from '../../../../framework/next-response-cleanup/owner.mjs';
import { protectPageResponse } from '../server/auth/page-response';

function heldRead() {
  let controller!: ReadableStreamDefaultController<Uint8Array>;
  const reader = new ReadableStream<Uint8Array>({
    start(value) {
      controller = value;
    },
  }).getReader();
  const actual = reader.read();
  let state = 'pending';
  void actual.then(
    () => {
      state = 'fulfilled';
    },
    () => {
      state = 'rejected';
    },
  );
  return { actual, controller, state: () => state, release: () => reader.releaseLock() };
}

async function fixture() {
  let accepted!: (response: ServerResponse) => void;
  let responseClosed!: () => void;
  let firstData!: () => void;
  const responseReady = new Promise<ServerResponse>((resolve) => (accepted = resolve));
  const closed = new Promise<void>((resolve) => (responseClosed = resolve));
  const data = new Promise<void>((resolve) => (firstData = resolve));
  const sockets = new Set<Socket>();
  let credits = 0;
  const server = createServer((_request, response) => {
    response.on('error', () => {});
    response.once('close', responseClosed);
    protectPageResponse(
      response,
      () => {},
      () => {},
      () => {
        credits++;
        return () => credits--;
      },
    );
    accepted(response);
  });
  server.on('connection', (socket) => {
    sockets.add(socket);
    socket.once('close', () => sockets.delete(socket));
  });
  await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve));
  const address = server.address();
  if (!address || typeof address === 'string') throw new Error('Owned listener missing');
  const client = request({ hostname: '127.0.0.1', port: address.port, agent: false }, (peer) => {
    peer.on('error', () => {});
    peer.once('data', firstData);
    peer.resume();
  });
  client.on('error', () => {});
  client.end();
  const response = await responseReady;
  const owner = createResponseCompressionOwner(response);
  return {
    response,
    owner,
    closed,
    data,
    credits: () => credits,
    allocate() {
      response.setHeader('Content-Type', 'text/plain');
      response.setHeader('Content-Encoding', 'gzip');
      const encoder = owner.createCompressor(() => createGzip());
      encoder.pipe(response);
      return encoder;
    },
    async dispose() {
      client.destroy();
      response.destroy();
      const disposed = new Promise<void>((resolve) => server.close(() => resolve()));
      for (const socket of sockets) socket.destroy();
      await disposed;
      await closed;
    },
  };
}

function watch<T>(actual: Promise<T>) {
  let state = 'pending';
  void actual.then(
    () => {
      state = 'fulfilled';
    },
    () => {
      state = 'rejected';
    },
  );
  return () => state;
}

describe('explicit actual end work independent of compression cleanup', () => {
  it.each([undefined, null, new Error('Actual source fault')])(
    'keeps passed read work after actual source error %s and encoder close',
    async (primary) => {
      const wire = await fixture();
      const end = heldRead();
      try {
        const encoder = wire.allocate();
        let source!: ReadableStreamDefaultController<Uint8Array>;
        const body = new ReadableStream<Uint8Array>({
          start(controller) {
            source = controller;
            controller.enqueue(Buffer.alloc(8192, 65));
          },
        });
        const operation = body.pipeTo(
          new WritableStream<Uint8Array>({
            write(chunk) {
              encoder.write(chunk);
              encoder.flush();
            },
            abort() {
              wire.response.destroy();
            },
          }),
        );
        const joined = joinResponseCompression(wire.response, operation, end.actual);
        const state = watch(joined);
        await wire.data;
        source.error(primary);
        await wire.closed;
        await wire.owner.join();
        expect(encoder.closed).toBe(true);
        expect(wire.credits()).toBe(0);
        expect(state()).toBe('pending');
        expect(end.state()).toBe('pending');
        end.controller.close();
        await expect(joined).rejects.toBe(primary);
        expect(wire.owner.failureState()).toMatchObject({
          hasOperationFailure: true,
          operationFailure: primary,
        });
      } finally {
        if (end.state() === 'pending') end.controller.close();
        await wire.dispose();
        end.release();
      }
    },
  );

  it('retains end work through a genuine signal abort with the original return policy', async () => {
    const wire = await fixture();
    const end = heldRead();
    try {
      const encoder = wire.allocate();
      const signal = new AbortController();
      const body = new ReadableStream<Uint8Array>({
        start(controller) {
          controller.enqueue(Buffer.alloc(8192, 65));
        },
      });
      const operation = body
        .pipeTo(
          new WritableStream<Uint8Array>({
            write(chunk) {
              encoder.write(chunk);
              encoder.flush();
            },
            abort() {
              wire.response.destroy();
            },
          }),
          { signal: signal.signal },
        )
        .catch((error: unknown) => {
          if (error === signal.signal.reason) {
            wire.owner.abort(error);
            return;
          }
          throw error;
        });
      const joined = joinResponseCompression(wire.response, operation, end.actual);
      const state = watch(joined);
      await wire.data;
      signal.abort();
      await wire.closed;
      await wire.owner.join();
      expect(encoder.closed).toBe(true);
      expect(state()).toBe('pending');
      expect(wire.credits()).toBe(0);
      end.controller.close();
      await expect(joined).resolves.toBeUndefined();
    } finally {
      if (end.state() === 'pending') end.controller.close();
      await wire.dispose();
      end.release();
    }
  });

  it('retains explicit end work on an already-destroyed response early return', async () => {
    const wire = await fixture();
    const end = heldRead();
    try {
      wire.response.destroy();
      await wire.closed;
      const joined = joinResponseCompression(
        wire.response,
        () => {
          if (wire.response.destroyed) return 'actual early return';
          throw new Error('Response should already be destroyed');
        },
        end.actual,
      );
      const state = watch(joined);
      await wire.owner.join();
      await nextTurn();
      expect(state()).toBe('pending');
      expect(wire.credits()).toBe(0);
      end.controller.close();
      await expect(joined).resolves.toBe('actual early return');
    } finally {
      if (end.state() === 'pending') end.controller.close();
      await wire.dispose();
      end.release();
    }
  });

  it.each([undefined, null, new Error('Actual end fault')])(
    'observes end rejection %s immediately while actual operation work remains pending',
    async (endError) => {
      const wire = await fixture();
      const end = heldRead();
      const operation = heldRead();
      try {
        wire.owner.markNoCompression();
        const joined = joinResponseCompression(wire.response, operation.actual, end.actual);
        const state = watch(joined);
        end.controller.error(endError);
        await nextTurn();
        expect(state()).toBe('pending');
        expect(operation.state()).toBe('pending');
        expect(wire.owner.failureState()).toMatchObject({
          hasEndFailure: true,
          endFailure: endError,
          hasCleanupFailure: false,
        });
        operation.controller.close();
        await expect(joined).rejects.toBe(endError);
        // An empty owner still permits the real native fallback response.
        expect(wire.response.destroyed).toBe(false);
        wire.response.writeHead(503, { 'Content-Length': '0' });
        wire.response.end();
        await wire.closed;
        expect(wire.credits()).toBe(0);
      } finally {
        if (end.state() === 'pending') end.controller.close();
        if (operation.state() === 'pending') operation.controller.close();
        await wire.dispose();
        end.release();
        operation.release();
      }
    },
  );

  it('preserves the upstream source primary and bounded separate end and cleanup failures', async () => {
    const wire = await fixture();
    const end = heldRead();
    try {
      const encoder = wire.allocate();
      const cause = new Error('Source cause');
      const primary = new Error('Upstream causal wrapper', { cause });
      const endError = new Error('End work fault');
      const cleanupError = new Error('Cleanup fault');
      const source = new ReadableStream({
        start(controller) {
          controller.error(cause);
        },
      });
      const operation = source
        .pipeTo(
          new WritableStream({
            abort() {
              wire.response.destroy();
            },
          }),
        )
        .catch(() => {
          throw primary;
        });
      wire.owner.recordCleanupFailure(cleanupError);
      const joined = joinResponseCompression(wire.response, operation, end.actual);
      const state = watch(joined);
      await wire.closed;
      await expect(wire.owner.join()).rejects.toBe(cleanupError);
      expect(encoder.closed).toBe(true);
      expect(state()).toBe('pending');
      end.controller.error(endError);
      await expect(joined).rejects.toBe(primary);
      wire.owner.recordEndFailure(new Error('Later end fault'));
      expect(wire.owner.failureState()).toMatchObject({
        operationFailure: primary,
        endFailure: endError,
        cleanupFailure: cleanupError,
        hasEndFailure: true,
      });
      expect(primary.cause).toBe(cause);
      expect(wire.credits()).toBe(0);
    } finally {
      if (end.state() === 'pending') end.controller.close();
      await wire.dispose();
      end.release();
    }
  });
});
