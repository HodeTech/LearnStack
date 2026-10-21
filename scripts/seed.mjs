import { readFile } from 'node:fs/promises';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { parseEnv } from 'node:util';
import { composeSelection, repositoryRoot } from './compose.mjs';
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
  const connection = values.ConnectionStrings__Default;
  if (!connection || (process.env.ConnectionStrings__Default !== undefined
      && process.env.ConnectionStrings__Default !== connection)) refuse('preflight', 'configuration');
  const processes = new DemoProcesses(signal);
  try {
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
