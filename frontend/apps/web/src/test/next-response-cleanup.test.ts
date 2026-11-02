// @vitest-environment node
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { createServer, request } from 'node:http';
import type { IncomingMessage, ServerResponse } from 'node:http';
import { createServer as createSecureServer, request as secureRequest } from 'node:https';
import type { Socket } from 'node:net';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { setImmediate as nextTurn } from 'node:timers/promises';
import type { TLSSocket } from 'node:tls';
import { fileURLToPath } from 'node:url';
import { createDeflate, createGzip, gunzipSync, inflateSync } from 'node:zlib';

import { describe, expect, it } from 'vitest';

import {
  createResponseCompressionOwner,
  joinResponseCompression,
} from '../../../../framework/next-response-cleanup/owner.mjs';
import { protectPageResponse } from '../server/auth/page-response';

function held<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((complete) => {
    resolve = complete;
  });
  return { promise, resolve };
}

/** Actual Node transport: no fabricated terminal events or callbacks. */
async function openWire(protocol: 'http' | 'https' = 'http', tls?: { key: Buffer; cert: Buffer }) {
  const accepted = held<ServerResponse>();
  const responseClosed = held<void>();
  const received = held<void>();
  const events: string[] = [];
  const chunks: Buffer[] = [];
  const sockets = new Map<Socket, Promise<void>>();
  let status: number | undefined;
  let headers: Record<string, string | string[] | undefined> = {};
  let peerAuthorized: boolean | undefined;
  const listener = (_request: IncomingMessage, response: ServerResponse) => {
    response.once('finish', () => events.push('finish'));
    response.once('close', () => {
      events.push('close');
      responseClosed.resolve();
    });
    response.on('error', () => events.push('error'));
    accepted.resolve(response);
  };
  if (protocol === 'https' && !tls) throw new Error('Owned TLS material required');
  const server = protocol === 'https' ? createSecureServer(tls!, listener) : createServer(listener);
  server.on('connection', (socket) => {
    sockets.set(socket, new Promise<void>((resolve) => socket.once('close', () => resolve())));
  });
  await new Promise<void>((resolve, reject) => {
    server.once('error', reject);
    server.listen(0, '127.0.0.1', resolve);
  });
  const address = server.address();
  if (!address || typeof address === 'string') throw new Error('Owned listener unavailable');
  const requester = protocol === 'https' ? secureRequest : request;
  const client = requester(
    {
      hostname: '127.0.0.1',
      port: address.port,
      agent: false,
      ...(tls ? { servername: 'localhost', ca: tls.cert } : {}),
    },
    (response) => {
      status = response.statusCode;
      headers = response.headers;
      response.on('data', (chunk: Buffer) => chunks.push(chunk));
      response.once('end', () => received.resolve());
      response.on('error', () => events.push('peer-error'));
    },
  );
  client.on('socket', (socket) => {
    if (protocol === 'https')
      socket.once('secureConnect', () => {
        peerAuthorized = (socket as TLSSocket).authorized;
      });
  });
  client.on('error', () => events.push('client-error'));
  client.end();
  const response = await accepted.promise;
  return {
    response,
    client,
    events,
    responseClosed: responseClosed.promise,
    received: received.promise,
    wire: () => ({ status, headers, body: Buffer.concat(chunks) }),
    peerAuthorized: () => peerAuthorized,
    async dispose() {
      client.destroy();
      response.destroy();
      const closed = new Promise<void>((resolve, reject) => {
        server.close((error) => (error ? reject(error) : resolve()));
      });
      for (const socket of sockets.keys()) socket.destroy();
      await closed;
      await Promise.all(sockets.values());
    },
  };
}

