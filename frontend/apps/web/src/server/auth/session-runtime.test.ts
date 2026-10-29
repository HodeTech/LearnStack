// @vitest-environment node
import { execFileSync } from 'node:child_process';
import { readFileSync } from 'node:fs';

import { describe, expect, it, vi } from 'vitest';

import { bffSessionProtocol } from './session-owner';

describe('native BFF owner composition', () => {
  it('a bundled request lookup never creates a fallback owner', () => {
    expect(() => bffSessionProtocol()).toThrow('BFF session owner unavailable');
  });

  it('the real compiled native module installs one immutable holder across independently loaded bundles', () => {
    const moduleUrl = new URL('../../../.server/auth/session-runtime.js', import.meta.url).href;
    const program = [
      "import assert from 'node:assert/strict';",
      "import { randomBytes } from 'node:crypto';",
      'const first = await import(process.argv[1]);',
      'const second = await import(process.argv[1] + "?independent-bundle");',
      'const { createBffRequestRuntime } = await import(new URL("./request-runtime.js", process.argv[1]));',
      'const { createPublicAdmissionRuntime } = await import(new URL("../public-admission-runtime.js", process.argv[1]));',
      'const admission = createPublicAdmissionRuntime(true); const requests = createBffRequestRuntime(admission.holder);',
      'let calls = 0;',
      'const dependencies = { provider: { begin: async uri => { ++calls; return { transaction: { callbackUri: uri, state: randomBytes(32).toString("base64url"), nonce: "owned", verifier: "owned" }, redirectUrl: "https://identity.example.test/" }; } }, account: async () => { throw new Error("unused"); } };',
      'assert.throws(() => first.installBffSessionRuntime(dependencies), /page owner unavailable/);',
      'const runtime = first.installBffSessionRuntime(dependencies, requests.preparation);',
      'assert.equal(calls, 0);',
      'assert.throws(() => second.installBffSessionRuntime(dependencies, requests.preparation), /already installed/);',
      'const key = Symbol.for("learnstack.bff-sessions.v1");',
      'const descriptor = Object.getOwnPropertyDescriptor(globalThis, key);',
      'assert.deepEqual([descriptor.enumerable, descriptor.writable, descriptor.configurable], [false, false, false]);',
      'assert.ok(Object.isFrozen(descriptor.value));',
      'assert.ok(Object.isFrozen(descriptor.value.store));',
      'assert.deepEqual(Object.keys(descriptor.value.store).sort(), ["start","complete","entry","create","select","switchScope","discardEntry","operate","preparePage","peek","admit","signout","consumeLogout","stop"].sort());',
      'assert.equal(globalThis[Symbol.for("learnstack.public-admission.v1")], undefined);',
      'const login = await descriptor.value.store.start({ host: "institution.example.test:3000", source: "203.0.113.1", returnPath: "/studio" });',
      'assert.equal(calls, 1);',
      'assert.equal(runtime.counts().owners, 1);',
      'assert.equal(login.proof.length, 43);',
      'await runtime.stop();',
      'await requests.stop();',
      'assert.equal(runtime.counts().owners, 0);',
      'await assert.rejects(descriptor.value.store.start({ host: "institution.example.test:3000", source: "203.0.113.1", returnPath: "/" }), { reason: "stopped" });',
      'assert.throws(() => second.installBffSessionRuntime(dependencies), /already installed/);',
    ].join('\n');
    expect(
      execFileSync(process.execPath, ['--input-type=module', '-e', program, moduleUrl], {
        encoding: 'utf8',
        timeout: 10_000,
      }),
    ).toBe('');
  });

  it('rejects a planted mutable, malformed or mismatched-version holder instead of using it', () => {
    const original = Object.getOwnPropertyDescriptor;
    let planted: PropertyDescriptor | undefined;
    const descriptor = vi
      .spyOn(Object, 'getOwnPropertyDescriptor')
      .mockImplementation((object, key) =>
        object === globalThis && key === Symbol.for('learnstack.bff-sessions.v1')
          ? planted
          : original(object, key),
      );
    try {
      for (const value of [
        { version: 1, store: {} },
        Object.freeze({ version: 2, store: Object.freeze({}) }),
        Object.freeze({ version: 1, store: Object.freeze({ start: () => {} }) }),
      ]) {
        planted = { value, configurable: false, writable: false, enumerable: false };
        expect(() => bffSessionProtocol()).toThrow('BFF session owner unavailable');
      }
    } finally {
      descriptor.mockRestore();
    }
  });

  it('retains passive server-only lookup and the independent native-public holder', () => {
    const facade = readFileSync(new URL('./session-owner.ts', import.meta.url), 'utf8');
    expect(facade).toContain("import 'server-only'");
    expect(facade).not.toContain('new InMemoryBffSessionStore');
    const native = readFileSync(new URL('./session-runtime.ts', import.meta.url), 'utf8');
    expect(native).toContain('Object.getOwnPropertyDescriptor');
    expect(native).toContain('configurable: false');
    expect(native).not.toContain('learnstack.public-admission.v1');
  });
});
