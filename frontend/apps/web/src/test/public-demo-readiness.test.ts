// @vitest-environment node
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { afterEach, describe, expect, it, vi } from 'vitest';

import { readBrowserDevToolsPort } from '../../scripts/admission-browser.mjs';
import {
  PublicDemoFailure,
  publicDemoFailure,
  waitForPublicDemoBrowserPort,
} from '../../scripts/public-demo-diagnostics.mjs';

const roots: string[] = [];
const canary = 'private-profile-endpoint-canary';
const portFile = () => {
  const root = mkdtempSync(join(tmpdir(), 'learnstack-public-port-'));
  roots.push(root);
  return join(root, 'DevToolsActivePort');
};
afterEach(() => {
  vi.restoreAllMocks();
  for (const root of roots.splice(0)) rmSync(root, { recursive: true, force: true });
});

describe('owned public-demo browser readiness', () => {
  it('waits for actual missing, empty and incomplete numeric publication before admitting the full port', async () => {
    const file = portFile();
    const observations: string[] = [];
    const values = ['', '3', '12345\n/devtools/browser/' + canary];
    const assertActive = vi.fn();
    const result = await waitForPublicDemoBrowserPort({
      assertActive,
      readPort: () => {
        const value = readBrowserDevToolsPort(file);
        observations.push(value.kind);
        const next = values.shift();
        if (next !== undefined) writeFileSync(file, next);
        return value;
      },
    });
    expect(result).toBe(12345);
    expect(observations).toEqual(['missing', 'empty', 'malformed', 'ready']);
    expect(assertActive.mock.calls.length).toBeGreaterThanOrEqual(observations.length);
    expect(readFileSync(file, 'utf8')).toContain(canary);
  });

  it.each([
    '3',
    '12345',
    '0x1234\n',
    '1e3\n',
    '12.5\n',
    '12345junk\n',
    ' 12345\n',
    '01234\n',
    '65536\n',
  ])('does not publish an incomplete or noncanonical first line: %j', (value) => {
    const file = portFile();
    writeFileSync(file, value);
    expect(readBrowserDevToolsPort(file)).toEqual({ kind: 'malformed' });
  });

  it.each([1, 65535])('admits the complete decimal range boundary %i', (port) => {
    const file = portFile();
    writeFileSync(file, port + '\n/devtools/browser/' + canary);
    expect(readBrowserDevToolsPort(file)).toEqual({ kind: 'ready', port });
  });

  it.each([
    ['missing', 'deadline'],
    ['empty', 'deadline'],
    ['malformed', 'port-invalid'],
    ['unreadable', 'port-invalid'],
    ['unknown', 'port-invalid'],
  ])('keeps a persistent %s observation bounded and closed', async (kind, reason) => {
    const failure = await waitForPublicDemoBrowserPort({
      assertActive: () => {},
      readPort: () => ({ kind }),
      timeout: 10,
    }).catch((error: unknown) => error);
    expect(failure).toBeInstanceOf(PublicDemoFailure);
    expect(publicDemoFailure('startup-port', failure)).toMatchObject({ reason });
    expect(JSON.stringify(publicDemoFailure('startup-port', failure))).not.toContain(canary);
  });

  it('keeps a thrown filesystem error private', async () => {
    const failure = await waitForPublicDemoBrowserPort({
      assertActive: () => {},
      readPort: () => {
        throw new Error(canary);
      },
    }).catch((error: unknown) => error);
    expect(String(failure)).not.toContain(canary);
    expect(publicDemoFailure('startup-port', failure)).toMatchObject({ reason: 'port-invalid' });
  });

  it('rechecks owner and native browser death after a ready observation', async () => {
    let exited = false;
    const readPort = vi.fn(() => {
      exited = true;
      return { kind: 'ready', port: 12345 };
    });
    const failure = await waitForPublicDemoBrowserPort({
      assertActive: () => {
        if (exited) throw new PublicDemoFailure('chromium-exited');
      },
      readPort,
    }).catch((error: unknown) => error);
    expect(readPort).toHaveBeenCalledOnce();
    expect(
      publicDemoFailure('startup-port', failure, {}, { exitCode: 0, signalCode: null }),
    ).toMatchObject({
      reason: 'chromium-exited',
      process: 'browser',
      exitCode: 0,
    });
  });

  it('rejects a ready port observed after the existing allowance expires', async () => {
    let now = 0;
    vi.spyOn(Date, 'now').mockImplementation(() => now);
    await expect(
      waitForPublicDemoBrowserPort({
        assertActive: () => {},
        readPort: () => {
          now = 10;
          return { kind: 'ready', port: 12345 };
        },
        timeout: 10,
      }),
    ).rejects.toMatchObject({ reason: 'deadline' });
  });

  it.each([0, -1, 10_001, 1.5, Infinity])(
    'does not enlarge or invalidate the startup bound: %s',
    async (timeout) => {
      const readPort = vi.fn();
      await expect(
        waitForPublicDemoBrowserPort({ assertActive: () => {}, readPort, timeout }),
      ).rejects.toMatchObject({ reason: 'deadline' });
      expect(readPort).not.toHaveBeenCalled();
    },
  );

  it('cancels an in-flight publication wait without exposing the owned reason', async () => {
    const controller = new AbortController();
    const readPort = vi.fn(() => ({ kind: 'empty' }));
    const waiting = waitForPublicDemoBrowserPort({
      assertActive: () => {},
      readPort,
      signal: controller.signal,
    }).catch((error: unknown) => error);
    controller.abort(new Error(canary));
    const failure = await waiting;
    expect(readPort).toHaveBeenCalledOnce();
    const row = publicDemoFailure('startup-port', failure, {}, undefined, controller.signal);
    expect(row).toMatchObject({ reason: 'cancelled' });
    expect(JSON.stringify(row)).not.toContain(canary);
  });
});
