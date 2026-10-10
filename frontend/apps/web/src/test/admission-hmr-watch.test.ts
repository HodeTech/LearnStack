// @vitest-environment node
import { EventEmitter } from 'node:events';

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { watchDevelopmentSource } from '../../scripts/admission-hmr-watch.mjs';

class FixtureSocket extends EventEmitter {
  readyState = 0;
  terminated = 0;

  terminate() {
    this.terminated++;
    this.readyState = 3;
    this.emit('close');
  }

  accept() {
    this.emit('upgrade', { statusCode: 101 });
    this.readyState = 1;
    this.emit('open');
  }

  frame(value: unknown) {
    this.emit('message', Buffer.from(JSON.stringify(value)));
  }
}

function start(socket = new FixtureSocket()) {
  const createSocket = vi.fn((_url: string, _options: unknown) => socket);
  const ready = watchDevelopmentSource({
    app: '/disposable/app',
    certificate: 'fixture CA',
    createSocket,
  });
  return { socket, ready, createSocket };
}

async function connected(socket = new FixtureSocket()) {
  const setup = start(socket);
  socket.accept();
  socket.frame({ action: 'sync', hash: 'initial-client-hash', errors: [] });
  return { ...setup, watcher: await setup.ready };
}

beforeEach(() => vi.useFakeTimers());
afterEach(() => {
  expect(vi.getTimerCount()).toBe(0);
  vi.useRealTimers();
});

