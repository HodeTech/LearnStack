import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import {
  existsSync,
  mkdirSync,
  mkdtempSync,
  readFileSync,
  realpathSync,
  rmSync,
  symlinkSync,
  writeFileSync,
} from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import test from 'node:test';
import { promisify } from 'node:util';
import { chromiumPrerequisite, chromiumProfile } from './ci-public-chromium.mjs';

const execute = promisify(execFile);

function fixture(t, { setting = '1', loadFails = false, smokeFails = false } = {}) {
  const temp = mkdtempSync(join(realpathSync(tmpdir()), 'ci-public-chromium-test-'));
  t.after(() => rmSync(temp, { recursive: true, force: true }));
  const executable = join(temp, 'managed/chrome');
  mkdirSync(join(temp, 'managed'));
  writeFileSync(executable, '#!/bin/sh\nprintf "<html><head></head><body></body></html>\\n"\n', {
    mode: 0o700,
  });
  const restriction = join(temp, 'userns');
  if (setting !== null) writeFileSync(restriction, setting);
  const env = {
    GITHUB_ACTIONS: 'true',
    RUNNER_ENVIRONMENT: 'github-hosted',
    GITHUB_RUN_ID: '123',
    GITHUB_RUN_ATTEMPT: '2',
    GITHUB_JOB: 'public-demo',
    RUNNER_TEMP: temp,
  };
  const directory = join(temp, 'learnstack-public-chromium-123-2-public-demo');
  const calls = [];
  const loaded = new Set(['foreign-profile']);
  const control = { removeFails: false };
  async function run(command, args, options) {
    calls.push({ command, args, options });
    if (command === 'pnpm') {
      assert.deepEqual(args.slice(0, 4), ['exec', 'node', '--input-type=module', '-e']);
      assert.match(args[4], /chromium\.executablePath\(\)/);
      assert.ok(options.cwd.endsWith('/frontend'));
      return executable;
    }
    if (command === 'sudo') {
      assert.deepEqual(args.slice(0, 3), ['-n', 'apparmor_parser', '--skip-cache']);
      const source = readFileSync(args[4], 'utf8');
      const name = source.match(/profile (\S+) /)[1];
      if (args[3] === '--add') {
        // Model --add refusing a foreign definition with this same name.
        if (loadFails) {
          loaded.add(name);
          throw new Error('Definition already exists');
        }
        assert.ok(!loaded.has(name));
        loaded.add(name);
      } else {
        assert.equal(args[3], '--remove');
        if (control.removeFails) throw new Error('Removal failed');
        assert.ok(loaded.delete(name));
      }
      return '';
    }
    assert.equal(command, executable);
    assert.equal(options.timeout, 20000);
    assert.ok(args.includes('--headless=new') && args.includes('--dump-dom'));
    assert.equal(args.at(-1), 'about:blank');
    assert.ok(!args.some((arg) => /no-sandbox|disable.*sandbox|ignore-certificate/.test(arg)));
    assert.equal(
      args.find((arg) => arg.startsWith('--user-data-dir=')),
      `--user-data-dir=${directory}/smoke`,
    );
    assert.ok(existsSync(join(directory, 'smoke')));
    if (smokeFails) throw new Error('Sandbox launch refused');
    return (await execute(command, args, { timeout: options.timeout })).stdout;
  }
  const prerequisite = chromiumPrerequisite({
    env,
    platform: 'linux',
    run,
    restriction,
  });
  return {
    prerequisite,
    directory,
    executable,
    env,
    restriction,
    calls,
    loaded,
    control,
    temp,
  };
}

test('AppArmor source attaches only a literal executable path', () => {
  const name = `learnstack-public-chromium-${'a'.repeat(32)}`;
  const profile = chromiumProfile(
    name,
    '/home/runner/.cache/ms-playwright/chromium-1248/chrome-linux64/chrome',
  );
  assert.match(profile, /^abi <abi\/4\.0>,\ninclude <tunables\/global>\n/);
  assert.match(profile, /flags=\(unconfined\) \{\n  userns,\n\}\n$/);
  for (const path of [
    '/tmp/**/chrome',
    '/tmp/[ab]/chrome',
    '/tmp/../bin/chrome',
    '/tmp/"/chrome',
    '/tmp/\n/chrome',
  ])
    assert.throws(() => chromiumProfile(name, path));
});

test('prepare loads one exact profile, renders sandbox-enabled smoke and cleans only its policy', async (t) => {
  const f = fixture(t);
  const foreign = join(f.temp, 'foreign-profile-file');
  writeFileSync(foreign, 'untouched');
  assert.deepEqual(await f.prerequisite.prepare(), {
    executable: f.executable,
    apparmor: true,
  });
  assert.equal(f.loaded.size, 2);
  const source = readFileSync(join(f.directory, 'profile'), 'utf8');
  assert.ok(source.includes(`"${f.executable}" flags=(unconfined)`));
  assert.ok(!existsSync(join(f.directory, 'smoke')));
  await f.prerequisite.cleanup();
  await f.prerequisite.cleanup();
  assert.deepEqual([...f.loaded], ['foreign-profile']);
  assert.equal(readFileSync(foreign, 'utf8'), 'untouched');
  assert.ok(!existsSync(f.directory));
  assert.deepEqual(
    f.calls.filter((call) => call.command === 'sudo').map((call) => call.args[3]),
    ['--add', '--remove'],
  );
});

