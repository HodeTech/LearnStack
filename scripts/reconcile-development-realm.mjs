import { readFile, realpath } from 'node:fs/promises';
import { resolve, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { composeSelection, repositoryRoot } from './compose.mjs';
import { DemoCommandError, DemoProcesses, installDemoCancellation } from './demo-process.mjs';
import { publicSeederEnvironment } from './demo-database.mjs';
import { reconcileRealm, realmPassMilliseconds } from './development-realm-core.mjs';
import { acquireRealmLock, isLocalDockerEndpoint, readRealmEnvironmentFile, realmEnvironment, validateRealmTarget } from './development-realm-owner.mjs';
import { RealmError, realmFailureNotice, refuse } from './development-realm-errors.mjs';

/** Shared seed/demo operation. Neither target nor secrets are command-line parameters. */
export async function reconcileDevelopmentRealm({ selected, signal }) {
  const deadline = AbortSignal.timeout(realmPassMilliseconds);
  const active = signal ? AbortSignal.any([signal, deadline]) : deadline;
  const processes = new DemoProcesses(active);
  let lock;
  let result;
  let failure;
  const run = (args) => processes.run('docker', args, {
    cwd: selected.cwd, env: selected.env, label: 'Realm target inspection', capture: true, timeout: 5000,
  });
  try {
    if (!selected || !['dev', 'e2e'].includes(selected.mode) || selected.cwd !== repositoryRoot
        || (selected.env.DOCKER_HOST && !isLocalDockerEndpoint(selected.env.DOCKER_HOST)))
      refuse('preflight', 'configuration');
    const values = realmEnvironment(await readRealmEnvironmentFile(selected.file));
    const context = JSON.parse(await run(['context', 'inspect']));
    const model = JSON.parse(await run([...selected.args, 'config', '--format', 'json']));
    const ids = (await run([...selected.args, 'ps', '--quiet', '--no-trunc', 'keycloak'])).trim().split(/\s+/).filter(Boolean);
    if (ids.length !== 1 || !/^[a-f0-9]{64}$/.test(ids[0])) refuse('preflight', 'configuration');
    const inspections = JSON.parse(await run(['inspect', ids[0]]));
    if (!Array.isArray(inspections) || inspections.length !== 1) refuse('preflight', 'configuration');
    const target = validateRealmTarget(selected, model, ids, inspections[0], context, values);
    lock = await acquireRealmLock(target.lockIdentity);
    const manifest = JSON.parse(await processes.run('dotnet', [join(repositoryRoot,
      'backend/src/LearnStack.Tools.Seeder/bin/Release/net10.0/LearnStack.Tools.Seeder.dll'), '--identity-manifest'], {
      cwd: join(repositoryRoot, 'backend'), env: publicSeederEnvironment(undefined),
      label: 'Realm identity projection', capture: true, timeout: 5000,
    }));
    const desired = JSON.parse(await readFile(join(repositoryRoot, 'infra/keycloak/realms/learnstack.json'), 'utf8'));
    result = await reconcileRealm({ ...target, adminUsername: values.KEYCLOAK_ADMIN,
      adminPassword: values.KEYCLOAK_ADMIN_PASSWORD, webSecret: values.KEYCLOAK_WEB_CLIENT_SECRET,
      manifest, desired, signal: active });
  } catch (error) {
    failure = active.aborted ? new RealmError(error instanceof RealmError ? error.stage : 'preflight',
      signal?.aborted ? 'cancelled' : 'deadline')
      : error instanceof RealmError ? error : new RealmError('preflight',
        error instanceof DemoCommandError && ['deadline', 'cancelled', 'cleanup'].includes(error.reason)
          ? error.reason : 'configuration');
  }
  // If child cleanup is not established, keep the exclusive file as an explicit refusal.
  try { await processes.dispose(); }
  catch { refuse('cleanup', 'cleanup'); }
  if (lock) {
    try { await lock.release(); }
    catch { refuse('cleanup', 'cleanup'); }
  }
  if (failure) throw failure;
  return result;
}

if (process.argv[1] && await realpath(resolve(process.argv[1])).catch(() => '') === fileURLToPath(import.meta.url)) {
  const stop = new AbortController();
  const remove = installDemoCancellation(() => stop.abort());
  try {
    if (process.argv.length !== 2) refuse('preflight', 'configuration');
    const selected = composeSelection(process.env.LEARNSTACK_COMPOSE_MODE ?? 'dev');
    const result = await reconcileDevelopmentRealm({ selected, signal: stop.signal });
    console.log(`realm: completed (${result.mutations} writes)`);
  } catch (error) {
    console.error(realmFailureNotice(error));
    process.exitCode = 1;
  } finally { remove(); }
}
