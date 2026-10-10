// @vitest-environment node
import type { ChildProcess } from 'node:child_process';
import { EventEmitter } from 'node:events';
import { existsSync, mkdtempSync, realpathSync, rmSync, symlinkSync, writeFileSync } from 'node:fs';
import type { Server } from 'node:net';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import {
  createFixtureOwner,
  createPrivateScanner,
  fixtureEnvironment,
} from '../../scripts/fixture-support.mjs';
import { stopTestChild } from '../../scripts/stop-test-child.mjs';

vi.mock('../../scripts/stop-test-child.mjs', () => ({ stopTestChild: vi.fn() }));

afterEach(() => {
  vi.resetAllMocks();
  vi.unstubAllEnvs();
});

describe('fixture private-output scanner', () => {
  const secret = 'private-fixture-credential';
  const envelope = 'v1.' + 'A'.repeat(12000) + '.' + 'B'.repeat(43);

  it.each([
    ['secret', secret],
    ['carrier', 'X-LeArNsTaCk-Hop-Secret'],
    ['envelope', envelope],
  ])('remembers an early %s after bounded output rolls over', (_name, privateValue) => {
    const scanner = createPrivateScanner([secret]);
    scanner.push(privateValue);
    scanner.push('\nsafe output\n'.repeat(7000));
    scanner.finish();
    expect(scanner.leaked).toBe(true);
    expect(scanner.tail.length).toBe(65536);
    expect(scanner.tail.includes(privateValue)).toBe(false);
  });

  it.each([
    ['secret', secret],
    ['carrier', 'x-learnstack-host'],
    ['envelope', envelope],
  ])('detects a %s split at supported boundaries', (_name, privateValue) => {
    // Envelope controls cover the beginning, large payload and MAC boundary;
    // short secrets/carriers cover every split.
    const splits =
      privateValue.length > 100
        ? [1, 2, 3, 4096, 8192, 12002, 12003, 12004, privateValue.length - 1]
        : Array.from({ length: privateValue.length - 1 }, (_, index) => index + 1);
    for (const split of splits) {
      const scanner = createPrivateScanner([secret]);
      scanner.push(privateValue.slice(0, split));
      scanner.push(privateValue.slice(split));
      scanner.finish();
      expect(scanner.leaked, `split ${split}`).toBe(true);
    }
  });

  it('keeps benign version text and independent clean output clean', () => {
    const scanner = createPrivateScanner([secret]);
    scanner.push('API /api/v1/public/site, version v1., v1.payload.short ');
    scanner.push('clean output\n'.repeat(7000));
    expect(scanner.leaked).toBe(false);
    expect(scanner.contains('v1.payload.' + 'A'.repeat(44))).toBe(false);
    expect(scanner.contains('v1.payload.' + 'A'.repeat(43) + '=')).toBe(false);
    expect(scanner.contains('av1.payload.' + 'A'.repeat(43))).toBe(false);
    expect(scanner.contains('v1.payload.' + 'A'.repeat(43))).toBe(true);
    expect(scanner.leaked).toBe(false); // A pure response check does not change log state.
    expect(scanner.tail.length).toBe(65536);
  });

  it.each(['m', '='])('keeps split malformed MAC suffix %s clean', (suffix) => {
    const scanner = createPrivateScanner();
    scanner.push('v1.payload.' + 'm'.repeat(43));
    expect(scanner.leaked).toBe(false);
    scanner.push(suffix);
    scanner.finish();
    expect(scanner.leaked).toBe(false);
  });

  it('finalizes an exact envelope at EOF and detects a real delimiter immediately', () => {
    const scanner = createPrivateScanner();
    scanner.push('v1.payload.' + 'm'.repeat(43));
    expect(scanner.leaked).toBe(false);
    scanner.finish();
    expect(scanner.leaked).toBe(true);
    const delimited = createPrivateScanner();
    delimited.push('v1.payload.' + 'm'.repeat(43) + '\n');
    expect(delimited.leaked).toBe(true);
  });

  it('refuses empty or unbounded secret scanner inputs', () => {
    expect(() => createPrivateScanner([''])).toThrow('Invalid fixture scanner secret');
    expect(() => createPrivateScanner(['a'.repeat(65537)])).toThrow(
      'Invalid fixture scanner secret',
    );
  });
});