describe('actual response compression ownership', () => {
  it.each(['http', 'https'] as const)(
    'refuses a new native write after actual %s socket end without inventing callback settlement',
    async (protocol) => {
      let directory: string | undefined;
      let tls: { key: Buffer; cert: Buffer } | undefined;
      let wire: Awaited<ReturnType<typeof openWire>> | undefined;
      try {
        if (protocol === 'https') {
          directory = mkdtempSync(join(tmpdir(), 'learnstack-next-response-tls-'));
          const key = join(directory, 'key.pem');
          const cert = join(directory, 'cert.pem');
          execFileSync(
            'openssl',
            [
              'req',
              '-x509',
              '-newkey',
              'rsa:2048',
              '-nodes',
              '-keyout',
              key,
              '-out',
              cert,
              '-subj',
              '/CN=localhost',
              '-days',
              '1',
              '-addext',
              'subjectAltName=DNS:localhost',
            ],
            { stdio: 'ignore', timeout: 5000 },
          );
          tls = { key: readFileSync(key), cert: readFileSync(cert) };
        }
        wire = await openWire(protocol, tls);
        let credits = 0;
        let nativeWrites = 0;
        const nativeWrite = wire.response.write;
        wire.response.write = function (this: ServerResponse, ...args: unknown[]) {
          nativeWrites++;
          return Reflect.apply(nativeWrite, this, args) as boolean;
        };
        protectPageResponse(
          wire.response,
          () => {},
          () => {},
          () => {
            credits++;
            return () => {
              credits--;
            };
          },
        );
        const owner = createResponseCompressionOwner(wire.response);
        const encoder = owner.createCompressor(() => createGzip());
        let actualEncoderClose = false;
        encoder.once('close', () => {
          actualEncoderClose = true;
        });
        const socket = wire.response.socket!;
        socket.end();
        expect(socket.destroyed).toBe(false);
        expect(socket.writable).toBe(false);
        expect(socket.writableEnded).toBe(true);
        expect(wire.response.write(Buffer.from('late output'))).toBe(false);
        wire.response.end();
        expect(nativeWrites).toBe(0);
        expect(credits).toBe(0);
        await wire.responseClosed;
        await owner.join();
        expect(actualEncoderClose).toBe(true);
        expect(credits).toBe(0);
        if (protocol === 'https') expect(wire.peerAuthorized()).toBe(true);
      } finally {
        await wire?.dispose();
        if (directory) rmSync(directory, { recursive: true, force: true });
      }
    },
  );

  it('executes every qualified pipe copy and both exact router join expressions', () => {
    const directory = mkdtempSync(join(tmpdir(), 'learnstack-next-response-controls-'));
    try {
      const output = execFileSync(
        process.execPath,
        [fileURLToPath(new URL('../../scripts/test-next-response-cleanup.mjs', import.meta.url))],
        {
          cwd: directory,
          env: { PATH: process.env.PATH, NODE_ENV: 'production' },
          encoding: 'utf8',
          timeout: 25_000,
          maxBuffer: 1024 * 1024,
        },
      );
      expect(JSON.parse(output)).toEqual({
        kind: 'qualified-next-response-controls',
        cases: 440,
        actualPipeClosures: 27,
        drainCases: 189,
        drainCopies: 27,
        drainShapeCases: 6,
        drainRealCloseAndHandlerJoined: true,
        exactRouterInitializers: 2,
        healthyWireMatches: 29,
        negativeStockRetentionControls: 29,
        explicitEndWorkCases: 324,
        originalEndWorkEscapeControls: 135,
        patchedExplicitEndWorkJoined: true,
        patchedEndWorkCauseIdentityPreserved: true,
        patchedActualCloseBeforeHandler: true,
        esmModuleSyntaxAdapted: true,
        fullInitializedRouterQualified: false,
      });
    } finally {
      rmSync(directory, { recursive: true, force: true });
    }
  }, 30_000);

  it('reserves one immutable owner and refuses a second installation', async () => {
    const wire = await openWire();
    try {
      const owner = createResponseCompressionOwner(wire.response);
      const descriptor = Object.getOwnPropertyDescriptor(
        wire.response,
        Symbol.for('learnstack.next.responseCompressionOwner'),
      );
      expect(descriptor).toMatchObject({ writable: false, configurable: false, value: owner });
      expect(Object.isFrozen(owner)).toBe(true);
      expect(() => createResponseCompressionOwner(wire.response)).toThrow('Duplicate');
      expect(owner.join()).toBe(owner.join());
      owner.markNoCompression();
      wire.response.end();
      await owner.join();
      await wire.received;
    } finally {
      await wire.dispose();
    }
  });

  it.each(['gzip', 'deflate'] as const)(
    'joins actual %s close while preserving bytes and foreign drain listeners',
    async (encoding) => {
      const wire = await openWire();
      try {
        const payload = Buffer.alloc(8192, 65);
        const owner = createResponseCompressionOwner(wire.response);
        const stream = owner.createCompressor(() =>
          encoding === 'gzip' ? createGzip() : createDeflate(),
        );
        let closed = false;
        stream.once('close', () => {
          closed = true;
        });
        const foreign = () => {};
        const resume = () => stream.resume();
        wire.response.on('drain', foreign);
        expect(owner.setResponseDrainListener(resume)).toBe(resume);
        wire.response.on('drain', resume);
        expect(() => owner.setResponseDrainListener(() => {})).toThrow('registration');
        wire.response.setHeader('Content-Encoding', encoding);
        stream.on('data', (chunk: Buffer) => {
          if (!wire.response.write(chunk)) stream.pause();
        });
        stream.once('end', () => wire.response.end());
        stream.end(payload);
        await joinResponseCompression(wire.response, Promise.resolve());
        expect(closed).toBe(true);
        expect(wire.response.listeners('drain')).toEqual([foreign]);
        expect(() => owner.setResponseDrainListener(resume)).toThrow('registration');
        await wire.received;
        const result = wire.wire();
        expect(result.headers['content-encoding']).toBe(encoding);
        expect(encoding === 'gzip' ? gunzipSync(result.body) : inflateSync(result.body)).toEqual(
          payload,
        );
      } finally {
        await wire.dispose();
      }
    },
  );

  it('closes a never-ended encoder after actual native refusal finish then close', async () => {
    const wire = await openWire();
    try {
      let active = true;
      protectPageResponse(
        wire.response,
        () => {
          if (!active) throw new Error('Private invalidated output');
        },
        () => {},
        () => () => {},
      );
      const owner = createResponseCompressionOwner(wire.response);
      const stream = owner.createCompressor(() => createGzip());
      let actualClose = false;
      stream.once('close', () => {
        actualClose = true;
      });
      wire.response.setHeader('Content-Encoding', 'gzip');
      active = false;
      wire.response.end();
      await joinResponseCompression(wire.response, Promise.resolve());
      await wire.received;
      await wire.responseClosed;
      expect(actualClose).toBe(true);
      expect(stream.writableEnded).toBe(false);
      expect(wire.events.indexOf('finish')).toBeLessThan(wire.events.indexOf('close'));
      expect(wire.wire()).toMatchObject({ status: 503, headers: { 'content-length': '0' } });
      expect(wire.wire().headers['content-encoding']).toBeUndefined();
      expect(wire.wire().body.length).toBe(0);
    } finally {
      await wire.dispose();
    }
  });

  it('retains genuine end work after actual peer and encoder close', async () => {
    const wire = await openWire();
    const work = held<number>();
    try {
      const owner = createResponseCompressionOwner(wire.response);
      const stream = owner.createCompressor(() => createGzip());
      let actualClose = false;
      stream.once('close', () => {
        actualClose = true;
      });
      let joined = false;
      const completion = joinResponseCompression(wire.response, work.promise).then((value) => {
        joined = true;
        return value;
      });
      wire.client.destroy();
      await wire.responseClosed;
      await owner.join();
      await nextTurn();
      expect(actualClose).toBe(true);
      expect(wire.events).not.toContain('finish');
      expect(joined).toBe(false);
      work.resolve(42);
      await expect(completion).resolves.toBe(42);
    } finally {
      work.resolve(42);
      await wire.dispose();
    }
  });

  it.each([undefined, null, new Error('Private primary')])(
    'preserves primary identity and a separate cleanup failure %#',
    async (primary) => {
      const wire = await openWire();
      try {
        const owner = createResponseCompressionOwner(wire.response);
        const cleanup = new Error('Private cleanup');
        owner.recordCleanupFailure(cleanup);
        await expect(
          joinResponseCompression(wire.response, async () => {
            throw primary;
          }),
        ).rejects.toBe(primary);
        expect(owner.failureState()).toMatchObject({
          hasOperationFailure: true,
          operationFailure: primary,
          hasCleanupFailure: true,
          cleanupFailure: cleanup,
        });
        expect(wire.response.destroyed).toBe(false);
        expect(owner.allocationClosed()).toBe(true);
        owner.markNoCompression();
        owner.markNoCompression();
        wire.response.writeHead(503, { 'content-length': '0' });
        wire.response.end();
        await wire.received;
      } finally {
        await wire.dispose();
      }
    },
  );

  it('observes a genuine encoder error immediately and rejects only after actual close', async () => {
    const wire = await openWire();
    try {
      const owner = createResponseCompressionOwner(wire.response);
      const stream = owner.createCompressor(() => createGzip());
      let original: Error | undefined;
      let closed = false;
      stream.once('error', (error) => {
        original = error;
      });
      stream.once('close', () => {
        closed = true;
      });
      stream.end();
      stream.write(Buffer.from('actual write after end'));
      const failure = await joinResponseCompression(wire.response, Promise.resolve()).catch(
        (error: unknown) => error,
      );
      expect(failure).toBe(original);
      expect(original).toMatchObject({ code: 'ERR_STREAM_WRITE_AFTER_END' });
      expect(closed).toBe(true);
      await wire.responseClosed;
    } finally {
      await wire.dispose();
    }
  });

  it('cannot acknowledge empty cleanup during a reentrant factory failure', async () => {
    const wire = await openWire();
    try {
      const owner = createResponseCompressionOwner(wire.response);
      const primary = new Error('Private reentrant failure');
      let closed = false;
      await expect(
        joinResponseCompression(wire.response, () => {
          owner.createCompressor(() => {
            const stream = createGzip();
            stream.once('close', () => {
              closed = true;
            });
            stream.write(Buffer.alloc(8192));
            owner.fail(primary);
            return stream;
          });
          throw primary;
        }),
      ).rejects.toBe(primary);
      expect(closed).toBe(true);
      expect(owner.failureState().operationFailure).toBe(primary);
    } finally {
      await wire.dispose();
    }
  });

  it.each([{ level: 100 }, { chunkSize: 1 }])(
    'retains unknown construction cleanup after a real constructor throw %#',
    async (options) => {
      const wire = await openWire();
      try {
        const owner = createResponseCompressionOwner(wire.response);
        let primary: unknown;
        let settled = false;
        try {
          owner.createCompressor(() => createGzip(options));
        } catch (error) {
          primary = error;
        }
        expect(primary).toMatchObject({ code: 'ERR_OUT_OF_RANGE' });
        const retained = joinResponseCompression(wire.response, Promise.reject(primary));
        retained.then(
          () => {
            settled = true;
          },
          () => {
            settled = true;
          },
        );
        await wire.responseClosed;
        await nextTurn();
        expect(settled).toBe(false);
        expect(owner.failureState()).toMatchObject({
          hasOperationFailure: true,
          operationFailure: primary,
          hasCleanupFailure: true,
          cleanupFailure: primary,
          constructionUnconfirmed: true,
        });
        expect(() => owner.markNoCompression()).toThrow();
        let factoryCalled = false;
        expect(() =>
          owner.createCompressor(() => {
            factoryCalled = true;
            return createGzip();
          }),
        ).toThrow();
        expect(factoryCalled).toBe(false);
      } finally {
        await wire.dispose();
      }
    },
  );

  it('cancels a live response on explicit actual abort without inventing a resource', async () => {
    const wire = await openWire();
    try {
      const owner = createResponseCompressionOwner(wire.response);
      const controller = new AbortController();
      controller.abort();
      expect(wire.response.destroyed).toBe(false);
      owner.abort(controller.signal.reason);
      await owner.join();
      expect(wire.response.destroyed).toBe(true);
      expect(owner.failureState().operationFailure).toBe(controller.signal.reason);
      await wire.responseClosed;
      expect(wire.events).not.toContain('finish');
    } finally {
      await wire.dispose();
    }
  });

  it('does not interpret normal handler return as a no-resource disposition', async () => {
    const wire = await openWire();
    try {
      const owner = createResponseCompressionOwner(wire.response);
      let joined = false;
      const result = joinResponseCompression(wire.response, Promise.resolve(42)).then((value) => {
        joined = true;
        return value;
      });
      await nextTurn();
      expect(joined).toBe(false);
      owner.markNoCompression();
      await expect(result).resolves.toBe(42);
      let created = false;
      expect(() =>
        owner.createCompressor(() => {
          created = true;
          return createGzip();
        }),
      ).toThrow('allocation');
      expect(created).toBe(false);
      wire.response.end();
      await wire.received;
    } finally {
      await wire.dispose();
    }
  });

  it('does not erase an actual resource after its close', async () => {
    const wire = await openWire();
    try {
      const owner = createResponseCompressionOwner(wire.response);
      owner.createCompressor(() => createGzip());
      expect(() => owner.markNoCompression()).toThrow();
      wire.response.end();
      await owner.join();
      expect(() => owner.markNoCompression()).toThrow();
      await wire.received;
    } finally {
      await wire.dispose();
    }
  });

  it('allows a genuine direct consumer without compression middleware', async () => {
    const wire = await openWire();
    try {
      await expect(joinResponseCompression(wire.response, Promise.resolve(42))).resolves.toBe(42);
      wire.response.end();
      await wire.received;
    } finally {
      await wire.dispose();
    }
  });
});

