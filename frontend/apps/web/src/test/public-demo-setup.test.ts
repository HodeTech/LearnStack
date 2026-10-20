// @vitest-environment node
import { spawnSync } from 'node:child_process';

import { expect, it } from 'vitest';

it.each(['provenance', 'build', 'leaf', 'save', 'directory', 'existing'])(
  'retains closed setup failure and attempts owned cleanup after %s refusal',
  (stage) => {
    const execution = spawnSync(
      process.execPath,
      [
        '--input-type=module',
        '-e',
        `
      import fs from 'node:fs';
      import { syncBuiltinESMExports } from 'node:module';
      const failure = new Error('private-setup-canary');
      const stage = ${JSON.stringify(stage)};
      const original = { ...fs };
      let owned = false;
      let saved;
      let writes = 0;
      let disposed = false;
      const diagnostics = [];
      const at = (path, suffix) => String(path).split(String.fromCharCode(92)).join('/').endsWith(suffix);
      console.error = (line) => diagnostics.push(line);
      fs.existsSync = (path) => at(path, '/artifacts/public-demo')
        ? stage === 'existing' || owned : original.existsSync(path);
      fs.mkdirSync = (path, ...args) => {
        if (!at(path, '/artifacts/public-demo')) return original.mkdirSync(path, ...args);
        if (stage === 'directory') throw failure;
        owned = true;
      };
      fs.writeFileSync = (path, value, ...args) => {
        if (!at(path, '/artifacts/public-demo/receipt.json'))
          return original.writeFileSync(path, value, ...args);
        writes++;
        if (stage === 'save' && writes > 1) throw new Error('private-write-canary');
        saved = JSON.parse(value);
      };
      fs.readFileSync = (path, ...args) => {
        if (!at(path, '/.next/BUILD_ID')) return original.readFileSync(path, ...args);
        if (stage === 'build') throw failure;
        return 'control-build';
      };
      syncBuiltinESMExports();
      const { verifyPublicDemo } = await import(${JSON.stringify(new URL('../../scripts/verify-public-demo.mjs', import.meta.url).href)});
      let caught;
      try {
        await verifyPublicDemo({
          mode: 'ci', privateRoot: '/private-control', setupMilliseconds: 0,
          manifest: [], completions: { total: 0, statuses: {}, paths: {}, malformed: false, pendingBytes: 0 },
          processes: { run: async () => { if (['provenance', 'save'].includes(stage)) throw failure; return 'a'.repeat(40); } },
          leaf: { publicKey: { export: () => { throw failure; } } },
          dispose: async () => { disposed = true; return { passed: true }; },
        });
      } catch (error) { caught = error; }
      process.stdout.write(JSON.stringify({ sameError: caught === failure, failed: !!caught, disposed, saved, writes, diagnostics }));
    `,
      ],
      { encoding: 'utf8', timeout: 15000 },
    );
    expect(execution.status, execution.stderr).toBe(0);
    const result = JSON.parse(execution.stdout);
    expect(result).toMatchObject({ failed: true, disposed: true });
    if (['provenance', 'build', 'leaf', 'save', 'directory'].includes(stage))
      expect(result.sameError).toBe(true);
    if (['existing', 'directory'].includes(stage)) expect(result.writes).toBe(0);
    else if (stage !== 'save')
      expect(result.saved).toMatchObject({
        passed: false,
        failure: { stage: 'setup', reason: 'unknown' },
        cleanup: { passed: true },
      });
    expect(JSON.stringify(result.diagnostics)).toContain('setup');
    expect(JSON.stringify(result.saved) + JSON.stringify(result.diagnostics)).not.toContain(
      'private-setup-canary',
    );
    expect(JSON.stringify(result.saved) + JSON.stringify(result.diagnostics)).not.toContain(
      'private-write-canary',
    );
  },
);
