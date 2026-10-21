import { readFile } from 'node:fs/promises';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { parseEnv } from 'node:util';
import { composeProcessEnvironment, composeSelection, repositoryRoot } from './compose.mjs';
import { DemoCommandError, DemoProcesses, installDemoCancellation } from './demo-process.mjs';
import { publicSeederEnvironment } from './demo-database.mjs';
import { RealmError, realmFailureNotice, refuse } from './development-realm-errors.mjs';
import { realmEnvironment } from './development-realm-owner.mjs';
import { reconcileDevelopmentRealm } from './reconcile-development-realm.mjs';
import { waitCompose } from './wait-compose.mjs';

const backendRoot = join(repositoryRoot, 'backend');
const seederDll = join(backendRoot,
  'src/LearnStack.Tools.Seeder/bin/Release/net10.0/LearnStack.Tools.Seeder.dll');

async function selectedConfiguration(selected) {
  const source = await readFile(selected.file, 'utf8');
  realmEnvironment(source);
  return parseEnv(source);
}

/** One selected stack and cancellation owner for readiness, application seed and identity. */
export async function seedDevelopment({ selected, signal }) {
  const values = await selectedConfiguration(selected);
  for (const key of ['ConnectionStrings__Default', 'ConnectionStrings__Migration'])
    if (!values[key] || (process.env[key] !== undefined && process.env[key] !== values[key]))
      refuse('preflight', 'configuration');
  const connection = values.ConnectionStrings__Default;
  const prerequisites = { ...publicSeederEnvironment(), ...composeProcessEnvironment(selected.env) };
  const selectedPrerequisites = { ...prerequisites,
    LEARNSTACK_COMPOSE_PROJECT: selected.project, LEARNSTACK_COMPOSE_ENV_FILE: selected.file,
    LEARNSTACK_COMPOSE_MODE: selected.mode };
  const processes = new DemoProcesses(signal);
  try {
    await processes.run('docker', ['compose', 'version'], {
      cwd: repositoryRoot, env: prerequisites, label: 'Docker Compose prerequisite', timeout: 5000,
    });
    await processes.run('make', ['dev'], {
      cwd: repositoryRoot,
      env: selectedPrerequisites, label: 'Selected stack startup', timeout: 600000,
    });
    // Keep readiness Docker handles in this owner. A nested wait-postgres CLI
    // would create detached grandchildren with a competing teardown deadline.
    await waitCompose(selected, processes, signal, true);
    await processes.run('make', ['migrate'], {
      cwd: repositoryRoot,
      env: { ...selectedPrerequisites, ConnectionStrings__Migration: values.ConnectionStrings__Migration },
      label: 'Selected migrations', timeout: 300000,
    });
    await waitCompose(selected, processes, signal);
    await processes.run('dotnet', ['build', 'src/LearnStack.Tools.Seeder', '--configuration', 'Release', '--nologo'], {
      cwd: backendRoot, env: publicSeederEnvironment(), label: 'Release Seeder build', timeout: 180000,
    });
    await processes.run('dotnet', [seederDll], {
      cwd: backendRoot, env: publicSeederEnvironment(connection), label: 'Application seed', timeout: 180000,
    });
    return await reconcileDevelopmentRealm({ selected, signal });
  } finally {
    await processes.dispose();
  }
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const controller = new AbortController();
  const removeCancellation = installDemoCancellation(() => controller.abort());
  try {
    const args = process.argv.slice(2);
    if (args.length > 1 || (args.length === 1 && args[0] !== '--validate-config'))
      refuse('preflight', 'configuration');
    const selected = composeSelection(process.env.LEARNSTACK_COMPOSE_MODE ?? 'dev');
    if (args[0] === '--validate-config') await selectedConfiguration(selected);
    else {
      const result = await seedDevelopment({ selected, signal: controller.signal });
      console.log(`seed: application data and development realm converged (${result.mutations} realm writes).`);
      console.log('seed: existing credentials are retained; account setup is documented in infra/keycloak/README.md.');
    }
  } catch (error) {
    if (error instanceof RealmError) {
      console.error(realmFailureNotice(error));
    } else {
      console.error(error instanceof DemoCommandError
        ? error.message.replace(/^demo:/, 'seed:') : 'seed: setup or convergence failed; verify configuration and migrations.');
    }
    process.exitCode = 1;
  } finally {
    removeCancellation();
  }
}
