// Webpack and native bootstrap share this builtins-only development authority.
import { randomBytes } from 'node:crypto';
import { existsSync, readFileSync, renameSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import {
  FrameworkAdmissionError,
  frameworkDevelopmentRestartGraph,
  verifyFrameworkDevelopment,
} from './framework-installation.mjs';

function refuse() {
  throw new FrameworkAdmissionError('development_compiler');
}
function statePath(appRoot, kind) {
  return join(appRoot, '.framework-state', `compiler-${kind}.json`);
}
function writeState(appRoot, kind, state) {
  const target = statePath(appRoot, kind);
  const temporary = `${target}.${randomBytes(8).toString('hex')}.tmp`;
  writeFileSync(temporary, `${JSON.stringify(state)}\n`, { mode: 0o600, flag: 'wx' });
  renameSync(temporary, target);
}
export function createFrameworkDevelopmentPlugin({ appRoot, kind }) {
  if (!['client', 'server', 'edge'].includes(kind)) refuse();
  return {
    apply(compiler) {
      const name = `LearnStackFrameworkAdmission:${kind}`;
      let generation = 0;
      let runningGeneration = 0;
      const compilations = new WeakMap();
      const invalidate = () => {
        generation++;
        writeState(appRoot, kind, { format: 1, generation, phase: 'compiling' });
      };
      compiler.hooks.invalid.tap(name, invalidate);
      compiler.hooks.beforeCompile.tap(name, () => {
        invalidate();
        runningGeneration = generation;
        try {
          verifyFrameworkDevelopment({ appRoot });
        } catch (error) {
          writeState(appRoot, kind, { format: 1, generation, phase: 'failed' });
          throw error;
        }
      });
      compiler.hooks.thisCompilation.tap(name, (compilation) =>
        compilations.set(compilation, runningGeneration),
      );
      compiler.hooks.done.tap(name, (stats) => {
        let phase = compilations.get(stats.compilation) === generation ? 'ready' : 'compiling';
        try {
          verifyFrameworkDevelopment({ appRoot });
        } catch {
          phase = 'failed';
        }
        if (stats.hasErrors()) phase = 'failed';
        writeState(appRoot, kind, { format: 1, generation, phase });
      });
      compiler.hooks.failed.tap(name, () => {
        writeState(appRoot, kind, { format: 1, generation, phase: 'failed' });
      });
    },
  };
}
export function createFrameworkDevelopmentAdmission({ identity }) {
  const restart = frameworkDevelopmentRestartGraph(identity);
  const assert = () => {
    if (JSON.stringify(frameworkDevelopmentRestartGraph(identity)) !== JSON.stringify(restart))
      refuse();
    for (const kind of ['client', 'server', 'edge']) {
      const path = statePath(identity.appRoot, kind);
      if (!existsSync(path)) continue; // A lazy compiler has not produced code yet.
      let state;
      try {
        state = JSON.parse(readFileSync(path, 'utf8'));
      } catch {
        refuse();
      }
      if (state.format !== 1 || state.phase !== 'ready') refuse();
    }
  };
  return Object.freeze({ assert });
}

export function protectFrameworkDevelopmentResponse(response, admission) {
  const writeHead = response.writeHead;
  const write = response.write;
  const end = response.end;
  let refused = false;
  function admitted() {
    if (refused) return false;
    try {
      admission.assert();
      return true;
    } catch {
      refused = true;
      if (!response.headersSent) {
        // These are the original native writers. No stale compiled response is
        // reclassified as fresh; any already allocated compressor remains owned.
        Reflect.apply(writeHead, response, [
          503,
          { 'cache-control': 'no-store', 'content-length': '0' },
        ]);
        Reflect.apply(end, response, []);
      } else response.destroy();
      return false;
    }
  }
  function rejectUnissued(args) {
    const callback = args[args.length - 1];
    if (typeof callback === 'function')
      queueMicrotask(() =>
        Reflect.apply(callback, response, [new Error('Framework output admission closed')]),
      );
  }
  response.writeHead = function (...args) {
    if (!admitted()) return response;
    return Reflect.apply(writeHead, this, args);
  };
  response.write = function (...args) {
    if (!admitted()) {
      rejectUnissued(args);
      return false;
    }
    return Reflect.apply(write, this, args);
  };
  response.end = function (...args) {
    if (!admitted()) {
      rejectUnissued(args);
      return response;
    }
    return Reflect.apply(end, this, args);
  };
}