describe('development source compilation barrier', () => {
  it('requires the real 101, open socket and sync before exposing the source writer', async () => {
    const { socket, ready, createSocket } = start();
    let completed = false;
    void ready.then(() => {
      completed = true;
    });
    socket.accept();
    socket.frame({ action: 'built', hash: 'client-only', errors: [] });
    await Promise.resolve();
    expect(completed).toBe(false);
    socket.frame({ action: 'sync', hash: 'initial', errors: [] });
    const watcher = await ready;
    expect(createSocket).toHaveBeenCalledWith('wss://127.0.0.1:3000/_next/webpack-hmr', {
      ca: 'fixture CA',
      servername: 'localhost',
      rejectUnauthorized: true,
      maxPayload: 256 * 1024,
    });
    watcher.close();
    expect(socket.terminated).toBe(1);
    expect(socket.eventNames()).toEqual([]);
  });

  it.each(['missing-upgrade', 'missing-open', 'missing-sync'])(
    'refuses %s at the bounded handshake deadline',
    async (mode) => {
      const { socket, ready } = start();
      const rejected = expect(ready).rejects.toThrow('Development HMR watcher failed');
      if (mode !== 'missing-upgrade') socket.emit('upgrade', { statusCode: 101 });
      if (mode !== 'missing-open') {
        socket.readyState = 1;
        socket.emit('open');
      }
      if (mode !== 'missing-sync') socket.frame({ action: 'sync', hash: 'initial' });
      await vi.advanceTimersByTimeAsync(15_000);
      await rejected;
      expect(socket.terminated).toBe(1);
      expect(socket.eventNames()).toEqual([]);
    },
  );

  it('ignores old changes, later sync and client builds until the server compilation completes', async () => {
    const { socket, watcher } = await connected();
    socket.frame({ action: 'serverComponentChanges', hash: 'old-server-hash' });
    let completed = false;
    const write = vi.fn();
    const changed = watcher.afterWrite(write).then(() => {
      completed = true;
    });
    expect(write).toHaveBeenCalledOnce();
    socket.frame({ action: 'sync', hash: 'new-client-hash', errors: [] });
    socket.frame({ action: 'building' });
    socket.frame({ action: 'built', hash: 'new-client-hash', errors: [] });
    await Promise.resolve();
    expect(completed).toBe(false);
    socket.frame({ action: 'serverComponentChanges', hash: 'new-multi-compiler-hash' });
    await changed;
    watcher.close();
    expect(socket.eventNames()).toEqual([]);
  });

  it('installs the waiter before invoking the write and supports a later independent write', async () => {
    const { socket, watcher } = await connected();
    await watcher.afterWrite(() => socket.frame({ action: 'serverComponentChanges', hash: 'one' }));
    await watcher.afterWrite(() => socket.frame({ action: 'serverComponentChanges', hash: 'two' }));
    watcher.close();
    watcher.close();
    expect(socket.terminated).toBe(1);
  });

  it.each(['no-frame', 'sync-only', 'built-only'])(
    'fails %s at the source-change deadline instead of allowing a speculative HTTP retry',
    async (mode) => {
      const { socket, watcher } = await connected();
      const changed = watcher.afterWrite(() => {});
      const rejected = expect(changed).rejects.toThrow('Development HMR watcher failed');
      if (mode === 'sync-only') socket.frame({ action: 'sync', hash: 'unrelated' });
      if (mode === 'built-only') socket.frame({ action: 'built', hash: 'client' });
      await vi.advanceTimersByTimeAsync(15_000);
      await rejected;
      expect(socket.terminated).toBe(1);
      expect(socket.eventNames()).toEqual([]);
    },
  );

  it.each([
    'error',
    'close',
    'malformed',
    'oversized',
    'compile-error',
    'server-error',
    'empty-hash',
    'write-throws',
    'explicit-close',
  ])('fails %s safely and cleans the active update wait', async (mode) => {
    const { socket, watcher } = await connected();
    const changed = watcher.afterWrite(() => {
      if (mode === 'write-throws') throw new Error('private fixture source value');
    });
    const rejected = expect(changed).rejects.toThrow(/^Development HMR watcher failed$/);
    if (mode === 'error') socket.emit('error', new Error('private fixture provider value'));
    if (mode === 'close') {
      socket.readyState = 3;
      socket.emit('close');
    }
    if (mode === 'malformed') socket.emit('message', Buffer.from('private invalid JSON'));
    if (mode === 'oversized') socket.emit('message', Buffer.alloc(256 * 1024 + 1, 120));
    if (mode === 'compile-error')
      socket.frame({ action: 'built', errors: ['private compiler text'] });
    if (mode === 'server-error')
      socket.frame({ action: 'serverError', errorJSON: 'private error' });
    if (mode === 'empty-hash') socket.frame({ action: 'serverComponentChanges', hash: '' });
    if (mode === 'explicit-close') watcher.close();
    await rejected;
    expect(socket.eventNames()).toEqual([]);
    await expect(watcher.afterWrite(() => {})).rejects.toThrow('Development HMR watcher failed');
  });

  it.each(['bad-status', 'malformed', 'error', 'close'])(
    'cleans %s before the HMR connection is ready',
    async (mode) => {
      const { socket, ready } = start();
      const rejected = expect(ready).rejects.toThrow(/^Development HMR watcher failed$/);
      if (mode === 'bad-status') socket.emit('upgrade', { statusCode: 200 });
      if (mode === 'malformed') socket.emit('message', Buffer.from('{'));
      if (mode === 'error') socket.emit('error', new Error('private handshake error'));
      if (mode === 'close') {
        socket.readyState = 3;
        socket.emit('close');
      }
      await rejected;
      expect(socket.eventNames()).toEqual([]);
    },
  );

  it('absorbs the connecting-socket error emitted by ws during termination', async () => {
    class ConnectingSocket extends FixtureSocket {
      override terminate() {
        this.terminated++;
        this.emit('error', new Error('WebSocket was closed before the connection was established'));
        this.readyState = 3;
        this.emit('close');
      }
    }
    const { socket, ready } = start(new ConnectingSocket());
    const rejected = expect(ready).rejects.toThrow('Development HMR watcher failed');
    await vi.advanceTimersByTimeAsync(15_000);
    await rejected;
    expect(socket.eventNames()).toEqual([]);
  });
});
