import { EventEmitter } from 'node:events';
import { IncomingMessage } from 'node:http';
import type { ServerResponse } from 'node:http';
import { Socket } from 'node:net';

/** Controllable transport callbacks; close/finish never silently settle held writes. */
export class SnapshotWire extends EventEmitter {
  destroyed = false;
  writableEnded = false;
  headersSent = false;
  statusCode = 200;
  readonly chunks: string[] = [];
  readonly callbacks: (() => void)[] = [];
  readonly headers = new Map<string, unknown>();
  get native(): ServerResponse {
    return this as unknown as ServerResponse;
  }
  getHeaderNames() {
    return [...this.headers.keys()];
  }
  removeHeader(name: string) {
    this.headers.delete(name);
  }
  writeHead(status: number, fields?: Record<string, unknown>) {
    this.statusCode = status;
    if (fields) for (const [name, value] of Object.entries(fields)) this.headers.set(name, value);
    this.headersSent = true;
    return this;
  }
  flushHeaders() {
    if (!this.headersSent) this.writeHead(this.statusCode);
  }
  write(chunk: string, ...args: unknown[]) {
    this.flushHeaders();
    this.chunks.push(chunk);
    this.remember(args);
    return false;
  }
  end(...args: unknown[]) {
    this.flushHeaders();
    if (typeof args[0] === 'string') this.chunks.push(args[0]);
    this.remember(args);
    this.writableEnded = true;
    this.emit('finish');
    return this;
  }
  destroy() {
    if (!this.destroyed) {
      this.destroyed = true;
      this.emit('close');
    }
    return this;
  }
  drainCallbacks() {
    for (const callback of this.callbacks.splice(0)) callback();
  }
  private remember(args: unknown[]) {
    const callback = args[args.length - 1];
    if (typeof callback === 'function')
      this.callbacks.push(() => Reflect.apply(callback, this, []));
  }
}

export function heldWork() {
  let finish!: () => void;
  const promise = new Promise<void>((resolve) => {
    finish = resolve;
  });
  return { promise, finish };
}

export const snapshotBinding = Object.freeze({
  host: 'institution.example.test:3000',
  peer: '203.0.113.7',
  method: 'GET',
  target: '/studio',
});
export function snapshotRequest() {
  return new IncomingMessage(new Socket());
}
