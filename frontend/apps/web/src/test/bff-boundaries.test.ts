// @vitest-environment node
import { execFileSync } from 'node:child_process';
import { EventEmitter } from 'node:events';
import { readFileSync } from 'node:fs';

import { describe, expect, it } from 'vitest';

import { bffHost, bffOpaque, bffSource } from './bff-fixture';
import { createBffRequestRuntime } from '../server/auth/request-runtime';

const context = { host: bffHost, peer: bffSource, method: 'GET', target: '/studio' };
function lifetime() {
  return {
    request: Object.assign(new EventEmitter(), { aborted: false, headers: {} }),
    response: Object.assign(new EventEmitter(), { destroyed: false, writableEnded: false }),
  };
}
const projection = () => ({
  state: 'authenticated' as const,
  displayName: 'School',
  expiresAt: Date.now() + 30_000,
  limitedByAbsolute: false,
  csrfToken: bffOpaque(),
});
describe('Bff_Credentials_And_Projections_Are_Confined', () => {
  it('isolates overlapping equal bindings and closes escaped readers on actual response completion', async () => {
    const runtime = createBffRequestRuntime();
    const first = lifetime();
    const second = lifetime();
    const a = projection();
    const b = projection();
    let escaped: ReturnType<typeof runtime.holder.read>;
    await runtime.run(context, first.request, first.response, async () => {
      expect(() => runtime.holder.read(context)).toThrow();
      runtime.holder.publish(context, a);
      escaped = runtime.holder.read(context);
      expect(escaped.session).toEqual(a);
      expect(Object.isFrozen(escaped.session)).toBe(true);
      await runtime.run(context, second.request, second.response, async () => {
        runtime.holder.publish(context, b);
        expect(runtime.holder.read(context).session).toEqual(b);
        expect(() => escaped.assertActive()).toThrow();
        second.response.emit('finish');
        expect(() => runtime.holder.read(context)).toThrow();
      });
      escaped.assertActive();
      first.response.emit('finish');
      expect(() => escaped.assertActive()).toThrow();
    });
    expect(runtime.counts()).toBe(0);
    runtime.stop();
  });
  it.each(['aborted', 'finish', 'close'])(
    'actual %s closes request authority and removes native listeners',
    (event) => {
      const runtime = createBffRequestRuntime();
      const { request, response } = lifetime();
      runtime.run(context, request, response, () => {
        runtime.holder.publish(context, null);
        (event === 'aborted' ? request : response).emit(event);
        expect(() => runtime.holder.read(context)).toThrow();
        expect(
          request.listenerCount('aborted') +
            response.listenerCount('finish') +
            response.listenerCount('close'),
        ).toBe(0);
      });
      expect(runtime.counts()).toBe(0);
    },
  );
  it('copies only safe fields and denies re-publication, wrong bindings and publication after stop', () => {
    const runtime = createBffRequestRuntime();
    const { request, response } = lifetime();
    const value = { ...projection(), token: 'private-canary' };
    runtime.run(context, request, response, () => {
      runtime.holder.publish(context, value);
      value.displayName = 'mutated';
      expect(runtime.holder.read(context).session?.displayName).toBe('School');
      expect(JSON.stringify(runtime.holder.read(context))).not.toContain('private-canary');
      expect(() => runtime.holder.publish(context, null)).toThrow();
      expect(() => runtime.holder.read({ ...context, host: 'other.example.test:3000' })).toThrow();
      runtime.stop();
      expect(() => runtime.holder.read(context)).toThrow();
    });
    expect(() => runtime.run(context, request, response, () => null)).toThrow();
  });
  it('captures only the activity scalar before Next removes or mutates Flight headers', () => {
    const runtime = createBffRequestRuntime();
    for (const prefetch of [false, true]) {
      const owner = lifetime();
      Object.assign(owner.request.headers, {
        rsc: '1',
        ...(prefetch ? { 'next-router-prefetch': '1' } : {}),
      });
      runtime.run(context, owner.request, owner.response, () => {
        delete (owner.request.headers as Record<string, string>).rsc;
        delete (owner.request.headers as Record<string, string>)['next-router-prefetch'];
        expect(runtime.holder.activity(context)).toBe(prefetch ? 'passive' : 'read');
        runtime.holder.publish(context, null);
        expect(() => runtime.holder.activity(context)).toThrow();
        owner.response.emit('finish');
      });
    }
    expect(runtime.counts()).toBe(0);
    runtime.stop();
  });
  it('the actual separately compiled native module and pinned NextRequest share only the immutable projection', () => {
    const moduleUrl = new URL('../../.server/auth/request-runtime.js', import.meta.url).href;
    const program = [
      "import assert from 'node:assert/strict';",
      "import { EventEmitter } from 'node:events';",
      "import { NextRequest } from 'next/server.js';",
      'const { installBffRequestRuntime } = await import(process.argv[1]);',
      'const runtime = installBffRequestRuntime();',
      'const context = { host: "institution.example.test:3000", peer: "203.0.113.1", method: "GET", target: "/studio" };',
      'const request = Object.assign(new EventEmitter(), { aborted: false, headers: {} }); const response = Object.assign(new EventEmitter(), { destroyed: false, writableEnded: false });',
      'const next = new NextRequest("https://" + context.host + context.target); assert.equal(next.method, context.method);',
      'await runtime.run(context, request, response, async () => { runtime.holder.publish(context, null); await Promise.resolve(); assert.equal(runtime.holder.read(context).session, null); response.emit("finish"); assert.throws(() => runtime.holder.read(context)); });',
      'assert.equal(runtime.counts(), 0); runtime.stop();',
    ].join('\n');
    expect(
      execFileSync(process.execPath, ['--input-type=module', '-e', program, moduleUrl], {
        encoding: 'utf8',
        timeout: 10_000,
      }),
    ).toBe('');
  });
  it('the minimal production-mode Next protocol harness preserves native session authority through HTTP', () => {
    const script = new URL('../../scripts/verify-bff-protocol.mjs', import.meta.url);
    expect(
      execFileSync(process.execPath, [script.pathname], {
        encoding: 'utf8',
        timeout: 150_000,
        maxBuffer: 65536,
      }),
    ).toBe('BFF native Next protocol: passed\n');
  }, 160_000);
  it('the same actual Next proof rejects a copy that drops native activity at the middleware handoff', () => {
    const script = new URL('../../scripts/verify-bff-protocol.mjs', import.meta.url);
    let failure: unknown;
    try {
      execFileSync(process.execPath, [script.pathname, '--without-native-activity'], {
        encoding: 'utf8',
        timeout: 150_000,
        maxBuffer: 65536,
        stdio: ['ignore', 'pipe', 'pipe'],
      });
    } catch (error) {
      failure = error;
    }
    expect(failure).toMatchObject({
      status: 1,
      signal: null,
      stdout: '',
      stderr: 'BFF native Next protocol failed: rsc\n',
    });
  }, 160_000);
  it('the same actual Next proof rejects a nonzero native stop instead of publishing success', () => {
    const script = new URL('../../scripts/verify-bff-protocol.mjs', import.meta.url);
    let failure: unknown;
    try {
      execFileSync(process.execPath, [script.pathname, '--without-successful-native-stop'], {
        encoding: 'utf8',
        timeout: 150_000,
        maxBuffer: 65536,
        stdio: ['ignore', 'pipe', 'pipe'],
      });
    } catch (error) {
      failure = error;
    }
    expect(failure).toMatchObject({
      status: 1,
      signal: null,
      stdout: '',
      stderr: 'BFF native Next protocol failed: native-stop\n',
    });
  }, 160_000);
  it('the actual production middleware and localized shells use the native BFF without exposing credentials', () => {
    const script = new URL('../../scripts/verify-bff-protocol.mjs', import.meta.url);
    expect(
      execFileSync(process.execPath, [script.pathname, '--ui'], {
        encoding: 'utf8',
        timeout: 150_000,
        maxBuffer: 65536,
      }),
    ).toBe('BFF production UI: passed\n');
  }, 160_000);
  it('development callback/logout canaries stay out of actual console and persisted framework traces', () => {
    const script = new URL('../../scripts/verify-bff-protocol.mjs', import.meta.url);
    expect(
      execFileSync(process.execPath, [script.pathname, '--dev'], {
        encoding: 'utf8',
        timeout: 150_000,
        maxBuffer: 65536,
      }),
    ).toBe('BFF development diagnostics: passed\n');
  }, 160_000);
  it('the same development canary kills removal of native trace suppression even with logging false', () => {
    const script = new URL('../../scripts/verify-bff-protocol.mjs', import.meta.url);
    let failure: unknown;
    try {
      execFileSync(process.execPath, [script.pathname, '--without-native-diagnostics'], {
        encoding: 'utf8',
        timeout: 150_000,
        maxBuffer: 65536,
        stdio: ['ignore', 'pipe', 'pipe'],
      });
    } catch (error) {
      failure = error;
    }
    expect(failure).toMatchObject({
      status: 1,
      signal: null,
      stdout: '',
      stderr: 'BFF native Next protocol failed: dev-trace\n',
    });
  }, 160_000);
  it('auth() is a passive server-only projection lookup and has no credential/provider/idle/cookie writer', () => {
    const source = readFileSync(new URL('../server/auth/auth.ts', import.meta.url), 'utf8');
    expect(source).toContain("import 'server-only'");
    expect(source).toContain('read(binding)');
    for (const forbidden of [
      'session-store',
      'session-owner',
      "from './provider",
      '.admit(',
      '.refresh(',
      '.set(',
      'cookies(',
    ])
      expect(source).not.toContain(forbidden);
  });
});