for (const setting of ['0', null]) {
  test(`restriction ${setting ?? 'absent'} needs no policy but still requires browser smoke`, async (t) => {
    const f = fixture(t, { setting });
    assert.equal((await f.prerequisite.prepare()).apparmor, false);
    await f.prerequisite.cleanup();
    assert.ok(f.calls.some((call) => call.command === f.executable));
    assert.ok(!f.calls.some((call) => call.command === 'sudo'));
  });
}

test('--add failure never removes the existing foreign definition', async (t) => {
  const f = fixture(t, { loadFails: true });
  await assert.rejects(f.prerequisite.prepare(), (error) => {
    assert.ok(error instanceof AggregateError);
    assert.match(error.errors[0].message, /Definition already exists/);
    assert.match(error.errors[1].message, /load was not confirmed/);
    return true;
  });
  assert.equal(f.loaded.size, 2);
  assert.deepEqual(
    f.calls.filter((call) => call.command === 'sudo').map((call) => call.args[3]),
    ['--add'],
  );
  assert.equal(readFileSync(join(f.directory, 'phase'), 'utf8'), 'pending');
  await assert.rejects(f.prerequisite.cleanup(), /load was not confirmed/);
  assert.equal(f.loaded.size, 2);
});

test('smoke failure unloads its successfully created policy and removes its profile', async (t) => {
  const f = fixture(t, { smokeFails: true });
  await assert.rejects(f.prerequisite.prepare(), /Sandbox launch refused/);
  assert.deepEqual([...f.loaded], ['foreign-profile']);
  assert.ok(!existsSync(f.directory));
});

test('duplicate prepare refuses the existing marker without loading again', async (t) => {
  const f = fixture(t);
  await f.prerequisite.prepare();
  await assert.rejects(f.prerequisite.prepare(), /marker must be fresh/);
  assert.equal(f.calls.filter((call) => call.command === 'sudo').length, 1);
  await f.prerequisite.cleanup();
});

for (const mutation of ['runner', 'profile', 'extra-file', 'symlink', 'pending']) {
  test(`cleanup refuses ${mutation} ownership mismatch before any policy command`, async (t) => {
    const f = fixture(t);
    await f.prerequisite.prepare();
    const commands = f.calls.length;
    if (mutation === 'runner') {
      const marker = join(f.directory, 'owner.json');
      const state = JSON.parse(readFileSync(marker, 'utf8'));
      state.runner = '456-1-other-job';
      writeFileSync(marker, JSON.stringify(state));
    } else if (mutation === 'profile') {
      writeFileSync(join(f.directory, 'profile'), 'profile foreign-profile { }');
    } else if (mutation === 'extra-file') {
      writeFileSync(join(f.directory, 'foreign'), 'untouched');
    } else if (mutation === 'symlink') {
      const marker = join(f.directory, 'owner.json');
      const foreign = join(f.temp, 'foreign-marker');
      writeFileSync(foreign, readFileSync(marker));
      rmSync(marker);
      symlinkSync(foreign, marker);
    } else {
      writeFileSync(join(f.directory, 'phase'), 'pending');
    }
    await assert.rejects(f.prerequisite.cleanup());
    assert.equal(f.calls.length, commands);
    assert.equal(f.loaded.size, 2);
    assert.ok(existsSync(f.directory));
  });
}

test('failed unload retains owned marker and allows a successful cleanup retry', async (t) => {
  const f = fixture(t);
  await f.prerequisite.prepare();
  f.control.removeFails = true;
  await assert.rejects(f.prerequisite.cleanup(), /Removal failed/);
  assert.equal(readFileSync(join(f.directory, 'phase'), 'utf8'), 'loaded');
  f.control.removeFails = false;
  await f.prerequisite.cleanup();
  assert.deepEqual([...f.loaded], ['foreign-profile']);
  assert.ok(!existsSync(f.directory));
});

test('unknown kernel setting and missing executable fail before creating owned state', async (t) => {
  const f = fixture(t, { setting: 'unexpected' });
  await assert.rejects(f.prerequisite.prepare(), /Unknown AppArmor/);
  assert.ok(!existsSync(f.directory));
  rmSync(f.executable);
  await assert.rejects(f.prerequisite.prepare(), /ENOENT/);
  assert.ok(!existsSync(f.directory));
  assert.ok(!f.calls.some((call) => call.command === 'sudo'));
});

test('host policy operations refuse a local or self-hosted environment', (t) => {
  const f = fixture(t);
  for (const change of [
    { GITHUB_ACTIONS: 'false' },
    { RUNNER_ENVIRONMENT: 'self-hosted' },
    { GITHUB_RUN_ID: '../other' },
  ])
    assert.throws(() => chromiumPrerequisite({ env: { ...f.env, ...change }, platform: 'linux' }));
});
