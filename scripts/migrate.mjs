import { execFileSync, spawnSync } from 'node:child_process';
import { existsSync } from 'node:fs';
import { basename, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { parseEnv } from 'node:util';
import { composeProcessEnvironment, readComposeEnvironmentFile } from './compose.mjs';
import { publicSeederEnvironment } from './demo-database.mjs';
import { RealmError, realmFailureNotice } from './development-realm-errors.mjs';

const connectionKey = 'ConnectionStrings__Migration';
const identityKeys = ['Identity__AuthorityId', 'Authentication__Tenant__Issuer'];
const keys = [connectionKey, ...identityKeys];
class MigrationConfigurationError extends Error {}
const configurationFailure = () => new MigrationConfigurationError('Migration configuration refused: use one source for ConnectionStrings__Migration and the complete identity authority pair, or omit both identity keys for schema-only migration. No migration was attempted.');

function fileConfiguration(path) {
  if (!existsSync(path)) return {};
  const source = readComposeEnvironmentFile(path);
  if (source.includes('\0')) throw configurationFailure();
  const parsed = parseEnv(source);
  for (const key of keys) {
    const canonical = [...source.matchAll(new RegExp(`^${key}=`, 'gm'))].length;
    const assignments = [...source.matchAll(new RegExp(`^[ \\t]*(?:export[ \\t]+)?${key}[ \\t]*=`, 'gm'))].length;
    if (canonical !== assignments || canonical > 1 || (canonical === 1 && !parsed[key]))
      throw configurationFailure();
  }
  return Object.fromEntries(keys.filter(key => parsed[key] !== undefined).map(key => [key, parsed[key]]));
}

/** A migration credential and its optional immutable registration are one deployment selection. */
export function migrationConfiguration(environment = process.env, root = process.cwd()) {
  const ambient = Object.fromEntries(keys.filter(key => environment[key] !== undefined && environment[key] !== '')
    .map(key => [key, environment[key]]));
  let selected;
  if (Object.keys(ambient).length !== 0) {
    if (!ambient[connectionKey] || (identityKeys.some(key => ambient[key]) && !identityKeys.every(key => ambient[key])))
      throw configurationFailure();
    // Complete ambient deployment input needs no file. Schema-only ambient input
    // must not silently inherit (or silently discard) a file's authority pair.
    if (!identityKeys.some(key => ambient[key])) {
      const file = fileConfiguration(resolve(root, environment.LEARNSTACK_COMPOSE_ENV_FILE ?? '.env'));
      if (identityKeys.some(key => file[key] !== undefined)) throw configurationFailure();
    }
    selected = ambient;
  } else {
    selected = fileConfiguration(resolve(root, environment.LEARNSTACK_COMPOSE_ENV_FILE ?? '.env'));
  }
  if (!selected[connectionKey] || (identityKeys.some(key => selected[key]) && !identityKeys.every(key => selected[key]))
      || keys.some(key => selected[key] !== undefined && (!selected[key].trim() || /[\r\n\0]/.test(selected[key]))))
    throw configurationFailure();
  return Object.freeze(selected);
}

function validateRole(connection, root, environment) {
  let role;
  try {
    role = execFileSync('awk', ['-f', join(root, 'scripts/connection-string.awk'), '-v', 'field=user'], {
      input: connection, encoding: 'utf8', env: publicSeederEnvironment(undefined, environment),
      stdio: ['pipe', 'pipe', 'pipe'], timeout: 5000, maxBuffer: 2048,
    }).trim();
  } catch { throw configurationFailure(); }
  if (role !== 'learnstack_migration') {
    const named = /^[a-z_][a-z0-9_]*$/.test(role) ? role : '<invalid>';
    throw new MigrationConfigurationError(`ConnectionStrings__Migration names Username='${named}', not learnstack_migration. Use the Npgsql key/value form; no migration was attempted.`);
  }
}

function run(args, cwd, env, quiet = false) {
  const result = spawnSync('dotnet', args, { cwd, env, stdio: ['ignore', quiet ? 'ignore' : 'inherit', 'inherit'] });
  return result.error || result.signal ? 1 : result.status ?? 1;
}

/** Make owns the ordered project list; each child receives only its own credential. */
export function migrate(projects, environment = process.env, root = process.cwd()) {
  // Preserve the credential-role refusal before reading any unrelated file.
  if (environment[connectionKey]) validateRole(environment[connectionKey], root, environment);
  const configuration = migrationConfiguration(environment, root);
  validateRole(configuration[connectionKey], root, environment);
  const prerequisites = { ...publicSeederEnvironment(undefined, environment), ...composeProcessEnvironment(environment) };
  for (const key of ['LEARNSTACK_COMPOSE_PROJECT', 'LEARNSTACK_COMPOSE_ENV_FILE', 'LEARNSTACK_COMPOSE_MODE'])
    if (environment[key] !== undefined) prerequisites[key] = environment[key];
  const backend = join(root, 'backend');
  for (const [args, quiet] of [
    [['tool', 'restore'], true],
    [['build', 'src/LearnStack.Api', '--configuration', 'Release', '--nologo'], false],
    ...(configuration.Identity__AuthorityId
      ? [[['build', 'src/LearnStack.Tools.IdentityAuthorityInstaller', '--configuration', 'Release', '--nologo'], false]] : []),
  ]) {
    const status = run(args, backend, prerequisites, quiet);
    if (status !== 0) return status;
  }
  let failed = 0;
  const chains = [...new Set(projects)].filter(project => existsSync(join(root, project, 'Persistence/Migrations')));
  for (const project of chains) {
    console.log(`==> ${basename(project)}`);
    if (run(['ef', 'database', 'update', '--project', '../' + project,
      '--startup-project', 'src/LearnStack.Api', '--configuration', 'Release', '--no-build'],
    backend, { ...prerequisites, [connectionKey]: configuration[connectionKey] }) !== 0) failed = 1;
  }
  if (chains.length === 0) console.log('No project carries Persistence/Migrations yet.');
  if (!failed && configuration.Identity__AuthorityId)
    failed = run(['backend/src/LearnStack.Tools.IdentityAuthorityInstaller/bin/Release/net10.0/LearnStack.Tools.IdentityAuthorityInstaller.dll'],
      root, { ...publicSeederEnvironment(undefined, environment), ...configuration });
  return failed;
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try { process.exitCode = migrate(process.argv.slice(2)); }
  catch (error) {
    // Keep closed setup guidance without exposing private reader/provider details.
    const message = error instanceof RealmError ? realmFailureNotice(error)
      : error instanceof MigrationConfigurationError ? error.message : configurationFailure().message;
    console.error(message);
    process.exitCode = 1;
  }
}