describe('fixture positive environment', () => {
  let root: string;
  beforeEach(() => {
    root = mkdtempSync(join(tmpdir(), 'learnstack-environment-'));
  });
  afterEach(() => {
    rmSync(root, { recursive: true, force: true });
  });
  it('drops inherited secrets, loaders, proxies, debug and TLS trust overrides', () => {
    for (const [key, value] of Object.entries({
      NODE_OPTIONS: '--require=/unsafe/loader',
      SWC_NATIVE_BINDING_CACHE: '/unsafe/cache',
      SWC_NATIVE_BINDING_CACHE_SKIP_SECURITY_CHECK: '1',
      NODE_TLS_REJECT_UNAUTHORIZED: '0',
      NODE_EXTRA_CA_CERTS: '/unsafe/ca',
      SSL_CERT_FILE: '/unsafe/ca',
      HTTPS_PROXY: 'http://unsafe',
      DEBUG: 'next:*',
      NEXT_PUBLIC_SECRET: 'unsafe',
      LEARNSTACK_PUBLIC_HOP_SECRET: 'inherited',
    }))
      vi.stubEnv(key, value);
    const env = fixtureEnvironment({ root, nodePath: '/explicit/modules' });
    expect(env).toEqual({
      PATH: process.env.PATH,
      TMPDIR: root,
      SWC_NATIVE_BINDING_CACHE: join(realpathSync(root), 'swc-native-cache'),
      NODE_ENV: 'production',
      NODE_PATH: '/explicit/modules',
      NODE_TLS_REJECT_UNAUTHORIZED: '1',
      NEXT_TELEMETRY_DISABLED: '1',
      CI: 'true',
    });
  });

  it('keeps native materialization inside the owned root through a temporary-directory alias', () => {
    const alias = join(root, 'alias');
    symlinkSync(root, alias, 'dir');
    const env = fixtureEnvironment({ root: alias });
    expect(env.SWC_NATIVE_BINDING_CACHE).toBe(join(realpathSync(root), 'swc-native-cache'));
    expect(env.HOME).toBeUndefined();
    expect(env.SWC_NATIVE_BINDING_CACHE_SKIP_SECURITY_CHECK).toBeUndefined();
  });

  it('accepts only explicitly configured runtime keys and deliberate debug canaries', () => {
    const privateValues = {
      LEARNSTACK_PUBLIC_API_ORIGIN: 'http://127.0.0.1:5080',
      LEARNSTACK_PUBLIC_HOP_SECRET: 'owned-secret',
      LEARNSTACK_PUBLIC_TLS_CERT: '/owned/cert',
      LEARNSTACK_PUBLIC_TLS_KEY: '/owned/key',
    };
    const env = fixtureEnvironment({
      root,
      nodeEnv: 'development',
      privateValues,
      debug: 'next:*',
    });
    expect(env).toMatchObject({ ...privateValues, NODE_ENV: 'development', DEBUG: 'next:*' });
    expect(() => fixtureEnvironment({ root, privateValues: { NODE_OPTIONS: 'unsafe' } })).toThrow(
      'Unsupported fixture environment',
    );
    expect(() =>
      fixtureEnvironment({ root, privateValues: { NEXT_PUBLIC_SECRET: 'unsafe' } }),
    ).toThrow('Unsupported fixture environment');
  });
});

