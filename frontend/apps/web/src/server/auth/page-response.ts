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
  const tracked = (
    method: ServerResponse['write'] | ServerResponse['end'],
    args: unknown[],
    ending = false,
  ) => {
    const complete = beginWrite();
    const last = args[args.length - 1];
    const callback =
      typeof last === 'function' ? (args.pop() as (...values: unknown[]) => void) : undefined;
    let settled = false;
    let finished = false;
    const done = () => {
      if (!settled) {
        settled = true;
        if (ending) {
          response.removeListener('finish', endFinished);
          response.removeListener('close', endClosed);
        }
        complete();
      }
    };
    const completed = function (this: ServerResponse, ...values: unknown[]) {
      try {
        callback?.apply(this, values);
      } finally {
        done();
      }
    };
    const endFinished = () => {
      finished = true;
      response.removeListener('close', endClosed);
    };
    const endClosed = () => {
      // Native end's callback observes successful finish; a TLS error can close
      // the actual response without that event, even with writableFinished=true.
      // Only this failed end observation retires here. Issued write callbacks and
      // finish-triggered callbacks still settle themselves; cancellation is not cleanup.
      if (!finished) {
        response.removeListener('finish', completed);
        done();
      }
    };
    if (ending) {
      response.once('finish', endFinished);
      response.once('close', endClosed);
    }
    args.push(completed);
    try {
      return Reflect.apply(method, response, args);
    } catch (error) {
      if (ending) response.removeListener('finish', completed);
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
    return tracked(end, args, true) as ServerResponse;
  } as ServerResponse['end'];
  return Object.freeze({ refuse });
}
