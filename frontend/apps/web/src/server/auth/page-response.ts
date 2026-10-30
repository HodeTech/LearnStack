import type { ServerResponse } from 'node:http';

/** Native response fence. No buffering, substituted backpressure or cleanup inferred from abort. */
export function protectPageResponse(
  response: ServerResponse,
  check: () => void,
  started: () => void,
  beginWrite: () => () => void,
) {
  const writeHead = response.writeHead;
  const flushHeaders = response.flushHeaders;
  const write = response.write;
  const end = response.end;
  let refusing = false;
  const refuse = () => {
    if (refusing || response.destroyed || response.writableEnded) return;
    refusing = true;
    if (response.headersSent) {
      response.destroy();
      return;
    }
    for (const name of response.getHeaderNames()) response.removeHeader(name);
    Reflect.apply(writeHead, response, [
      503,
      { 'cache-control': 'no-store', 'content-length': '0' },
    ]);
    Reflect.apply(end, response, []);
  };
  const allowed = () => {
    // Node can drop writes without invoking their callbacks after socket.destroy()
    // but before response.close. Do not acquire work that can never settle.
    if (refusing || response.destroyed || response.socket?.destroyed) return false;
    try {
      check();
      return true;
    } catch {
      refuse();
      return false;
    }
  };
  const rejectCallback = (args: unknown[]) => {
    const callback = args[args.length - 1];
    if (typeof callback === 'function')
      queueMicrotask(() =>
        Reflect.apply(callback, response, [new Error('Native page output closed')]),
      );
  };
  const tracked = (method: ServerResponse['write'] | ServerResponse['end'], args: unknown[]) => {
    const complete = beginWrite();
    const last = args[args.length - 1];
    const callback =
      typeof last === 'function' ? (args.pop() as (...values: unknown[]) => void) : undefined;
    let settled = false;
    const done = () => {
      if (!settled) {
        settled = true;
        complete();
      }
    };
    args.push(function (this: ServerResponse, ...values: unknown[]) {
      try {
        callback?.apply(this, values);
      } finally {
        done();
      }
    });
    try {
      return Reflect.apply(method, response, args);
    } catch (error) {
      done();
      throw error;
    }
  };
  response.writeHead = function (this: ServerResponse, ...args: unknown[]) {
    if (!allowed()) return this;
    started();
    return Reflect.apply(writeHead, this, args);
  } as ServerResponse['writeHead'];
  response.flushHeaders = function (this: ServerResponse) {
    if (!allowed()) return;
    started();
    return flushHeaders.call(this);
  };
  response.write = function (this: ServerResponse, ...args: unknown[]) {
    if (!allowed()) {
      rejectCallback(args);
      return false;
    }
    started();
    return tracked(write, args) as boolean;
  } as ServerResponse['write'];
  response.end = function (this: ServerResponse, ...args: unknown[]) {
    if (!allowed()) {
      rejectCallback(args);
      return this;
    }
    started();
    return tracked(end, args) as ServerResponse;
  } as ServerResponse['end'];
  return Object.freeze({ refuse });
}