describe('fixture resource ownership', () => {
  it('joins cleanup once, keeps files until children stop, and refuses late acquisition', async () => {
    const owner = createFixtureOwner();
    const root = owner.ownRoot(mkdtempSync(join(tmpdir(), 'learnstack-owner-control-')));
    writeFileSync(join(root, 'owned'), 'owned');
    const child = {} as ChildProcess;
    expect(owner.ownChild(child)).toBe(child);
    let stopped!: () => void;
    vi.mocked(stopTestChild).mockImplementation(
      () =>
        new Promise<void>((resolve) => {
          stopped = resolve;
        }),
    );
    const cleanup = owner.dispose();
    expect(owner.dispose()).toBe(cleanup);
    expect(() => owner.assertActive()).toThrow('Fixture owner is closed');
    expect(() => owner.ownRoot('/late')).toThrow('Fixture owner is closed');
    expect(() => owner.ownChild({} as ChildProcess)).toThrow('Fixture owner is closed');
    expect(() => owner.ownServer({} as Server)).toThrow('Fixture owner is closed');
    await Promise.resolve();
    expect(existsSync(root)).toBe(true);
    stopped();
    await cleanup;
    expect(existsSync(root)).toBe(false);
    expect(stopTestChild).toHaveBeenCalledTimes(1);
  });

  it('attempts every resource and retains files if a child cannot be confirmed stopped', async () => {
    const owner = createFixtureOwner();
    const root = owner.ownRoot(mkdtempSync(join(tmpdir(), 'learnstack-owner-failure-')));
    owner.ownChild({} as ChildProcess);
    owner.ownChild({} as ChildProcess);
    vi.mocked(stopTestChild)
      .mockRejectedValueOnce(new Error('planted cleanup refusal'))
      .mockResolvedValueOnce(undefined);
    const closeAllConnections = vi.fn();
    const close = vi.fn((done: (error?: NodeJS.ErrnoException) => void) => done());
    const server = { closeAllConnections, close } as unknown as Server;
    expect(owner.ownServer(server)).toBe(server);
    await expect(owner.dispose()).rejects.toThrow('Fixture cleanup failed');
    expect(stopTestChild).toHaveBeenCalledTimes(2);
    expect(closeAllConnections).toHaveBeenCalledOnce();
    expect(close).toHaveBeenCalledOnce();
    expect(existsSync(root)).toBe(true);
    rmSync(root, { recursive: true, force: true });
  });

  it('waits for final child output before removing files', async () => {
    const owner = createFixtureOwner();
    const root = owner.ownRoot(mkdtempSync(join(tmpdir(), 'learnstack-owner-drain-')));
    const output = Object.assign(new EventEmitter(), { closed: false, readableEnded: false });
    const scanner = createPrivateScanner(['shutdown-secret']);
    output.on('data', (chunk) => scanner.push(chunk));
    output.on('end', () => scanner.finish());
    owner.ownChild({ stdout: output } as unknown as ChildProcess);
    vi.mocked(stopTestChild).mockResolvedValue(undefined);
    const pending = owner.dispose();
    await Promise.resolve();
    await Promise.resolve();
    expect(existsSync(root)).toBe(true);
    output.emit('data', 'shutdown-secret');
    output.emit('end');
    await pending;
    expect(scanner.leaked).toBe(true);
    expect(existsSync(root)).toBe(false);
  });

  it.each(['SIGINT', 'SIGTERM', 'close', 'disconnect', 'stdout', 'stderr'] as const)(
    'routes %s through shared cleanup before exiting once',
    async (trigger) => {
      const exits: number[] = [];
      const owner = createFixtureOwner({
        exit: (code) => {
          exits.push(code);
        },
      });
      const control = new EventEmitter();
      const event = trigger === 'disconnect' ? 'disconnect' : 'close';
      const signalBefore = process.listeners(trigger === 'SIGINT' ? 'SIGINT' : 'SIGTERM');
      const stream = trigger === 'stderr' ? process.stderr : process.stdout;
      const errorsBefore = stream.listeners('error');
      owner.install({ control, event });
      const root = owner.ownRoot(mkdtempSync(join(tmpdir(), 'learnstack-owner-cancel-')));
      owner.ownChild({} as ChildProcess);
      let stopped!: () => void;
      vi.mocked(stopTestChild).mockImplementation(
        () =>
          new Promise<void>((resolve) => {
            stopped = resolve;
          }),
      );
      if (trigger === 'close' || trigger === 'disconnect') control.emit(event);
      else if (trigger === 'stdout' || trigger === 'stderr') {
        const handler = stream
          .listeners('error')
          .find((listener) => !errorsBefore.includes(listener))!;
        handler(Object.assign(new Error('owned broken pipe'), { code: 'EPIPE' }));
      } else {
        const handler = process
          .listeners(trigger)
          .find((listener) => !signalBefore.includes(listener))!;
        handler(trigger);
      }
      await Promise.resolve();
      expect(exits).toEqual([]);
      expect(existsSync(root)).toBe(true);
      stopped();
      await owner.cancel();
      await owner.cancel();
      expect(exits).toEqual([1]);
      expect(existsSync(root)).toBe(false);
      expect(stopTestChild).toHaveBeenCalledOnce();
      expect(control.listenerCount(event)).toBe(0);
    },
  );

  it('ordinary disposal removes handlers and never exits', async () => {
    const exit = vi.fn();
    const owner = createFixtureOwner({ exit });
    const control = new EventEmitter();
    const before = process.listenerCount('SIGTERM');
    owner.install({ control });
    owner.install({ control });
    expect(process.listenerCount('SIGTERM')).toBe(before + 1);
    await owner.dispose();
    expect(exit).not.toHaveBeenCalled();
    expect(process.listenerCount('SIGTERM')).toBe(before);
    expect(control.listenerCount('close')).toBe(0);
  });
});
