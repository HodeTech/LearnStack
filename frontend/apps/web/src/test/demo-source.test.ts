// @vitest-environment node
import { execFileSync, spawnSync } from 'node:child_process';
import { mkdirSync, mkdtempSync, readFileSync, realpathSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';

import { afterEach, describe, expect, it, vi } from 'vitest';

import { DemoProcesses } from '../../../../../scripts/demo-process.mjs';
import { demoSource } from '../../../../../scripts/demo-source.mjs';

const roots: string[] = [];
const owners: DemoProcesses[] = [];
afterEach(async () => {
  vi.unstubAllEnvs();
  await Promise.all(owners.splice(0).map((owner) => owner.dispose()));
  for (const root of roots.splice(0)) rmSync(root, { recursive: true, force: true });
});

function fixture() {
  const root = realpathSync(mkdtempSync(join(tmpdir(), 'learnstack-demo-source-')));
  roots.push(root);
  const env = {
    NODE_ENV: 'test' as const,
    PATH: process.env.PATH,
    HOME: root,
    XDG_CONFIG_HOME: root,
    GIT_CONFIG_NOSYSTEM: '1',
    GIT_CONFIG_GLOBAL: process.platform === 'win32' ? 'NUL' : '/dev/null',
    GIT_TERMINAL_PROMPT: '0',
  };
  const git = (...args: string[]) =>
    execFileSync(
      'git',
      [
        '-c',
        'user.name=Demo Source Control',
        '-c',
        'user.email=demo-source@example.invalid',
        '-c',
        'commit.gpgsign=false',
        '-c',
        'tag.gpgsign=false',
        ...args,
      ],
      { cwd: root, env, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] },
    ).trim();
  git('init', '--quiet', '--object-format=sha1');
  writeFileSync(join(root, '.gitignore'), '.next/\nartifacts/\n.data/\n.env\n');
  writeFileSync(join(root, 'source.ts'), 'export const value = 1;\n');
  git('add', '.');
  git('commit', '--quiet', '-m', 'fixture');
  const processes = new DemoProcesses(new AbortController().signal);
  owners.push(processes);
  const remaining = (bound: number) => Math.min(bound, 15000);
  return { root, env, git, processes, remaining };
}

