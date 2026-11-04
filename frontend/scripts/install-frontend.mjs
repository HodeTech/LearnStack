// The supported restore and lifecycle hooks use no installed dependency.
import { readFileSync, realpathSync } from 'node:fs';
import { isAbsolute, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  acquireFrameworkLease,
  FRAMEWORK_FRONTEND_ROOT,
  FrameworkAdmissionError,
  resolveQualifiedPnpm,
  verifyFrameworkInstallation,
} from './framework-installation.mjs';
import {
  assertFrameworkProcessFamilySupport,
  runFrameworkProcessFamily,
} from './framework-process-family.mjs';

const root = FRAMEWORK_FRONTEND_ROOT;
const appRoot = join(root, 'apps/web');
function fail(reason) {
  throw new FrameworkAdmissionError(reason);
}
function installationOwner() {
  try {
    const value = JSON.parse(readFileSync(join(root, '.framework-state/writer.json'), 'utf8'));
    if (
      value.format !== 1 ||
      value.kind !== 'install' ||
      !Number.isSafeInteger(value.pid) ||
      !/^[0-9a-f]{32}$/.test(value.nonce)
    )
      fail('install_owner');
    process.kill(value.pid, 0);
    return value;
  } catch {
    fail('install_owner');
  }
}
function outerOwner() {
  const token = process.env.LEARNSTACK_FRAMEWORK_INSTALL_OWNER;
  if (token === undefined) return false;
  if (installationOwner().nonce !== token) fail('install_owner');
  return true;
}
function completeInstallation() {
  const manager = resolveQualifiedPnpm({
    frontendRoot: root,
    executable: process.env.npm_execpath,
    lifecycle: true,
  });
  if (!manager) fail('manager_missing');
  verifyFrameworkInstallation({ appRoot, frontendRoot: root, distribution: true });
  if (!outerOwner()) fail('install_owner');
}
async function main(values) {
  if (values.length > 1) fail('install_arguments');
  const option = values[0];
  if (option === '--manager-path') {
    const path = process.env.npm_execpath;
    if (!path || !isAbsolute(path) || path.includes('\n') || path.includes('\r'))
      fail('manager_probe');
    process.stdout.write(`${realpathSync(path)}\n`);
    return;
  }
  if (option === '--lifecycle') {
    resolveQualifiedPnpm({
      frontendRoot: root,
      executable: process.env.npm_execpath,
      lifecycle: true,
    });
    if (!outerOwner()) fail('install_owner');
    return;
  }
  if (option === '--installed') {
    completeInstallation();
    return;
  }
  if (option !== undefined && option !== '--prepare-lock') fail('install_arguments');
  const prepareLock = option === '--prepare-lock';
  assertFrameworkProcessFamilySupport();
  const manager = resolveQualifiedPnpm({ frontendRoot: root });
  const lease = acquireFrameworkLease({ appRoot, frontendRoot: root, kind: 'install' });
  let failed = false;
  try {
    await runFrameworkProcessFamily(
      manager.command,
      [
        ...manager.args,
        'install',
        '--use-store-server=false',
        ...(prepareLock ? ['--lockfile-only', '--ignore-scripts'] : ['--frozen-lockfile']),
      ],
      {
        cwd: root,
        stdio: 'inherit',
        environment: { ...process.env, LEARNSTACK_FRAMEWORK_INSTALL_OWNER: lease.record.nonce },
      },
    );
    if (!prepareLock)
      verifyFrameworkInstallation({ appRoot, frontendRoot: root, distribution: true });
  } catch (error) {
    failed = true;
    throw error;
  } finally {
    try {
      lease.release();
    } catch (error) {
      if (!failed) throw error;
      console.error('Framework install ownership cleanup failed');
    }
  }
}
if (process.argv[1] && realpathSync(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    await main(process.argv.slice(2));
  } catch (error) {
    console.error(
      error instanceof FrameworkAdmissionError ? error.message : 'Framework installation refused',
    );
    process.exitCode = 1;
  }
}
