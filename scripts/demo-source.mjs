import { realpathSync } from 'node:fs';

/** Read only this checkout, without ambient Git repository/configuration controls. */
export async function demoSource(processes, root, remaining, expected) {
  const ownedRoot = realpathSync(root);
  const environment = {
    PATH: process.env.PATH,
    GIT_CONFIG_NOSYSTEM: '1',
    GIT_CONFIG_GLOBAL: process.platform === 'win32' ? 'NUL' : '/dev/null',
    GIT_OPTIONAL_LOCKS: '0',
    GIT_TERMINAL_PROMPT: '0',
  };
  const git = (args) => {
    const timeout = remaining(15000);
    if (!Number.isFinite(timeout) || timeout <= 0)
      throw new Error('Demo source verification deadline');
    return processes.run('git', args, {
      cwd: ownedRoot,
      env: environment,
      label: 'Read-only demo source verification',
      capture: true,
      timeout: Math.min(timeout, 15000),
    });
  };
  const topLevel = (await git(['rev-parse', '--show-toplevel'])).trim();
  if (realpathSync(topLevel) !== ownedRoot)
    throw new Error('Demo source must belong to the owned repository root');
  const identity = async () => {
    const lines = (await git(['rev-parse', 'HEAD', 'HEAD^{tree}'])).trim().split('\n');
    if (lines.length !== 2 || lines.some((value) => !/^[a-f0-9]{40}$/.test(value)))
      throw new Error('Demo source identity is invalid');
    return Object.freeze({ commit: lines[0], tree: lines[1] });
  };
  const before = await identity();
  // Include staged, tracked and nonignored untracked inputs. Status output stays
  // private; ignored build/configuration artifacts are not source attestation.
  const status = await git([
    'status', '--porcelain=v1', '-z', '--untracked-files=all', '--ignore-submodules=none',
  ]);
  if (status !== '') throw new Error('CI demo requires clean Git source');
  const after = await identity();
  if (after.commit !== before.commit || after.tree !== before.tree ||
    (expected && (after.commit !== expected.commit || after.tree !== expected.tree)))
    throw new Error('Demo source changed since its captured checkpoint');
  return after;
}