describe('observational CI demo source checkpoints', () => {
  it('captures clean commit/tree and permits ignored generated/private state', async () => {
    const f = fixture();
    const before = await demoSource(f.processes, f.root, f.remaining);
    expect(before).toEqual({
      commit: f.git('rev-parse', 'HEAD'),
      tree: f.git('rev-parse', 'HEAD^{tree}'),
    });
    expect(Object.isFrozen(before)).toBe(true);
    mkdirSync(join(f.root, '.next'));
    writeFileSync(join(f.root, '.next/BUILD_ID'), 'generated');
    mkdirSync(join(f.root, 'artifacts'));
    writeFileSync(join(f.root, 'artifacts/receipt.json'), '{}');
    writeFileSync(join(f.root, '.env'), 'PRIVATE=not-retained');
    await expect(demoSource(f.processes, f.root, f.remaining, before)).resolves.toEqual(before);
  });

  it.each(['staged', 'unstaged', 'untracked'])(
    'refuses actual %s source at a later checkpoint',
    async (kind) => {
      const f = fixture();
      const before = await demoSource(f.processes, f.root, f.remaining);
      writeFileSync(join(f.root, kind === 'untracked' ? 'new-source.ts' : 'source.ts'), 'changed');
      if (kind === 'staged') f.git('add', 'source.ts');
      await expect(demoSource(f.processes, f.root, f.remaining, before)).rejects.toThrow(
        'CI demo requires clean Git source',
      );
    },
  );

  it('refuses a clean changed commit even when its tree is unchanged', async () => {
    const f = fixture();
    const before = await demoSource(f.processes, f.root, f.remaining);
    f.git('commit', '--quiet', '--allow-empty', '-m', 'different commit');
    expect(f.git('rev-parse', 'HEAD^{tree}')).toBe(before.tree);
    await expect(demoSource(f.processes, f.root, f.remaining, before)).rejects.toThrow(
      'Demo source changed since its captured checkpoint',
    );
  });

  it('refuses a changed commit during the status/identity checkpoint', async () => {
    const f = fixture();
    const runner = {
      run: async (...args: Parameters<DemoProcesses['run']>) => {
        const result = await f.processes.run(...args);
        if (args[1][0] === 'status')
          f.git('commit', '--quiet', '--allow-empty', '-m', 'raced commit');
        return result;
      },
    };
    await expect(demoSource(runner, f.root, f.remaining)).rejects.toThrow(
      'Demo source changed since its captured checkpoint',
    );
  });

  it('anchors the real top-level directory and strips ambient Git redirects/configuration', async () => {
    const f = fixture();
    const other = fixture();
    for (const [key, value] of Object.entries({
      GIT_DIR: join(other.root, '.git'),
      GIT_WORK_TREE: other.root,
      GIT_INDEX_FILE: join(other.root, 'foreign-index'),
      GIT_CONFIG_COUNT: '1',
      GIT_CONFIG_KEY_0: 'core.bare',
      GIT_CONFIG_VALUE_0: 'true',
    }))
      vi.stubEnv(key, value);
    await expect(demoSource(f.processes, f.root, f.remaining)).resolves.toMatchObject({
      commit: f.git('rev-parse', 'HEAD'),
    });
    const nested = join(f.root, 'nested');
    mkdirSync(nested);
    await expect(demoSource(f.processes, nested, f.remaining)).rejects.toThrow(
      'Demo source must belong to the owned repository root',
    );
  });

  it.each([0, -1, NaN, Infinity])(
    'refuses an exhausted/nonfinite command allowance: %s',
    async (budget) => {
      const f = fixture();
      const run = vi.fn<DemoProcesses['run']>();
      await expect(demoSource({ run }, f.root, () => budget)).rejects.toThrow(
        'Demo source verification deadline',
      );
      expect(run).not.toHaveBeenCalled();
    },
  );

  it('shortens every actual Git command to the remaining finite allowance', async () => {
    const f = fixture();
    let remaining = 5000;
    const bounds: number[] = [];
    const runner = {
      run: async (...args: Parameters<DemoProcesses['run']>) => {
        bounds.push(args[2]!.timeout!);
        return f.processes.run(...args);
      },
    };
    await demoSource(runner, f.root, (bound) => Math.min(bound, (remaining -= 500)));
    expect(bounds).toEqual([4500, 4000, 3500, 3000]);
  });

  it.each(['ci', 'local'] as const)(
    'the actual %s launcher qualifies source before restores only in CI',
    (mode) => {
      const f = fixture();
      mkdirSync(join(f.root, 'scripts'));
      for (const name of [
        'demo',
        'demo-process',
        'demo-source',
        'demo-observation',
        'demo-database',
        'public-runtime',
        'wait-compose',
        'compose',
      ])
        writeFileSync(
          join(f.root, `scripts/${name}.mjs`),
          readFileSync(new URL(`../../../../../scripts/${name}.mjs`, import.meta.url)),
        );
      writeFileSync(
        join(f.root, 'scripts/demo-http.mjs'),
        'export async function vacant() {}\nexport async function demoRequest() {}\nexport async function boundedBody() {}\n',
      );
      mkdirSync(join(f.root, 'frontend/apps/web/scripts'), { recursive: true });
      writeFileSync(
        join(f.root, 'frontend/apps/web/scripts/stop-test-child.mjs'),
        readFileSync(new URL('../../scripts/stop-test-child.mjs', import.meta.url)),
      );
      f.git('add', '.');
      f.git('commit', '--quiet', '-m', 'actual launcher');
      if (mode === 'local') writeFileSync(join(f.root, 'source.ts'), 'local development change');
      const execution = spawnSync(
        process.execPath,
        [
          '--input-type=module',
          '-e',
          `
        const { DemoProcesses } = await import(${JSON.stringify(pathToFileURL(join(f.root, 'scripts/demo-process.mjs')).href)});
        const run = DemoProcesses.prototype.run;
        const commands = [];
        const stop = new Error('restore boundary');
        DemoProcesses.prototype.run = async function(command, args, options) {
          commands.push({ command, args });
          if (command === 'git') return run.call(this, command, args, options);
          if (command === 'pnpm' && args[0] === 'install') throw stop;
          return '';
        };
        const { startDemo } = await import(${JSON.stringify(pathToFileURL(join(f.root, 'scripts/demo.mjs')).href)});
        console.log = () => {};
        let caught;
        try { await startDemo(${JSON.stringify(mode)}); } catch (error) { caught = error; }
        process.stdout.write(JSON.stringify({ stopped: caught === stop, commands }));
      `,
        ],
        { env: { ...f.env, NODE_ENV: 'test' }, encoding: 'utf8', timeout: 20000 },
      );
      expect(execution.status, execution.stderr).toBe(0);
      const result = JSON.parse(execution.stdout) as {
        stopped: boolean;
        commands: Array<{ command: string; args: string[] }>;
      };
      expect(result.stopped).toBe(true);
      const sourceCommands = result.commands.filter((row) => row.command === 'git');
      expect(sourceCommands).toHaveLength(mode === 'ci' ? 4 : 0);
      if (mode === 'ci') expect(result.commands.slice(0, 4)).toEqual(sourceCommands);
      expect(result.commands.at(-1)).toMatchObject({
        command: 'pnpm',
        args: ['install', '--frozen-lockfile'],
      });
    },
    25000,
  );
});
