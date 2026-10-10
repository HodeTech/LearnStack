// Test-only observer of the pinned Next compiler's real HMR completion frame.
import { createRequire } from 'node:module';
import { join } from 'node:path';

const DEADLINE_MS = 15_000;
const MAX_FRAME_BYTES = 256 * 1024;

export async function watchDevelopmentSource({ app, certificate, createSocket }) {
  let socket;
  try {
    const open =
      createSocket ??
      ((url, options) => {
        const WebSocket = createRequire(join(app, 'package.json'))('next/dist/compiled/ws');
        return new WebSocket(url, options);
      });
    socket = open('wss://127.0.0.1:3000/_next/webpack-hmr', {
      ca: certificate,
      servername: 'localhost',
      rejectUnauthorized: true,
      maxPayload: MAX_FRAME_BYTES,
    });
  } catch {
    throw new Error('Development HMR watcher unavailable');
  }

  let phase = 'connecting';
  let accepted = false;
  let opened = false;
  let synchronized = false;
  let resolveReady;
  let rejectReady;
  let pending;
  const ready = new Promise((resolve, reject) => {
    resolveReady = resolve;
    rejectReady = reject;
  });
  const readyTimer = setTimeout(() => stop(), DEADLINE_MS);

  function detach() {
    for (const [event, listener] of [
      ['upgrade', upgrade],
      ['open', open],
      ['message', message],
      ['error', error],
      ['close', close],
    ])
      socket.off(event, listener);
  }

  function stop(terminate = true) {
    if (phase === 'closed') return;
    const wasConnecting = phase === 'connecting';
    phase = 'closed';
    clearTimeout(readyTimer);
    const failure = new Error('Development HMR watcher failed');
    if (wasConnecting) rejectReady(failure);
    if (pending) {
      clearTimeout(pending.timer);
      pending.reject(failure);
      pending = undefined;
    }
    socket.off('upgrade', upgrade);
    socket.off('open', open);
    socket.off('message', message);
    if (terminate) {
      // ws may emit an error while terminating a connecting socket. Keep the
      // fixed error handler until close, then remove all owned listeners.
      try {
        socket.terminate();
      } catch {
        detach();
      }
    } else detach();
  }

  function completeReady() {
    if (phase !== 'connecting' || !accepted || !opened || !synchronized) return;
    if (socket.readyState !== 1) return stop();
    phase = 'ready';
    clearTimeout(readyTimer);
    resolveReady();
  }

  function upgrade(response) {
    if (response.statusCode !== 101) return stop();
    accepted = true;
    completeReady();
  }

  function open() {
    opened = true;
    completeReady();
  }

  function message(bytes) {
    let frame;
    try {
      const value = bytes.toString();
      if (Buffer.byteLength(value) > MAX_FRAME_BYTES) return stop();
      frame = JSON.parse(value);
      if (frame === null || typeof frame !== 'object' || Array.isArray(frame)) return stop();
    } catch {
      return stop();
    }
    if (frame.action === 'serverError' || (Array.isArray(frame.errors) && frame.errors.length > 0))
      return stop();
    if (frame.action === 'sync') {
      if (typeof frame.hash !== 'string' || !frame.hash.trim()) return stop();
      synchronized = true;
      completeReady();
    } else if (frame.action === 'serverComponentChanges' && pending) {
      if (typeof frame.hash !== 'string' || !frame.hash.trim()) return stop();
      clearTimeout(pending.timer);
      const resolve = pending.resolve;
      pending = undefined;
      resolve();
    }
    // 'built' is only the client compiler. Initial sync and earlier change
    // frames cannot release a waiter installed for a later source write.
  }

  function error() {
    stop();
  }

  function close() {
    stop(false);
    detach();
  }

  socket.on('upgrade', upgrade);
  socket.on('open', open);
  socket.on('message', message);
  socket.on('error', error);
  socket.on('close', close);
  await ready;

  return Object.freeze({
    afterWrite(writeSource) {
      if (phase !== 'ready' || pending) {
        stop();
        return Promise.reject(new Error('Development HMR watcher failed'));
      }
      const result = new Promise((resolve, reject) => {
        pending = {
          resolve,
          reject,
          timer: setTimeout(() => stop(), DEADLINE_MS),
        };
      });
      try {
        // Register first: a fast compiler can send its update during the write.
        writeSource();
      } catch {
        stop();
      }
      return result;
    },
    close: () => stop(),
  });
}
