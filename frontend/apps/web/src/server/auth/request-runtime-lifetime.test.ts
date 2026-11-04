// @vitest-environment node
import { execFileSync } from 'node:child_process';
import { chmodSync, mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { createServer, request } from 'node:http';
import type { ServerResponse } from 'node:http';
import { createServer as createHttpsServer } from 'node:https';
import { createConnection } from 'node:net';
import type { Socket } from 'node:net';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import type { Duplex } from 'node:stream';
import { connect as connectTls } from 'node:tls';
import type { TLSSocket } from 'node:tls';

import { describe, expect, it, vi } from 'vitest';

import {
  heldWork,
  snapshotBinding,
  snapshotRequest,
  SnapshotWire,
} from '../../test/page-response-fixture';
import { createPublicAdmissionRuntime } from '../public-admission-runtime';
import type { NativeEntrySnapshot, PageSite } from './page-contract';
import type { NativePageLease, NativeSnapshotTicket } from './page-snapshot-contract';
import { createBffRequestRuntime } from './request-runtime';

const site: PageSite = Object.freeze({
  displayName: 'School',
  defaultLocale: 'en',
  enabledLocales: ['en'],
  theme: null,
  showPlatformAttribution: true,
});
const entry: NativeEntrySnapshot = {
  site,
  locale: 'en',
  view: { kind: 'entry', entry: null, cursor: null },
};
function fixture() {
  const admission = createPublicAdmissionRuntime(true);
  const runtime = createBffRequestRuntime(admission.holder);
  const run = <T>(wire: SnapshotWire, action: () => T, binding = snapshotBinding) => {
    const request = snapshotRequest();
    return admission.run(binding, request, wire.native, () => {
      admission.holder.begin(binding).publish(site);
      return runtime.run(binding, request, wire.native, action);
    });
  };
  return { runtime, run };
}

describe('actual native snapshot request lifetime', () => {
  it.each([
    'healthy',
    'tls_error',
    'tls_error_handler',
    'tls_error_framework',
    'tls_error_write',
    'tls_error_api',
  ] as const)(
    'joins actual HTTPS end settlement without abandoning other work (%s)',
    async (mode) => {
      const root = mkdtempSync(join(tmpdir(), 'learnstack-end-lifetime-'));
      const admission = createPublicAdmissionRuntime(true);
      const runtime = createBffRequestRuntime(admission.holder);
      const held = mode !== 'healthy' && mode !== 'tls_error';
      const holdWrite = mode === 'tls_error_write';
      const handler = heldWork();
      const framework = heldWork();
      const nested = heldWork();
      const api = heldWork();
      const issued = vi.fn();
      const ended = vi.fn();
      const sockets = new Set<Duplex>();
      let server: ReturnType<typeof createHttpsServer> | undefined;
      let tcp: Socket | undefined;
      let client: TLSSocket | undefined;
      let response: ServerResponse | undefined;
      let releaseIssued: (() => void) | undefined;
      let finishSeen = false;
      let closeSeen = false;
      let received = 0;
      let timer: ReturnType<typeof setTimeout> | undefined;
      let served!: () => void;
      let failed!: (error: unknown) => void;
      const handled = new Promise<void>((resolve, reject) => {
        served = resolve;
        failed = reject;
      });
      let closed!: () => void;
      const responseClosed = new Promise<void>((resolve) => {
        closed = resolve;
      });
      let clientClosed!: () => void;
      const peerClosed = new Promise<void>((resolve) => {
        clientClosed = resolve;
      });
      try {
        const key = join(root, 'key.pem');
        const cert = join(root, 'cert.pem');
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
            '-days',
            '1',
            '-subj',
            '/CN=localhost',
            '-addext',
            'subjectAltName=DNS:localhost,IP:127.0.0.1',
          ],
          { timeout: 10_000, stdio: 'ignore' },
        );
        chmodSync(key, 0o600);
        chmodSync(cert, 0o600);
        const certificate = readFileSync(cert);
        server = createHttpsServer(
          { key: readFileSync(key), cert: certificate },
          (incoming, outgoing) => {
            response = outgoing;
            outgoing.on('error', () => {});
            outgoing.socket!.on('error', () => {});
            outgoing.once('finish', () => {
              finishSeen = true;
            });
            outgoing.once('close', () => {
              closeSeen = true;
              closed();
              // The server's actual terminal event precedes disposal of this paused peer.
              if (mode !== 'healthy') {
                client?.destroy();
                tcp?.destroy();
              }
            });
            if (holdWrite) {
              const write = outgoing.write;
              outgoing.write = function (this: ServerResponse, ...args: unknown[]) {
                const callback = args.pop();
                if (typeof callback !== 'function') throw new Error('Expected issued callback');
                args.push((...values: unknown[]) => {
                  releaseIssued = () => {
                    releaseIssued = undefined;
                    Reflect.apply(callback, this, values);
                  };
                });
                return Reflect.apply(write, this, args) as boolean;
              } as ServerResponse['write'];
            }
            try {
              admission.run(snapshotBinding, incoming, outgoing, () => {
                admission.holder.begin(snapshotBinding).publish(site);
                runtime.run(snapshotBinding, incoming, outgoing, () => {
                  if (held) {
                    const lease = runtime.preparation.reserve(
                      runtime.preparation.current(snapshotBinding),
                      {},
                    );
                    if (mode === 'tls_error_api') lease.track(api.promise);
                    lease.pin(() => true, 30_000);
                    lease.publishEntry(entry);
                    if (mode === 'tls_error_framework') {
                      const context = runtime.framework.get()!;
                      context.waitUntil(
                        framework.promise.then(() => {
                          context.waitUntil(nested.promise);
                        }),
                      );
                    }
                  } else runtime.holder.publish(snapshotBinding, null);
                  outgoing.setHeader('content-type', 'application/octet-stream');
                  outgoing.setHeader('connection', 'close');
                  if (holdWrite) outgoing.write('x', issued);
                  const payload = Buffer.alloc(16 * 1024 * 1024 - (holdWrite ? 1 : 0), 97);
                  expect(outgoing.end(payload, ended)).toBe(outgoing);
                  expect(outgoing.destroyed).toBe(false);
                  expect(outgoing.socket!.destroyed).toBe(false);
                  expect(outgoing.writableFinished).toBe(false);
                  expect(outgoing.writableLength).toBeGreaterThan(outgoing.writableHighWaterMark);
                  expect(ended).not.toHaveBeenCalled();
                  if (mode !== 'healthy') {
                    // A native TLS protocol error, after a genuinely pending end.
                    // Ordinary peer reset is not assumed to suppress finish.
                    tcp!.write(Buffer.from([0x17, 0x03, 0x03, 0x00, 0x01, 0x00]));
                  }
                  return mode === 'tls_error_handler' ? handler.promise : undefined;
                });
              });
              served();
            } catch (error) {
              failed(error);
            }
          },
        );
        server.on('connection', (socket) => {
          sockets.add(socket);
          socket.on('error', () => {});
          socket.once('close', () => sockets.delete(socket));
        });
        await new Promise<void>((resolve, reject) => {
          server!.once('error', reject);
          server!.listen(0, '127.0.0.1', resolve);
        });
        const address = server.address();
        if (!address || typeof address === 'string') throw new Error('Expected HTTPS listener');
        tcp = createConnection({ host: '127.0.0.1', port: address.port });
        client = connectTls({
          socket: tcp,
          servername: 'localhost',
          ca: certificate,
          rejectUnauthorized: true,
        });
        client.on('error', failed);
        client.on('data', (chunk: Buffer) => {
          received += chunk.length;
        });
        client.once('close', clientClosed);
        client.once('secureConnect', () => {
          if (mode !== 'healthy') client!.pause();
          client!.write('GET /studio HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n');
        });
        await Promise.race([
          Promise.all([handled, responseClosed, peerClosed]),
          new Promise<never>((_, reject) => {
            timer = setTimeout(() => reject(new Error('HTTPS lifetime deadline')), 5000);
          }),
        ]);
        clearTimeout(timer);
        expect(client.authorized).toBe(true);
        expect(closeSeen).toBe(true);
        expect(finishSeen).toBe(mode === 'healthy');
        expect(ended).toHaveBeenCalledTimes(mode === 'healthy' ? 1 : 0);
        if (mode === 'healthy') expect(received).toBeGreaterThanOrEqual(16 * 1024 * 1024);
        let stopped = false;
        const stopping = runtime.stop().then(() => {
          stopped = true;
        });
        const turn = () => new Promise<void>((resolve) => setImmediate(resolve));
        if (held) {
          await turn();
          expect(stopped).toBe(false);
          expect(runtime.counts()).toBe(mode === 'tls_error_api' ? 0 : 1);
          expect(runtime.snapshotCounts().active).toBe(1);
          expect(issued).not.toHaveBeenCalled();
          if (mode === 'tls_error_handler') handler.finish();
          else if (mode === 'tls_error_api') api.finish();
          else if (mode === 'tls_error_framework') {
            framework.finish();
            await turn();
            expect(stopped).toBe(false);
            expect(runtime.counts()).toBe(1);
            nested.finish();
          } else {
            await vi.waitFor(() => expect(releaseIssued).toBeTypeOf('function'));
            releaseIssued!();
            expect(issued).toHaveBeenCalledOnce();
          }
        }
        await vi.waitFor(() => expect(stopped).toBe(true));
        await stopping;
        expect(runtime.counts()).toBe(0);
        expect(runtime.snapshotCounts()).toEqual({ active: 0, owners: 0, bytes: 0 });
      } finally {
        clearTimeout(timer);
        handler.finish();
        framework.finish();
        nested.finish();
        api.finish();
        releaseIssued?.();
        client?.destroy();
        tcp?.destroy();
        response?.destroy();
        for (const socket of sockets) socket.destroy();
        admission.shutdown();
        // The pre-fix counter can retain an impossible end callback forever.
        // Its failed assertion must still close every physical test resource.
        void runtime.stop();
        if (server) await new Promise<void>((resolve) => server!.close(() => resolve()));
        await new Promise<void>((resolve) => setImmediate(resolve));
        rmSync(root, { recursive: true, force: true });
        expect(sockets.size).toBe(0);
        expect(server?.listening ?? false).toBe(false);
      }
    },
    20_000,
  );

  it.each([false, true])(
    'refuses destroyed-socket writes before close without abandoning an earlier callback (held=%s)',
    async (holdIssuedCallback) => {
      const admission = createPublicAdmissionRuntime(true);
      const runtime = createBffRequestRuntime(admission.holder);
      const rejectedWrite = vi.fn();
      const rejectedEnd = vi.fn();
      const issued = vi.fn();
      let releaseIssued: (() => void) | undefined;
      let response!: ServerResponse;
      let closed = false;
      let served!: () => void;
      let failed!: (error: unknown) => void;
      const handled = new Promise<void>((resolve, reject) => {
        served = resolve;
        failed = reject;
      });
      const server = createServer((incoming, outgoing) => {
        response = outgoing;
        outgoing.once('close', () => {
          closed = true;
        });
        if (holdIssuedCallback) {
          const write = outgoing.write;
          outgoing.write = function (this: ServerResponse, ...args: unknown[]) {
            const callback = args.pop();
            if (typeof callback !== 'function') throw new Error('Expected tracked callback');
            args.push((...values: unknown[]) => {
              releaseIssued = () => {
                releaseIssued = undefined;
                Reflect.apply(callback, this, values);
              };
            });
            return Reflect.apply(write, this, args) as boolean;
          } as ServerResponse['write'];
        }
        try {
          admission.run(snapshotBinding, incoming, outgoing, () => {
            admission.holder.begin(snapshotBinding).publish(site);
            runtime.run(snapshotBinding, incoming, outgoing, () => {
              const lease = runtime.preparation.reserve(
                runtime.preparation.current(snapshotBinding),
                {},
              );
              lease.pin(() => true, 30_000);
              lease.publishEntry(entry);
              if (holdIssuedCallback) outgoing.write('admitted', issued);
              outgoing.socket!.destroy();
              expect(closed).toBe(false);
              expect(outgoing.write('withheld', rejectedWrite)).toBe(false);
              expect(outgoing.end('withheld', rejectedEnd)).toBe(outgoing);
            });
          });
          served();
        } catch (error) {
          failed(error);
        }
      });
      try {
        await new Promise<void>((resolve, reject) => {
          server.once('error', reject);
          server.listen(0, '127.0.0.1', resolve);
        });
        const address = server.address();
        if (!address || typeof address === 'string') throw new Error('Expected local address');
        const completed = new Promise<void>((resolve) => {
          const call = request(
            { host: '127.0.0.1', port: address.port, path: '/studio', agent: false },
            (received) => {
              received.resume();
              received.once('end', resolve);
              received.once('aborted', resolve);
              received.once('error', () => resolve());
            },
          );
          call.once('error', () => resolve());
          call.end();
        });
        await handled;
        await completed;
        await vi.waitFor(() => {
          expect(closed).toBe(true);
          expect(response.destroyed).toBe(true);
          expect(rejectedWrite).toHaveBeenCalledExactlyOnceWith(expect.any(Error));
          expect(rejectedEnd).toHaveBeenCalledExactlyOnceWith(expect.any(Error));
        });
        if (holdIssuedCallback) {
          await vi.waitFor(() => expect(releaseIssued).toBeTypeOf('function'));
          expect(issued).not.toHaveBeenCalled();
          expect(runtime.counts()).toBe(1);
          expect(runtime.snapshotCounts().active).toBe(1);
          releaseIssued!();
          expect(issued).toHaveBeenCalledOnce();
        }
        await vi.waitFor(() => {
          expect(runtime.counts()).toBe(0);
          expect(runtime.snapshotCounts()).toEqual({ active: 0, owners: 0, bytes: 0 });
        });
        await runtime.stop();
      } finally {
        releaseIssued?.();
        response?.destroy();
        admission.shutdown();
        // A dirty implementation may strand a callback forever; do not let that
        // expected assertion failure turn into an unbounded test cleanup wait.
        void runtime.stop();
        await new Promise<void>((resolve) => server.close(() => resolve()));
      }
    },
  );

  it('rejects copied tickets and an authentic ticket borrowed by an equal-binding neighbor', async () => {
    const { runtime, run } = fixture();
    const first = new SnapshotWire();
    const second = new SnapshotWire();
    let ticket!: NativeSnapshotTicket;
    run(first, () => {
      ticket = runtime.preparation.current(snapshotBinding);
      expect(Object.isFrozen(ticket.binding)).toBe(true);
      expect(() => runtime.preparation.reserve({ ...ticket }, {})).toThrow();
      run(second, () => {
        expect(() => runtime.preparation.reserve(ticket, {})).toThrow();
        second.native.end();
      });
      const lease = runtime.preparation.reserve(ticket, {});
      lease.pin(() => true, 30000);
      lease.publishEntry(entry);
      expect(() => runtime.preparation.reserve(ticket, {})).toThrow();
      first.native.end();
    });
    first.drainCallbacks();
    second.drainCallbacks();
    await runtime.stop();
    expect(runtime.snapshotCounts().active).toBe(0);
  });

  it('holds credit after finish through actual handler, nested waitUntil, API and native write cleanup', async () => {
    const { runtime, run } = fixture();
    const wire = new SnapshotWire();
    const handler = heldWork();
    const framework = heldWork();
    const nested = heldWork();
    const api = heldWork();
    let lease!: NativePageLease;
    const running = run(wire, async () => {
      lease = runtime.preparation.reserve(runtime.preparation.current(snapshotBinding), {});
      lease.track(api.promise);
      lease.pin(() => true, 30000);
      lease.publishEntry(entry);
      const context = runtime.framework.get()!;
      context.waitUntil(
        framework.promise.then(() => {
          context.waitUntil(nested.promise);
        }),
      );
      wire.native.write('prepared');
      wire.native.end();
      await handler.promise;
    });
    let cleaned = false;
    void lease.cleaned.then(() => {
      cleaned = true;
    });
    const stopping = runtime.stop();
    handler.finish();
    await running;
    api.finish();
    framework.finish();
    await Promise.resolve();
    await Promise.resolve();
    expect(cleaned).toBe(false);
    expect(runtime.snapshotCounts().active).toBe(1);
    nested.finish();
    await Promise.resolve();
    await Promise.resolve();
    expect(cleaned).toBe(false);
    wire.drainCallbacks();
    await stopping;
    expect(cleaned).toBe(true);
    expect(runtime.counts()).toBe(0);
    expect(runtime.snapshotCounts()).toEqual({ active: 0, owners: 0, bytes: 0 });
  });

  it('does not release a non-page response merely because its handler or stop settles first', async () => {
    const { runtime, run } = fixture();
    const wire = new SnapshotWire();
    const handler = heldWork();
    const running = run(wire, async () => {
      runtime.holder.publish(snapshotBinding, null);
      wire.native.write('ordinary');
      await handler.promise;
    });
    let stopped = false;
    const stopping = runtime.stop().then(() => {
      stopped = true;
    });
    handler.finish();
    await running;
    await Promise.resolve();
    expect(stopped).toBe(false);
    wire.drainCallbacks();
    await stopping;
    expect(stopped).toBe(true);
  });
});
