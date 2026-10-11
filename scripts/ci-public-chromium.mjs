import assert from 'node:assert/strict';
import { randomBytes } from 'node:crypto';
import {
  accessSync,
  constants,
  lstatSync,
  mkdirSync,
  readFileSync,
  readdirSync,
  realpathSync,
  renameSync,
  rmdirSync,
  rmSync,
  unlinkSync,
  writeFileSync,
} from 'node:fs';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { DemoProcesses, installDemoCancellation } from './demo-process.mjs';

const root = fileURLToPath(new URL('../', import.meta.url));
const restrictionPath = '/proc/sys/kernel/apparmor_restrict_unprivileged_userns';

async function runCommand(command, args, options) {
  const controller = new AbortController();
  const removeCancellation = installDemoCancellation(() => controller.abort());
  const processes = new DemoProcesses(controller.signal);
  try {
    return await processes.run(command, args, {
      ...options,
      capture: true,
      observe: (chunk, stream) => {
        if (stream === 'stderr') process.stderr.write(chunk);
      },
    });
  } finally {
    try {
      await processes.dispose();
    } finally {
      removeCancellation();
    }
  }
}

/** AppArmor attachment syntax is a pattern language: accept literal paths only. */
export function chromiumProfile(name, executable) {
  assert.match(name, /^learnstack-public-chromium-[a-f0-9]{32}$/);
  assert.match(executable, /^\/[a-zA-Z0-9_./-]+\/chrome$/);
  assert.equal(resolve(executable), executable);
  // Chromium's documented userns exception, narrowed to this executable.
  // https://chromium.googlesource.com/chromium/src/+/main/docs/security/apparmor-userns-restrictions.md
  return `abi <abi/4.0>,\ninclude <tunables/global>\nprofile ${name} "${executable}" flags=(unconfined) {\n  userns,\n}\n`;
}

function statOrMissing(path) {
  try {
    return lstatSync(path);
  } catch (error) {
    if (error.code === 'ENOENT') return undefined;
    throw error;
  }
}