// Each failing native removal happens after removing its exact owned callback.
// Later cleanup failures must not replace the first failure or prevent other removals.
it.each([
  ['finish', false],
  ['close', false],
  ['drain', false],
  ['finish', true],
  ['close', true],
  ['drain', true],
] as const)(
  'retires remaining owned callbacks after %s removal throws (primary=%s)',
  async (failedRemoval, withPrimary) => {
    const wire = await openWire();
    const responseRemove = wire.response.removeListener;
    let restoreStream: (() => void) | undefined;
    try {
      const foreign = () => {};
      for (const name of ['finish', 'close', 'drain']) wire.response.on(name, foreign);
      const owner = createResponseCompressionOwner(wire.response);
      const terminal = wire.response.listeners('finish').at(-1)!;
      const stream = owner.createCompressor(() => createGzip());
      const streamRemove = stream.removeListener;
      restoreStream = () => {
        stream.removeListener = streamRemove;
      };
      const ownedError = stream.listeners('error')[0]!;
      stream.on('error', foreign);
      const ownedDrain = owner.setResponseDrainListener(() => stream.resume());
      wire.response.on('drain', ownedDrain);
      const primary = new Error('Private removal primary');
      const cleanup = new Error('Private first removal failure');
      const later = new Error('Private later removal failure');
      const attempts: string[] = [];
      wire.response.removeListener = function (event, listener) {
        const result = Reflect.apply(responseRemove, this, [event, listener]) as typeof this;
        if (
          event === 'finish' || event === 'close'
            ? listener === terminal
            : event === 'drain' && listener === ownedDrain
        ) {
          attempts.push(String(event));
          if (event === failedRemoval) throw cleanup;
        }
        return result;
      };
      stream.removeListener = function (event, listener) {
        const result = Reflect.apply(streamRemove, this, [event, listener]) as typeof this;
        if (event === 'error' && listener === ownedError) {
          attempts.push('error');
          throw later;
        }
        return result;
      };
      const completion = joinResponseCompression(wire.response, async () => {
        if (withPrimary) throw primary;
        wire.response.end();
      });
      let observed: unknown;
      try {
        await completion;
      } catch (error) {
        observed = error;
      }
      assert.equal(observed, withPrimary ? primary : cleanup);
      assert.equal(stream.closed, true);
      assert.deepEqual(attempts, ['finish', 'close', 'drain', 'error']);
      await assert.rejects(owner.join(), (error) => error === cleanup);
      assert.equal(owner.failureState().hasCleanupFailure, true);
      assert.equal(owner.failureState().cleanupFailure, cleanup);
      assert.equal(owner.failureState().hasOperationFailure, withPrimary);
      assert.equal(owner.failureState().operationFailure, withPrimary ? primary : undefined);
      for (const name of ['finish', 'close']) {
        assert.equal(wire.response.listeners(name).includes(terminal), false);
        assert.equal(wire.response.listeners(name).includes(foreign), true);
      }
      assert.equal(wire.response.listeners('drain').includes(ownedDrain), false);
      assert.equal(wire.response.listeners('drain').includes(foreign), true);
      assert.equal(stream.listeners('error').includes(ownedError), false);
      assert.equal(stream.listeners('error').includes(foreign), true);
      await wire.responseClosed;
    } finally {
      wire.response.removeListener = responseRemove;
      restoreStream?.();
      await wire.dispose();
    }
  },
);