/** Injections are for tests only; the CLI always uses fixed host paths/commands. */
export function chromiumPrerequisite({
  env = process.env,
  platform = process.platform,
  run = runCommand,
  restriction = restrictionPath,
} = {}) {
  assert.equal(platform, 'linux', 'Chromium prerequisite requires Linux');
  assert.equal(env.GITHUB_ACTIONS, 'true', 'Chromium prerequisite requires GitHub Actions');
  assert.equal(env.RUNNER_ENVIRONMENT, 'github-hosted', 'Requires an ephemeral hosted runner');
  assert.match(env.GITHUB_RUN_ID ?? '', /^\d+$/);
  assert.match(env.GITHUB_RUN_ATTEMPT ?? '', /^\d+$/);
  assert.match(env.GITHUB_JOB ?? '', /^[a-zA-Z0-9_-]+$/);
  assert.ok(env.RUNNER_TEMP && resolve(env.RUNNER_TEMP) === realpathSync(env.RUNNER_TEMP));
  const runner = `${env.GITHUB_RUN_ID}-${env.GITHUB_RUN_ATTEMPT}-${env.GITHUB_JOB}`;
  const directory = join(env.RUNNER_TEMP, `learnstack-public-chromium-${runner}`);
  const marker = join(directory, 'owner.json');
  const profile = join(directory, 'profile');
  const phase = join(directory, 'phase');
  const uid = process.getuid();
  const policy = (action) =>
    run('sudo', ['-n', 'apparmor_parser', '--skip-cache', action, profile], {
      cwd: root,
      timeout: 10000,
      label: `Owned Chromium AppArmor ${action}`,
    });
  const updatePhase = (value) => {
    writeFileSync(join(directory, 'phase-next'), value, {
      flag: 'wx',
      mode: 0o600,
    });
    renameSync(join(directory, 'phase-next'), phase);
  };
  function ownedState() {
    const dir = lstatSync(directory);
    assert.ok(dir.isDirectory() && dir.uid === uid && (dir.mode & 0o777) === 0o700);
    assert.deepEqual(readdirSync(directory).sort(), ['owner.json', 'phase', 'profile']);
    for (const file of [marker, profile, phase]) {
      const stat = lstatSync(file);
      assert.ok(stat.isFile() && stat.uid === uid && stat.nlink === 1);
      assert.equal(stat.mode & 0o777, 0o600);
    }
    const state = JSON.parse(readFileSync(marker, 'utf8'));
    assert.deepEqual(Object.keys(state).sort(), [
      'executable',
      'name',
      'required',
      'runner',
      'schema',
    ]);
    assert.equal(state.schema, 1);
    assert.equal(state.runner, runner);
    assert.equal(typeof state.required, 'boolean');
    assert.equal(readFileSync(profile, 'utf8'), chromiumProfile(state.name, state.executable));
    const status = readFileSync(phase, 'utf8');
    assert.ok(['pending', 'loaded', 'unloaded', 'unneeded'].includes(status));
    assert.ok(state.required ? status !== 'unneeded' : status === 'unneeded');
    return { state, status };
  }
  async function cleanup() {
    if (!statOrMissing(directory)) return;
    const { status } = ownedState();
    // An interrupted load is not proof of ownership. Retain evidence and fail.
    assert.notEqual(status, 'pending', 'Chromium AppArmor load was not confirmed');
    if (status === 'loaded') {
      await policy('--remove');
      updatePhase('unloaded');
    }
    ownedState();
    for (const file of [profile, phase, marker]) unlinkSync(file);
    rmdirSync(directory);
  }
  async function prepare() {
    assert.ok(!statOrMissing(directory), 'Chromium prerequisite marker must be fresh');
    const discovered = (
      await run(
        'pnpm',
        [
          'exec',
          'node',
          '--input-type=module',
          '-e',
          "import { chromium } from 'playwright'; process.stdout.write(chromium.executablePath());",
        ],
        {
          cwd: join(root, 'frontend'),
          timeout: 10000,
          label: 'Managed Chromium executable',
        },
      )
    ).trim();
    const executable = realpathSync(discovered);
    accessSync(executable, constants.X_OK);
    assert.ok(lstatSync(executable).isFile(), 'Managed Chromium must be a regular executable');
    const name = `learnstack-public-chromium-${randomBytes(16).toString('hex')}`;
    const source = chromiumProfile(name, executable);
    let required = false;
    try {
      const setting = readFileSync(restriction, 'utf8').trim();
      assert.ok(['0', '1'].includes(setting), 'Unknown AppArmor userns restriction');
      required = setting === '1';
    } catch (error) {
      if (error.code !== 'ENOENT') throw error;
    }
    mkdirSync(directory, { mode: 0o700 });
    writeFileSync(marker, JSON.stringify({ schema: 1, runner, name, executable, required }), {
      flag: 'wx',
      mode: 0o600,
    });
    writeFileSync(profile, source, { flag: 'wx', mode: 0o600 });
    writeFileSync(phase, required ? 'pending' : 'unneeded', {
      flag: 'wx',
      mode: 0o600,
    });
    try {
      if (required) {
        // --add never replaces another definition. If it fails or is interrupted,
        // retain pending state: neither ownership nor absence has been proven.
        await policy('--add');
        updatePhase('loaded');
      }
      const smoke = join(directory, 'smoke');
      mkdirSync(smoke, { mode: 0o700 });
      try {
        const document = await run(
          executable,
          [
            '--headless=new',
            `--user-data-dir=${smoke}`,
            '--no-first-run',
            '--no-default-browser-check',
            '--disable-background-networking',
            '--disable-component-update',
            '--disable-sync',
            '--no-proxy-server',
            '--dump-dom',
            'about:blank',
          ],
          {
            cwd: root,
            timeout: 20000,
            label: 'Sandbox-enabled managed Chromium smoke',
          },
        );
        assert.match(document, /<html[\s>]/i, 'Chromium smoke must render a document');
      } finally {
        rmSync(smoke, { recursive: true });
      }
    } catch (error) {
      try {
        await cleanup();
      } catch (cleanupError) {
        throw new AggregateError([error, cleanupError], 'Chromium prerequisite and cleanup failed');
      }
      throw error;
    }
    return { executable, apparmor: required };
  }
  return { prepare, cleanup };
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    const [action, ...extra] = process.argv.slice(2);
    assert.ok(['prepare', 'cleanup'].includes(action) && extra.length === 0);
    const result = await chromiumPrerequisite()[action]();
    console.log(
      `Chromium prerequisite ${action} passed${result ? ` (AppArmor: ${result.apparmor})` : ''}`,
    );
  } catch (error) {
    console.error(error.message);
    process.exitCode = 1;
  }
}
